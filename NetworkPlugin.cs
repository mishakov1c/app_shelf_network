using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;
using AppShelf;

namespace AppShelf.Network
{
    internal static class NetworkPlugin
    {
        private const string Id = "appshelf.network";
        private const string Version = "1.0.1";
        private static Control dispatcher;
        private static Form reportWindow, settingsWindow;
        private static TextBox reportBox;
        private static Button refreshButton;
        private static string dataDirectory;
        private static NetworkSettings settings;
        private static System.Windows.Forms.Timer monitor;
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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (dispatcher = new Control())
            {
                // Create the handle on the STA before the protocol worker starts.
                IntPtr handle = dispatcher.Handle;
                Thread worker = new Thread(RunProtocol);
                worker.IsBackground = true;
                worker.Start();
                Application.Run();
                // The normal exit is requested only after the final frame is flushed.
                worker.Join(1000);
            }
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
                            dispatcher.Invoke((MethodInvoker)delegate { Handle(request, reply); });
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
            catch (Exception) { exitCode = 1; }
            finally
            {
                try { dispatcher.BeginInvoke((MethodInvoker)delegate { Application.ExitThread(); }); }
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
                    monitor = new System.Windows.Forms.Timer { Interval = 8000 };
                    monitor.Tick += delegate { RefreshMonitor(); };
                    UpdateMonitor(); break;
                case "getWidgets": reply.Widgets = Snapshot(); break;
                case "ping": break;
                case "shutdown":
                    generation++;
                    if (monitor != null) { monitor.Stop(); monitor.Dispose(); monitor = null; }
                    break;
                default: throw new InvalidOperationException();
            }
        }

        private static void ResetResults()
        {
            firstResult = secondResult = routeResult = settings.ShowNetwork ? "Проверяем…" : "Выключено";
        }

        private static void UpdateMonitor()
        {
            if (monitor == null) return;
            if (!settings.ShowNetwork) { monitor.Stop(); return; }
            monitor.Start(); RefreshMonitor();
        }

