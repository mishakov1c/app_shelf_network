# Deterministic WinForms STA test of the real monitor and settings/report UI.
# Substitute only the network I/O boundaries; never send live packets.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path ([IO.Path]::GetTempPath()) ('AppShelf-NetworkMonitor-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $program = [IO.File]::ReadAllText((Join-Path $root 'NetworkPlugin.cs'))
    foreach ($index in @(1,2)) {
        $anchor = "NetworkChecks.ProbeAsync(NetworkChecks.Resolve(captured,$index),$index)"
        if ($program.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Monitor isolation boundary missing.' }
        $program = $program.Replace($anchor, "NetworkMonitorTests.Probe(NetworkChecks.Resolve(captured,$index),$index)")
    }
    $anchor = 'Task.Run(new Func<string>(NetworkProbes.RouteSummary))'
    if ($program.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Route isolation boundary missing.' }
    $program = $program.Replace($anchor, 'Task.FromResult("Маршрут: fixture")')
    $anchor = 'ApplyDarkTheme(form); form.Show(); RefreshReport();'
    if ($program.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Report isolation boundary missing.' }
    $program = $program.Replace($anchor, 'ApplyDarkTheme(form); form.Show();')
    $harness = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AppShelf;

namespace AppShelf.Network
{
    public static class NetworkMonitorTests
    {
        private static readonly BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly List<TaskCompletionSource<string>> First = new List<TaskCompletionSource<string>>();
        private static readonly List<TaskCompletionSource<string>> Second = new List<TaskCompletionSource<string>>();
        private static readonly List<string> Targets = new List<string>();

        private static void Assert(bool value, string message)
        { if (!value) throw new Exception(message); }

        private static void Set(string field, object value)
        { typeof(NetworkPlugin).GetField(field, Flags).SetValue(null, value); }

        private static object Get(string field)
        { return typeof(NetworkPlugin).GetField(field, Flags).GetValue(null); }

        private static object Call(string method)
        { return typeof(NetworkPlugin).GetMethod(method, Flags).Invoke(null, null); }

        internal static Task<string> Probe(NetworkChecks.Target target, int index)
        {
            TaskCompletionSource<string> task = new TaskCompletionSource<string>();
            if (index == 1) { First.Add(task); Targets.Add(target.Value); }
            else Second.Add(task);
            return task.Task;
        }

        private static void WaitFor(Func<bool> ready, string failure)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!ready() && watch.ElapsedMilliseconds < 3000)
            { Application.DoEvents(); Thread.Sleep(5); }
            Assert(ready(), failure);
        }

        private static string FirstResult()
        { return ((PluginWidget[])Call("Snapshot"))[0].Elements[0].Label; }

        public static void Run(string folder)
        {
            using (Control dispatcher = new Control())
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 60000 })
            {
                IntPtr handle = dispatcher.Handle;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                NetworkSettings old = new NetworkSettings { ShowNetwork = true, NetworkCheck1Mode = "https",
                    NetworkCheck1Target = "https://old.example/path", NetworkCheck2Mode = "icmp", NetworkCheck2Target = "127.0.0.2" };
                Set("dataDirectory", folder);
                Set("settings", old); Set("monitor", timer); Set("backgroundStarted", true);
                Call("ResetResults"); Call("RefreshMonitor"); Call("RefreshMonitor");
                Assert(First.Count == 1 && Second.Count == 1, "Overlapping requests started");
                NetworkSettings next = new NetworkSettings { ShowNetwork = true, NetworkCheck1Mode = "icmp",
                    NetworkCheck1Target = "127.0.0.1", NetworkCheck2Mode = "icmp", NetworkCheck2Target = "127.0.0.2" };
                Set("settings", next); Set("generation", 1); Call("ResetResults"); Call("UpdateMonitor");
                Assert(First.Count == 1 && Second.Count == 1, "Settings started overlapping requests");
                First[0].SetResult("stale"); Second[0].SetResult("stale2");
                WaitFor(delegate { return First.Count == 2 && Second.Count == 2; }, "Latest generation not started");
                Assert(FirstResult() != "stale" && Targets[1] == "127.0.0.1", "Stale result or stale target");
                First[1].SetResult("fresh"); Second[1].SetResult("fresh2");
                WaitFor(delegate { return !(bool)Get("pending"); }, "Fresh probes did not finish");
                Assert(FirstResult() == "fresh", "Fresh result not published");
                Call("RefreshMonitor");
                Assert(First.Count == 3 && Second.Count == 3, "Third generation did not start");
                next = new NetworkSettings { ShowNetwork = false, NetworkCheck1Mode = "tcp", NetworkCheck1Target = "localhost:443" };
                Set("settings", next); Set("generation", 2); Call("ResetResults"); Call("UpdateMonitor");
                First[2].SetResult("stale after disable"); Second[2].SetResult("stale2");
                WaitFor(delegate { return !(bool)Get("pending"); }, "Disabled probes did not finish");
                Assert(!timer.Enabled && First.Count == 3 && FirstResult() == "Выключено", "Disabled monitor published or restarted");
                Console.WriteLine("PASS: overlapping requests, stale results, replacement targets and disabled monitoring");

                try
                {
                    Call("OpenReport");
                    Form report = (Form)Get("reportWindow");
                    TextBox box = (TextBox)Get("reportBox");
                    Assert(report != null && box != null && box.ReadOnly && box.Multiline && !box.WordWrap &&
                        box.ScrollBars == ScrollBars.Both, "Report missing read-only two-axis scrolling");
                    Call("OpenReport"); Assert(Object.ReferenceEquals(report, Get("reportWindow")), "Duplicate report window");
                    Call("OpenSettings");
                    Form settings = (Form)Get("settingsWindow");
                    TableLayoutPanel layout = (TableLayoutPanel)settings.Controls[0];
                    CheckBox enabled = (CheckBox)layout.GetControlFromPosition(0,0);
                    ComboBox mode = (ComboBox)layout.GetControlFromPosition(1,1);
                    TextBox address = (TextBox)layout.GetControlFromPosition(1,2);
                    Assert(!enabled.Checked && mode.SelectedIndex == 3 && address.Text == "localhost:443", "Settings did not load");
                    address.Text = "127.0.0.1:8443";
                    ((Button)layout.GetControlFromPosition(1,5)).PerformClick();
                    NetworkSettings saved = NetworkSettings.Load(folder);
                    Assert(!saved.ShowNetwork && saved.NetworkCheck1Target == "127.0.0.1:8443", "UI save did not persist target");
                    Assert(First.Count == 3 && !timer.Enabled, "Settings UI enabled traffic unexpectedly");
                    Console.WriteLine("PASS: report composition, singleton windows and real settings save");
                }
                finally
                {
                    Form form = (Form)Get("settingsWindow"); if (form != null && !form.IsDisposed) form.Close();
                    form = (Form)Get("reportWindow"); if (form != null && !form.IsDisposed) form.Close();
                    timer.Stop(); Set("monitor", null);
                }
            }
        }
    }
}
'@
    $sources = @(
        [IO.File]::ReadAllText((Join-Path $root 'PluginProtocol.cs')),
        [IO.File]::ReadAllText((Join-Path $root 'NetworkChecks.cs')),
        $program, $harness)
    $parameters = New-Object CodeDom.Compiler.CompilerParameters
    $parameters.GenerateInMemory = $true
    $parameters.TreatWarningsAsErrors = $true
    foreach ($ref in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll','System.Windows.Forms.dll','System.Drawing.dll')) {
        [void]$parameters.ReferencedAssemblies.Add($ref)
    }
    $result = $provider.CompileAssemblyFromSource($parameters,[string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    $result.CompiledAssembly.GetType('AppShelf.Network.NetworkMonitorTests').GetMethod('Run').Invoke($null,[object[]]@([string]$temp))
} finally {
    $provider.Dispose()
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
