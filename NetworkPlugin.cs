using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Reflection;
using AppShelf;

[assembly: AssemblyTitle("NetworkPlugin")]
[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]

namespace AppShelf.Network
{
    internal static partial class NetworkPlugin
    {
        private const string Id = "appshelf.network";
        private const string Version = "1.1.1";
        private static Dispatcher dispatcher;
        private static Window reportWindow, settingsWindow;
        private static TextBox reportBox;
        private static Button refreshButton;
        private static string dataDirectory;
        private static NetworkSettings settings;
        private static DispatcherTimer monitor;
        private static bool backgroundStarted, pending, reportPending;
        private static int generation, exitCode;
        private static string firstResult, secondResult, routeResult;

        [STAThread]
        private static int Main()
        {
            try
            {
                AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
                AppContext.SetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", false);
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)0;
            }
            catch (NotSupportedException) { }
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            dispatcher = Dispatcher.CurrentDispatcher;
            Thread worker = new Thread(RunProtocol) { IsBackground = true };
            worker.Start();
            app.Run();
            worker.Join(1000);
            return exitCode;
        }

        private static void RunProtocol()
        {
            try
            {
                using (Stream input = Console.OpenStandardInput())
                using (Stream output = Console.OpenStandardOutput())
                {
                    bool ready = false;
                    int previousId = 0;
                    while (true)
                    {
                        PluginRequest request = PluginFrame.Read<PluginRequest>(input);
                        if (request.ApiVersion != 1 || request.Id <= previousId ||
                            (!ready && request.Method != "hello") || (ready && request.Method == "hello"))
                            throw new InvalidDataException("Invalid request sequence.");
                        previousId = request.Id;
                        PluginResponse reply = new PluginResponse { ApiVersion = 1, Id = request.Id, Ok = true };
                        try
                        {
                            dispatcher.Invoke(new Action(delegate { Handle(request, reply); }));
                        }
                        catch (Exception)
                        {
                            reply = new PluginResponse { ApiVersion = 1, Id = request.Id, Ok = false, Error = "Unable to perform the requested operation." };
                        }
                        PluginFrame.Write(output, reply);
                        if (request.Method == "hello")
                        {
                            if (!reply.Ok) break;
                            ready = true;
                        }
                        if (request.Method == "shutdown") break;
                    }
                }
            }
            catch (EndOfStreamException) { }
            catch (Exception ex)
            {
                exitCode = 1;
                // Diagnostics belong on stderr, never in the framed stdout stream.
                try { Console.Error.WriteLine("NetworkPlugin IPC terminated: " + ex.GetType().Name + ": " + ex.Message); }
                catch (IOException) { }
            }
            finally
            {
                try { dispatcher.BeginInvoke(new Action(delegate { Application.Current.Shutdown(); })); }
                catch (InvalidOperationException) { }
            }
        }

        // Only the STA owns settings, timers, snapshots and windows.
        private static void Handle(PluginRequest request, PluginResponse reply)
        {
            switch (request.Method)
            {
                case "hello":
                    if (request.PluginId != Id || request.PluginVersion != Version ||
                        String.IsNullOrEmpty(request.DataDirectory) || !Path.IsPathRooted(request.DataDirectory))
                        throw new InvalidDataException("Incompatible handshake.");
                    dataDirectory = Path.GetFullPath(request.DataDirectory);
                    Directory.CreateDirectory(dataDirectory);
                    settings = NetworkSettings.Load(dataDirectory);
                    ResetResults();
                    reply.PluginId = Id; reply.PluginVersion = Version;
                    reply.Commands = new[] { new PluginCommand { Id = "open", Label = "Диагностика сети" } };
                    break;
                case "invoke":
                    if (request.CommandId != "open") throw new InvalidOperationException();
                    OpenReport(); break;
                case "settings": OpenSettings(); break;
                case "startBackground":
                    if (backgroundStarted) throw new InvalidOperationException();
                    backgroundStarted = true;
                    monitor = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                    monitor.Tick += delegate { RefreshMonitor(); };
                    UpdateMonitor(); break;
                case "getWidgets": reply.Widgets = Snapshot(); break;
                case "ping": break;
                case "shutdown":
                    recoveryCancellation.Cancel();
                    generation++;
                    if (monitor != null) { monitor.Stop(); monitor = null; }
                    break;
                default: throw new InvalidOperationException();
            }
        }

