using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AppShelf;

namespace AppShelf.Network
{
    internal sealed class VpnSettingsPanel
    {
        internal readonly StackPanel Content = new StackPanel();
        private readonly CheckBox enabled, indicator;
        private readonly StackPanel options = new StackPanel();
        private readonly StackPanel adapters = new StackPanel(), processes = new StackPanel();
        private readonly TextBlock status;
        private readonly TextBox adapterSearch = new TextBox { Tag = "vpnAdapterSearch" },
            processSearch = new TextBox { Tag = "vpnProcessSearch" };
        private readonly TextBlock adapterCount = PluginTheme.Label("", 12, true),
            processCount = PluginTheme.Label("", 12, true);
        private bool discovered;
        private readonly IVpnSystem discoverySystem;
        private string[] adapterIds, processPaths;

        internal VpnSettingsPanel(NetworkSettings settings, IVpnSystem discoverySystem = null)
        {
            this.discoverySystem = discoverySystem ?? new WindowsVpnSystem();
            adapterIds = settings.VpnAdapterIds ?? new string[0];
            processPaths = settings.VpnProcessPaths ?? new string[0];
            TextBlock title = PluginTheme.Label("VPN / TUN", 18);
            title.Margin = new Thickness(0, 22, 0, 10);
            Content.Children.Add(title);
            enabled = new CheckBox { Content = "Включить модуль VPN", IsChecked = settings.VpnEnabled, Tag = "vpnEnabled" };
            Content.Children.Add(enabled);
            Content.Children.Add(PluginTheme.Label("По умолчанию выключен. Нет автоматического выбора клиента или восстановления. " +
                "Отключение модуля не меняет Windows и не отменяет ранее выполненные системные действия.", 12, true));
            Content.Children.Add(options);
            indicator = new CheckBox { Content = new TextBlock { Text = "Предлагать четвёртый индикатор VPN / TUN", TextWrapping = TextWrapping.Wrap }, IsChecked = settings.ShowVpnIndicator,
                Tag = "vpnIndicator", Margin = new Thickness(0, 12, 0, 8) };
            options.Children.Add(indicator);
            options.Children.Add(PluginTheme.Label("После сохранения добавьте индикатор штатным выбором индикаторов AppShelf. " +
                "Он показывает интерфейс и выбор маршрута Windows, а не доступность VPN.", 12, true));
            Button refresh = PluginTheme.Button("Обновить список адаптеров и программ");
            refresh.Tag = "vpnDiscover"; refresh.Margin = new Thickness(0, 12, 0, 8);
            options.Children.Add(refresh);
            status = PluginTheme.Label("", 12, true); options.Children.Add(status);
            options.Children.Add(PluginTheme.Label("Адаптеры VPN — только явно выбранные идентификаторы", 13));
            AddSearch(options, adapterSearch, adapterCount, adapters, "Поиск адаптеров", "vpnAdapterCount");
            options.Children.Add(PluginTheme.Label("Программы VPN — точный путь EXE, не маска имени", 13));
            AddSearch(options, processSearch, processCount, processes, "Поиск программ", "vpnProcessCount");
            options.Children.Add(PluginTheme.Label("Выберите только свои VPN-объекты. Отключение физического адаптера прервёт сеть. " +
                "Недоступные для чтения и системные процессы не предлагаются. Каждый шаг восстановления подтверждается отдельно.", 12, true));
            Populate(adapters, adapterIds, adapterIds, false);
            Populate(processes, processPaths, processPaths, true);
            RefreshFilters();
            options.IsEnabled = settings.VpnEnabled;
            enabled.Checked += delegate { options.IsEnabled = true; if (!discovered) Discover(); };
            enabled.Unchecked += delegate { options.IsEnabled = false; };
            refresh.Click += delegate { Discover(); };
            if (settings.VpnEnabled) Discover();
        }
        private static string[] Checked(StackPanel list)
        {
            List<string> values = new List<string>();
            foreach (CheckBox item in list.Children) if (item.IsChecked == true) values.Add((string)item.Tag);
            return values.ToArray();
        }
        private void AddSearch(StackPanel parent, TextBox search, TextBlock count, StackPanel list, string title, string countTag)
        {
            TextBlock label = PluginTheme.Label(title, 12, true);
            label.Margin = new Thickness(0, 8, 0, 4);
            parent.Children.Add(label);
            DockPanel row = new DockPanel { LastChildFill = true };
            Button clear = PluginTheme.Button("Очистить");
            clear.Margin = new Thickness(8, 0, 0, 0);
            clear.Tag = (string)search.Tag + "Clear";
            DockPanel.SetDock(clear, Dock.Right);
            row.Children.Add(clear);
            search.MinHeight = 32;
            System.Windows.Automation.AutomationProperties.SetName(search, title);
            row.Children.Add(search);
            parent.Children.Add(row);
            count.Tag = countTag;
            count.Margin = new Thickness(0, 5, 0, 5);
            parent.Children.Add(count);
            parent.Children.Add(list);
            search.TextChanged += delegate { RefreshFilters(); };
            clear.Click += delegate { search.Clear(); search.Focus(); };
        }

