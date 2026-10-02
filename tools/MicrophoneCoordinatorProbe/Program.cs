using LinkBridge.App.Services;
using LinkBridge.Core.Discovery;
using LinkBridge.Core.Models;
using LinkBridge.Media;
using LinkBridge.Media.Audio;

static async Task Until(Func<bool> check, int ms=3000)
{ var end=DateTime.UtcNow.AddMilliseconds(ms);while(!check()){if(DateTime.UtcNow>end)throw new Exception("Condition timeout");await Task.Delay(20);} }
static void Assert(bool check,string label){if(!check)throw new Exception(label);Console.WriteLine("PASS "+label);}
var local=new MediaDevice(MediaDeviceKind.Microphone,"usb-local","USB MIC",MediaBackend.DirectShow);
var other=new MediaDevice(MediaDeviceKind.Microphone,"7","Mac USB MIC",MediaBackend.AvFoundation);
PeerInfo Peer(string addr,string os,string name)=>new(name,addr,os,45679,DateTimeOffset.UtcNow){Microphones=[other]};

// A machine without a virtual sink must still provide a consented physical source.
var control=new FakeControl{Peers=[Peer("192.0.2.2","macOS","Mac")]};
var factory=new FakeFactory{Available=false};
await using(var c=new SharedMicrophoneCoordinator(control,factory))
{
 c.SelectLocalSource(local);c.Start();
 Assert(await control.Request("192.0.2.2",56789),"source-only consented request can start without local sink");
 Assert(factory.Senders[0].Port==56789,"requested source port honored");
 var first=factory.Senders[0];
 c.SelectLocalSource(local with{Id="usb-other",Name="USB MIC B"});
 Assert(!first.IsRunning&&control.Ended>0,"source selection change stops capture and invalidates READY");
 Assert(await control.Request("192.0.2.2",56790),"changed source can restart at new port");
 var ended=control.Ended;first.End();
 Assert(control.Ended==ended&&factory.Senders[^1].IsRunning,"late old sender EOF cannot stop current sender");
 c.SelectLocalSource(null);
 Assert(!factory.Senders[^1].IsRunning&&!await control.Request("192.0.2.2",56790),"unselected physical source rejects request");
 Assert(!control.HasInput,"missing virtual backend is never advertised available");
}

// Real-demand direction is read-side, and each lease renewal delegates a fresh signed START.
control=new FakeControl{Peers=[Peer("192.0.2.3","Windows","A-SameOS"),Peer("192.0.2.2","macOS","Z-OtherOS")]};
factory=new FakeFactory();
await using(var c=new SharedMicrophoneCoordinator(control,factory))
{
 c.Start();await Until(()=>factory.Demand?.Started==true);
 Assert(factory.ReadId=="read-only-virtual"&&factory.WriteId=="write-only-virtual","application read ID differs from virtual sink write ID");
 factory.Demand!.Emit(MicrophoneDemandStatus.Unknown,null);
 await Task.Delay(100);Assert(control.Starts==0,"unknown demand does not become zero or trigger capture");
 factory.Demand.Emit(MicrophoneDemandStatus.Known,1);
 await Until(()=>control.Starts==1);
 Assert(control.Target=="192.0.2.2"&&factory.Receiver!.Permit(),"prefer other OS physical source and permit only current demand");
 await Until(()=>control.Starts>=2,6500);
 Assert(control.StartTokens.Count>=2,"lease renewed at four seconds with new transport call");
 factory.Demand.Emit(MicrophoneDemandStatus.Unknown,null);
 Assert(!factory.Receiver!.Permit()&&factory.Receiver!.Disconnected>0,"unknown revokes TLS sink permit and disconnects immediately");
 await Until(()=>control.Stops>0);Assert(!control.LastStopCanceled,"STOP uses independent noncancelled deadline");
 factory.Demand.Emit(MicrophoneDemandStatus.Known,1);await Until(()=>control.Starts>=3);
 factory.Demand.Emit(MicrophoneDemandStatus.Known,0);await Until(()=>control.Stops>=2);
 Assert(!factory.Receiver!.Permit(),"last application reader stops route");
}

