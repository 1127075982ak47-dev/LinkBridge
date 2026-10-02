using System.Buffers.Binary;
using System.Diagnostics;
using LinkBridge.Media;

void Assert(bool pass,string label){if(!pass)throw new Exception(label);Console.WriteLine("PASS "+label);}
async Task ExpectCanceled(Func<Task> run)
{try{await run();throw new Exception("Cancellation not observed");}catch(OperationCanceledException){}}

using(var canceled=new CancellationTokenSource())
{
    canceled.Cancel();await ExpectCanceled(()=>PlaybackDeviceTest.PlayAsync("__invalid__",canceled.Token));
    Assert(true,"public pre-canceled request returns before endpoint/native construction");
}

// The production pump sends non-zero PCM ONLY into this memory collector, never a sound device.
var data=new List<byte[]>();var timestamps=new List<long>();var clock=Stopwatch.StartNew();
await PlaybackDeviceTest.PumpMacToneAsync(block=>{data.Add(block.ToArray());timestamps.Add(clock.ElapsedMilliseconds);},()=>null,CancellationToken.None);
Assert(data.Count==30 && data.All(d=>d.Length==3840),"production tone pump emits bounded 20ms complete stereo blocks");
Assert(timestamps.Zip(timestamps.Skip(1),(a,b)=>b-a).All(ms=>ms>=8),"tone blocks are paced instead of dumped into the 80ms ring");
var pcm=data.SelectMany(d=>d).ToArray();var samples=new double[pcm.Length/4];var stereo=true;
for(var i=0;i<samples.Length;i++)
{
    var left=BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i*4));var right=BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i*4+2));
    stereo&=left==right;samples[i]=left/32768.0;
}
Assert(stereo && samples.Max(x=>Math.Abs(x))<=0.0801,"actual generated PCM is equal-channel stereo at low 0.08 full-scale gain");
double Rms(int begin,int length)=>Math.Sqrt(samples.Skip(begin).Take(length).Average(x=>x*x));
Assert(samples[0]==0 && Rms(0,960)<Rms(9600,960) && samples.Skip(26400).All(x=>x==0),"generated samples fade in and finish with silence after fade out");
double Energy(int frequency)
{
    double real=0,imaginary=0;
    for(var i=4800;i<19200;i++){var phase=2*Math.PI*frequency*i/48000;real+=samples[i]*Math.Cos(phase);imaginary+=samples[i]*Math.Sin(phase);}
    return real*real+imaginary*imaginary;
}
var peak=Enumerable.Range(0,17).Select(i=>620+i*5).MaxBy(Energy);
Assert(peak==660,"independent spectral measurement finds 660Hz peak");
using(var cancel=new CancellationTokenSource())
{
    var writes=0;await ExpectCanceled(()=>PlaybackDeviceTest.PumpMacToneAsync(_=>{writes++;cancel.Cancel();},()=>null,cancel.Token));
    Assert(writes==1,"cancellation during pump stops remaining blocks immediately");
}
var faultWrites=0;var rejected=false;
try{await PlaybackDeviceTest.PumpMacToneAsync(_=>faultWrites++,()=>faultWrites>0?new IOException("synthetic queue fault"):null,CancellationToken.None);}
catch(IOException error){rejected=error.InnerException?.Message=="synthetic queue fault";}
Assert(rejected && faultWrites==1,"asynchronous output fault is surfaced without additional PCM");

if(OperatingSystem.IsMacOS() && args.Contains("--silent-native"))
{
    const string uid="BuiltInSpeakerDevice"; // Previously verified present physical Mac mini output, never guessed/fallback.
    Assert(CoreAudioEndpoints.ResolveOutputUid(uid)==uid,"explicit physical UID resolves exactly");
    var before=CoreAudioEndpoints.ResolveOutputUid("");var volume=PlaybackVolumeController.Read(uid);
    var invalid=false;try{await PlaybackDeviceTest.PlayAsync("__linkbridge_invalid_output__");}
    catch(InvalidOperationException error){invalid=error.Message.Contains("已断开或不存在");}
    Assert(invalid,"invalid explicit UID fails metadata resolution without opening default output");
    await PlaybackDeviceTest.PlaySilentlyForProbeAsync(uid);
    Console.WriteLine("NATIVE silent explicit-UID playback/open/stop/dispose completed");
    using(var cancel=new CancellationTokenSource(45))
    {await ExpectCanceled(()=>PlaybackDeviceTest.PlaySilentlyForProbeAsync(uid,cancel.Token));}
    await PlaybackDeviceTest.PlaySilentlyForProbeAsync(uid); // Reopen after canceled disposal.
    Assert(CoreAudioEndpoints.ResolveOutputUid("")==before && PlaybackVolumeController.Read(uid)==volume,
        "silent native playback/cancel/reopen leaves actual default UID and system volume unchanged");
    Console.WriteLine("OBSERVED actual dOut UID="+before+"; explicit tested UID="+uid);
}
else Console.WriteLine("Native output not opened; pass --silent-native on target Mac for zero-PCM validation only.");
Console.WriteLine("ALL PLAYBACK TEST CASES PASSED; no audible device test, settings/default/volume changes or app deployment.");
