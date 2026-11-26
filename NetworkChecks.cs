// Existing AppShelf network checks extracted into the independent public plugin.
// Read-only diagnostics: no registry writes, process termination or adapter changes.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AppShelf.Network
{
    [DataContract]
    internal sealed class NetworkSettings
    {
        [DataMember(Name = "showNetwork")] public bool ShowNetwork = true;
        [DataMember(Name = "networkCheck1Mode", EmitDefaultValue = false)] public string NetworkCheck1Mode;
        [DataMember(Name = "networkCheck1Target", EmitDefaultValue = false)] public string NetworkCheck1Target;
        [DataMember(Name = "networkCheck2Mode", EmitDefaultValue = false)] public string NetworkCheck2Mode;
        [DataMember(Name = "networkCheck2Target", EmitDefaultValue = false)] public string NetworkCheck2Target;
        [OnDeserializing] private void Defaults(StreamingContext context) { ShowNetwork = true; }

        internal static NetworkSettings Load(string directory)
        {
            string path = Path.Combine(directory, "network-settings.json");
            if (!File.Exists(path)) return new NetworkSettings();
            using (FileStream stream = File.OpenRead(path))
            {
                if (stream.Length > 16384) throw new InvalidDataException("Network settings are too large.");
                NetworkSettings settings = (NetworkSettings)new DataContractJsonSerializer(typeof(NetworkSettings)).ReadObject(stream);
                if (settings == null) throw new InvalidDataException("Missing network settings.");
                settings.Validate();
                return settings;
            }
        }

        internal void Validate()
        {
            NetworkChecks.Target target; string error;
            if (!NetworkChecks.TryParse(NetworkCheck1Mode, NetworkCheck1Target, 1, out target, out error) ||
                !NetworkChecks.TryParse(NetworkCheck2Mode, NetworkCheck2Target, 2, out target, out error))
                throw new InvalidDataException(error);
        }

        internal void Save(string directory)
        {
            Validate();
            string path = Path.Combine(directory, "network-settings.json");
            string temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(NetworkSettings)).WriteObject(stream, this);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    internal static class NetworkChecks
    {
        internal sealed class Target
        {
            public string Mode;
            public string Value;
            public string Host;
            public int Port;
            public string Error;
        }

        public static string DefaultHost(int index)
        {
            return index == 1 ? "1.1.1.1" : "ya.ru";
        }

        internal static bool TryParse(string mode, string value, int index,
            out Target target, out string error)
        {
            target = null;
            error = null;
            if (index != 1 && index != 2)
            {
                error = "Недопустимый номер проверки.";
                return false;
            }
            mode = String.IsNullOrWhiteSpace(mode) ? "default" : mode.Trim().ToLowerInvariant();
            value = (value ?? "").Trim();
            if (mode == "default")
            {
                target = new Target { Mode = "default", Value = DefaultHost(index),
                    Host = DefaultHost(index), Port = 443 };
                return true;
            }
            if (mode != "icmp" && mode != "https" && mode != "tcp")
            {
                error = "Неизвестный тип проверки.";
                return false;
            }
            if (value.Length == 0 || value.Length > 512 ||
                value.IndexOfAny(new char[] { '\r', '\n', '\t', ' ' }) >= 0)
            {
                error = "Укажи один адрес без пробелов (не более 512 символов).";
                return false;
            }
            string host = value;
            int port = 443;
            if (mode == "https")
            {
                Uri uri;
                if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
                    uri.Fragment.Length != 0 ||
                    uri.Host.Length == 0 || Uri.CheckHostName(uri.Host) == UriHostNameType.Unknown)
                {
                    error = "Для HTTPS укажи полный адрес https://site.example/path без логина и фрагмента.";
                    return false;
                }
                host = uri.Host;
                port = uri.Port;
            }
            else if (mode == "tcp")
            {
                // Support host:port, IPv4:port and [IPv6]:port; never guess the port.
                int separator = value.LastIndexOf(':');
                if (separator <= 0 || separator == value.Length - 1 ||
                    !Int32.TryParse(value.Substring(separator + 1), out port) ||
                    port < 1 || port > 65535)
                {
                    error = "Для TCP укажи host:port, например server.company.local:443.";
                    return false;
                }
                host = value.Substring(0, separator);
                if (host.StartsWith("[", StringComparison.Ordinal) &&
                    host.EndsWith("]", StringComparison.Ordinal))
                    host = host.Substring(1, host.Length - 2);
            }
            if (mode != "https" &&
                (host.Length == 0 || host.IndexOfAny(new char[] {
                    '/', '\\', '@', '?', '#', '%', '[', ']', ' ' }) >= 0 ||
                 Uri.CheckHostName(host) == UriHostNameType.Unknown))
            {
                error = "Некорректный адрес узла. Укажи домен или IP без протокола.";
                return false;
            }
            target = new Target { Mode = mode, Value = value, Host = host, Port = port };
            return true;
        }

        internal static Target Resolve(NetworkSettings config, int index)
        {
            string mode = index == 1 ? config.NetworkCheck1Mode : config.NetworkCheck2Mode;
            string value = index == 1 ? config.NetworkCheck1Target : config.NetworkCheck2Target;
            Target target; string error;
            if (TryParse(mode, value, index, out target, out error)) return target;
            return new Target { Mode = mode, Value = value, Error = error };
        }

        internal static string Label(Target target, int index)
        {
            return target.Mode == "default" ? DefaultHost(index) : target.Value ?? ("Проверка " + index);
        }

        internal static async Task<string> ProbeAsync(Target target, int index)
        {
            if (!String.IsNullOrWhiteSpace(target.Error))
                return "Проверка " + index + ": " + target.Error;
            if (target.Mode == "default")
                return await NetworkProbes.ProbeEndpoint(target.Host,
                    index == 1 ? "https://1.1.1.1/cdn-cgi/trace" : "https://ya.ru/");
            long? duration;
            if (target.Mode == "icmp")
            {
                duration = await NetworkProbes.TryIcmp(target.Host);
                return "ICMP " + target.Host + ": " +
                    (duration.HasValue ? duration.Value + " мс" : "нет ответа (ICMP может блокироваться)");
            }
            if (target.Mode == "https")
            {
                duration = await NetworkProbes.TryHttps(target.Value);
                return "HTTPS " + target.Value + ": " +
                    (duration.HasValue ? duration.Value + " мс" : "нет ответа");
            }
            duration = await NetworkProbes.TryTcp(target.Host, target.Port);
            return "TCP " + target.Value + ": " +
                (duration.HasValue ? duration.Value + " мс" : "нет соединения");
        }
    }

    internal static class NetworkProbes
    {
        [DllImport("iphlpapi.dll")]
        private static extern uint GetBestInterface(uint destination, out uint interfaceIndex);
        internal static string RouteSummary()
        {
            // Reports Windows' selected interface, not the VPN app's toggle state.
            try
            {
                uint index;
                uint dest = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);
                if (GetBestInterface(dest, out index) != 0) return "Маршрут: неизвестен";
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        IPv4InterfaceProperties properties = nic.GetIPProperties().GetIPv4Properties();
                        if (properties != null && properties.Index == (int)index)
                        {
                            string shortName = nic.Name.Length > 24 ? nic.Name.Substring(0, 24) + "…" : nic.Name;
                            return "Маршрут: " + shortName;
                        }
                    }
                    catch (NetworkInformationException) { }
                }
                return "Маршрут: интерфейс #" + index;
            }
            catch { return "Маршрут: неизвестен"; }
        }

        internal static async Task<string> ProbeEndpoint(string host, string httpsUrl)
        {
            // Run tests simultaneously. HTTPS measures the HTTP request, not ICMP latency.
            Task<long?> icmp = TryIcmp(host);
            Task<long?> https = TryHttps(httpsUrl);
            await Task.WhenAll(icmp, https);
            string icmpText = icmp.Result.HasValue ? "ICMP " + icmp.Result.Value + " мс" : "ICMP —";
            string connectionText;
            if (https.Result.HasValue)
                connectionText = "HTTPS " + https.Result.Value + " мс";
            else
            {
                // An HTTPS failure may be due to TLS or HTTP policy, not routing.
                long? tcp = await TryTcp(host);
                connectionText = tcp.HasValue ? "TCP 443 " + (tcp.Value == 0 ? "<1" : tcp.Value.ToString()) +
                    " мс (HTTPS сбой)" : "HTTPS/TCP —";
            }
            return host + ": " + icmpText + " · " + connectionText;
        }

        private static Task<long?> TryTcp(string host)
        {
            return TryTcp(host, 443);
        }

        internal static async Task<long?> TryTcp(string host, int port)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    Task connect = client.ConnectAsync(host, port);
                    if (await Task.WhenAny(connect, Task.Delay(1800)) != connect) return null;
                    await connect;
                    watch.Stop();
                    return watch.ElapsedMilliseconds;
                }
            }
            catch { return null; }
        }

        internal static async Task<long?> TryIcmp(string host)
        {
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply result = await ping.SendPingAsync(host, 1500);
                    if (result.Status == IPStatus.Success) return result.RoundtripTime;
                }
            }
            catch { /* ICMP can be blocked: not a network failure by itself. */ }
            return null;
        }

        internal static async Task<long?> TryHttps(string url)
        {
            HttpWebRequest request = null;
            try
            {
                request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "HEAD";
                request.UserAgent = "AppShelf NetworkPlugin/1.0";
                request.Timeout = 3500;
                request.ReadWriteTimeout = 3500;
                request.AllowAutoRedirect = true;
                Stopwatch watch = Stopwatch.StartNew();
                Task<WebResponse> responseTask = request.GetResponseAsync();
                if (await Task.WhenAny(responseTask, Task.Delay(3500)) != responseTask)
                {
                    request.Abort();
                    return null;
                }
                try
                {
                    using (WebResponse response = await responseTask)
                    {
                        watch.Stop();
                        return watch.ElapsedMilliseconds;
                    }
                }
                catch (WebException ex)
                {
                    // A valid HTTP error (e.g. 403/405) still proves TLS/HTTP reachability.
                    HttpWebResponse response = ex.Response as HttpWebResponse;
                    if (response != null)
                    {
                        response.Dispose();
                        watch.Stop();
                        return watch.ElapsedMilliseconds;
                    }
                    return null;
                }
            }
            catch { return null; }
        }

        internal sealed class ProxySnapshot
        {
            public bool Enabled;
            public string Server;
            public string AutoConfigUrl;
        }

        public static ProxySnapshot ReadProxy()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false))
            {
                if (key == null) throw new InvalidOperationException("Раздел настроек прокси Windows не найден.");
                return new ProxySnapshot
                {
                    Enabled = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 0,
                    Server = Convert.ToString(key.GetValue("ProxyServer", "")) ?? "",
                    AutoConfigUrl = Convert.ToString(key.GetValue("AutoConfigURL", "")) ?? ""
                };
            }
        }

        public static string ProxyReport()
        {
            try
            {
                ProxySnapshot proxy = ReadProxy();
                StringBuilder result = new StringBuilder();
                result.AppendLine("Системный прокси WinINET (текущий пользователь):");
                result.AppendLine("  Ручной прокси: " + (proxy.Enabled ? "включён" : "выключен"));
                result.AppendLine("  Адрес: " + (String.IsNullOrWhiteSpace(proxy.Server) ? "не указан" : proxy.Server));
                result.AppendLine("  PAC-скрипт: " + (String.IsNullOrWhiteSpace(proxy.AutoConfigUrl) ? "не указан" : proxy.AutoConfigUrl));
                return result.ToString();
            }
            catch (Exception ex)
            {
                return "Не удалось проверить WinINET-прокси: " + ex.Message + Environment.NewLine;
            }
        }

        private static string DnsServersReport()
        {
            StringBuilder result = new StringBuilder();
            result.AppendLine("DNS-серверы активных сетевых адаптеров:");
            try
            {
                int count = 0;
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties properties = nic.GetIPProperties();
                    StringBuilder addresses = new StringBuilder();
                    foreach (IPAddress dns in properties.DnsAddresses)
                    {
                        if (addresses.Length > 0) addresses.Append(", ");
                        addresses.Append(dns);
                    }
                    if (addresses.Length == 0) continue;
                    result.AppendLine("  " + nic.Name + ": " + addresses);
                    count++;
                }
                if (count == 0) result.AppendLine("  Не обнаружены.");
            }
            catch (Exception ex) { result.AppendLine("  Ошибка: " + ex.Message); }
            return result.ToString();
        }

        private static async Task<string> CheckDnsAsync()
        {
            try
            {
                Task<IPAddress[]> resolve = Dns.GetHostAddressesAsync("ya.ru");
                if (await Task.WhenAny(resolve, Task.Delay(3500)) != resolve)
                    return "тайм-аут";
                IPAddress[] addresses = await resolve;
                return addresses.Length > 0 ? "работает" : "нет адресов";
            }
            catch (Exception ex) { return "ошибка: " + ex.GetType().Name; }
        }

        private static async Task<string> CheckHttpsAsync(bool direct)
        {
            HttpWebRequest request = null;
            try
            {
                request = (HttpWebRequest)WebRequest.Create("https://ya.ru/");
                request.Method = "HEAD";
                request.UserAgent = "AppShelf NetworkPlugin";
                request.Proxy = direct ? null : WebRequest.GetSystemWebProxy();
                request.AllowAutoRedirect = true;
                request.Timeout = 4000;
                request.ReadWriteTimeout = 4000;
                Stopwatch watch = Stopwatch.StartNew();
                Task<WebResponse> response = request.GetResponseAsync();
                if (await Task.WhenAny(response, Task.Delay(4500)) != response)
                {
                    request.Abort();
                    return "тайм-аут";
                }
                try
                {
                    using (WebResponse value = await response) { }
                }
                catch (WebException ex)
                {
                    // HTTP 403/405 means transport worked, not that the site is down.
                    if (!(ex.Response is HttpWebResponse)) return ex.Status.ToString();
                    ex.Response.Dispose();
                }
                return "доступен (" + watch.ElapsedMilliseconds + " мс)";
            }
            catch (WebException ex) { return ex.Status.ToString(); }
            catch (Exception ex) { return "ошибка: " + ex.GetType().Name; }
            finally { if (request != null) request.Abort(); }
        }

        internal sealed class ConnectivitySnapshot
        {
            public string Dns;
            public string DirectHttps;
            public string SystemHttps;

            public bool InternetAvailable
            {
                get {
                    return (DirectHttps ?? "").StartsWith("доступен", StringComparison.Ordinal) ||
                        (SystemHttps ?? "").StartsWith("доступен", StringComparison.Ordinal);
                }
            }

            public string ToReport()
            {
                return "Проверка реальной доступности:" + Environment.NewLine +
                    "  DNS ya.ru: " + Dns + Environment.NewLine +
                    "  HTTPS напрямую: " + DirectHttps + Environment.NewLine +
                    "  HTTPS через системный прокси: " + SystemHttps + Environment.NewLine +
                    "  Эти проверки не гарантируют доступность всех сайтов или работу браузера." + Environment.NewLine;
            }
        }

        internal static async Task<ConnectivitySnapshot> CheckConnectivityAsync()
        {
            Task<string> dns = CheckDnsAsync();
            Task<string> direct = CheckHttpsAsync(true);
            Task<string> system = CheckHttpsAsync(false);
            await Task.WhenAll(dns, direct, system);
            return new ConnectivitySnapshot
            {
                Dns = dns.Result, DirectHttps = direct.Result, SystemHttps = system.Result
            };
        }

        public static async Task<string> ConnectivityReportAsync()
        {
            return (await CheckConnectivityAsync()).ToReport();
        }

        internal static string ActiveInterfacesSummary()
        {
            StringBuilder result = new StringBuilder();
            try
            {
                int count = 0;
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (count++ >= 7)
                    {
                        result.AppendLine("… другие активные интерфейсы");
                        break;
                    }
                    result.Append(nic.Name).Append("  ·  ").Append(nic.NetworkInterfaceType);
                    try
                    {
                        IPInterfaceProperties props = nic.GetIPProperties();
                        foreach (UnicastIPAddressInformation address in props.UnicastAddresses)
                            if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                result.Append("  ·  ").Append(address.Address);
                                break;
                            }
                        foreach (IPAddress dns in props.DnsAddresses)
                            if (dns.AddressFamily == AddressFamily.InterNetwork)
                            {
                                result.Append("  ·  DNS ").Append(dns);
                                break;
                            }
                    }
                    catch (NetworkInformationException) { }
                    result.AppendLine();
                }
                if (count == 0) result.AppendLine("Нет активных интерфейсов.");
            }
            catch (Exception ex) { result.Append("Не удалось получить адаптеры: ").Append(ex.Message); }
            return result.ToString();
        }

        internal static string LocalReport()
        {
            return "Проверка: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                RouteSummary() + Environment.NewLine +
                "Маршрут показывает выбранный интерфейс Windows, а не состояние VPN." + Environment.NewLine +
                ActiveInterfacesSummary() + Environment.NewLine + ProxyReport() + Environment.NewLine +
                DnsServersReport() + Environment.NewLine +
                "WinHTTP, PAC и браузерные расширения могут использовать другие настройки прокси.";
        }
    }
}