        private static void ResetResults()
        {
            firstResult = secondResult = routeResult = settings.ShowNetwork ? "Проверяем…" : "Выключено";
            vpnResult = settings.VpnEnabled ? "Проверяем…" : "VPN-модуль выключен";
        }

        private static void UpdateMonitor()
        {
            if (monitor == null) return;
            if (!settings.ShowNetwork && !(settings.VpnEnabled && settings.ShowVpnIndicator)) { monitor.Stop(); return; }
            monitor.Start(); RefreshMonitor();
        }

        private static async void RefreshMonitor()
        {
            if ((!settings.ShowNetwork && !(settings.VpnEnabled && settings.ShowVpnIndicator)) || pending) return;
            pending = true;
            int version = generation;
            NetworkSettings captured = settings;
            try
            {
                Task<string> first = captured.ShowNetwork ? NetworkChecks.ProbeAsync(NetworkChecks.Resolve(captured,1),1) : Task.FromResult("Выключено");
                Task<string> second = captured.ShowNetwork ? NetworkChecks.ProbeAsync(NetworkChecks.Resolve(captured,2),2) : Task.FromResult("Выключено");
                Task<string> route = captured.ShowNetwork ? Task.Run(new Func<string>(NetworkProbes.RouteSummary)) : Task.FromResult("Выключено");
                Task<string> vpn = captured.VpnEnabled && captured.ShowVpnIndicator ?
                    Task.Run(delegate { try { return VpnModule.ReadStatus(captured); } catch (Exception) { return "Ошибка проверки TUN"; } }) : Task.FromResult("VPN-модуль выключен");
                await Task.WhenAll(first, second, route, vpn);
                if (version != generation) return;
                firstResult = first.Result; secondResult = second.Result; routeResult = route.Result;
                vpnResult = vpn.Result;
            }
            catch (Exception)
            {
                if (version == generation && settings.ShowNetwork)
                    firstResult = secondResult = routeResult = "Ошибка проверки";
            }
            finally
            {
                pending = false;
                if (version != generation && backgroundStarted && monitor != null) RefreshMonitor();
            }
        }

        private static string Bound(string value, int limit)
        {
            if (String.IsNullOrWhiteSpace(value)) return "Нет данных";
            value = value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            return value.Length <= limit ? value : value.Substring(0, limit - 1) + "…";
        }

        private static PluginWidget Indicator(string id, string title, string result)
        {
            return new PluginWidget { Id = id, Title = Bound(title,100), Kind = "indicator",
                Elements = new[] { new PluginWidgetElement { Type = "text", Label = Bound(result,240) },
                    new PluginWidgetElement { Type = "action", Label = "Подробнее", CommandId = "open" } } };
        }

        private static PluginWidget[] Snapshot()
        {
            List<PluginWidget> widgets = new List<PluginWidget> {
                Indicator("network1", NetworkChecks.Label(NetworkChecks.Resolve(settings,1),1), firstResult),
                Indicator("network2", NetworkChecks.Label(NetworkChecks.Resolve(settings,2),2), secondResult),
                Indicator("route", "Сеть", routeResult)
            };
            if (settings.VpnEnabled && settings.ShowVpnIndicator) widgets.Add(Indicator("tun", "VPN / TUN", vpnResult));
            return widgets.ToArray();
        }

