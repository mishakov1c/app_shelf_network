# Deterministic recovery tests. All mutation and connectivity boundaries are memory-only.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$harness = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
namespace AppShelf.Network {
 internal sealed class MemoryVpnSystem : IVpnSystem {
  internal readonly List<string> Calls = new List<string>();
  internal VpnAdapter[] Found = new[] {
    new VpnAdapter { Id="11111111-1111-1111-1111-111111111111",Name="Adapter fixture A",Index=10,State=OperationalStatus.Up },
    new VpnAdapter { Id="22222222-2222-2222-2222-222222222222",Name="Unselected adapter fixture",Index=20,State=OperationalStatus.Up }
  };
  internal VpnProcess[] Running = new[] {
    new VpnProcess { Id=111,Path=VpnModuleTests.FixturePath("selected","client.exe"),Started=123 },
    new VpnProcess { Id=222,Path=VpnModuleTests.FixturePath("other","client.exe"),Started=456 }
  };
  internal NetworkProbes.ProxySnapshot Current = new NetworkProbes.ProxySnapshot { Enabled=true,Server="localhost:1080",AutoConfigUrl="" };
  internal string Dns="ошибка";
  internal bool FailDisable, FailStop;
  internal int DisableCode, FlushCode;
  public VpnAdapter[] Adapters() { Calls.Add("adapters");return Found; }
  public VpnProcess[] Processes() { Calls.Add("processes");return Running; }
  public NetworkProbes.ProxySnapshot Proxy() { Calls.Add("proxy");return Current; }
  public Task StopAsync(VpnProcess p) { if(FailStop) throw new InvalidOperationException("reused PID");Calls.Add("stop:"+p.Id);return Task.FromResult(0); }
  public Task<int> DisableAsync(VpnAdapter a) { if(FailDisable) throw new System.ComponentModel.Win32Exception(1223);Calls.Add("disable:"+a.Id);return Task.FromResult(DisableCode); }
  public void SetProxy(bool enabled,string expected) { VpnModule.CheckProxy(Current,enabled,expected);Calls.Add("write-proxy");Current.Enabled=enabled; }
  public Task<NetworkProbes.ConnectivitySnapshot> ConnectivityAsync() { Calls.Add("connectivity");return Task.FromResult(new NetworkProbes.ConnectivitySnapshot { Dns=Dns, DirectHttps="нет",SystemHttps="нет" }); }
  public Task<int> FlushDnsAsync() { Calls.Add("flush");return Task.FromResult(FlushCode); }
 }
 public static class VpnModuleTests {
  private static int count;
  // Synthetic absolute paths for validation; no files/processes are created at these paths.
  internal static string FixturePath(string directory,string file) {
   return Path.Combine(Path.GetPathRoot(Environment.SystemDirectory),"SyntheticVpnFixtures",directory,file);
  }
  private static void Assert(bool condition,string message) { if(!condition) throw new Exception(message);count++; }
  private static void Reject(Action action,string message) { bool failed=false;try { action(); }catch { failed=true; }Assert(failed,message); }
  private static NetworkSettings Settings() {
   return new NetworkSettings { ShowNetwork=false,NetworkCheck1Mode="tcp",NetworkCheck1Target="localhost:443",
    NetworkCheck2Mode="tcp",NetworkCheck2Target="127.0.0.1:9",VpnEnabled=true,ShowVpnIndicator=true,
     VpnAdapterIds=new[]{"11111111-1111-1111-1111-111111111111"},VpnProcessPaths=new[]{FixturePath("selected","client.exe")} };
  }
  private static bool Mutated(MemoryVpnSystem s) { return s.Calls.Exists(delegate(string c){return c.StartsWith("stop:")||c.StartsWith("disable:")||c=="write-proxy"||c=="flush";}); }
  private static string Run(VpnRecovery r,NetworkSettings s) { return r.RunAsync(s,CancellationToken.None).GetAwaiter().GetResult(); }
  public static void Run(string folder) {
   var settings=Settings();
   var sys=new MemoryVpnSystem();
   var denied=new VpnRecovery(sys,delegate{return false;});
   Run(denied,settings);Assert(sys.Calls.Count==0,"Initial decline performed IO");
   int prompt=0;
   var skipped=new VpnRecovery(sys,delegate{return ++prompt==1;});
   Run(skipped,settings);Assert(!Mutated(sys),"Declined steps mutated OS");
   Assert(sys.Calls.FindAll(delegate(string c){return c=="connectivity";}).Count==2,"Final connectivity recheck missing");
   sys=new MemoryVpnSystem();
   var recovery=new VpnRecovery(sys,delegate{return true;});
   string report=Run(recovery,settings);
   Assert(sys.Calls.Contains("stop:111")&&!sys.Calls.Contains("stop:222"),"Process path allowlist failed");
   Assert(sys.Calls.Contains("disable:"+settings.VpnAdapterIds[0])&&!sys.Calls.Contains("disable:22222222-2222-2222-2222-222222222222"),"Adapter allowlist failed");
   Assert(sys.Calls.Contains("flush")&&report.Contains("Итоговая проверка"),"DNS/final report missing");
   Assert(recovery.DisabledProxy=="localhost:1080"&&!sys.Current.Enabled,"Proxy disable snapshot missing");
   recovery.Restore(settings,CancellationToken.None);
   Assert(sys.Current.Enabled&&recovery.DisabledProxy==null,"Proxy restore failed");
   sys=new MemoryVpnSystem();recovery=new VpnRecovery(sys,delegate{return true;});
   Run(recovery,settings);sys.Current.Server="localhost:1081";
   Reject(delegate{recovery.Restore(settings,CancellationToken.None);},"Changed proxy restored");
   Assert(!sys.Current.Enabled&&recovery.DisabledProxy=="localhost:1080","Restore failure lost original proxy");
   sys=new MemoryVpnSystem();sys.Current.Enabled=false;sys.Dns="работает";
   var empty=Settings();empty.VpnAdapterIds=null;empty.VpnProcessPaths=null;
   Run(new VpnRecovery(sys,delegate{return true;}),empty);
   Assert(!Mutated(sys),"Unconfigured module guessed recovery targets");
   Assert(VpnModule.ReadStatus(empty)=="Выберите адаптеры VPN","Unconfigured status missing");
   Assert(VpnModule.Selected(new[]{"{11111111-1111-1111-1111-111111111111}"},"11111111-1111-1111-1111-111111111111"),"GUID formatting broke selection");
   sys=new MemoryVpnSystem();settings=Settings();settings.VpnEnabled=false;
   recovery=new VpnRecovery(sys,delegate{return true;});
   Reject(delegate{Run(recovery,settings);},"Disabled module accepted recovery");
   Reject(delegate{recovery.DisableAsync(settings,CancellationToken.None).GetAwaiter().GetResult();},"Disabled module accepted adapter action");
   Assert(sys.Calls.Count==0,"Disabled module performed IO");
   Assert(VpnModule.ReadStatus(settings)=="VPN-модуль выключен"&&VpnModule.Report(settings)=="","Disabled diagnostics performed VPN work");
   settings=Settings();sys=new MemoryVpnSystem();
   var cancelled=new CancellationTokenSource();cancelled.Cancel();
   recovery=new VpnRecovery(sys,delegate{return true;});
   Reject(delegate{recovery.RunAsync(settings,cancelled.Token).GetAwaiter().GetResult();},"Cancellation ignored");
   Assert(sys.Calls.Count==0,"Cancelled recovery performed IO");
   sys=new MemoryVpnSystem();prompt=0;
   recovery=new VpnRecovery(sys,delegate{if(++prompt==2) settings.VpnEnabled=false;return true;});
   Reject(delegate{Run(recovery,settings);},"Settings disabled during confirmation were ignored");
   Assert(!Mutated(sys),"Changed settings allowed process termination");
   settings=Settings();sys=new MemoryVpnSystem();sys.FailDisable=true;
   recovery=new VpnRecovery(sys,delegate{return true;});
   Reject(delegate{Run(recovery,settings);},"UAC cancellation swallowed");
   Assert(!sys.Calls.Contains("write-proxy")&&!sys.Calls.Contains("flush"),"Recovery continued after cancelled UAC");
   sys=new MemoryVpnSystem();sys.DisableCode=1;
   recovery=new VpnRecovery(sys,delegate{return true;});
   Reject(delegate{Run(recovery,settings);},"Nonzero adapter exit code ignored");
   Assert(!sys.Calls.Contains("write-proxy")&&!sys.Calls.Contains("flush")&&recovery.LastActions.Contains("код Windows 1"),"Recovery continued or lost error result");
   sys=new MemoryVpnSystem();sys.FlushCode=1;
   recovery=new VpnRecovery(sys,delegate{return true;});
   Reject(delegate{Run(recovery,settings);},"Nonzero DNS exit code ignored");
   Assert(sys.Calls.FindAll(delegate(string c){return c=="connectivity";}).Count==1,"Recovery continued after DNS utility failure");
   sys=new MemoryVpnSystem();sys.FailStop=true;
   recovery=new VpnRecovery(sys,delegate{return true;});
   Reject(delegate{Run(recovery,settings);},"Changed process was ignored");
   Assert(!Mutated(sys),"Recovery continued after changed process");
   foreach(string server in new[]{"proxy.example:8080","http=localhost:1080;https=proxy.example:8080"}) {
    sys=new MemoryVpnSystem();sys.Current.Server=server;sys.Dns="работает";
    Run(new VpnRecovery(sys,delegate{return true;}),settings);
    Assert(!sys.Calls.Contains("write-proxy")&&!sys.Calls.Contains("flush"),"Remote/mixed proxy or healthy DNS mutated");
   }
   sys=new MemoryVpnSystem();sys.Current.AutoConfigUrl="https://example.test/proxy.pac";
   Run(new VpnRecovery(sys,delegate{return true;}),settings);Assert(!sys.Calls.Contains("write-proxy"),"PAC modified");
   foreach(string server in new[]{"127.0.0.1:1080","localhost:1080","http=127.0.0.1:1080;https=localhost:8080","socks=localhost:1080","http://127.0.0.1:8888","[::1]:1080"})
    Assert(VpnModule.IsLoopbackProxy(server),"Loopback rejected: "+server);
   foreach(string server in new[]{"","proxy.company.example:8080","http=127.0.0.1:8080;https=proxy.company.example:8080","127.0.0.1:1080;remote-proxy","http://user:secret@127.0.0.1:8080","127.0.0.1:8080/path","1.1.1.1:8080"})
    Assert(!VpnModule.IsLoopbackProxy(server),"Unsafe proxy accepted: "+server);
   var proxy=new NetworkProbes.ProxySnapshot { Enabled=true,Server="localhost:1080",AutoConfigUrl="" };
   Reject(delegate{VpnModule.CheckProxy(proxy,false,"localhost:1081");},"Changed server accepted");
   Reject(delegate{VpnModule.CheckProxy(proxy,true,proxy.Server);},"Unchanged state accepted");
   proxy.AutoConfigUrl="https://example.test/pac";
   Reject(delegate{VpnModule.CheckProxy(proxy,false,proxy.Server);},"PAC guard missing");
    var expected=new VpnProcess { Id=111,Path=FixturePath("selected","client.exe"),Started=123 };
   Assert(WindowsVpnSystem.SameProcess(expected,new VpnProcess{Id=111,Path=expected.Path,Started=123}),"Same process rejected");
   Assert(!WindowsVpnSystem.SameProcess(expected,new VpnProcess{Id=111,Path=expected.Path,Started=124}),"Reused PID accepted");
    Assert(!WindowsVpnSystem.SameProcess(expected,new VpnProcess{Id=111,Path=FixturePath("other","client.exe"),Started=123}),"Foreign path accepted");
   Assert(!WindowsVpnSystem.SafeProcessPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"system.exe")),"System process offered");
    Assert(!WindowsVpnSystem.SafeProcessPath(FixturePath("host","AppShelf.exe")),"Host process offered");
    Assert(!WindowsVpnSystem.SafeProcessPath(FixturePath("plugin","NetworkPlugin.exe")),"Plugin process offered");
   var a=new VpnAdapter {Id="11111111-1111-1111-1111-111111111111",Name="Tunnel",Index=10,State=OperationalStatus.Up};
   Assert(WindowsVpnSystem.SameAdapter(a,a),"Same adapter rejected");
   Assert(!WindowsVpnSystem.SameAdapter(a,new VpnAdapter{Id=a.Id,Name="Renamed",Index=10,State=OperationalStatus.Up}),"Renamed adapter accepted");
   Assert(!WindowsVpnSystem.SameAdapter(a,new VpnAdapter{Id=a.Id,Name=a.Name,Index=11,State=OperationalStatus.Up}),"Recreated adapter accepted");
   Assert(!WindowsVpnSystem.SameAdapter(a,new VpnAdapter{Id=a.Id,Name=a.Name,Index=10,State=OperationalStatus.Down}),"Down adapter accepted");
   Reject(delegate{WindowsVpnSystem.DisableArguments(new VpnAdapter{Name="bad\"name"});},"Command injection accepted");
   Reject(delegate{WindowsVpnSystem.DisableArguments(new VpnAdapter{Name="bad\\name"});},"Backslash quoting accepted");
   Assert(VpnModule.DescribeTun(new[]{a},new[]{10})=="TUN: выбран маршрут","Selected route missing");
   Assert(VpnModule.DescribeTun(new[]{a},new[]{99})=="TUN: маршрут не подтверждён","Stale Up adapter reported active");
   Assert(VpnModule.DescribeTun(new[]{a},new[]{-1})=="TUN: маршрут не подтверждён","Unknown route reported active");
   var b=new VpnAdapter {Id="22222222-2222-2222-2222-222222222222",Name="Tunnel B",Index=20,State=OperationalStatus.Up};
   Assert(VpnModule.DescribeTun(new[]{a,b},new[]{10,20})=="TUN: несколько адаптеров","Ambiguity lost");
   Assert(VpnModule.DescribeTun(new[]{a,b},new[]{10,10})=="TUN: выбран маршрут","OS selection not prioritized");
   Assert(VpnModule.DescribeTun(new[]{a,b},new[]{99})=="TUN: несколько адаптеров","Multiple stale adapters guessed");
   a.State=OperationalStatus.Down;b.State=OperationalStatus.Down;
   Assert(VpnModule.DescribeTun(new[]{a,b},new[]{99})=="TUN неактивен","Down state missing");
   a.State=OperationalStatus.Unknown;
   Assert(VpnModule.DescribeTun(new[]{a},new[]{99})=="Состояние TUN неизвестно","Unknown state missing");
   Assert(VpnModule.DescribeTun(new VpnAdapter[0],new[]{99})=="TUN отсутствует","Absent state missing");
   a.State=OperationalStatus.Up;a.Index=-1;
   Assert(VpnModule.DescribeTun(new[]{a},new[]{-1})=="TUN: маршрут не подтверждён","Unknown indices matched");
   Directory.CreateDirectory(folder);
   string file=Path.Combine(folder,"network-settings.json");
   string legacy="{\"showNetwork\":false,\"networkCheck1Mode\":\"tcp\",\"networkCheck1Target\":\"localhost:443\"}";
   File.WriteAllText(file,legacy);
   var loaded=NetworkSettings.Load(folder);
   Assert(!loaded.VpnEnabled&&!loaded.ShowVpnIndicator&&loaded.VpnAdapterIds==null&&loaded.VpnProcessPaths==null,"Old settings enabled VPN");
   Assert(File.ReadAllText(file)==legacy,"Loading legacy settings rewrote data");
   settings=Settings();settings.Save(folder);loaded=NetworkSettings.Load(folder);
   Assert(loaded.VpnEnabled&&loaded.ShowVpnIndicator&&loaded.VpnAdapterIds[0]==settings.VpnAdapterIds[0]&&loaded.VpnProcessPaths[0]==settings.VpnProcessPaths[0],"VPN settings roundtrip");
   string saved=File.ReadAllText(file);
    settings.VpnProcessPaths=new[]{FixturePath("selected","client.exe"),FixturePath("selected","client.exe").ToUpperInvariant()};
   Reject(delegate{settings.Save(folder);},"Duplicate process paths accepted");
   Assert(File.ReadAllText(file)==saved,"Invalid settings overwrote file");
   Reject(delegate{VpnModule.ValidateSelection(new[]{"not-a-guid"},null);},"Invalid adapter id accepted");
   Reject(delegate{VpnModule.ValidateSelection(null,new[]{@"\\server\client.exe"});},"UNC process path accepted");
   settings=Settings();settings.VpnProcessPaths=new string[16];
    for(int i=0;i<16;i++) settings.VpnProcessPaths[i]=FixturePath("selected",new string('я',470)+i+".exe");
   Reject(delegate{settings.Save(folder);},"Oversized serialized settings accepted");
   Assert(File.ReadAllText(file)==saved,"Oversized settings replaced saved file");
   Console.WriteLine("PASS: "+count+" VPN state, selection, migration, proxy, identity, cancellation and guarded recovery assertions; all mutations in memory.");
  }
 }
}
'@
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $parameters = New-Object CodeDom.Compiler.CompilerParameters
    $parameters.GenerateInMemory = $true
    $parameters.TreatWarningsAsErrors = $true
    foreach ($ref in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll')) { [void]$parameters.ReferencedAssemblies.Add($ref) }
    $sources = @([IO.File]::ReadAllText((Join-Path $root 'NetworkChecks.cs')), [IO.File]::ReadAllText((Join-Path $root 'VpnModule.cs')), $harness)
    $compiled = $provider.CompileAssemblyFromSource($parameters,[string[]]$sources)
    if ($compiled.Errors.Count) { throw ($compiled.Errors | Out-String) }
    $folder = Join-Path $env:LOCALAPPDATA ('Temp\opencode\VpnTests-' + [guid]::NewGuid().ToString('N'))
    $compiled.CompiledAssembly.GetType('AppShelf.Network.VpnModuleTests').GetMethod('Run').Invoke($null,[object[]]@([string]$folder))
} finally { $provider.Dispose() }
