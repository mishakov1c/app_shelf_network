// Language-independent plugin RPC over a child process's redirected binary streams.
// No listener, network connection, assembly loading or process launch in this layer.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AppShelf
{
    [DataContract]
    internal sealed class PluginCommand
    {
        [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; }
        [DataMember(Name = "label", IsRequired = true)] public string Label { get; set; }
    }

    [DataContract]
    internal sealed class PluginNotice
    {
        [DataMember(Name = "title", IsRequired = true)] public string Title { get; set; }
        [DataMember(Name = "message", IsRequired = true)] public string Message { get; set; }
    }

    // Declarative snapshots only: the host owns layout and renders plain text.
    [DataContract]
    internal sealed class PluginWidget
    {
        [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; }
        [DataMember(Name = "title", IsRequired = true)] public string Title { get; set; }
        [DataMember(Name = "kind", IsRequired = true)] public string Kind { get; set; }
        [DataMember(Name = "elements", IsRequired = true)] public PluginWidgetElement[] Elements { get; set; }
        [DataMember(Name = "layouts", EmitDefaultValue = false)] public PluginWidgetLayout[] Layouts { get; set; }
        [DataMember(Name = "defaultSize", EmitDefaultValue = false)] public string DefaultSize { get; set; }

        internal string PreferredSize { get { return DefaultSize ?? (Kind == "tile" ? "quarter" : "full"); } }
        internal bool SupportsSize(string size)
        {
            return Kind != "indicator" && IsGroupSize(size) &&
                (Layouts == null || Array.Exists(Layouts, delegate(PluginWidgetLayout layout) { return layout.Size == size; }));
        }
        internal static bool IsGroupSize(string size)
        {
            return size == "full" || size == "half-horizontal" || size == "half-vertical" || size == "quarter";
        }
        internal PluginWidget ForSize(string size)
        {
            if (!SupportsSize(size)) throw new ArgumentException("Unsupported widget size.", "size");
            PluginWidgetLayout layout = Layouts == null ? null : Array.Find(Layouts,
                delegate(PluginWidgetLayout item) { return item.Size == size; });
            return new PluginWidget { Id = Id, Title = Title, Kind = size == "quarter" ? "tile" : "card",
                Elements = layout == null ? Elements : layout.Elements };
        }
    }

    [DataContract]
    internal sealed class PluginWidgetLayout
    {
        [DataMember(Name = "size", IsRequired = true)] public string Size { get; set; }
        [DataMember(Name = "elements", IsRequired = true)] public PluginWidgetElement[] Elements { get; set; }
    }

    [DataContract]
    internal sealed class PluginWidgetElement
    {
        [DataMember(Name = "type", IsRequired = true)] public string Type { get; set; }
        [DataMember(Name = "label", IsRequired = true)] public string Label { get; set; }
        [DataMember(Name = "value", EmitDefaultValue = false)] public string Value { get; set; }
        [DataMember(Name = "percent", EmitDefaultValue = false)] public double? Percent { get; set; }
        [DataMember(Name = "commandId", EmitDefaultValue = false)] public string CommandId { get; set; }
    }

    [DataContract]
    internal sealed class PluginRequest
    {
        [DataMember(Name = "apiVersion", IsRequired = true)] public int ApiVersion { get; set; }
        [DataMember(Name = "id", IsRequired = true)] public int Id { get; set; }
        [DataMember(Name = "method", IsRequired = true)] public string Method { get; set; }
        [DataMember(Name = "pluginId", EmitDefaultValue = false)] public string PluginId { get; set; }
        [DataMember(Name = "pluginVersion", EmitDefaultValue = false)] public string PluginVersion { get; set; }
        [DataMember(Name = "locale", EmitDefaultValue = false)] public string Locale { get; set; }
        [DataMember(Name = "dataDirectory", EmitDefaultValue = false)] public string DataDirectory { get; set; }
        [DataMember(Name = "commandId", EmitDefaultValue = false)] public string CommandId { get; set; }
    }

    [DataContract]
    internal sealed class PluginResponse
    {
        [DataMember(Name = "apiVersion", IsRequired = true)] public int ApiVersion { get; set; }
        [DataMember(Name = "id", IsRequired = true)] public int Id { get; set; }
        [DataMember(Name = "ok", IsRequired = true)] public bool Ok { get; set; }
        [DataMember(Name = "pluginId", EmitDefaultValue = false)] public string PluginId { get; set; }
        [DataMember(Name = "pluginVersion", EmitDefaultValue = false)] public string PluginVersion { get; set; }
        [DataMember(Name = "commands", EmitDefaultValue = false)] public PluginCommand[] Commands { get; set; }
        [DataMember(Name = "notifications", EmitDefaultValue = false)] public PluginNotice[] Notifications { get; set; }
        [DataMember(Name = "widgets", EmitDefaultValue = false)] public PluginWidget[] Widgets { get; set; }
        [DataMember(Name = "error", EmitDefaultValue = false)] public string Error { get; set; }
    }

    internal static class PluginFrame
    {
        internal const int MaxPayload = 64 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static void ReadExact(Stream stream, byte[] bytes)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read <= 0) throw new EndOfStreamException("Plugin connection closed.");
                offset += read;
            }
        }

        internal static T Read<T>(Stream stream) where T : class
        {
            byte[] header = new byte[4];
            ReadExact(stream, header);
            uint size = (uint)header[0] | ((uint)header[1] << 8) |
                ((uint)header[2] << 16) | ((uint)header[3] << 24);
            if (size == 0 || size > MaxPayload) throw new InvalidDataException("Invalid plugin frame length.");
            byte[] payload = new byte[(int)size];
            ReadExact(stream, payload);
            try
            {
                string json = StrictUtf8.GetString(payload).Trim();
                if (json.Length < 2 || json[0] != '{' || json[json.Length - 1] != '}')
                    throw new InvalidDataException("Plugin frame must be a JSON object.");
                using (MemoryStream input = new MemoryStream(payload, false))
                {
                    T value = (T)new DataContractJsonSerializer(typeof(T)).ReadObject(input);
                    if (value == null) throw new InvalidDataException("Empty plugin message.");
                    return value;
                }
            }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid plugin UTF-8.", error); }
            catch (SerializationException error) { throw new InvalidDataException("Invalid plugin JSON.", error); }
        }

        internal static void Write<T>(Stream stream, T message) where T : class
        {
            byte[] payload;
            using (MemoryStream output = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(output, message);
                if (output.Length == 0 || output.Length > MaxPayload)
                    throw new InvalidDataException("Invalid outgoing plugin frame length.");
                payload = output.ToArray();
            }
            int size = payload.Length;
            byte[] header = { (byte)size, (byte)(size >> 8), (byte)(size >> 16), (byte)(size >> 24) };
            stream.Write(header, 0, header.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }
    }

    internal sealed class PluginRemoteException : IOException
    {
        internal PluginRemoteException(string message) : base(message) { }
    }

    // Exactly one request in flight. Transport owner must terminate/close the child
    // on abort, including a child blocked in a read/write. Never reuse a failed stream.
    internal sealed class PluginProtocolSession
    {
        internal const int ApiVersion = 1;
        private readonly Stream send;
        private readonly Stream receive;
        private readonly Action abortTransport;
        private readonly string expectedId;
        private readonly string expectedVersion;
        private readonly HashSet<string> capabilities;
        private readonly HashSet<string> commands = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Regex CommandId = new Regex(@"\A[a-z][a-z0-9_.-]{0,63}\z", RegexOptions.CultureInvariant);
        private int busy;
        private int failed;
        private int nextId;
        private bool ready;
        private bool stopped;
        private bool backgroundStarted;

        internal PluginProtocolSession(Stream send, Stream receive, Action abortTransport,
            string pluginId, string pluginVersion, IEnumerable<string> declaredCapabilities)
        {
            if (send == null || !send.CanWrite || receive == null || !receive.CanRead ||
                abortTransport == null || String.IsNullOrWhiteSpace(pluginId) ||
                String.IsNullOrWhiteSpace(pluginVersion) || declaredCapabilities == null)
                throw new ArgumentException("Invalid plugin connection.");
            this.send = send; this.receive = receive; this.abortTransport = abortTransport;
            expectedId = pluginId; expectedVersion = pluginVersion;
            capabilities = new HashSet<string>(declaredCapabilities, StringComparer.Ordinal);
        }

        internal bool Failed { get { return Interlocked.CompareExchange(ref failed, 0, 0) != 0; } }

        private void Abort()
        {
            if (Interlocked.Exchange(ref failed, 1) == 0)
            {
                // Nonblocking owner callback: terminate the owned process and close the pipes.
                try { abortTransport(); } catch (Exception) { }
            }
        }

        private static bool TextWithin(string value, int max)
        {
            return !String.IsNullOrWhiteSpace(value) && value.Length <= max &&
                value.IndexOf('\0') < 0;
        }

        private void ValidateWidgets(PluginWidget[] widgets)
        {
            if (widgets == null || widgets.Length > 8)
                throw new InvalidDataException("Invalid widget snapshot.");
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (PluginWidget widget in widgets)
            {
                if (widget == null || widget.Id == null || !CommandId.IsMatch(widget.Id) ||
                    !ids.Add(widget.Id) || !TextWithin(widget.Title, 100) ||
                    (widget.Kind != "card" && widget.Kind != "tile" && widget.Kind != "indicator") ||
                    widget.Elements == null || widget.Elements.Length == 0 || widget.Elements.Length > 12)
                    throw new InvalidDataException("Invalid widget descriptor.");
                if (widget.Kind == "indicator" && widget.Elements.Length > 2)
                    throw new InvalidDataException("Too many indicator elements.");
                ValidateWidgetElements(widget.Elements);
                if (widget.Kind == "indicator")
                {
                    if (widget.Layouts != null || widget.DefaultSize != null)
                        throw new InvalidDataException("Indicators cannot declare group layouts.");
                    continue;
                }
                if (widget.Layouts != null)
                {
                    if (widget.Layouts.Length == 0 || widget.Layouts.Length > 4)
                        throw new InvalidDataException("Invalid widget layout count.");
                    HashSet<string> sizes = new HashSet<string>(StringComparer.Ordinal);
                    foreach (PluginWidgetLayout layout in widget.Layouts)
                    {
                        if (layout == null || !PluginWidget.IsGroupSize(layout.Size) || !sizes.Add(layout.Size))
                            throw new InvalidDataException("Invalid or duplicate widget size.");
                        ValidateWidgetElements(layout.Elements);
                    }
                    if (widget.DefaultSize == null || !sizes.Contains(widget.DefaultSize))
                        throw new InvalidDataException("Widget default size is not declared.");
                }
                else if (widget.DefaultSize != null && !PluginWidget.IsGroupSize(widget.DefaultSize))
                    throw new InvalidDataException("Invalid widget default size.");
            }
        }

        private void ValidateWidgetElements(PluginWidgetElement[] elements)
        {
            if (elements == null || elements.Length == 0 || elements.Length > 12)
                throw new InvalidDataException("Invalid widget elements.");
            foreach (PluginWidgetElement element in elements)
            {
                if (element == null || !TextWithin(element.Label, 240))
                    throw new InvalidDataException("Invalid widget element.");
                bool hasValue = element.Value != null, hasPercent = element.Percent.HasValue,
                    hasCommand = element.CommandId != null;
                switch (element.Type)
                {
                    case "text":
                        if (hasValue || hasPercent || hasCommand) throw new InvalidDataException("Invalid text element.");
                        break;
                    case "metric":
                        if (!TextWithin(element.Value, 100) || hasPercent || hasCommand)
                            throw new InvalidDataException("Invalid metric element.");
                        break;
                    case "progress":
                        if (hasValue || hasCommand || !hasPercent || Double.IsNaN(element.Percent.Value) ||
                            Double.IsInfinity(element.Percent.Value) || element.Percent.Value < 0 || element.Percent.Value > 100)
                            throw new InvalidDataException("Invalid progress element.");
                        break;
                    case "action":
                        if (hasValue || hasPercent || !hasCommand || !commands.Contains(element.CommandId))
                            throw new InvalidDataException("Unknown widget action.");
                        break;
                    default: throw new InvalidDataException("Unknown widget element type.");
                }
            }
        }

        private void ValidateResponse(PluginRequest request, PluginResponse reply)
        {
            if (reply.ApiVersion != ApiVersion || reply.Id != request.Id)
                throw new InvalidDataException("Plugin response version or request ID mismatch.");
            if (!reply.Ok)
            {
                if (!TextWithin(reply.Error, 1000) || reply.Commands != null ||
                    reply.Notifications != null || reply.Widgets != null || reply.PluginId != null || reply.PluginVersion != null)
                    throw new InvalidDataException("Invalid plugin error response.");
                return;
            }
            if (reply.Error != null) throw new InvalidDataException("Unexpected plugin error.");
            if (request.Method == "getWidgets") ValidateWidgets(reply.Widgets);
            else if (reply.Widgets != null) throw new InvalidDataException("Unexpected plugin widgets.");
            if (request.Method == "hello")
            {
                if (reply.PluginId != expectedId || reply.PluginVersion != expectedVersion ||
                    reply.Notifications != null)
                    throw new InvalidDataException("Plugin handshake identity mismatch.");
                PluginCommand[] advertised = reply.Commands ?? new PluginCommand[0];
                if (advertised.Length > 32 || (advertised.Length != 0 &&
                    !capabilities.Contains("command") && !capabilities.Contains("window")))
                    throw new InvalidDataException("Unexpected plugin commands.");
                HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (PluginCommand item in advertised)
                {
                    if (item == null || item.Id == null || !CommandId.IsMatch(item.Id) ||
                        !TextWithin(item.Label, 100) || !ids.Add(item.Id))
                        throw new InvalidDataException("Invalid or duplicate plugin command.");
                }
            }
            else
            {
                if (reply.PluginId != null || reply.PluginVersion != null || reply.Commands != null)
                    throw new InvalidDataException("Unexpected plugin identity or commands.");
                if (request.Method != "poll" && reply.Notifications != null)
                    throw new InvalidDataException("Unexpected plugin notifications.");
                if (reply.Notifications != null)
                {
                    if (reply.Notifications.Length > 16)
                        throw new InvalidDataException("Too many plugin notifications.");
                    foreach (PluginNotice notice in reply.Notifications)
                        if (notice == null || !TextWithin(notice.Title, 100) || !TextWithin(notice.Message, 1000))
                            throw new InvalidDataException("Invalid plugin notification.");
                }
            }
        }

        private void CheckRequest(PluginRequest request)
        {
            if (Failed || stopped) throw new InvalidOperationException("Plugin session is closed.");
            if (request.Method == "hello")
            {
                if (ready || !TextWithin(request.Locale, 64) || !TextWithin(request.DataDirectory, 4096))
                    throw new InvalidOperationException("Invalid plugin handshake request.");
                return;
            }
            if (!ready) throw new InvalidOperationException("Plugin handshake required.");
            switch (request.Method)
            {
                case "invoke":
                    if (request.CommandId == null || !commands.Contains(request.CommandId))
                        throw new InvalidOperationException("Unknown plugin command.");
                    break;
                case "settings":
                    if (!capabilities.Contains("settings")) throw new InvalidOperationException("Plugin has no settings.");
                    break;
                case "startBackground":
                    if (!capabilities.Contains("background") || backgroundStarted)
                        throw new InvalidOperationException("Invalid background start.");
                    break;
                case "poll":
                    if (!capabilities.Contains("notification")) throw new InvalidOperationException("Plugin has no notifications.");
                    break;
                case "getWidgets":
                    if (!capabilities.Contains("widget")) throw new InvalidOperationException("Plugin has no widgets.");
                    break;
                case "ping":
                case "shutdown": break;
                default: throw new InvalidOperationException("Unknown plugin method.");
            }
        }

        private static void ObserveFault(Task<PluginResponse> io)
        {
            io.ContinueWith(delegate(Task<PluginResponse> task) { GC.KeepAlive(task.Exception); },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private async Task<PluginResponse> Exchange(PluginRequest request, int timeoutMs, CancellationToken cancel)
        {
            if (timeoutMs < 1 || timeoutMs > 60000) throw new ArgumentOutOfRangeException("timeoutMs");
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                throw new InvalidOperationException("Plugin request already in progress.");
            try
            {
                CheckRequest(request);
                cancel.ThrowIfCancellationRequested();
                request.ApiVersion = ApiVersion;
                request.Id = checked(++nextId);
                // .NET Framework redirected pipes can block on synchronous IO. Run both
                // directions on a worker, and abort the process on timeout/cancellation.
                Task<PluginResponse> io = Task.Run(delegate
                {
                    PluginFrame.Write(send, request);
                    PluginResponse reply = PluginFrame.Read<PluginResponse>(receive);
                    ValidateResponse(request, reply);
                    return reply;
                });
                try
                {
                    using (CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                    {
                        Task delay = Task.Delay(timeoutMs, timer.Token);
                        Task winner = await Task.WhenAny(io, delay).ConfigureAwait(false);
                        if (winner != io)
                        {
                            Abort();
                            // Observe eventual IO exceptions after the caller has returned.
                            ObserveFault(io);
                            cancel.ThrowIfCancellationRequested();
                            throw new TimeoutException("Plugin request timed out.");
                        }
                        timer.Cancel();
                        PluginResponse reply = await io.ConfigureAwait(false);
                        if (!reply.Ok)
                        {
                            if (request.Method == "hello" || request.Method == "shutdown") Abort();
                            throw new PluginRemoteException(reply.Error);
                        }
                        if (request.Method == "hello")
                        {
                            foreach (PluginCommand item in reply.Commands ?? new PluginCommand[0]) commands.Add(item.Id);
                            ready = true;
                        }
                        else if (request.Method == "startBackground") backgroundStarted = true;
                        else if (request.Method == "shutdown") stopped = true;
                        return reply;
                    }
                }
                catch (PluginRemoteException) { throw; }
                catch (Exception) { Abort(); throw; }
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        internal Task<PluginResponse> HelloAsync(string locale, string dataDirectory, int timeoutMs, CancellationToken cancel)
        {
            return Exchange(new PluginRequest { Method = "hello", PluginId = expectedId,
                PluginVersion = expectedVersion, Locale = locale, DataDirectory = dataDirectory }, timeoutMs, cancel);
        }

        internal Task<PluginResponse> InvokeAsync(string commandId, int timeoutMs, CancellationToken cancel)
        {
            return Exchange(new PluginRequest { Method = "invoke", CommandId = commandId }, timeoutMs, cancel);
        }

        internal Task<PluginResponse> SettingsAsync(int timeoutMs, CancellationToken cancel)
        {
            return Exchange(new PluginRequest { Method = "settings" }, timeoutMs, cancel);
        }

        internal Task<PluginResponse> StartBackgroundAsync(int timeoutMs, CancellationToken cancel)
        {
            return Exchange(new PluginRequest { Method = "startBackground" }, timeoutMs, cancel);
        }

        internal Task<PluginResponse> PollAsync(int timeoutMs, CancellationToken cancel)
        {
            return Exchange(new PluginRequest { Method = "poll" }, timeoutMs, cancel);
        }

        internal Task<PluginResponse> GetWidgetsAsync(int timeoutMs, CancellationToken cancel)
        {
            return Exchange(new PluginRequest { Method = "getWidgets" }, timeoutMs, cancel);
        }

        internal Task<PluginResponse> PingAsync(int timeoutMs, CancellationToken cancel)
        {
            return Exchange(new PluginRequest { Method = "ping" }, timeoutMs, cancel);
        }

        internal Task<PluginResponse> ShutdownAsync(int timeoutMs, CancellationToken cancel)
        {
            return Exchange(new PluginRequest { Method = "shutdown" }, timeoutMs, cancel);
        }
    }
}