        private static void Activate(Window window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        private static void OpenReport()
        {
            if (reportWindow != null) { Activate(reportWindow); return; }
            Window form = new Window { Title = "AppShelf — Диагностика сети", Width = 790, Height = 640,
                MinWidth = 520, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            PluginTheme.Prepare(form);
            DockPanel root = new DockPanel { Margin = new Thickness(22) }; form.Content = root;
            StackPanel header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            TextBlock heading = PluginTheme.Label("Диагностика сети", 22);
            heading.FontWeight = FontWeights.SemiBold; header.Children.Add(heading);
            TextBlock hint = PluginTheme.Label("Проверки соединения, DNS, прокси и маршрутов Windows.", 13, true);
            hint.Margin = new Thickness(0, 6, 0, 0); header.Children.Add(hint);
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            WrapPanel commands = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(commands, Dock.Bottom); root.Children.Add(commands);
            Button refresh = PluginTheme.Button("Повторить проверку", true);
            Button copy = PluginTheme.Button("Скопировать отчёт");
            Button config = PluginTheme.Button("Настройки");
            TextBox report = PluginTheme.Report();
            commands.Children.Add(refresh); commands.Children.Add(copy); commands.Children.Add(config);
            AddRecoveryControls(form, commands, config);
            root.Children.Add(report);
            refresh.Click += delegate { RefreshReport(); };
            copy.Click += delegate { try { if (report.Text.Length != 0) Clipboard.SetText(report.Text); } catch (Exception ex) { MessageBox.Show(form, ex.Message, "Сеть", MessageBoxButton.OK, MessageBoxImage.Error); } };
            config.Click += delegate { OpenSettings(); };
            form.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs args) { if (recoveryBusy) args.Cancel = true; };
            form.Closed += delegate { if (reportWindow == form) { reportWindow = null; reportBox = null; refreshButton = null; } };
            reportWindow = form; reportBox = report; refreshButton = refresh;
            form.Show(); RefreshReport();
        }

        private static async void RefreshReport()
        {
            if (reportPending || recoveryBusy || reportBox == null) return;
            reportPending = true;
            UpdateRecoveryControls();
            int version = generation;
            NetworkSettings captured = settings;
            TextBox target = reportBox;
            Button button = refreshButton;
            button.IsEnabled = false; target.Text = "Проверяем…";
            try
            {
                string local = await Task.Run(delegate { return NetworkProbes.LocalReport() +
                    (captured.VpnEnabled ? Environment.NewLine + Environment.NewLine + VpnModule.Report(captured) : ""); });
                string report;
                if (String.IsNullOrWhiteSpace(captured.NetworkCheck1Mode) && String.IsNullOrWhiteSpace(captured.NetworkCheck2Mode))
                    report = (await NetworkProbes.CheckConnectivityAsync()).ToReport();
                else
                {
                    Task<string> a = NetworkChecks.ProbeAsync(NetworkChecks.Resolve(captured,1),1);
                    Task<string> b = NetworkChecks.ProbeAsync(NetworkChecks.Resolve(captured,2),2);
                    await Task.WhenAll(a,b);
                    report = "Проверки пользовательских адресов (не оценка всего интернета):" +
                        Environment.NewLine + a.Result + Environment.NewLine + b.Result;
                }
                if (version == generation && target == reportBox)
                    target.Text = report + Environment.NewLine + Environment.NewLine + local;
            }
            catch (Exception ex)
            { if (target == reportBox && version == generation) target.Text = "Ошибка диагностики: " + ex.Message; }
            finally
            {
                reportPending = false;
                if (button == refreshButton) button.IsEnabled = true;
                UpdateRecoveryControls();
                if (reportBox != null && reportBox != target) RefreshReport();
            }
        }

        private static void AddTarget(StackPanel layout, int index, string savedMode, string savedTarget,
            out ComboBox mode, out TextBox address)
        {
            TextBlock title = PluginTheme.Label("Проверка " + index, 14);
            title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 12, 0, 6);
            layout.Children.Add(title);
            ComboBox list = new ComboBox { MinHeight = 34, Margin = new Thickness(0, 0, 0, 8),
                Tag = "mode" + index };
            System.Windows.Automation.AutomationProperties.SetName(list, "Режим проверки " + index);
            foreach (string name in new[] { "По умолчанию — ICMP + HTTPS", "ICMP — ping узла",
                "HTTPS — проверка сайта", "TCP — подключение к порту" }) list.Items.Add(name);
            string selected = savedMode ?? "default";
            list.SelectedIndex = selected == "icmp" ? 1 : selected == "https" ? 2 : selected == "tcp" ? 3 : 0;
            TextBox input = new TextBox { MinHeight = 34, Tag = "target" + index,
                Text = list.SelectedIndex == 0 ? NetworkChecks.DefaultHost(index) : savedTarget ?? "",
                IsEnabled = list.SelectedIndex != 0, VerticalContentAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(input, "Адрес проверки " + index);
            list.SelectionChanged += delegate
            {
                string host = NetworkChecks.DefaultHost(index);
                input.IsEnabled = list.SelectedIndex != 0;
                if (list.SelectedIndex == 0) input.Text = host;
                else if (String.IsNullOrWhiteSpace(input.Text) || input.Text == host)
                    input.Text = list.SelectedIndex == 2 ? (index == 1 ? "https://1.1.1.1/cdn-cgi/trace" : "https://ya.ru/") :
                        list.SelectedIndex == 3 ? host + ":443" : host;
            };
            layout.Children.Add(list); layout.Children.Add(input);
            mode = list; address = input;
        }

        private static string Mode(ComboBox box)
        { return box.SelectedIndex == 1 ? "icmp" : box.SelectedIndex == 2 ? "https" : box.SelectedIndex == 3 ? "tcp" : null; }

        private static void OpenSettings()
        {
            if (recoveryBusy) return;
            if (settingsWindow != null) { Activate(settingsWindow); return; }
            Window form = new Window { Title = "AppShelf — Настройки сети", Width = 640, Height = 620,
                MinWidth = 460, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            PluginTheme.Prepare(form);
            DockPanel root = new DockPanel { Margin = new Thickness(22) }; form.Content = root;
            StackPanel header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            TextBlock heading = PluginTheme.Label("Настройки сети", 22);
            heading.FontWeight = FontWeights.SemiBold; header.Children.Add(heading);
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
            Button save = PluginTheme.Button("Сохранить", true); save.Tag = "save";
            Button cancel = PluginTheme.Button("Отмена"); cancel.Click += delegate { form.Close(); };
            buttons.Children.Add(save); buttons.Children.Add(cancel);
            StackPanel layout = new StackPanel();
            ScrollViewer scroll = new ScrollViewer { Content = layout, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            root.Children.Add(scroll);
            CheckBox enabled = new CheckBox { Content = "Фоновые сетевые проверки", IsChecked = settings.ShowNetwork, Tag = "enabled" };
            layout.Children.Add(enabled);
            ComboBox one,two; TextBox first,second;
            AddTarget(layout,1,settings.NetworkCheck1Mode,settings.NetworkCheck1Target,out one,out first);
            AddTarget(layout,2,settings.NetworkCheck2Mode,settings.NetworkCheck2Target,out two,out second);
            VpnSettingsPanel vpn = new VpnSettingsPanel(settings);
            layout.Children.Add(vpn.Content);
            save.Click += delegate
            {
                try
                {
                    NetworkSettings next = new NetworkSettings { ShowNetwork = enabled.IsChecked == true,
                        NetworkCheck1Mode = Mode(one), NetworkCheck2Mode = Mode(two) };
                    next.NetworkCheck1Target = next.NetworkCheck1Mode == null ? null : first.Text.Trim();
                    next.NetworkCheck2Target = next.NetworkCheck2Mode == null ? null : second.Text.Trim();
                    vpn.Apply(next);
                    next.Save(dataDirectory);
                    settings = next; generation++; ResetResults(); UpdateMonitor();
                    if (reportBox != null) reportBox.Text = "Настройки изменились. Повторите проверку.";
                    UpdateRecoveryControls();
                    form.Close();
                }
                catch (Exception ex) { MessageBox.Show(form, ex.Message, "Настройки сети", MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            form.Closed += delegate { if (settingsWindow == form) settingsWindow = null; };
            settingsWindow = form; form.Show();
        }
    }
}
