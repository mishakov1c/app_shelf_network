# Deterministic WPF STA test of the real monitor and settings/report UI.
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
        $slot = if ($index -eq 1) { "first" } else { "second" }
        $anchor = "Task<string> $slot = captured.ShowNetwork ? NetworkChecks.ProbeAsync(NetworkChecks.Resolve(captured,$index),$index)"
        if ($program.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Monitor isolation boundary missing.' }
        $program = $program.Replace($anchor, "Task<string> $slot = captured.ShowNetwork ? NetworkMonitorTests.Probe(NetworkChecks.Resolve(captured,$index),$index)")
    }
    $anchor = 'Task.Run(new Func<string>(NetworkProbes.RouteSummary))'
    if ($program.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Route isolation boundary missing.' }
    $program = $program.Replace($anchor, 'Task.FromResult("Маршрут: fixture")')
    $anchor = 'form.Show(); RefreshReport();'
    if ($program.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Report isolation boundary missing.' }
    $program = $program.Replace($anchor, 'form.Show();')
    $program = $program.Replace('VpnModule.ReadStatus(captured)', 'NetworkMonitorTests.VpnStatus(captured)')
    $program = $program.Replace('new VpnSettingsPanel(settings)', 'new VpnSettingsPanel(settings, new DiscoveryFixture())')
    $harness = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AppShelf;

namespace AppShelf.Network
{
    internal sealed class DiscoveryFixture : IVpnSystem
    {
        internal static int Reads;
        internal static string ProcessPath;
        public VpnAdapter[] Adapters() { Reads++; return new[] { new VpnAdapter { Id="11111111-1111-1111-1111-111111111111", Name="VPN fixture",Index=10,State=System.Net.NetworkInformation.OperationalStatus.Up } }; }
        public VpnProcess[] Processes() { Reads++; return new[] { new VpnProcess {Id=111,Path=ProcessPath,Started=123} }; }
        public NetworkProbes.ProxySnapshot Proxy() { throw new Exception("Unexpected proxy read"); }
        public Task StopAsync(VpnProcess p) { throw new Exception("Unsafe test action"); }
        public Task<int> DisableAsync(VpnAdapter a) { throw new Exception("Unsafe test action"); }
        public void SetProxy(bool enabled,string expected) { throw new Exception("Unsafe test action"); }
        public Task<NetworkProbes.ConnectivitySnapshot> ConnectivityAsync() { throw new Exception("Unexpected connectivity"); }
        public Task<int> FlushDnsAsync() { throw new Exception("Unsafe test action"); }
    }
    public static class NetworkMonitorTests
    {
        private static readonly BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly List<TaskCompletionSource<string>> First = new List<TaskCompletionSource<string>>();
        private static readonly List<TaskCompletionSource<string>> Second = new List<TaskCompletionSource<string>>();
        private static readonly List<string> Targets = new List<string>();
        private static string tunState = "TUN отсутствует";
        private static int vpnReads;
        private static ManualResetEvent vpnGate;
        internal static string VpnStatus(NetworkSettings captured) {
            Interlocked.Increment(ref vpnReads);
            if (vpnGate != null && !vpnGate.WaitOne(3000)) throw new Exception("Fixture timeout");
            if (tunState == "throw") throw new Exception("Simulated adapter read failure");
            return tunState;
        }

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
            { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { })); Thread.Sleep(5); }
            Assert(ready(), failure);
        }

        private static string FirstResult()
        { return ((PluginWidget[])Call("Snapshot"))[0].Elements[0].Label; }

        private static T Find<T>(DependencyObject root, string tag) where T : FrameworkElement
        {
            T match = root as T;
            if (match != null && Object.Equals(match.Tag, tag)) return match;
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                DependencyObject node = child as DependencyObject;
                if (node == null) continue;
                T found = Find<T>(node, tag);
                if (found != null) return found;
            }
            return null;
        }

        public static void Run(string folder)
        {
            DiscoveryFixture.ProcessPath = System.IO.Path.Combine(System.IO.Path.GetPathRoot(folder), "SyntheticVpnFixtures", "client.exe");
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
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
                Assert(!timer.IsEnabled && First.Count == 3 && FirstResult() == "Выключено", "Disabled monitor published or restarted");
                Console.WriteLine("PASS: overlapping requests, stale results, replacement targets and disabled monitoring");

                try
                {
                    Call("OpenReport");
                    Window report = (Window)Get("reportWindow");
                    TextBox box = (TextBox)Get("reportBox");
                    Assert(report != null && box != null && box.IsReadOnly && box.TextWrapping == TextWrapping.NoWrap &&
                        box.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && box.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto, "Report missing read-only two-axis scrolling");
                    Call("OpenReport"); Assert(Object.ReferenceEquals(report, Get("reportWindow")), "Duplicate report window");
                    Call("OpenSettings");
                    Window settings = (Window)Get("settingsWindow");
                    Assert(report.Icon != null && report.Icon.IsFrozen &&
                        Object.ReferenceEquals(report.Icon, settings.Icon), "Both WPF windows must share the embedded frozen icon");
                    CheckBox enabled = Find<CheckBox>(settings, "enabled");
                    ComboBox mode = Find<ComboBox>(settings, "mode1");
                    TextBox address = Find<TextBox>(settings, "target1");
                    Assert(enabled != null && mode != null && address != null && enabled.IsChecked == false && mode.SelectedIndex == 3 && address.Text == "localhost:443", "Settings did not load");
                    Call("OpenSettings"); Assert(Object.ReferenceEquals(settings, Get("settingsWindow")), "Duplicate settings window");
                    mode.ApplyTemplate(); Assert(mode.Template.FindName("PART_Popup", mode) != null && settings.Resources.MergedDictionaries.Count >= 2, "Dark combo style missing");
                    address.Text = "127.0.0.1:8443";
                    Find<Button>(settings, "save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    NetworkSettings saved = NetworkSettings.Load(folder);
                    Assert(!saved.ShowNetwork && saved.NetworkCheck1Target == "127.0.0.1:8443", "UI save did not persist target");
                    Assert(First.Count == 3 && !timer.IsEnabled, "Settings UI enabled traffic unexpectedly");
                    Console.WriteLine("PASS: report composition, singleton windows and real settings save");
                    Assert(DiscoveryFixture.Reads == 0 && vpnReads == 0, "Disabled VPN performed discovery/monitoring");
                    Assert(Find<Button>(report, "vpnRecover").Visibility == Visibility.Collapsed &&
                        Find<Button>(report, "vpnDisable").Visibility == Visibility.Collapsed, "Disabled VPN exposes recovery");
                    Call("OpenSettings"); settings = (Window)Get("settingsWindow");
                    Find<CheckBox>(settings, "vpnEnabled").IsChecked = true;
                    Assert(DiscoveryFixture.Reads == 2, "VPN discovery did not run on explicit enable");
                    Find<CheckBox>(settings, "vpnIndicator").IsChecked = true;
                    Find<CheckBox>(settings, "11111111-1111-1111-1111-111111111111").IsChecked = true;
                    Find<CheckBox>(settings, DiscoveryFixture.ProcessPath).IsChecked = true;
                    Find<Button>(settings, "save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    saved = NetworkSettings.Load(folder);
                    Assert(saved.VpnEnabled && saved.ShowVpnIndicator && saved.VpnAdapterIds.Length == 1 &&
                        saved.VpnProcessPaths[0] == DiscoveryFixture.ProcessPath && !saved.ShowNetwork, "VPN settings UI lost selections/network toggle");
                    WaitFor(delegate { return !(bool)Get("pending"); }, "VPN monitor did not finish");
                    Assert(timer.IsEnabled && First.Count == 3 && Second.Count == 3, "VPN-only monitoring sent network probes");
                    Assert(((PluginWidget[])Call("Snapshot")).Length == 4, "Fourth indicator absent");
                    foreach (string state in new[] { "TUN: выбран маршрут", "TUN: маршрут не подтверждён", "TUN: несколько адаптеров",
                        "TUN неактивен", "Состояние TUN неизвестно", "TUN отсутствует", "Ошибка проверки TUN" })
                    {
                        tunState = state; Call("RefreshMonitor");
                        WaitFor(delegate { return !(bool)Get("pending"); }, "VPN state did not finish");
                        Assert(((PluginWidget[])Call("Snapshot"))[3].Elements[0].Label == state, "VPN state not reflected");
                    }
                    Assert(Find<Button>(report, "vpnRecover").Visibility == Visibility.Visible, "Enabled recovery not shown");
                    tunState = "throw"; Call("RefreshMonitor");
                    WaitFor(delegate { return !(bool)Get("pending"); }, "VPN error did not finish");
                    Assert(((PluginWidget[])Call("Snapshot"))[3].Elements[0].Label == "Ошибка проверки TUN", "VPN exception escaped indicator boundary");
                    tunState = "stale VPN state"; vpnGate = new ManualResetEvent(false);
                    int beforeReads = vpnReads;
                    Call("RefreshMonitor");
                    WaitFor(delegate { return vpnReads > beforeReads; }, "Delayed VPN task did not start");
                    Call("OpenSettings"); settings = (Window)Get("settingsWindow");
                    Find<CheckBox>(settings, "vpnIndicator").IsChecked = false;
                    Find<Button>(settings, "save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    vpnGate.Set();
                    WaitFor(delegate { return !(bool)Get("pending"); }, "Stale VPN task did not finish");
                    vpnGate.Dispose(); vpnGate = null;
                    Assert(((PluginWidget[])Call("Snapshot")).Length == 3 && !timer.IsEnabled, "Optional indicator did not stop background reads");
                    Call("OpenSettings"); settings = (Window)Get("settingsWindow");
                    Find<CheckBox>(settings, "vpnEnabled").IsChecked = false;
                    Find<Button>(settings, "save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    saved = NetworkSettings.Load(folder);
                    int reads = vpnReads;
                    Call("RefreshMonitor");
                    Assert(!saved.VpnEnabled && saved.VpnAdapterIds.Length == 1 && saved.VpnProcessPaths.Length == 1 &&
                        vpnReads == reads && !timer.IsEnabled, "Disabling VPN lost choices or restarted IO");
                    Console.WriteLine("PASS: disabled VPN has no discovery/IO/actions; VPN settings selection, seven indicator transitions, independent monitoring and full disable");
                }
                finally
                {
                    Window form = (Window)Get("settingsWindow"); if (form != null) form.Close();
                    form = (Window)Get("reportWindow"); if (form != null) form.Close();
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
        [IO.File]::ReadAllText((Join-Path $root 'PluginTheme.cs')),
        [IO.File]::ReadAllText((Join-Path $root 'VpnModule.cs')),
        [IO.File]::ReadAllText((Join-Path $root 'VpnUi.cs')),
        $program, $harness)
    $parameters = New-Object CodeDom.Compiler.CompilerParameters
    $parameters.GenerateInMemory = $true
    $parameters.TreatWarningsAsErrors = $true
    $icon = Join-Path $root 'Assets\NetworkPlugin.ico'
    $parameters.CompilerOptions = '/resource:"' + $icon + '",AppShelf.Network.Icon.ico'
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($ref in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll','System.Xaml.dll',
        (Join-Path $framework 'WPF\WindowsBase.dll'),(Join-Path $framework 'WPF\PresentationCore.dll'),(Join-Path $framework 'WPF\PresentationFramework.dll'))) {
        [void]$parameters.ReferencedAssemblies.Add($ref)
    }
    $result = $provider.CompileAssemblyFromSource($parameters,[string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    $result.CompiledAssembly.GetType('AppShelf.Network.NetworkMonitorTests').GetMethod('Run').Invoke($null,[object[]]@([string]$temp))
} finally {
    $provider.Dispose()
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
