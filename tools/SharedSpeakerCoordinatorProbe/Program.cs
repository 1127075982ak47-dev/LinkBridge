using System.Collections.Concurrent;
using System.Diagnostics;
using LinkBridge.App.Services;
using LinkBridge.Core.Discovery;
using LinkBridge.Media.Audio;

static void Assert(bool pass, string message)
{ if (!pass) throw new Exception(message); Console.WriteLine("PASS " + message); }
static async Task Until(Func<bool> check, int timeout=2500)
{
    var timer=Stopwatch.StartNew();
    while (!check()) { if (timer.ElapsedMilliseconds>timeout) throw new TimeoutException("Probe condition timeout"); await Task.Delay(5); }
}

var f=new Fixture { Backend=false };
await using(var c=f.Create())
{
    c.Start(true); await Until(()=>c.State.Message.Contains("未找到")); await Task.Delay(100);
    Assert(f.Starts==0 && !c.State.IsSending,"missing backend does not start sender");
}
f=new Fixture { Peers=[] };
await using(var c=f.Create())
{
    c.Start(true); await Until(()=>c.State.Message.Contains("等待")); f.Peers=[Fixture.Windows];
    await Until(()=>c.State.IsSending); await Task.Delay(120);
    Assert(f.Starts==1 && f.Recoveries==1,"Windows discovery starts once without per-tick restart");
    f.Peers=[]; await Until(()=>!f.Running && !c.State.IsSending);
    Assert(c.State.Message.Contains("等待"),"disappearing Windows peer stops sender");
    f.Peers=[Fixture.Windows]; await Until(()=>c.State.IsSending);
    f.RouteCurrent=false; f.Peers=[]; await Until(()=>c.State.ManuallyOverridden);
    var count=f.Starts; f.Peers=[Fixture.Windows]; await Task.Delay(120);
    Assert(!f.Running && !c.State.IsSending && f.Starts==count,"manual route with simultaneous peer loss remains paused after rediscovery");
    c.Enable(); await Until(()=>c.State.IsSending);
    Assert(f.Starts==count+1 && !c.State.ManuallyOverridden,"explicit Enable resumes manually paused route");
}
f=new Fixture();
await using(var c=f.Create())
{
    c.Start(false); await Until(()=>c.State.Message.Contains("暂停")); await Task.Delay(120);
    Assert(f.Starts==0,"disabled setting never auto-starts despite ready Windows/backend");
    c.Enable(); await Until(()=>c.State.IsSending); await c.DisableAsync();
    await Until(()=>!c.State.IsSending); await Task.Delay(80);
    Assert(f.Starts==1 && !f.Running,"disable stops immediately and cannot restart next tick");
}
foreach(var removed in new[]{"peer","backend"})
{
    f=new Fixture { HoldStart=true };
    await using var c=f.Create(); var notifications=new ConcurrentQueue<SharedSpeakerState>();c.StateChanged+=notifications.Enqueue;
    c.Start(true);await Until(()=>f.Starts==1);
    if(removed=="peer")f.Peers=[];else f.Backend=false;
    f.Release.TrySetResult();await Until(()=>f.CompletedStarts==1 && !f.Running);
    Assert(!notifications.Any(s=>s.IsSending),"late START after "+removed+" disappearance never publishes connected");
}
f=new Fixture { HoldStart=true };
await using(var c=f.Create())
{
    var notifications=new ConcurrentQueue<SharedSpeakerState>();c.StateChanged+=notifications.Enqueue;
    c.Start(true);await Until(()=>f.Starts==1);await c.DisableAsync();f.Release.TrySetResult();
    await Until(()=>f.CompletedStarts==1 && !f.Running && f.Stops>=2);
    Assert(!notifications.Any(s=>s.IsSending),"late START ignoring cancellation is stopped without connected publication");
}
f=new Fixture { HoldStart=true, HonorCancellation=true, AllocateBeforeCancellation=true };
await using(var c=f.Create())
{
    c.Start(true);await Until(()=>f.Starts==1);var stopped=f.Stops;await c.DisableAsync();
    await Until(()=>f.CanceledStarts==1 && !f.Running && f.Stops>stopped+1);
    Assert(!c.State.IsSending,"cancelled START with allocated resources performs immediate cleanup");
}
f=new Fixture { FailStarts=1 };
await using(var c=f.Create(retry:TimeSpan.FromMilliseconds(350)))
{
    c.Start(true);await Until(()=>f.Starts==1 && c.State.Message.Contains("未就绪"));await Task.Delay(130);
    Assert(f.Starts==1,"temporary startup failure backs off without restart storm");
    var timer=Stopwatch.StartNew();c.Enable();await Until(()=>c.State.IsSending);
    Assert(f.Starts==2 && timer.ElapsedMilliseconds<250,"Enable immediately retries startup during backoff");
}
f=new Fixture { FailRecoveries=1 };
await using(var c=f.Create(retry:TimeSpan.FromSeconds(1)))
{
    c.Start(true);await Until(()=>c.State.Message.Contains("恢复失败"));await Task.Delay(120);
    Assert(f.Starts==0 && f.Recoveries==1,"failed recovery blocks sender and respects backoff");
    var timer=Stopwatch.StartNew();c.Enable();await Until(()=>c.State.IsSending);
    Assert(f.Recoveries==2 && f.Starts==1 && timer.ElapsedMilliseconds<300,"Enable immediately retries failed recovery before starting sender");
}
f=new Fixture();
await using(var c=f.Create())
{
    c.Start(true);await Until(()=>c.State.IsSending);f.StopRouteError="synthetic failed return";f.Peers=[];
    await Until(()=>!c.State.IsSending && c.State.Message.Contains("尚未恢复"));
    Assert(c.State.Message.Contains("synthetic failed return"),"failed route restoration is shown instead of successful original-output claim");
    var starts=f.Starts;f.Peers=[Fixture.Windows];await Task.Delay(100);
    Assert(f.Starts==starts,"unresolved previous route restoration blocks a new sender");
    f.StopRouteError=null;c.Enable();await Until(()=>c.State.IsSending);
    Assert(f.Starts==starts+1,"resolved previous route permits an explicit retry");
}
f=new Fixture { HoldStart=true };
var disposing=f.Create();var afterDispose=new ConcurrentQueue<SharedSpeakerState>();disposing.StateChanged+=afterDispose.Enqueue;
disposing.Start(true);await Until(()=>f.Starts==1);var disposeTimer=Stopwatch.StartNew();
await disposing.DisposeAsync();
Console.WriteLine("MEASURE bounded-dispose-ms="+disposeTimer.ElapsedMilliseconds);
Assert(disposeTimer.ElapsedMilliseconds<4700 && !f.Running,"Dispose stays bounded when START ignores cancellation");
f.Release.TrySetResult();await Until(()=>f.CompletedStarts==1 && !f.Running && f.Stops>=3);
Assert(!afterDispose.Any(s=>s.IsSending),"START resolving after bounded Dispose is cleaned and never publishes connected");
f=new Fixture { HoldStart=true };
using(var lifetime=new CancellationTokenSource())
{
    await using var c=f.Create(lifetime.Token);var notifications=new ConcurrentQueue<SharedSpeakerState>();c.StateChanged+=notifications.Enqueue;
    c.Start(true);await Until(()=>f.Starts==1);lifetime.Cancel();f.Release.TrySetResult();
    await Until(()=>f.CompletedStarts==1 && !f.Running);
    Assert(!notifications.Any(s=>s.IsSending),"lifetime cancellation revokes pending START and prevents late connected state");
}
f=new Fixture();
await using(var c=f.Create())
{
    var notifications=new ConcurrentQueue<SharedSpeakerState>();c.StateChanged+=notifications.Enqueue;
    f.TargetReadHook=()=>c.DisableAsync().GetAwaiter().GetResult();
    c.Start(true);await Until(()=>f.CompletedStarts==1 && !f.Running);await Task.Delay(50);
    Assert(!notifications.Any(s=>s.IsSending),"Disable between ready-property reads cannot publish stale connected version");
}

