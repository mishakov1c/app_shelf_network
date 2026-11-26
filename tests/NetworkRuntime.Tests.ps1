# Windows PowerShell 5.1 -STA. Exercise the actual network package through the host runtime.
param([Parameter(Mandatory=$true)][string]$HostSourceDirectory)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path $env:LOCALAPPDATA ('Temp\AppShelf-NetworkRuntime-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    & (Join-Path $root 'Build.ps1') -OutputDirectory $temp
    $package = Join-Path $temp 'NetworkPlugin-1.0.0.asplugin'
    if (-not (Test-Path -LiteralPath $package)) { throw 'Network package missing.' }
    $harness = @'
using System;
using System.Diagnostics;
using System.IO;
using AppShelf;

public static class NetworkRuntimeTests
{
    private static void Assert(bool value, string message)
    { if (!value) throw new Exception(message); }

    public static void Run(string folder, string package)
    {
        const string id = "appshelf.network";
        const string version = "1.0.0";
        PluginPackageStore store = new PluginPackageStore(folder);
        store.Install(package, delegate { return true; });
        Assert(!store.List()[0].Enabled, "Network plugin installed enabled");
        string data = store.GetDataDirectory(id);
        Directory.CreateDirectory(data);
        string settings = Path.Combine(data, "network-settings.json");
        // No external traffic in this lifecycle test. Start with saved monitoring off.
        File.WriteAllText(settings, "{\"showNetwork\":false,\"networkCheck1Mode\":\"tcp\",\"networkCheck1Target\":\"localhost:443\"}");
        int pid = 0;
        using (PluginRuntime runtime = new PluginRuntime(store, 5000))
        {
            runtime.SetEnabledAsync(id, true).GetAwaiter().GetResult();
            PluginWidgetSnapshot snapshot = runtime.ReadWidgetSnapshotAsync(id, version, true).GetAwaiter().GetResult();
            pid = snapshot.ProcessId;
            Assert(pid > 0 && snapshot.Version == version && snapshot.Widgets.Length == 3, "Network snapshot");
            foreach (string key in new[] { "network1", "network2", "route" })
            {
                PluginWidget widget = Array.Find(snapshot.Widgets, delegate(PluginWidget w) { return w.Id == key; });
                Assert(widget != null && widget.Kind == "indicator" && widget.Elements.Length == 2 &&
                    widget.Elements[0].Label == "\u0412\u044b\u043a\u043b\u044e\u0447\u0435\u043d\u043e" &&
                    widget.Elements[1].CommandId == "open", "Incorrect disabled snapshot " + key);
                if (key == "network1") Assert(widget.Title == "localhost:443", "Target setting not loaded");
            }
            PluginWidgetSnapshot second = runtime.ReadWidgetSnapshotAsync(id, version, false).GetAwaiter().GetResult();
            Assert(second.ProcessId == pid, "Unnecessary child restart");
            runtime.SetEnabledAsync(id, false).GetAwaiter().GetResult();
            Assert(runtime.State(id).Status == "idle", "Disable did not stop monitor");
        }
        try { using (Process child = Process.GetProcessById(pid)) Assert(child.WaitForExit(3000), "Child still running"); }
        catch (ArgumentException) {}
        Assert(File.ReadAllText(settings).Contains("localhost:443"), "Plugin data was changed");
        Console.WriteLine("PASS: actual installed network package, disabled monitoring, indicators and process lifecycle");
    }
}

'@
    $sources = @(
        [IO.File]::ReadAllText((Join-Path $HostSourceDirectory 'PluginProtocol.cs')),
        [IO.File]::ReadAllText((Join-Path $HostSourceDirectory 'PluginPackages.cs')),
        [IO.File]::ReadAllText((Join-Path $HostSourceDirectory 'PluginRuntime.cs')),
        $harness)
    $parameters = New-Object CodeDom.Compiler.CompilerParameters
    $parameters.GenerateInMemory = $true
    $parameters.TreatWarningsAsErrors = $true
    foreach ($ref in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll','System.IO.Compression.dll')) {
        [void]$parameters.ReferencedAssemblies.Add($ref)
    }
    $result = $provider.CompileAssemblyFromSource($parameters,[string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    $result.CompiledAssembly.GetType('NetworkRuntimeTests').GetMethod('Run').Invoke($null,[object[]]@([string]$temp,[string]$package))
} finally {
    $provider.Dispose()
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
