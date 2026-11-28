// VPN route selection and guarded recovery adapted from AppShelf (GPL-3.0).
// Generalized on 2026-11-28: no client-specific names or automatic recovery targets.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AppShelf.Network
{
    internal sealed class VpnAdapter
    {
        public string Id, Name;
        public string[] Ipv4;
        public int Index;
        public OperationalStatus State;
        public override string ToString() { return Name + " · " + State + " · " + Id; }
    }

    internal sealed class VpnProcess
    {
        public string Path;
        public int Id;
        public long Started;
        public override string ToString() { return System.IO.Path.GetFileName(Path) + " · " + Path; }
    }

    // All mutable OS boundaries are replaceable in tests, not selected by a runtime flag.
    internal interface IVpnSystem
    {
        VpnAdapter[] Adapters();
        VpnProcess[] Processes();
        NetworkProbes.ProxySnapshot Proxy();
        Task StopAsync(VpnProcess process);
        Task<int> DisableAsync(VpnAdapter adapter);
        void SetProxy(bool enabled, string expectedServer);
        Task<NetworkProbes.ConnectivitySnapshot> ConnectivityAsync();
        Task<int> FlushDnsAsync();
    }

    internal static class VpnModule
    {
        internal static bool Selected(string[] selection, string value)
        {
            return selection != null && Array.Exists(selection, delegate(string item) {
                Guid first, second;
                return String.Equals(item, value, StringComparison.OrdinalIgnoreCase) ||
                    (Guid.TryParse(item, out first) && Guid.TryParse(value, out second) && first == second);
            });
        }

        internal static void ValidateSelection(string[] adapters, string[] paths)
        {
            if ((adapters != null && adapters.Length > 16) || (paths != null && paths.Length > 16))
                throw new InvalidDataException("Можно выбрать не более 16 адаптеров и 16 программ VPN.");
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (adapters != null) foreach (string id in adapters)
            {
                Guid parsed;
                if (!Guid.TryParse(id, out parsed) || !seen.Add(parsed.ToString("D")))
                    throw new InvalidDataException("Некорректный или повторный идентификатор адаптера.");
            }
            seen.Clear();
            if (paths != null) foreach (string path in paths)
            {
                if (String.IsNullOrWhiteSpace(path) || path.Length > 512 || !Path.IsPathRooted(path) ||
                    path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0 ||
                    !String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase) || !seen.Add(path))
                    throw new InvalidDataException("Выберите полный локальный путь EXE без повторов.");
            }
        }

        // Pure logic: an Up interface is not proof that it carries traffic.
        internal static string DescribeTun(VpnAdapter[] adapters, int[] routes)
        {
            int active = 0, matched = 0;
            bool unknown = false;
            foreach (VpnAdapter adapter in adapters)
            {
                unknown |= adapter.State == OperationalStatus.Unknown;
                if (adapter.State != OperationalStatus.Up) continue;
                active++;
                if (adapter.Index >= 0 && Array.IndexOf(routes, adapter.Index) >= 0) matched++;
            }
            if (matched == 1) return "TUN: выбран маршрут";
            if (matched > 1 || active > 1) return "TUN: несколько адаптеров";
            if (active == 1) return "TUN: маршрут не подтверждён";
            if (adapters.Length == 0) return "TUN отсутствует";
            return unknown ? "Состояние TUN неизвестно" : "TUN неактивен";
        }

        internal static string ReadStatus(NetworkSettings settings)
        {
            if (!settings.VpnEnabled) return "VPN-модуль выключен";
            if (settings.VpnAdapterIds == null || settings.VpnAdapterIds.Length == 0) return "Выберите адаптеры VPN";
            VpnAdapter[] adapters = Array.FindAll(new WindowsVpnSystem().Adapters(),
                delegate(VpnAdapter a) { return Selected(settings.VpnAdapterIds, a.Id); });
            return DescribeTun(adapters, new[] { BestIndex("1.1.1.1"), BestIndex("8.8.8.8") });
        }

        [DllImport("iphlpapi.dll")] private static extern uint GetBestInterface(uint destination, out uint index);
        [DllImport("iphlpapi.dll")] private static extern uint GetIpForwardTable(IntPtr table, ref int size, bool order);
        [StructLayout(LayoutKind.Sequential)]
        private struct Route
        {
            public uint Destination, Mask, Policy, NextHop, Index, Type, Protocol, Age, NextHopAs;
            public uint Metric1, Metric2, Metric3, Metric4, Metric5;
        }
        private static int BestIndex(string address)
        {
            uint index;
            return GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse(address).GetAddressBytes(), 0), out index) == 0 &&
                index <= Int32.MaxValue ? (int)index : -1;
        }
        private static string Ip(uint value) { return new IPAddress(BitConverter.GetBytes(value)).ToString(); }
        private static int Prefix(uint mask)
        {
            int count = 0;
            foreach (byte b in BitConverter.GetBytes(mask))
                for (int bit = 0; bit < 8; bit++) if ((b & (1 << bit)) != 0) count++;
            return count;
        }
        private static string Routes(VpnAdapter[] adapters)
        {
            int length = 0;
            uint code = GetIpForwardTable(IntPtr.Zero, ref length, true);
            if (code != 122 || length < 4 || length > 16 * 1024 * 1024)
                throw new Win32Exception((int)code, "Не удалось прочитать размер таблицы IPv4.");
            // Retry boundedly when Windows grows the routing table between calls.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int allocated = length;
                IntPtr buffer = Marshal.AllocHGlobal(allocated);
                try
                {
                    code = GetIpForwardTable(buffer, ref length, true);
                    if (code == 122 && length > allocated && length <= 16 * 1024 * 1024) continue;
                    if (code != 0) throw new Win32Exception((int)code);
                    int count = Marshal.ReadInt32(buffer), size = Marshal.SizeOf(typeof(Route));
                    if (count < 0 || length > allocated || count > (length - 4) / size)
                        throw new InvalidDataException("Некорректный размер таблицы IPv4.");
                    StringBuilder text = new StringBuilder();
                    int found = 0;
                    for (int i = 0; i < count; i++)
                    {
                        Route row = (Route)Marshal.PtrToStructure(IntPtr.Add(buffer, 4 + i * size), typeof(Route));
                        if (!Array.Exists(adapters, delegate(VpnAdapter a) { return a.Index >= 0 && (uint)a.Index == row.Index; })) continue;
                        found++;
                        text.AppendLine("  " + Ip(row.Destination) + "/" + Prefix(row.Mask) + " · next hop " +
                            Ip(row.NextHop) + " · метрика " + row.Metric1 + " · интерфейс #" + row.Index);
                    }
                    return "Маршрутов IPv4 выбранных адаптеров: " + found + Environment.NewLine + text;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new InvalidOperationException("Таблица IPv4 меняется. Повторите проверку.");
        }

        // Only the VPN-specific appendix. Common DNS, proxy and connectivity appear once in the main report.
        internal static string Report(NetworkSettings settings)
        {
            if (!settings.VpnEnabled) return "";
            WindowsVpnSystem system = new WindowsVpnSystem();
            VpnAdapter[] adapters = Array.FindAll(system.Adapters(), delegate(VpnAdapter a) { return Selected(settings.VpnAdapterIds, a.Id); });
            int[] sampled = new[] { BestIndex("1.1.1.1"), BestIndex("8.8.8.8") };
            StringBuilder text = new StringBuilder("VPN / TUN" + Environment.NewLine);
            text.AppendLine(settings.VpnAdapterIds == null || settings.VpnAdapterIds.Length == 0 ?
                "Адаптеры не выбраны." : DescribeTun(adapters, sampled));
            text.AppendLine("Выбор Windows (индексы IPv4): 1.1.1.1 → #" + sampled[0] + "; 8.8.8.8 → #" + sampled[1] + ". -1 означает ошибку чтения.");
            if (settings.VpnAdapterIds != null) foreach (string id in settings.VpnAdapterIds)
            {
                VpnAdapter adapter = Array.Find(adapters, delegate(VpnAdapter a) { return String.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase); });
                text.AppendLine(adapter == null ? "  Не найден: " + id : "  " + adapter + " · интерфейс #" + adapter.Index);
                if (adapter != null && adapter.Ipv4 != null) foreach (string ip in adapter.Ipv4) text.AppendLine("    IPv4: " + ip);
            }
            try { text.Append(Routes(adapters)); }
            catch (Exception ex) { text.AppendLine("Ошибка таблицы IPv4: " + ex.Message); }
            VpnProcess[] processes = system.Processes();
            if (settings.VpnProcessPaths != null) foreach (string path in settings.VpnProcessPaths)
                text.AppendLine("  Программа: " + path + " · процессов: " +
                    Array.FindAll(processes, delegate(VpnProcess p) { return String.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase); }).Length);
            text.AppendLine("Активный TUN нормален при включённом VPN. Маршруты не подтверждают работоспособность VPN.");
            text.AppendLine("Выбор Windows проверен для 1.1.1.1 и 8.8.8.8 только по IPv4; раздельная маршрутизация и IPv6 могут отличаться.");
            return text.ToString();
        }

        internal static bool IsLoopbackProxy(string configured)
        {
            if (String.IsNullOrWhiteSpace(configured)) return false;
            int valid = 0;
            foreach (string entry in configured.Split(';'))
            {
                string part = entry.Trim();
                if (part.Length == 0) continue;
                int equals = part.IndexOf('=');
                if (equals >= 0) part = part.Substring(equals + 1).Trim();
                Uri uri;
                if (part.Length == 0 || !Uri.TryCreate(part.Contains("://") ? part : "http://" + part, UriKind.Absolute, out uri) ||
                    uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
                IPAddress ip;
                if (!String.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                    (!IPAddress.TryParse(uri.Host, out ip) || !IPAddress.IsLoopback(ip))) return false;
                valid++;
            }
            return valid > 0;
        }
        internal static void CheckProxy(NetworkProbes.ProxySnapshot current, bool enabled, string expected)
        {
            if (!String.Equals(current.Server, expected, StringComparison.Ordinal) || !IsLoopbackProxy(current.Server) ||
                !String.IsNullOrWhiteSpace(current.AutoConfigUrl) || current.Enabled == enabled)
                throw new InvalidOperationException("Прокси изменился, не является локальным либо использует PAC. Действие отменено.");
        }
    }

    internal sealed class WindowsVpnSystem : IVpnSystem
    {
        public VpnAdapter[] Adapters()
        {
            List<VpnAdapter> found = new List<VpnAdapter>();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                int index = -1;
                List<string> addresses = new List<string>();
                try
                {
                    IPInterfaceProperties properties = nic.GetIPProperties();
                    IPv4InterfaceProperties props = properties.GetIPv4Properties();
                    if (props != null) index = props.Index;
                    foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                        if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) addresses.Add(address.Address.ToString());
                }
                catch (NetworkInformationException) { }
                found.Add(new VpnAdapter { Id = nic.Id, Name = nic.Name, Index = index, State = nic.OperationalStatus, Ipv4 = addresses.ToArray() });
            }
            return found.ToArray();
        }
        internal static bool SafeProcessPath(string path)
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
            return !String.IsNullOrEmpty(path) && !path.StartsWith(windows, StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(Path.GetFileName(path), "AppShelf.exe", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(Path.GetFileName(path), "NetworkPlugin.exe", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(path, typeof(WindowsVpnSystem).Assembly.Location, StringComparison.OrdinalIgnoreCase);
        }
        public VpnProcess[] Processes()
        {
            List<VpnProcess> found = new List<VpnProcess>();
            int own; using (Process self = Process.GetCurrentProcess()) own = self.Id;
            foreach (Process process in Process.GetProcesses())
            {
                using (process) try
                {
                    if (process.Id <= 4 || process.Id == own) continue;
                    ProcessModule module = process.MainModule;
                    if (module == null) continue;
                    string path = module.FileName;
                    if (SafeProcessPath(path)) found.Add(new VpnProcess { Id = process.Id, Path = path, Started = process.StartTime.ToUniversalTime().Ticks });
                }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
            }
            return found.ToArray();
        }
        public NetworkProbes.ProxySnapshot Proxy() { return NetworkProbes.ReadProxy(); }
        internal static bool SameProcess(VpnProcess expected, VpnProcess actual)
        {
            return expected.Id > 4 && expected.Id == actual.Id && expected.Started == actual.Started &&
                String.Equals(expected.Path, actual.Path, StringComparison.OrdinalIgnoreCase) && SafeProcessPath(actual.Path);
        }
        public async Task StopAsync(VpnProcess expected)
        {
            using (Process process = Process.GetProcessById(expected.Id))
            using (Process self = Process.GetCurrentProcess())
            {
                VpnProcess actual = new VpnProcess { Id = process.Id, Path = process.MainModule.FileName, Started = process.StartTime.ToUniversalTime().Ticks };
                if (self.Id == actual.Id || !SameProcess(expected, actual))
                    throw new InvalidOperationException("Процесс изменился. Завершение отменено.");
                process.Kill();
                bool exited = await Task.Run(delegate { return process.WaitForExit(3000); });
                if (!exited) throw new InvalidOperationException("Процесс не завершился за 3 секунды.");
            }
        }
        internal static bool SameAdapter(VpnAdapter expected, VpnAdapter actual)
        {
            return actual != null && String.Equals(expected.Id, actual.Id, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(expected.Name, actual.Name, StringComparison.Ordinal) && expected.Index == actual.Index &&
                actual.State == OperationalStatus.Up;
        }
        internal static string DisableArguments(VpnAdapter adapter)
        {
            if (String.IsNullOrWhiteSpace(adapter.Name) || adapter.Name.IndexOfAny(new[] { '"', '\\', '\r', '\n', '\0' }) >= 0)
                throw new InvalidOperationException("Недопустимое имя адаптера.");
            return "interface set interface name=\"" + adapter.Name + "\" admin=disabled";
        }
        public Task<int> DisableAsync(VpnAdapter expected)
        {
            VpnAdapter current = Array.Find(Adapters(), delegate(VpnAdapter a) { return String.Equals(a.Id, expected.Id, StringComparison.OrdinalIgnoreCase); });
            if (!SameAdapter(expected, current)) throw new InvalidOperationException("Адаптер изменился. Отключение отменено.");
            return ElevatedAsync("netsh.exe", DisableArguments(current));
        }
        private static async Task<int> ElevatedAsync(string file, string arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), file),
                Arguments = arguments, UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using (Process process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Не удалось запустить системную команду.");
                await Task.Run(delegate { process.WaitForExit(); });
                return process.ExitCode;
            }
        }
        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);
        public void SetProxy(bool enabled, string expectedServer)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
            {
                if (key == null) throw new InvalidOperationException("Нет доступа к настройкам прокси.");
                VpnModule.CheckProxy(Proxy(), enabled, expectedServer);
                key.SetValue("ProxyEnable", enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }
        public Task<NetworkProbes.ConnectivitySnapshot> ConnectivityAsync() { return NetworkProbes.CheckConnectivityAsync(); }
        public Task<int> FlushDnsAsync() { return ElevatedAsync("ipconfig.exe", "/flushdns"); }
    }

    internal sealed class VpnRecovery
    {
        private readonly IVpnSystem system;
        private readonly Func<string, bool> confirm;
        internal string DisabledProxy { get; private set; }
        internal string LastActions { get; private set; }
        internal void ResetLog() { LastActions = ""; }
        internal VpnRecovery(IVpnSystem system, Func<string, bool> confirm) { this.system = system; this.confirm = confirm; }
        private static void Check(NetworkSettings settings, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (!settings.VpnEnabled) throw new InvalidOperationException("VPN-модуль выключен.");
        }
        internal async Task<string> DisableAsync(NetworkSettings settings, CancellationToken cancel)
        {
            Check(settings, cancel);
            StringBuilder log = new StringBuilder();
            VpnAdapter[] adapters = system.Adapters();
            foreach (VpnAdapter adapter in adapters)
            {
                if (!VpnModule.Selected(settings.VpnAdapterIds, adapter.Id) || adapter.State != OperationalStatus.Up) continue;
                Check(settings, cancel);
                if (!confirm("Отключить выбранный адаптер «" + adapter.Name + "» (" + adapter.Id + ")?\n\n" +
                    "Это может прервать VPN, интернет и RDP. Требуется UAC. Клиент может создать адаптер снова.")) continue;
                Check(settings, cancel);
                int code = await system.DisableAsync(adapter);
                log.AppendLine(adapter.Name + ": " + (code == 0 ? "отключён." : "код Windows " + code));
                LastActions += adapter.Name + ": код Windows " + code + Environment.NewLine;
                if (code != 0) throw new InvalidOperationException("Windows не отключила адаптер. Следующие шаги отменены.");
            }
            return log.ToString();
        }
        internal async Task<string> RunAsync(NetworkSettings settings, CancellationToken cancel)
        {
            ResetLog();
            Check(settings, cancel);
            if (!confirm("Запустить пошаговое восстановление сети?\n\nКаждое изменение требует отдельного подтверждения. " +
                "VPN и RDP могут отключиться. Рекомендуется сначала скопировать отчёт.")) return "Никакие изменения не выполнялись.";
            StringBuilder log = new StringBuilder();
            Check(settings, cancel);
            foreach (VpnProcess process in system.Processes())
            {
                if (!VpnModule.Selected(settings.VpnProcessPaths, process.Path)) continue;
                Check(settings, cancel);
                if (!confirm("Принудительно завершить выбранный процесс?\n" + process.Path + "\nPID " + process.Id +
                    "\n\nНесохранённое состояние будет потеряно. Другие пути EXE не затрагиваются.")) continue;
                Check(settings, cancel);
                await system.StopAsync(process);
                log.AppendLine("Завершён PID " + process.Id + ": " + process.Path);
                LastActions += "Завершён PID " + process.Id + ": " + process.Path + Environment.NewLine;
            }
            log.Append(await DisableAsync(settings, cancel));
            Check(settings, cancel);
            NetworkProbes.ProxySnapshot proxy = system.Proxy();
            if (proxy.Enabled && VpnModule.IsLoopbackProxy(proxy.Server) && String.IsNullOrWhiteSpace(proxy.AutoConfigUrl))
            {
                if (confirm("Временно выключить ручной прокси WinINET?\n" + proxy.Server +
                    "\n\nПринадлежность прокси VPN-клиенту не подтверждена. Его может использовать другая программа. " +
                    "Адрес сохранится. Кнопка «Вернуть прокси» действует в течение сеанса плагина."))
                {
                    Check(settings, cancel);
                    system.SetProxy(false, proxy.Server);
                    DisabledProxy = proxy.Server;
                    log.AppendLine("Локальный прокси WinINET выключен по подтверждению.");
                    LastActions += "Локальный прокси WinINET выключен по подтверждению." + Environment.NewLine;
                }
            }
            else if (proxy.Enabled || !String.IsNullOrWhiteSpace(proxy.AutoConfigUrl))
                log.AppendLine("Удалённый, смешанный прокси или PAC не изменялся.");
            Check(settings, cancel);
            NetworkProbes.ConnectivitySnapshot connectivity = await system.ConnectivityAsync();
            log.AppendLine(connectivity.ToReport());
            Check(settings, cancel);
            if (connectivity.Dns != "работает" && confirm("Проверка DNS не прошла. Очистить только кеш DNS Windows?\n\n" +
                "Команда ipconfig /flushdns требует UAC. Адреса DNS-серверов не меняются."))
            {
                Check(settings, cancel);
                int code = await system.FlushDnsAsync();
                log.AppendLine(code == 0 ? "Кеш DNS очищен." : "Очистка DNS: код Windows " + code);
                LastActions += "Очистка DNS: код Windows " + code + Environment.NewLine;
                if (code != 0) throw new InvalidOperationException("Windows не очистила кеш DNS. Восстановление остановлено.");
            }
            // Never infer successful recovery from exit codes alone.
            Check(settings, cancel);
            log.AppendLine("Итоговая проверка:");
            log.AppendLine((await system.ConnectivityAsync()).ToReport());
            return log.Length == 0 ? "Никакие изменения не выполнялись." : log.ToString();
        }
        internal void Restore(NetworkSettings settings, CancellationToken cancel)
        {
            Check(settings, cancel);
            if (DisabledProxy == null || !confirm("Вернуть ручной прокси WinINET (" + DisabledProxy +
                ")?\n\nЭто может вернуть зависимость браузера от локального прокси.")) return;
            Check(settings, cancel);
            system.SetProxy(true, DisabledProxy);
            DisabledProxy = null;
        }
    }
}