// Actual production route/ledger implementation, injected metadata backend only.
// This specifically verifies the recovered-lease failure previously found in native routing.
var directory=Path.Combine(Path.GetTempPath(),"LinkBridgeSpeakerRouteProbe-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);var ledger=Path.Combine(directory,"speaker-route.json");
try
{
    var backend=new MetadataBackend();
    SeedCrashLedger(backend,ledger);
    using(var recovering=new MacSharedSpeakerRoute(backend,ledger))
    {
        for(var i=0;i<2;i++)
        {
            var failed=false;try{recovering.RecoverPendingLease();}catch(InvalidOperationException ex)
            {failed=ex.Message.Contains("已断开") && !ex.Message.Contains("正在使用");}
            Assert(failed && File.Exists(ledger),"actual recovery-owned lease retains valid ledger and retries missing original (attempt "+(i+1)+")");
        }
        backend.OriginalPresent=true;
        Assert(recovering.RecoverPendingLease() && backend.Default=="original-speaker" && !File.Exists(ledger),
            "actual route recovery succeeds after original device returns without starting sender");
    }
    backend=new MetadataBackend();SeedCrashLedger(backend,ledger);
    using(var recovering=new MacSharedSpeakerRoute(backend,ledger))
    {
        try{recovering.RecoverPendingLease();}catch(InvalidOperationException){}
        backend.Default="manual-speaker";
        Assert(!recovering.RecoverPendingLease() && backend.Default=="manual-speaker" && !File.Exists(ledger),
            "actual recovery retry preserves subsequent user output instead of overwriting it");
    }
    backend=new MetadataBackend();
    using(var active=new MacSharedSpeakerRoute(backend,ledger))
    {
        active.Start(true,true);var refused=false;
        try{active.RecoverPendingLease();}catch(InvalidOperationException ex){refused=ex.Message.Contains("正在使用");}
        Assert(refused && active.IsCurrentRoute,"actual ordinary active lease still rejects crash recovery");
    }
}
finally
{
    foreach(var file in new[]{ledger,ledger+".lock"})if(File.Exists(file))File.Delete(file);
    Directory.Delete(directory); // Non-recursive; refuses unexpected leftovers.
}
Console.WriteLine("ALL SPEAKER COORDINATOR CASES PASSED; no settings, drivers, microphone, speaker, default route or production sockets opened.");

static void SeedCrashLedger(MetadataBackend backend,string ledger)
{
    var source=new MacSharedSpeakerRoute(backend,ledger);source.Start(true,true);
    backend.OriginalPresent=false;var failed=false;
    try{source.Dispose();}catch(InvalidOperationException){failed=true;}
    Assert(failed && File.Exists(ledger) && backend.Default==MacSharedSpeakerRoute.BlackHoleUid,
        "actual source preserves crash-restoration ledger when original device disappears");
}

sealed class Fixture
{
    public static readonly PeerInfo Windows=new("Test Windows","192.0.2.20","Windows",45679,DateTimeOffset.UtcNow);
    public IReadOnlyList<PeerInfo> Peers=[Windows];
    public bool Backend=true, Running, RouteCurrent=true, HoldStart, HonorCancellation, AllocateBeforeCancellation;
    public string? Target, StopRouteError, RouteError;
    public Action? TargetReadHook;
    public int Starts, Stops, Recoveries, CompletedStarts, CanceledStarts, FailStarts, FailRecoveries;
    public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public SharedSpeakerCoordinator Create(CancellationToken lifetime=default,TimeSpan? retry=null) => new(
        ()=>Peers,()=>Backend,()=>Volatile.Read(ref Running),ReadTarget,()=>RouteError,Start,Stop,Recover,lifetime,
        pulse:TimeSpan.FromMilliseconds(15),retry:retry??TimeSpan.FromMilliseconds(250),isRouteCurrent:()=>RouteCurrent);
    private void Recover()
    {
        var attempt=Interlocked.Increment(ref Recoveries);
        if(attempt<=FailRecoveries)throw new IOException("synthetic recovery failure");
        RouteError=null;
    }
    private string? ReadTarget()
    {
        var value=Target;
        if(value is not null && Volatile.Read(ref Running))Interlocked.Exchange(ref TargetReadHook,null)?.Invoke();
        return value;
    }
    private async Task Start(string address,CancellationToken token)
    {
        var attempt=Interlocked.Increment(ref Starts);
        if(attempt<=FailStarts)throw new IOException("synthetic output not ready");
        if(HoldStart)
        {
            try { if(HonorCancellation)await Release.Task.WaitAsync(token);else await Release.Task; }
            catch(OperationCanceledException)
            {
                if(AllocateBeforeCancellation){Target=address;Volatile.Write(ref Running,true);}
                Interlocked.Increment(ref CanceledStarts);throw;
            }
        }
        Target=address;RouteCurrent=true;Volatile.Write(ref Running,true);Interlocked.Increment(ref CompletedStarts);
    }
    private ValueTask Stop()
    {
        Interlocked.Increment(ref Stops);Volatile.Write(ref Running,false);Target=null;RouteError=StopRouteError;
        return ValueTask.CompletedTask;
    }
}
sealed class MetadataBackend : IMacSharedSpeakerBackend
{
    public string Default="original-speaker";
    public bool OriginalPresent=true;
    public string GetDefaultOutputUid()=>Default;
    public int GetOutputChannelCount(string uid)=>uid=="original-speaker" ? (OriginalPresent?2:0)
        : uid is "manual-speaker" or MacSharedSpeakerRoute.BlackHoleUid ? 2:0;
    public void SetDefaultOutputUid(string uid)
    {if(GetOutputChannelCount(uid)==0)throw new InvalidOperationException("synthetic missing device");Default=uid;}
}
