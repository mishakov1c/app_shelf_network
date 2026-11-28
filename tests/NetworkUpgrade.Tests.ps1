# Real host package store/runtime, isolated profile; no live network probes or recovery.
param(
    [Parameter(Mandatory=$true)][string]$HostSourceDirectory,
    [string]$BaselinePackage
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
if (-not $BaselinePackage) { $BaselinePackage = Join-Path $root 'dist\NetworkPlugin-1.0.3.asplugin' }
if (-not (Test-Path -LiteralPath $BaselinePackage)) { throw 'Supply the accepted NetworkPlugin 1.0.3 package.' }
$temp = Join-Path $env:LOCALAPPDATA ('Temp\opencode\NetworkUpgrade-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
& (Join-Path $root 'Build.ps1') -OutputDirectory $temp
$harness = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using AppShelf;
public static class NetworkUpgradeTests {
 private static void Assert(bool ok,string message) { if(!ok)throw new Exception(message); }
 private static string Hash(string file) { using(var sha=SHA256.Create())using(var input=File.OpenRead(file))return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant(); }
 private static void Exited(int pid) { try { using(var p=Process.GetProcessById(pid))Assert(p.WaitForExit(5000),"Old process survived update"); }catch(ArgumentException){} }
 public static void Run(string folder,string oldPackage,string nextPackage) {
  const string id="appshelf.network";
  var store=new PluginPackageStore(Path.Combine(folder,"profile"));
  var old=store.Install(oldPackage,delegate{return true;});
  Assert(old.Id==id&&old.Version=="1.0.3","Not an actual 1.0.3 baseline");
  Assert(old.Update!=null&&old.Update.Repository=="mishakov1c/app_shelf_network","Old update source");
  string data=store.GetDataDirectory(id);Directory.CreateDirectory(data);
  string file=Path.Combine(data,"network-settings.json");
  string original="{\"showNetwork\":false,\"networkCheck1Mode\":\"tcp\",\"networkCheck1Target\":\"localhost:443\",\"networkCheck2Mode\":\"icmp\",\"networkCheck2Target\":\"127.0.0.1\"}";
  File.WriteAllText(file,original);File.WriteAllText(Path.Combine(data,"unlisted.txt"),"preserve");
  string backup=Path.Combine(folder,"backups");
  using(var runtime=new PluginRuntime(store,5000)) {
   runtime.SetEnabledAsync(id,true).GetAwaiter().GetResult();
   var first=runtime.ReadWidgetSnapshotAsync(id,"1.0.3",true).GetAwaiter().GetResult();
   Assert(first.Widgets.Length==3&&first.ProcessId>0,"Old runtime failed");
   bool failed=false;
   try {store.Install(nextPackage,delegate{return true;},id,runtime.BeforePackageCommit,backup,true,new string('0',64));}catch(InvalidDataException){failed=true;}
   Assert(failed&&store.ReadInstalled(id).Version=="1.0.3","Bad hash accepted");
   Assert(runtime.ReadWidgetSnapshotAsync(id,"1.0.3",false).GetAwaiter().GetResult().ProcessId==first.ProcessId,"Rejected update stopped process");
   Assert(store.Install(nextPackage,delegate{return false;},id,runtime.BeforePackageCommit,backup,true)==null,"Decline ignored");
   Assert(runtime.ReadWidgetSnapshotAsync(id,"1.0.3",false).GetAwaiter().GetResult().ProcessId==first.ProcessId,"Declined update stopped process");
   var next=store.Install(nextPackage,delegate{return true;},id,runtime.BeforePackageCommit,backup,true,Hash(nextPackage));
   Assert(next.Version=="1.1.0"&&next.Id==old.Id&&next.EntryPoint==old.EntryPoint,"Update identity changed");
   Assert(next.Update!=null&&next.Update.Provider==old.Update.Provider&&next.Update.Repository==old.Update.Repository,"Update source changed");
   Assert(next.BackupFiles.Length==1&&next.BackupFiles[0]=="network-settings.json","Backup allowlist changed");
   Exited(first.ProcessId);
   Assert(!store.List()[0].Enabled,"Update enabled without consent");
   Assert(File.ReadAllText(file)==original&&File.ReadAllText(Path.Combine(data,"unlisted.txt"))=="preserve","Update altered data");
   string[] archives=Directory.GetFiles(Path.Combine(backup,"BeforePluginUpdate"),"*.asplugin");
   Assert(archives.Length==1,"Previous package backup missing");
   runtime.SetEnabledAsync(id,true).GetAwaiter().GetResult();
   var current=runtime.ReadWidgetSnapshotAsync(id,"1.1.0",true).GetAwaiter().GetResult();
   Assert(current.Widgets.Length==3,"VPN enabled during migration");
   foreach(var widget in current.Widgets) Assert(widget.Elements[0].Label=="Выключено","Disabled network regression");
   Assert(current.ProcessId!=first.ProcessId,"Old process reused");
   runtime.SetEnabledAsync(id,false).GetAwaiter().GetResult();
    // Synthetic path only: this EXE is never created or launched.
    string fixturePath=Path.Combine(Path.GetPathRoot(folder),"SyntheticVpnFixtures","client.exe");
    string selected="{\"showNetwork\":false,\"networkCheck1Mode\":\"tcp\",\"networkCheck1Target\":\"localhost:443\",\"vpnEnabled\":true,\"showVpnIndicator\":true,\"vpnAdapterIds\":[\"11111111-1111-1111-1111-111111111111\"],\"vpnProcessPaths\":[\""+fixturePath.Replace("\\","\\\\")+"\"]}";
   File.WriteAllText(file,selected);
   runtime.SetEnabledAsync(id,true).GetAwaiter().GetResult();
   current=runtime.ReadWidgetSnapshotAsync(id,"1.1.0",true).GetAwaiter().GetResult();
   Assert(current.Widgets.Length==4&&Array.Exists(current.Widgets,delegate(PluginWidget w){return w.Id=="tun"&&w.Elements[1].CommandId=="open";}),"Fourth indicator missing");
   int pid=current.ProcessId;
   for(int i=0;i<30;i++) {
    current=runtime.ReadWidgetSnapshotAsync(id,"1.1.0",false).GetAwaiter().GetResult();
    var tun=Array.Find(current.Widgets,delegate(PluginWidget w){return w.Id=="tun";});
    if(tun.Elements[0].Label=="TUN отсутствует")break;
    Thread.Sleep(100);
   }
   Assert(current.ProcessId==pid&&Array.Find(current.Widgets,delegate(PluginWidget w){return w.Id=="tun";}).Elements[0].Label=="TUN отсутствует","VPN background snapshot or single process failed");
   runtime.SetEnabledAsync(id,false).GetAwaiter().GetResult();
   store.SetBackupDataEnabled(id,true);byte[] bytes=store.CaptureBackup();File.WriteAllBytes(store.RecoveredPath(),bytes);
   File.WriteAllText(file,original);
   runtime.RestoreDataAsync(id,"1.1.0",Path.Combine(folder,"safety")).GetAwaiter().GetResult();
   Assert(File.ReadAllText(file)==selected&&!store.List()[0].Enabled,"VPN selections not restored safely");
   // The archived previous EXE can still read additive settings in a separate rollback store.
   var rollback=new PluginPackageStore(Path.Combine(folder,"rollback"));rollback.Install(archives[0],delegate{return true;});
   string previousData=rollback.GetDataDirectory(id);Directory.CreateDirectory(previousData);
   File.WriteAllText(Path.Combine(previousData,"network-settings.json"),selected);
   using(var previous=new PluginRuntime(rollback,5000)) {
    previous.SetEnabledAsync(id,true).GetAwaiter().GetResult();
    var restored=previous.ReadWidgetSnapshotAsync(id,"1.0.3",true).GetAwaiter().GetResult();
    Assert(restored.Widgets.Length==3,"Archived version cannot read additive settings");
   }
   Assert(File.ReadAllText(Path.Combine(previousData,"network-settings.json"))==selected,"Old version rewrote new settings");
  }
  Console.WriteLine("PASS: actual 1.0.3 -> 1.1.0 update, declined/bad-hash rejection, old process stop, unchanged data/source, VPN-off regression, fourth indicator, backup/restore and archived 1.0.3 rollback.");
 }
}
'@
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $parameters = New-Object CodeDom.Compiler.CompilerParameters
    $parameters.GenerateInMemory = $true
    $parameters.TreatWarningsAsErrors = $true
    foreach ($ref in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll','System.IO.Compression.dll')) { [void]$parameters.ReferencedAssemblies.Add($ref) }
    $sources = @(
        [IO.File]::ReadAllText((Join-Path $HostSourceDirectory 'PluginProtocol.cs')),
        [IO.File]::ReadAllText((Join-Path $HostSourceDirectory 'PluginPackages.cs')),
        [IO.File]::ReadAllText((Join-Path $HostSourceDirectory 'PluginRuntime.cs')), $harness)
    $compiled = $provider.CompileAssemblyFromSource($parameters,[string[]]$sources)
    if ($compiled.Errors.Count) { throw ($compiled.Errors | Out-String) }
    $compiled.CompiledAssembly.GetType('NetworkUpgradeTests').GetMethod('Run').Invoke($null,[object[]]@([string]$temp,[IO.Path]::GetFullPath($BaselinePackage),[string](Join-Path $temp 'NetworkPlugin-1.1.0.asplugin')))
    Write-Host ('Evidence profile: ' + $temp)
} finally { $provider.Dispose() }
