using System.Text.Json;
using LinkBridge.Media.Audio;
using System.Diagnostics;

if (args.Length == 2 && args[0] == "--lock-child")
{
    try { using var child = new MacSharedSpeakerRoute(new FakeBackend(),args[1]); Environment.Exit(4); }
    catch (IOException) { Environment.Exit(0); }
    return;
}

if (args.Contains("--native-metadata"))
{
    var native = new CoreAudioSharedSpeakerBackend();
    Console.WriteLine($"Default UID: {native.GetDefaultOutputUid()}");
    Console.WriteLine($"BlackHole2ch UID exact output channels: {native.GetOutputChannelCount(MacSharedSpeakerRoute.BlackHoleUid)}");
    Console.WriteLine("Metadata only: did not create ledger, change defaults or open audio.");
    return;
}
var directory = Path.Combine(Path.GetTempPath(), "LinkBridge-route-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "route.json");
void Assert(bool good, string label) { if (!good) throw new Exception(label); Console.WriteLine("PASS " + label); }
void Throws(Action action, string label) { try { action(); } catch { Console.WriteLine("PASS " + label); return; } throw new Exception(label); }
void Ledger(string original="physical-a", string route=MacSharedSpeakerRoute.BlackHoleUid, string owner="com.linkbridge.desktop") => File.WriteAllText(path,JsonSerializer.Serialize(new { Version=1,Owner=owner,LeaseId=Guid.NewGuid().ToString("N"),OriginalUid=original,RouteUid=route }));
var fake = new FakeBackend();
using (var route = new MacSharedSpeakerRoute(fake,path))
{
    Throws(()=>route.Start(false,true),"unpaired connection cannot route");
    Throws(()=>route.Start(true,false),"missing first PCM cannot route");
    Assert(fake.Sets==0&&!File.Exists(path),"rejected starts do not change defaults or ledger");
    Throws(()=>new MacSharedSpeakerRoute(fake,path),"exclusive cross-instance ledger ownership");
    var childInfo=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false};
    if(Path.GetFileNameWithoutExtension(Environment.ProcessPath)=="dotnet")childInfo.ArgumentList.Add(typeof(FakeBackend).Assembly.Location);
    childInfo.ArgumentList.Add("--lock-child");childInfo.ArgumentList.Add(path);
    using(var child=Process.Start(childInfo)!){Assert(child.WaitForExit(5000)&&child.ExitCode==0,"exclusive lease lock also excludes another process");}
    route.Start(true,true); route.Start(true,true);
    Assert(fake.Current==MacSharedSpeakerRoute.BlackHoleUid&&fake.Sets==1&&File.Exists(path)&&route.IsCurrentRoute,"confirmed stream acquires one idempotent lease");
    Assert(route.Stop()&&fake.Current=="physical-a"&&!File.Exists(path),"stop restores exact original UID and clears ledger");
    route.Start(true,true);fake.Current="physical-b";
    Assert(!route.IsCurrentRoute,"manual native output selection revokes current-route status");
    Assert(!route.Stop()&&fake.Current=="physical-b"&&!File.Exists(path),"user manual output choice is preserved");
    route.Start(true,true);fake.Channels.Remove("physical-b");
    Throws(()=>route.Stop(),"hot-unplug original reports failure without fallback");
    Assert(File.Exists(path)&&fake.Current==MacSharedSpeakerRoute.BlackHoleUid,"failed restoration retains recoverable ownership record");
    fake.Channels["physical-b"]=2;Assert(route.Stop()&&fake.Current=="physical-b","restoration can retry after endpoint returns");
    fake.Current="physical-a";fake.FailSet=true;
    Throws(()=>route.Start(true,true),"CoreAudio set failure surfaced");
    Assert(!File.Exists(path)&&fake.Current=="physical-a","failed acquisition leaves no false lease");
    fake.FailSet=false;fake.ThrowAfterSet=true;
    Throws(()=>route.Start(true,true),"partial set failure surfaced");
    Assert(fake.Current=="physical-a"&&!File.Exists(path),"partial hardware change is rolled back");
    fake.ThrowAfterSet=false;fake.Channels[MacSharedSpeakerRoute.BlackHoleUid]=16;
    Throws(()=>route.Start(true,true),"BlackHole UID with wrong channels is rejected");
    fake.Channels[MacSharedSpeakerRoute.BlackHoleUid]=2;
    fake.Current=MacSharedSpeakerRoute.BlackHoleUid;Throws(()=>route.Start(true,true),"preexisting BlackHole selection is not falsely claimed");
    fake.Current="physical-a";
    Parallel.For(0,16,_=>route.Start(true,true));Assert(route.IsActive,"concurrent Start is serialized");route.Stop();
    fake.Reads=0;fake.SwitchOnRead=2;
    Throws(()=>route.Start(true,true),"manual output change during ledger preparation cancels acquisition");
    Assert(fake.Current=="physical-b"&&!File.Exists(path),"acquisition race preserves new user selection");fake.SwitchOnRead=0;
}
// A private persisted record simulates abrupt process termination without deliberately leaving a real output changed.
fake.Current=MacSharedSpeakerRoute.BlackHoleUid;Ledger();
using(var route=new MacSharedSpeakerRoute(fake,path)) Assert(route.RecoverPendingLease()&&fake.Current=="physical-a"&&!File.Exists(path),"crash recovery only restores prior lease, never reconnects");
Ledger();fake.Current="physical-b";
using(var route=new MacSharedSpeakerRoute(fake,path)) Assert(!route.RecoverPendingLease()&&fake.Current=="physical-b"&&!File.Exists(path),"crash recovery honors subsequent manual selection");
Ledger(owner:"another.app");
using(var route=new MacSharedSpeakerRoute(fake,path)) Throws(()=>route.RecoverPendingLease(),"foreign ledger cannot mutate output");
File.Delete(path);fake.Current="physical-a";
using(var route=new MacSharedSpeakerRoute(fake,path)){route.Start(true,true);}
Assert(fake.Current=="physical-a"&&!File.Exists(path),"Dispose returns active lease");
Directory.CreateDirectory(path);
using(var route=new MacSharedSpeakerRoute(fake,path))Throws(()=>route.Start(true,true),"atomic ledger write failure surfaced before device mutation");
Assert(fake.Current=="physical-a","failed persistence never changes default output");
Directory.Delete(path);
Console.WriteLine("All isolated route tests passed. No native default, audio device or secret accessed. Evidence: "+directory);

sealed class FakeBackend:IMacSharedSpeakerBackend
{
    public string Current="physical-a";public int Sets,Reads,SwitchOnRead;public bool FailSet,ThrowAfterSet;
    public Dictionary<string,int> Channels=new(){{"physical-a",2},{"physical-b",2},{MacSharedSpeakerRoute.BlackHoleUid,2}};
    public string GetDefaultOutputUid(){if(++Reads==SwitchOnRead)Current="physical-b";return Current;}
    public int GetOutputChannelCount(string uid)=>Channels.GetValueOrDefault(uid);
    public void SetDefaultOutputUid(string uid){if(FailSet)throw new InvalidOperationException("fake OSStatus=-1");Current=uid;Sets++;if(ThrowAfterSet&&uid==MacSharedSpeakerRoute.BlackHoleUid)throw new InvalidOperationException("fake OSStatus=-2 after mutation");}
}
