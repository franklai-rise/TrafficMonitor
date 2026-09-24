using VpnManager.Core;

var root = Path.Combine(Path.GetTempPath(), "VpnManagerTests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var passed = 0;
void Check(bool b) { if (!b) throw new Exception("assertion failed"); }
(VpnPaths p, SwitchService s, StatusCollector c) Setup(FakeSystem f) {
    var d = Path.Combine(root, Guid.NewGuid().ToString()); Directory.CreateDirectory(d);
    var clash = Path.Combine(d, "Clash.exe"); var tizi = Path.Combine(d, "TiziGo.exe");
    File.WriteAllText(clash, "fake"); File.WriteAllText(tizi, "fake");
    var p = new VpnPaths(clash,tizi,"",Path.Combine(d,"region.txt"),d); var c = new StatusCollector(f,p);
    return (p,new(f,p,c,new(d)),c);
}
async Task Run(string name, FakeSystem f, VpnMode target, Action<SwitchResult,FakeSystem> check) {
    var r = await Setup(f).s.SwitchAsync(target,default); check(r,f); Console.WriteLine("PASS "+name); passed++;
}
try {
passed += await CodexExitTests.RunAsync(root);
await Run("Codex preflight",new(){Codex=true},VpnMode.Clash,(r,f)=>Check(!r.Success&&f.Mutations==0));
await Run("Foreign port",new(){ClashPort=true,OwnsPort=false},VpnMode.Direct,(r,f)=>Check(!r.Success&&f.Mutations==0));
var machine=FakeSystem.Clash(); machine.Machine["HTTPS_PROXY"]="foreign";
await Run("Machine proxy conflict",machine,VpnMode.Direct,(r,f)=>Check(!r.Success&&f.Mutations==0));
var normal=FakeSystem.Clash(); normal.User["NO_PROXY"]="custom,localhost"; normal.RejectTiziIfClashPresent=true;
await Run("Clash releases before TiziGo starts",normal,VpnMode.TiziGo,(r,f)=>Check(r.Success&&r.FinalState.Mode==VpnMode.TiziGo&&f.Probes==1&&f.User["NO_PROXY"]=="custom,localhost"));
await Run("TiziGo to Clash",FakeSystem.Tizi(),VpnMode.Clash,(r,f)=>Check(r.Success&&r.FinalState.ProxyMatchesClash&&!f.TiziAdapter&&f.Probes==1));
await Run("Direct mode",FakeSystem.Clash(),VpnMode.Direct,(r,f)=>Check(r.Success&&!f.ClashPort&&!r.FinalState.AnyUserProxy));
var start=FakeSystem.Tizi(); start.FailClashRestart=true;
await Run("Target startup failure",start,VpnMode.Clash,(r,f)=>Check(!r.Success&&r.RestoreSucceeded&&f.TiziAdapter));
var partial=FakeSystem.Clash(); partial.IncompleteStart=true;
await Run("Incomplete target cleanup",partial,VpnMode.TiziGo,(r,f)=>Check(!r.Success&&r.RestoreSucceeded&&!f.TiziAdapter&&f.ClashPort));
var existing=FakeSystem.Tizi(); existing.Routes.Remove("8000::/1");
await Run("Existing partial TUN",existing,VpnMode.Clash,(r,f)=>Check(!r.Success&&f.Mutations==0));
var denied=FakeSystem.Tizi(); denied.RefuseTiziClose=true;denied.KillFails=true;
await Run("Denied stop does not start new target",denied,VpnMode.Clash,(r,f)=>Check(!r.Success&&r.RestoreSucceeded&&!f.ClashPort&&f.TiziAdapter));
var hides=FakeSystem.Clash(); hides.GuiHides=true;
await Run("Hidden Clash tray is fully stopped",hides,VpnMode.Direct,(r,f)=>Check(r.Success&&!f.ClashGui&&!f.ClashPort&&f.Kills>0));
var residual=FakeSystem.Clash(); residual.ResidualCore=true;
await Run("Verified residual core",residual,VpnMode.Direct,(r,f)=>Check(r.Success&&f.Kills>=1&&!f.ClashPort));
var noKill=FakeSystem.Clash(); noKill.ResidualCore=true; noKill.KillFails=true;
await Run("Unstoppable core",noKill,VpnMode.Direct,(r,f)=>Check(!r.Success&&r.RestoreSucceeded&&r.FinalState.ProxyMatchesClash));
var preProbe=FakeSystem.Clash(); preProbe.ProbeResults.Enqueue(false);
await Run("HTTPS failure restores original",preProbe,VpnMode.TiziGo,(r,f)=>Check(!r.Success&&r.RestoreSucceeded&&f.ClosesClash>0&&!f.TiziAdapter&&f.ClashPort));
var postProbe=FakeSystem.Clash(); postProbe.ProbeResults.Enqueue(true);postProbe.ProbeResults.Enqueue(false);
await Run("Independent HTTPS success",postProbe,VpnMode.TiziGo,(r,f)=>Check(r.Success&&f.TiziAdapter&&!f.ClashPort&&f.Probes==1));
var recovery=FakeSystem.Clash(); recovery.ProbeResults.Enqueue(true); recovery.ProbeResults.Enqueue(false); recovery.FailClashRestart=true;
recovery.ProbeResults.Clear();recovery.ProbeResults.Enqueue(false);
await Run("Failed recovery reported",recovery,VpnMode.TiziGo,(r,f)=>Check(!r.Success&&!r.RestoreSucceeded&&r.Summary.Contains("手动恢复")));
var env=FakeSystem.Tizi();env.FailEnvironmentOnce=true;env.User["HTTP_PROXY"]="original";
await Run("Partial environment write rollback",env,VpnMode.Clash,(r,f)=>Check(!r.Success&&r.RestoreSucceeded&&f.User["HTTP_PROXY"]=="original"&&!f.ClashPort));
var codex=FakeSystem.Clash();codex.CodexOnStart=true;
await Run("Codex starts during wait",codex,VpnMode.TiziGo,(r,f)=>Check(!r.Success&&r.RestoreSucceeded&&f.ClosesClash>0));

var canceled=FakeSystem.Clash(); using var cts=new CancellationTokenSource();canceled.OnStart=cts.Cancel;
var cancelResult=await Setup(canceled).s.SwitchAsync(VpnMode.TiziGo,cts.Token);
Check(!cancelResult.Success&&cancelResult.RestoreSucceeded&&!canceled.TiziAdapter);passed++;Console.WriteLine("PASS Cancellation recovery");
var concurrent=new FakeSystem{HoldFirstWait=new(TaskCreationOptions.RunContinuationsAsynchronously)};
var service=Setup(concurrent).s;var first=service.SwitchAsync(VpnMode.Clash,default);
var second=await service.SwitchAsync(VpnMode.TiziGo,default);Check(!second.Success&&concurrent.Starts==1);
concurrent.HoldFirstWait.SetResult();Check((await first).Success&&concurrent.Starts==1);passed++;Console.WriteLine("PASS Concurrent requests");
var missing=new FakeSystem();var missingSetup=Setup(missing);File.Delete(missingSetup.p.ClashExe);
Check(!(await missingSetup.s.SwitchAsync(VpnMode.Clash,default)).Success&&missing.Mutations==0);passed++;Console.WriteLine("PASS Missing executable");

var manualClash=FakeSystem.Clash();var manualClashSetup=Setup(manualClash);
var clashProxy=new CodexProxyService(manualClash,manualClashSetup.c,manualClashSetup.p).Apply(VpnMode.Clash);
Check(clashProxy.Success&&manualClash.Mutations==0&&manualClash.Starts==0&&manualClash.Kills==0);passed++;Console.WriteLine("PASS Existing Clash proxy unchanged");
var manualTizi=FakeSystem.Tizi();manualTizi.User["NO_PROXY"]="custom,localhost";
foreach(var name in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY"})manualTizi.User[name]="http://127.0.0.1:7890";
var manualTiziSetup=Setup(manualTizi);
var tiziProxy=new CodexProxyService(manualTizi,manualTiziSetup.c,manualTiziSetup.p).Apply(VpnMode.TiziGo);
Check(tiziProxy.Success&&new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY"}.All(name=>manualTizi.User[name] is null)
    &&manualTizi.User["NO_PROXY"]=="custom,localhost"&&manualTizi.Starts==0&&manualTizi.Kills==0&&manualTizi.Probes==0);passed++;Console.WriteLine("PASS TiziGo TUN clears Codex proxy without VPN operations");
var manualDirect=FakeSystem.Clash();var directSetup=Setup(manualDirect);
var wrongTarget=new CodexProxyService(manualDirect,directSetup.c,directSetup.p).Apply(VpnMode.Direct);
Check(!wrongTarget.Success&&manualDirect.Mutations==0);passed++;Console.WriteLine("PASS Manual VPN target mismatch does not write");
var manualCodex=FakeSystem.Clash();manualCodex.Codex=true;var codexSetup=Setup(manualCodex);
Check(!new CodexProxyService(manualCodex,codexSetup.c,codexSetup.p).Apply(VpnMode.Clash).Success&&manualCodex.Mutations==0);passed++;Console.WriteLine("PASS Running Codex blocks proxy change");
var manualForeign=new FakeSystem{ClashPort=true,OwnsPort=false};var foreignSetup=Setup(manualForeign);
Check(!new CodexProxyService(manualForeign,foreignSetup.c,foreignSetup.p).Apply(VpnMode.Clash).Success&&manualForeign.Mutations==0);passed++;Console.WriteLine("PASS Foreign proxy port is rejected");
var manualMachine=FakeSystem.Tizi();manualMachine.Machine["HTTPS_PROXY"]="machine-proxy";var machineSetup=Setup(manualMachine);
Check(!new CodexProxyService(manualMachine,machineSetup.c,machineSetup.p).Apply(VpnMode.TiziGo).Success&&manualMachine.Mutations==0);passed++;Console.WriteLine("PASS Machine proxy conflict is rejected");
var manualFailure=FakeSystem.Tizi();manualFailure.User["HTTP_PROXY"]="previous";manualFailure.FailEnvironmentOnce=true;var failureSetup=Setup(manualFailure);
var failedProxy=new CodexProxyService(manualFailure,failureSetup.c,failureSetup.p).Apply(VpnMode.TiziGo);
Check(!failedProxy.Success&&failedProxy.RestoreSucceeded&&manualFailure.User["HTTP_PROXY"]=="previous"
    &&manualFailure.Starts==0&&manualFailure.Kills==0);passed++;Console.WriteLine("PASS Partial proxy write restores original variables");

var format=Setup(FakeSystem.Tizi()); File.WriteAllText(format.p.TiziGoRegionFile,"jp");
Check(format.c.ToSnapshot(format.c.Collect()).DisplayText=="日本\nTUN · TiziGo");passed++;Console.WriteLine("PASS Two-line formatting");
var directState=Setup(new()).c.Collect(exitIp:"203.0.113.8",exitCountry:"中国",exitLocation:"中国 上海市");
var directSnapshot=Setup(new()).c.ToSnapshot(directState);
Check(directSnapshot.DisplayText=="中国 上海市\n203.0.113.8 · 普通直连"&&directSnapshot.CountrySource.Contains("ping0.cc"));passed++;Console.WriteLine("PASS Direct IP formatting");
var snapshot=Setup(new());var store=new SnapshotStore(snapshot.p.StateDirectory);
store.Write(snapshot.c.ToSnapshot(snapshot.c.Collect()));
await Task.WhenAll(Enumerable.Range(0,8).Select(n=>Task.Run(()=>{for(var i=0;i<20;i++){store.Write(snapshot.c.ToSnapshot(snapshot.c.Collect()));Check(store.Read()!=null);}})));
Check(!Directory.EnumerateFiles(snapshot.p.StateDirectory,"*.tmp").Any());passed++;Console.WriteLine("PASS Concurrent atomic snapshots");
var style=new DisplaySettings("微软雅黑",17,"#ABCDEF","right");DisplaySettings.Write(root,style);Check(DisplaySettings.Read(root)==style);
var bytes=File.ReadAllBytes(Path.Combine(root,"vpn-display-settings.ini"));Check(bytes[0]==255&&bytes[1]==254);passed++;Console.WriteLine("PASS Chinese style persistence");
File.WriteAllText(Path.Combine(root,"vpn-display-settings.ini"),"[display]\nfont_size=3\nfont_size=999\ncolor=bad\nalignment=bad");
var malformed=DisplaySettings.Read(root);Check(malformed.FontSize==28&&malformed.Color==DisplaySettings.Default.Color&&malformed.Alignment=="left");passed++;Console.WriteLine("PASS Malformed style");
Check(ProcessPathRules.IsWithinDirectory(@"C:\VPN\Core\clash.exe",@"C:\VPN"));
Check(!ProcessPathRules.IsWithinDirectory(@"C:\VPN-other\clash.exe",@"C:\VPN"));
Check(!ProcessPathRules.IsWithinDirectory(@"C:\VPN\..\other\clash.exe",@"C:\VPN"));
Check(!ProcessPathRules.IsWithinDirectory(@"C:\anything.exe",@"C:\"));passed++;Console.WriteLine("PASS Path identity");
Check(Ping0GeoParser.Parse("45.150.165.158\n美国 华盛顿州 西雅圖 — 斯巴达\nAS201106\n")?.Location=="美国 华盛顿州 西雅圖");
Check(Ping0GeoParser.Parse("<html>rate limited</html>")==null);passed++;Console.WriteLine("PASS Geo parser");
var controllerFile=Path.Combine(root,"controller.yaml");
File.WriteAllText(controllerFile,"external-controller: 127.0.0.1:9090 # local\nsecret: 'fake-test-secret'\n");
var handler=new ControllerHandler();
var resolver=new ClashControllerResolver(controllerFile,()=>handler);
Check((await resolver.TryResolveAsync(default)).Node=="规则分流"&&handler.Calls==1);passed++;Console.WriteLine("PASS Rule mode has no unique node");
handler=new ControllerHandler{Mode="global"};resolver=new(controllerFile,()=>handler);
var node=await resolver.TryResolveAsync(default);Check(node.Country=="日本"&&node.Node=="JP Tokyo"&&handler.Calls==2);passed++;Console.WriteLine("PASS Nested global selector");
File.WriteAllText(controllerFile,"external-controller: example.com:9090\nsecret: fake\n");
handler=new ControllerHandler();resolver=new(controllerFile,()=>handler);
Check((await resolver.TryResolveAsync(default)).Node==null&&handler.Calls==0);passed++;Console.WriteLine("PASS Controller secrets stay on loopback");
Console.WriteLine($"All {passed} offline checks passed. No real VPN, routes, environment or HTTP were changed.");
} finally { Directory.Delete(root,true); }

sealed class FakeSystem:ISystemGateway {
public bool ClashGui,ClashPort,TiziGui,TiziAdapter,Codex,IncompleteStart,RefuseTiziClose,GuiHides,ResidualCore,KillFails,FailClashRestart,FailEnvironmentOnce,CodexOnStart,RejectTiziIfClashPresent;
public bool OwnsPort=true;public int Starts,Kills,Mutations,Probes,ClosesClash;public Action? OnStart;public TaskCompletionSource? HoldFirstWait;private bool held;
public HashSet<string> Routes=new();public Dictionary<string,string?> User=new(StringComparer.OrdinalIgnoreCase),Machine=new(StringComparer.OrdinalIgnoreCase);public Queue<bool> ProbeResults=new();
public static FakeSystem Clash(){var f=new FakeSystem{ClashGui=true,ClashPort=true};foreach(var n in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY"})f.User[n]="http://127.0.0.1:7890";return f;}
public static FakeSystem Tizi(){var f=new FakeSystem{TiziGui=true,TiziAdapter=true};f.Routes.UnionWith(["0.0.0.0/1","128.0.0.0/1","::/1","8000::/1"]);return f;}
static bool ClashPath(string p)=>p.EndsWith("Clash.exe");
public bool IsProcessRunningAtPath(string p)=>ClashPath(p)?ClashGui:TiziGui;
public bool IsPortListening(int p)=>ClashPort;
public bool IsPortOwnedBy(int p,string d,IReadOnlyCollection<string> n)=>ClashPort&&OwnsPort;
public bool IsAdapterUp(string n)=>TiziAdapter;
public IReadOnlyCollection<string> GetRoutesForAdapter(string n,bool forceRefresh=false)=>Routes;
public bool IsCodexRunning()=>Codex;
public void Start(string p){Starts++;Mutations++;if(ClashPath(p)&&FailClashRestart)return;if(ClashPath(p)){ClashGui=ClashPort=true;}else{if(RejectTiziIfClashPresent&&ClashPort)throw new IOException("Clash still owns the port");TiziGui=TiziAdapter=true;Routes.UnionWith(["0.0.0.0/1","128.0.0.0/1","::/1","8000::/1"]);if(IncompleteStart)Routes.Remove("8000::/1");}if(CodexOnStart)Codex=true;OnStart?.Invoke();}
public bool RequestCloseAtPath(string p){Mutations++;if(!ClashPath(p)&&RefuseTiziClose)return false;if(ClashPath(p))ClosesClash++;if(GuiHides)return true;if(ClashPath(p)){ClashGui=false;if(!ResidualCore)ClashPort=false;}else{TiziGui=false;if(!ResidualCore){TiziAdapter=false;Routes.Clear();}}return true;}
public int TerminateProcessesInDirectory(string d,IReadOnlyCollection<string> n){Mutations++;Kills++;if(KillFails)return 0;if(n.Contains("TiziGo")){var active=TiziGui;TiziGui=false;return active?1:0;}if(n.Contains("sing-box")){var active=TiziAdapter;TiziAdapter=false;Routes.Clear();return active?1:0;}if(n.Contains("Clash for Windows")){var active=ClashGui;ClashGui=false;return active?1:0;}var core=ClashPort;ClashPort=false;return core?1:0;}
public async Task<bool> WaitUntilAsync(Func<bool> c,TimeSpan timeout,CancellationToken t){if(HoldFirstWait!=null&&!held){held=true;await HoldFirstWait.Task.WaitAsync(t);}t.ThrowIfCancellationRequested();return c();}
public Task<bool> ProbePathAsync(VpnMode mode,CancellationToken t){t.ThrowIfCancellationRequested();Probes++;return Task.FromResult(ProbeResults.Count==0||ProbeResults.Dequeue());}
public string? GetUserEnvironment(string n)=>User.GetValueOrDefault(n);
public string? GetMachineEnvironment(string n)=>Machine.GetValueOrDefault(n);
public void SetUserEnvironment(string n,string? v){Mutations++;if(FailEnvironmentOnce&&n=="HTTPS_PROXY"){FailEnvironmentOnce=false;throw new IOException("simulated");}User[n]=v;}
public void BroadcastEnvironmentChanged(){}
}

sealed class ControllerHandler:System.Net.Http.HttpMessageHandler {
    public string Mode="rule";public int Calls;
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken token){
        Calls++;
        if(request.Headers.Authorization?.Parameter!="fake-test-secret")throw new Exception("missing auth");
        var json=request.RequestUri!.AbsolutePath=="/configs" ? "{\"mode\":\""+Mode+"\"}" : """{"proxies":{"GLOBAL":{"now":"group"},"group":{"now":"JP Tokyo"},"JP Tokyo":{"type":"ss"}}}""";
        return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.StringContent(json)});
    }
}
