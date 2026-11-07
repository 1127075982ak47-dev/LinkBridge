using System.Text.Json;
using LinkBridge.Core;
using LinkBridge.Core.DisplayLayout;
using LinkBridge.Input;

if(args is ["--inventory"]) {Console.WriteLine(JsonSerializer.Serialize(NativeScreens.Read()));return;}
if(args.Length>=3 && args[0]=="--peer") {
    var request=args[2] switch {
        "get"=>new LayoutRequest("get"),"identify"=>new LayoutRequest("identify"),"restore"=>new LayoutRequest("restore"),
        "apply" when args.Length==4=>new LayoutRequest("apply",JsonSerializer.Deserialize<ScreenLayout>(File.ReadAllText(args[3]))),
        _=>throw new ArgumentException("Unknown request")};
    var response=await LayoutTransport.RequestAsync(args[1],SettingsStore.Load().InputPairingSecret,request);
    Console.WriteLine(JsonSerializer.Serialize(response));return;
}
void Check(bool c,string m){if(!c)throw new Exception(m);}
void Reject(Action a,string m){try{a();}catch(InvalidDataException){Console.WriteLine("PASS "+m);return;}throw new Exception(m);}
var windows=new[]{new ScreenInfo("w1","Windows 1",0,0,3840,2160,true),new ScreenInfo("w2","Windows 2",3840,0,2560,1440,false)};
var mac=new[]{new ScreenInfo("m1","Mac",1920,-1200,1920,1200,true)};
var plan=new ScreenLayout{WindowsScreenId="w2",MacScreenId="m1",Edge="bottom",Offset=320};
var bottom=ScreenPortal.Create(plan,windows,mac);
Check(bottom.Start==4160&&bottom.End==6080,"centered seam");
Check(!bottom.AtEdge(3839,900)&&!bottom.AtEdge(2000,2159),"Windows internal/bottom remain local");
Check(bottom.AtEdge(4160,1439)&&!bottom.AtEdge(4159,1439)&&!bottom.AtEdge(6080,1439),"bounded seam");
foreach(var x in new[]{4160,4700,6079}){var p=bottom.Enter(x,1439);var b=bottom.Leave(p.X,p.Y);Check(b.X==x&&b.Y==1408,"exact return coordinate");}
Check(bottom.Returns(600,24,0,-25)&&!bottom.Returns(600,24,-900,0),"top-only return");
Reject(()=>ScreenPortal.Create(plan with{WindowsScreenId="w1",Edge="right",Offset=0},windows,mac),"reject internal Windows boundary");
Reject(()=>ScreenPortal.Create(plan with{Offset=-900},windows,mac),"reject remote overlap with another Windows screen");
Reject(()=>ScreenPortal.Create(plan with{Offset=2560},windows,mac),"reject disconnected rectangles");
Reject(()=>ScreenPortal.Create(plan with{MacScreenId="unplugged"},windows,mac),"reject unplugged display");
Reject(()=>ScreenPortal.Create(plan with{CoveragePercent=0},windows,mac),"reject invalid coverage");
foreach(var edge in new[]{"left","right","top","bottom"}){
    var p=ScreenPortal.Create(plan with{WindowsScreenId="w1",Edge=edge,Offset=0},[windows[0]],mac);
    var local=edge switch{"left"=>(0,600),"right"=>(3839,600),"top"=>(600,0),_=>(600,2159)};
    var enter=p.Enter(local.Item1,local.Item2);var ret=p.Leave(enter.X,enter.Y);
    Check(p.AtEdge(local.Item1,local.Item2),"four-edge hit");
    Check(edge is "top" or "bottom"?ret.X==local.Item1:ret.Y==local.Item2,"four-edge roundtrip");
}
var half=ScreenPortal.Create(plan with{CoveragePercent=50},windows,mac);Check(half.End-half.Start==960,"coverage cropping");
Check(!half.Returns(10,0,0,-10)&&half.Returns(960,0,0,-10),"Mac return also obeys the visible seam");
Console.WriteLine("PASS four directions, monitor offsets, Mac target origin, bounded entry/return and coverage");
var secret=Guid.NewGuid().ToString("N");int applies=0;
await using(var service=new LayoutTransport(secret,_=>true,(r,ct)=>{if(r.Action=="apply")applies++;return Task.FromResult(new LayoutResponse(true,new("test","fixture",windows,plan)));},0)){
    service.Start();
    var response=await LayoutTransport.RequestAsync("127.0.0.1",secret,new("get"),port:service.Port);
    Check(response.Snapshot?.Screens.Length==2&&applies==0,"read-only authenticated inventory");
    bool failed=false;try{await LayoutTransport.RequestAsync("127.0.0.1",new string('x',32),new("apply",plan),port:service.Port);}catch{failed=true;}
    Check(failed&&applies==0,"wrong pairing must not mutate layout");
    await LayoutTransport.RequestAsync("127.0.0.1",secret,new("apply",plan),port:service.Port);
    Check(applies==1,"one authenticated application");
}
Console.WriteLine("PASS paired TLS inventory, wrong-key rejection and authenticated apply; no production settings or hooks touched");