        // Keep every checkbox in the logical list, even when hidden by a filter.
        // Checked() therefore includes hidden choices on save and discovery refresh.
        private void Populate(StackPanel list, string[] values, string[] selected, bool programs)
        {
            list.Children.Clear();
            foreach (string value in values)
            {
                FrameworkElement content;
                if (programs)
                {
                    StackPanel details = new StackPanel();
                    TextBlock name = PluginTheme.Label(System.IO.Path.GetFileName(value), 13);
                    name.FontWeight = FontWeights.SemiBold;
                    details.Children.Add(name);
                    details.Children.Add(PluginTheme.Label(value, 11, true));
                    content = details;
                }
                else content = PluginTheme.Label(value, 12);
                CheckBox box = new CheckBox { Content = content, Tag = value,
                    IsChecked = VpnModule.Selected(selected, value), Margin = new Thickness(0, 5, 0, 5) };
                box.Checked += delegate { RefreshFilters(); };
                box.Unchecked += delegate { RefreshFilters(); };
                list.Children.Add(box);
            }
        }

        private static void Filter(StackPanel list, string query, TextBlock count)
        {
            int found = 0, selected = 0;
            query = (query ?? "").Trim();
            foreach (CheckBox box in list.Children)
            {
                string label = box.Content is TextBlock ? ((TextBlock)box.Content).Text : "";
                bool match = ((string)box.Tag).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                box.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                if (match) found++;
                if (box.IsChecked == true) selected++;
            }
            count.Text = "Найдено: " + found + " из " + list.Children.Count + " · Выбрано: " + selected +
                (found == 0 ? " · Нет результатов" : "");
        }

        private void RefreshFilters()
        {
            Filter(adapters, adapterSearch.Text, adapterCount);
            Filter(processes, processSearch.Text, processCount);
        }

        private void Discover()
        {
            if (enabled.IsChecked != true) return;
            adapterIds = Checked(adapters); processPaths = Checked(processes);
            try
            {
                VpnAdapter[] detectedAdapters = discoverySystem.Adapters();
                VpnProcess[] detectedProcesses = discoverySystem.Processes();
                List<string> ids = new List<string>(adapterIds);
                foreach (VpnAdapter adapter in detectedAdapters) if (!VpnModule.Selected(ids.ToArray(), adapter.Id)) ids.Add(adapter.Id);
                List<string> paths = new List<string>(processPaths);
                foreach (VpnProcess process in detectedProcesses) if (!VpnModule.Selected(paths.ToArray(), process.Path)) paths.Add(process.Path);
                ids.Sort(StringComparer.OrdinalIgnoreCase); paths.Sort(StringComparer.OrdinalIgnoreCase);
                Populate(adapters, ids.ToArray(), adapterIds, false); Populate(processes, paths.ToArray(), processPaths, true);
                foreach (CheckBox box in adapters.Children)
                {
                    VpnAdapter match = Array.Find(detectedAdapters, delegate(VpnAdapter a) { return String.Equals(a.Id, (string)box.Tag, StringComparison.OrdinalIgnoreCase); });
                    ((TextBlock)box.Content).Text = match == null ? (string)box.Tag + " · не найден" : match.ToString();
                }
                foreach (CheckBox box in processes.Children)
                    if (!Array.Exists(detectedProcesses, delegate(VpnProcess p) { return String.Equals(p.Path, (string)box.Tag, StringComparison.OrdinalIgnoreCase); }))
                        ((StackPanel)box.Content).Children.Add(PluginTheme.Label("Не запущена / недоступна", 11, true));
                RefreshFilters();
                status.Text = "Обнаружено адаптеров: " + detectedAdapters.Length + ". Доступных процессов: " + detectedProcesses.Length +
                    ". Ничего не выбирается автоматически.";
                discovered = true;
            }
            catch (Exception ex) { status.Text = "Не удалось обновить списки: " + ex.Message; }
        }
        internal void Apply(NetworkSettings settings)
        {
            settings.VpnEnabled = enabled.IsChecked == true;
            settings.ShowVpnIndicator = indicator.IsChecked == true;
            settings.VpnAdapterIds = Checked(adapters);
            settings.VpnProcessPaths = Checked(processes);
        }
    }