        private static async void RefreshMonitor()
        {
            if (!settings.ShowNetwork || pending) return;
            pending = true;
            int version = generation;
            NetworkSettings captured = settings;
            try
            {
                Task<string> first = NetworkChecks.ProbeAsync(NetworkChecks.Resolve(captured,1),1);
                Task<string> second = NetworkChecks.ProbeAsync(NetworkChecks.Resolve(captured,2),2);
                Task<string> route = Task.Run(new Func<string>(NetworkProbes.RouteSummary));
                await Task.WhenAll(first, second, route);
                if (version != generation || !settings.ShowNetwork) return;
                firstResult = first.Result; secondResult = second.Result; routeResult = route.Result;
            }
            catch (Exception)
            {
                if (version == generation && settings.ShowNetwork)
                    firstResult = secondResult = routeResult = "Ошибка проверки";
            }
            finally
            {
                pending = false;
                if (version != generation && backgroundStarted && monitor != null && settings.ShowNetwork) RefreshMonitor();
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
            return new[] {
                Indicator("network1", NetworkChecks.Label(NetworkChecks.Resolve(settings,1),1), firstResult),
                Indicator("network2", NetworkChecks.Label(NetworkChecks.Resolve(settings,2),2), secondResult),
                Indicator("route", "Сеть", routeResult)
            };
        }

        private static void Activate(Form form)
        {
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Show(); form.Activate();
        }

        private static void ApplyDarkTheme(Control control)
        {
            Color surface = Color.FromArgb(20, 28, 42);
            Color field = Color.FromArgb(31, 42, 60);
            Color foreground = Color.FromArgb(235, 240, 248);
            control.ForeColor = foreground;
            control.BackColor = surface;
            TextBox editor = control as TextBox;
            if (editor != null) editor.BackColor = field;
            Button button = control as Button;
            if (button != null)
            {
                button.UseVisualStyleBackColor = false;
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = field;
                button.FlatAppearance.BorderColor = Color.FromArgb(59, 75, 98);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(46, 60, 81);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(39, 52, 72);
            }
            CheckBox checkbox = control as CheckBox;
            if (checkbox != null) checkbox.UseVisualStyleBackColor = false;
            foreach (Control child in control.Controls) ApplyDarkTheme(child);
        }


        private static void OpenReport()
        {
            if (reportWindow != null && !reportWindow.IsDisposed) { Activate(reportWindow); return; }
            Form form = new Form { Text = "AppShelf — Диагностика сети", Size = new Size(790,640),
                MinimumSize = new Size(520,400), StartPosition = FormStartPosition.CenterScreen };
            FlowLayoutPanel commands = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(5) };
            Button refresh = new Button { Text = "Повторить проверку", AutoSize = true, Height = 32 };
            Button copy = new Button { Text = "Скопировать отчёт", AutoSize = true, Height = 32 };
            Button config = new Button { Text = "Настройки", AutoSize = true, Height = 32 };
            TextBox report = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                WordWrap = false, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 10) };
            commands.Controls.Add(refresh); commands.Controls.Add(copy); commands.Controls.Add(config);
            form.Controls.Add(report); form.Controls.Add(commands);
            refresh.Click += delegate { RefreshReport(); };
            copy.Click += delegate { try { if (report.Text.Length != 0) Clipboard.SetText(report.Text); } catch (Exception ex) { MessageBox.Show(form, ex.Message); } };
            config.Click += delegate { OpenSettings(); };
            form.FormClosed += delegate { if (reportWindow == form) { reportWindow = null; reportBox = null; refreshButton = null; } };
            reportWindow = form; reportBox = report; refreshButton = refresh;
            ApplyDarkTheme(form); form.Show(); RefreshReport();
        }

        private static async void RefreshReport()
        {
            if (reportPending || reportBox == null) return;
            reportPending = true;
            int version = generation;
            NetworkSettings captured = settings;
            TextBox target = reportBox;
            Button button = refreshButton;
            button.Enabled = false; target.Text = "Проверяем…";
            try
            {
                string local = await Task.Run(new Func<string>(NetworkProbes.LocalReport));
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
                if (version == generation && target == reportBox && !target.IsDisposed)
                    target.Text = report + Environment.NewLine + Environment.NewLine + local;
            }
            catch (Exception ex)
            { if (target == reportBox && !target.IsDisposed && version == generation) target.Text = "Ошибка диагностики: " + ex.Message; }
            finally
            {
                reportPending = false;
                if (!button.IsDisposed) button.Enabled = true;
                if (reportBox != null && reportBox != target) RefreshReport();
            }
        }

        private static void AddTarget(TableLayoutPanel layout, int row, int index, string savedMode, string savedTarget,
            out ComboBox mode, out TextBox address)
        {
            ComboBox list = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            list.Items.AddRange(new object[] { "По умолчанию — ICMP + HTTPS", "ICMP — ping узла",
                "HTTPS — проверка сайта", "TCP — подключение к порту" });
            string selected = savedMode ?? "default";
            list.SelectedIndex = selected == "icmp" ? 1 : selected == "https" ? 2 : selected == "tcp" ? 3 : 0;
            TextBox input = new TextBox { Dock = DockStyle.Fill, Text = list.SelectedIndex == 0 ?
                NetworkChecks.DefaultHost(index) : savedTarget ?? "", Enabled = list.SelectedIndex != 0 };
            list.SelectedIndexChanged += delegate
            {
                string host = NetworkChecks.DefaultHost(index);
                input.Enabled = list.SelectedIndex != 0;
                if (list.SelectedIndex == 0) input.Text = host;
                else if (String.IsNullOrWhiteSpace(input.Text) || input.Text == host)
                    input.Text = list.SelectedIndex == 2 ? (index == 1 ? "https://1.1.1.1/cdn-cgi/trace" : "https://ya.ru/") :
                        list.SelectedIndex == 3 ? host + ":443" : host;
            };
            layout.Controls.Add(new Label { Text = "Проверка " + index, AutoSize = true },0,row);
            layout.Controls.Add(list,1,row); layout.Controls.Add(input,1,row+1);
            mode = list; address = input;
        }

        private static string Mode(ComboBox box)
        { return box.SelectedIndex == 1 ? "icmp" : box.SelectedIndex == 2 ? "https" : box.SelectedIndex == 3 ? "tcp" : null; }

        private static void OpenSettings()
        {
            if (settingsWindow != null && !settingsWindow.IsDisposed) { Activate(settingsWindow); return; }
            Form form = new Form { Text = "AppShelf — Настройки сети", Size = new Size(580,290),
                MinimumSize = new Size(460,290), StartPosition = FormStartPosition.CenterScreen };
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12),
                ColumnCount = 2, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,110));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            for (int i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute,32));
            CheckBox enabled = new CheckBox { Text = "Фоновые сетевые проверки", AutoSize = true, Checked = settings.ShowNetwork };
            layout.Controls.Add(enabled,0,0); layout.SetColumnSpan(enabled,2);
            ComboBox one,two; TextBox first,second;
            AddTarget(layout,1,1,settings.NetworkCheck1Mode,settings.NetworkCheck1Target,out one,out first);
            AddTarget(layout,3,2,settings.NetworkCheck2Mode,settings.NetworkCheck2Target,out two,out second);
            Button save = new Button { Text = "Сохранить", AutoSize = true, Dock = DockStyle.Right };
            layout.Controls.Add(save,1,5); form.Controls.Add(layout);
            save.Click += delegate
            {
                try
                {
                    NetworkSettings next = new NetworkSettings { ShowNetwork = enabled.Checked,
                        NetworkCheck1Mode = Mode(one), NetworkCheck2Mode = Mode(two) };
                    next.NetworkCheck1Target = next.NetworkCheck1Mode == null ? null : first.Text.Trim();
                    next.NetworkCheck2Target = next.NetworkCheck2Mode == null ? null : second.Text.Trim();
                    next.Save(dataDirectory);
                    settings = next; generation++; ResetResults(); UpdateMonitor();
                    if (reportBox != null) reportBox.Text = "Настройки изменились. Повторите проверку.";
                    form.Close();
                }
                catch (Exception ex) { MessageBox.Show(form, ex.Message, "Настройки сети", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            form.FormClosed += delegate { if (settingsWindow == form) settingsWindow = null; };
            settingsWindow = form; ApplyDarkTheme(form); form.Show();
        }
    }
}