// A late source factory completion after disposal must be forced back to stopped.
control=new FakeControl{Peers=[Peer("192.0.2.2","macOS","Mac")]};factory=new FakeFactory{Available=false,LateSender=true};
var lateCoordinator=new SharedMicrophoneCoordinator(control,factory);lateCoordinator.SelectLocalSource(local);lateCoordinator.Start();
var pending=control.Request("192.0.2.2",56791);await Until(()=>factory.Senders.Count==1&&factory.Senders[0].Entered);
await lateCoordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
factory.Senders[0].Release.TrySetResult();
Assert(!await pending.WaitAsync(TimeSpan.FromSeconds(3))&&!factory.Senders[0].IsRunning,"late source result after bounded Dispose cannot resurrect capture");

// Disposal must cancel pending consumer START and emit a fresh STOP even when START ignores cancellation.
control=new FakeControl{Peers=[Peer("192.0.2.2","macOS","Mac")],HoldPrepare=true};factory=new FakeFactory();
var pendingCoordinator=new SharedMicrophoneCoordinator(control,factory);pendingCoordinator.Start();await Until(()=>factory.Demand?.Started==true);
factory.Demand!.Emit(MicrophoneDemandStatus.Known,1);await Until(()=>control.Starts==1);
pendingCoordinator.SelectLocalSource(local);
Assert(await control.Request("192.0.2.2",56792).WaitAsync(TimeSpan.FromSeconds(1)),"source actor remains usable while consumer awaits MIC_READY");
await pendingCoordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
Assert(control.Stops>=1&&!control.LastStopCanceled&&!factory.Receiver!.Permit(),"Dispose sends independent STOP while START hangs");
control.ReleasePrepare.TrySetResult(true);await Task.Delay(100);
Assert(!factory.Receiver!.Permit()&&factory.Receiver!.Disconnected>0,"late MIC_READY cannot restore disposed receiver permission");
// A receiver factory finishing after bounded shutdown must never open a listener.
control=new FakeControl{Peers=[Peer("192.0.2.2","macOS","Mac")]};factory=new FakeFactory{HoldReceiver=true};
var lateReceiverCoordinator=new SharedMicrophoneCoordinator(control,factory);lateReceiverCoordinator.Start();await Until(()=>factory.ReceiverFactoryEntered);
await lateReceiverCoordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
factory.ReleaseReceiver.Set();await Until(()=>factory.Receiver is not null&&factory.Receiver.Disconnected>0);
Assert(factory.Receiver!.Started==0&&!control.HasInput,"late receiver factory after Dispose cannot start listener or advertise sink");
// Twenty-second lost/delayed READY must not block four-second renewals or
// five-second backend health checks. These calls deliberately ignore cancellation.
control=new FakeControl{Peers=[Peer("192.0.2.2","macOS","Mac")],DelayPrepareMs=20000};factory=new FakeFactory();
var slowCoordinator=new SharedMicrophoneCoordinator(control,factory);slowCoordinator.Start();await Until(()=>factory.Demand?.Started==true);
factory.Demand!.Emit(MicrophoneDemandStatus.Known,1);await Until(()=>control.Starts>=2,7000);
var interval=(control.StartTimes[1]-control.StartTimes[0]).TotalMilliseconds;
Assert(interval>=3500&&interval<=6000&&control.DelayedCompletions==0,$"20s READY does not block fresh renewal (interval {interval:0}ms)");
factory.Available=false;await Until(()=>!control.HasInput,6500);
Assert(control.DelayedCompletions==0&&factory.Receiver!.Disconnected>0,"backend hot-unplug revokes receiver while READY still hangs");
await Until(()=>control.DelayedCompletions>=1,22000);
Assert(control.Starts<=3&&!factory.Receiver!.Permit()&&!control.HasInput,"late 20s READY ignored; actual unfinished jobs remain bounded at three");
await slowCoordinator.DisposeAsync();
Console.WriteLine("All coordinator invariants passed; no driver, microphone, speaker or native audio stream accessed.");