    internal static partial class NetworkPlugin
    {
        private static string vpnResult;
        private static bool recoveryBusy;
        private static Button disableButton, recoverButton, restoreButton, reportConfigButton;
        private static readonly CancellationTokenSource recoveryCancellation = new CancellationTokenSource();
        private static readonly VpnRecovery recovery = new VpnRecovery(new WindowsVpnSystem(), ConfirmRecovery);
        private static bool ConfirmRecovery(string message)
        {
            return reportWindow != null && MessageBox.Show(reportWindow, message, "NetworkPlugin — VPN / восстановление",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        }
        private static void AddRecoveryControls(Window form, WrapPanel commands, Button config)
        {
            reportConfigButton = config;
            disableButton = PluginTheme.Button("Отключить выбранные адаптеры…");
            recoverButton = PluginTheme.Button("Восстановить сеть пошагово…");
            restoreButton = PluginTheme.Button("Вернуть прокси…");
            disableButton.Tag = "vpnDisable"; recoverButton.Tag = "vpnRecover"; restoreButton.Tag = "vpnRestore";
            commands.Children.Add(disableButton); commands.Children.Add(recoverButton); commands.Children.Add(restoreButton);
            disableButton.Click += async delegate { await RunRecoveryAsync(0); };
            recoverButton.Click += async delegate { await RunRecoveryAsync(1); };
            restoreButton.Click += async delegate { await RunRecoveryAsync(2); };
            UpdateRecoveryControls();
        }
        private static void UpdateRecoveryControls()
        {
            if (recoverButton == null || settings == null) return;
            bool enabled = settings.VpnEnabled && !recoveryBusy && !reportPending;
            recoverButton.Visibility = disableButton.Visibility = settings.VpnEnabled ? Visibility.Visible : Visibility.Collapsed;
            restoreButton.Visibility = settings.VpnEnabled && recovery.DisabledProxy != null ? Visibility.Visible : Visibility.Collapsed;
            recoverButton.IsEnabled = restoreButton.IsEnabled = enabled;
            disableButton.IsEnabled = enabled && settings.VpnAdapterIds != null && settings.VpnAdapterIds.Length > 0;
            reportConfigButton.IsEnabled = !recoveryBusy;
        }
        private static async Task RunRecoveryAsync(int action)
        {
            if (recoveryBusy || reportPending || !settings.VpnEnabled || reportWindow == null) return;
            if (settingsWindow != null)
            {
                MessageBox.Show(reportWindow, "Сначала закройте настройки.", "NetworkPlugin");
                return;
            }
            recoveryBusy = true; UpdateRecoveryControls(); refreshButton.IsEnabled = false;
            string result = "";
            recovery.ResetLog();
            try
            {
                if (action == 0) result = await recovery.DisableAsync(settings, recoveryCancellation.Token);
                else if (action == 1) result = await recovery.RunAsync(settings, recoveryCancellation.Token);
                else recovery.Restore(settings, recoveryCancellation.Token);
            }
            catch (Win32Exception ex) { result = recovery.LastActions + (ex.NativeErrorCode == 1223 ? "\nЗапрос UAC отменён." : "\nОшибка Windows: " + ex.Message); }
            catch (OperationCanceledException) { result = recovery.LastActions + "\nОперация отменена."; }
            catch (Exception ex) { result = recovery.LastActions + "\nВосстановление остановлено: " + ex.Message; }
            finally
            {
                recoveryBusy = false;
                if (refreshButton != null) refreshButton.IsEnabled = true;
                UpdateRecoveryControls();
            }
            if (!recoveryCancellation.IsCancellationRequested && reportWindow != null)
            {
                MessageBox.Show(reportWindow, String.IsNullOrWhiteSpace(result) ? "Действие завершено; проверьте отчёт." : result,
                    "NetworkPlugin — результат", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshReport();
            }
        }
    }
}