sealed class FakeControl:IMicrophoneControl
{
 public event Func<string,int,Task<bool>>? StartRequested;public event Action<string>? StopRequested;
 public string LocalPlatform=>"Windows";public bool HasInput{get;set;}
 public List<PeerInfo> Peers=[];public int Starts,Stops,Ended;public string? Target;public bool LastStopCanceled,HoldPrepare;public int DelayPrepareMs,DelayedCompletions;public List<DateTime> StartTimes=[];
 public List<CancellationToken> StartTokens=[];public TaskCompletionSource<bool> ReleasePrepare=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public IReadOnlyList<PeerInfo> Snapshot()=>Peers.ToArray();
 public Task<bool> Request(string a,int p)=>StartRequested?.Invoke(a,p)??Task.FromResult(false);
 public Task<bool> Prepare(string a,int p,CancellationToken ct){Target=a;Starts++;StartTokens.Add(ct);StartTimes.Add(DateTime.UtcNow);return DelayPrepareMs>0?Delayed():HoldPrepare?ReleasePrepare.Task:Task.FromResult(true);}
 private async Task<bool> Delayed(){await Task.Delay(DelayPrepareMs);Interlocked.Increment(ref DelayedCompletions);return true;}
 public Task<bool> Stop(string a,CancellationToken ct){Stops++;LastStopCanceled=ct.IsCancellationRequested;return Task.FromResult(true);}
 public bool SourceEnded(string a,int p){Ended++;return true;}
 public void RequestStop(string a)=>StopRequested?.Invoke(a);
}
sealed class FakeFactory:IMicrophoneCoordinatorFactory
{
 public bool Available=true,LateSender,HoldReceiver,ReceiverFactoryEntered;public ManualResetEventSlim ReleaseReceiver=new(false);public List<FakeSender> Senders=[];public FakeReceiver? Receiver;public FakeDemand? Demand;
 public string? WriteId,ReadId;
 public SharedMicrophoneEndpoint Diagnose()=>new(Available,Available?"write-only-virtual":null,"virtual","test virtual input",Available?"ready":"dedicated backend missing"){ApplicationInputId=Available?"read-only-virtual":null};
 public IMicrophoneSenderHandle CreateSender(int p){var s=new FakeSender(p,LateSender);Senders.Add(s);return s;}
 public IMicrophoneReceiverHandle CreateReceiver(string id,Func<string,bool> peer,Func<bool> permit){WriteId=id;ReceiverFactoryEntered=true;if(HoldReceiver)ReleaseReceiver.Wait();return Receiver=new(permit);}
 public IMicrophoneDemandHandle CreateDemand(Action<MicrophoneDemandSnapshot> callback)=>Demand=new(callback,id=>ReadId=id);
}
sealed class FakeSender(int port,bool late):IMicrophoneSenderHandle
{
 public int Port=>port;public bool Entered;public bool IsRunning{get;private set;}public long SentBytes=>IsRunning?1920:0;
 public TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);public event Action? StreamEnded;
 public async Task StartAsync(string address,MediaDevice device,CancellationToken ct){Entered=true;if(late)await Release.Task;else ct.ThrowIfCancellationRequested();IsRunning=true;}
 public ValueTask StopAsync(){IsRunning=false;return ValueTask.CompletedTask;}
 public ValueTask DisposeAsync(){IsRunning=false;return ValueTask.CompletedTask;}
 public void End()=>StreamEnded?.Invoke();
}
sealed class FakeReceiver(Func<bool> permit):IMicrophoneReceiverHandle
{
 public Func<bool> Permit=>permit;public bool IsConnected=>false;public int Port=>56780;public int Disconnected,Started;
 public event Action<bool>? ConnectionChanged;
 public void Start(){Started++;}public void Disconnect(){Disconnected++;ConnectionChanged?.Invoke(false);}
 public ValueTask DisposeAsync(){Disconnect();return ValueTask.CompletedTask;}
}
sealed class FakeDemand(Action<MicrophoneDemandSnapshot> callback,Action<string> started):IMicrophoneDemandHandle
{
 public bool Started;public void Start(string id){Started=true;started(id);}public void Dispose(){Started=false;}
 public void Emit(MicrophoneDemandStatus s,int? count)=>callback(new(s,count,[],s.ToString()));
}
