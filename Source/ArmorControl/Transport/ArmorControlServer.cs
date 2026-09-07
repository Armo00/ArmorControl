using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArmorOverhaul.ArmorControl.Core;
using ArmorOverhaul.ArmorControl.Protocol;

namespace ArmorOverhaul.ArmorControl.Transport
{
    internal sealed class ArmorControlServer : IDisposable
    {
        private const int MaximumHeaderBytes = 32768;
        private const int MaximumFrameBytes = 65536;
        private const string WebSocketMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly int port;
        private readonly string webRoot;
        private readonly MainThreadBridge bridge;
        private readonly string accessToken;
        private readonly ConcurrentDictionary<string, ClientSession> clients = new ConcurrentDictionary<string, ClientSession>();
        private CancellationTokenSource cancellation;
        private TcpListener listener;
        private Task acceptTask;
        private Task broadcastTask;
        private string lastError;
        private long fastSerializationTicks;
        private long fastSerializationMaximumTicks;
        private long fastSerializationSamples;
        private int vesselImageClients;

        internal ArmorControlServer(int port, string webRoot, MainThreadBridge bridge)
            : this(IPAddress.Any, port, webRoot, bridge, null)
        {
        }

        internal ArmorControlServer(IPAddress bindAddress, int port, string webRoot, MainThreadBridge bridge, string accessToken = null)
        {
            this.port = port;
            this.webRoot = EnsureDirectorySeparator(Path.GetFullPath(webRoot));
            this.bridge = bridge;
            this.accessToken = accessToken ?? string.Empty;
            BindAddress = bindAddress ?? IPAddress.Any;
        }

        internal IPAddress BindAddress { get; private set; }

        internal int ClientCount { get { return clients.Count; } }
        internal int VesselImageClientCount { get { return Volatile.Read(ref vesselImageClients); } }
        internal string LastError { get { return Volatile.Read(ref lastError); } }

        internal void Start()
        {
            if (cancellation != null)
            {
                return;
            }

            cancellation = new CancellationTokenSource();
            listener = new TcpListener(BindAddress, port);
            try
            {
                listener.Start(32);
            }
            catch
            {
                listener = null;
                cancellation.Dispose();
                cancellation = null;
                throw;
            }
            acceptTask = Task.Run(() => AcceptLoop(cancellation.Token));
            broadcastTask = Task.Run(() => BroadcastLoop(cancellation.Token));
        }

        internal void Stop()
        {
            CancellationTokenSource source = cancellation;
            if (source == null)
            {
                return;
            }

            cancellation = null;
            source.Cancel();
            try { listener.Stop(); } catch { }
            foreach (ClientSession client in clients.Values)
            {
                client.Dispose();
            }
            clients.Clear();
            source.Dispose();
        }

        public void Dispose()
        {
            Stop();
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    TcpClient socket = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    socket.NoDelay = true;
                    Task ignored = Task.Run(() => HandleConnection(socket, token), token);
                }
                catch (ObjectDisposedException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    RecordError("Accept failed: " + exception.Message);
                    await DelayQuietly(100, token).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleConnection(TcpClient socket, CancellationToken serverToken)
        {
            using (socket)
            {
                NetworkStream stream = socket.GetStream();
                HttpRequest request;
                try
                {
                    request = await ReadRequest(stream, serverToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    RecordError("Request rejected: " + exception.Message);
                    return;
                }

                if (request == null)
                {
                    return;
                }

                if ((request.Path == "/ws/realtime" || request.Path == "/ws/vessel") && request.IsWebSocket)
                {
                    if (!IsAuthorized(request))
                    {
                        await WriteHttp(stream, 401, "Unauthorized", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Invalid ArmorControl access token"), serverToken).ConfigureAwait(false);
                        return;
                    }
                    if (request.Path == "/ws/vessel")
                        await HandleVesselImageWebSocket(stream, request, serverToken).ConfigureAwait(false);
                    else
                        await HandleWebSocket(socket, stream, request, serverToken).ConfigureAwait(false);
                    return;
                }

                await HandleHttp(stream, request, serverToken).ConfigureAwait(false);
            }
        }

        private async Task HandleHttp(NetworkStream stream, HttpRequest request, CancellationToken token)
        {
            if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHttp(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("GET only"), token).ConfigureAwait(false);
                return;
            }

            if (request.Path.StartsWith("/api/", StringComparison.Ordinal) && !IsAuthorized(request))
            {
                await WriteHttp(stream, 401, "Unauthorized", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Invalid ArmorControl access token"), token).ConfigureAwait(false);
                return;
            }

            if (request.Path == "/health")
            {
                byte[] health = Encoding.UTF8.GetBytes(WireProtocol.Health(
                    ClientCount,
                    bridge.LatestMetrics,
                    SerializationAverageMilliseconds(),
                    Milliseconds(Volatile.Read(ref fastSerializationMaximumTicks))));
                await WriteHttp(stream, 200, "OK", "application/json; charset=utf-8", health, token).ConfigureAwait(false);
                return;
            }

            if (request.Path == "/api/v1/crew")
            {
                byte[] crew = Encoding.UTF8.GetBytes(WireProtocol.Crew(bridge.LatestStructure));
                await WriteHttp(stream, 200, "OK", "application/json; charset=utf-8", crew, token).ConfigureAwait(false);
                return;
            }

            if (request.Path == "/api/v1/vessel")
            {
                byte[] vessel = Encoding.UTF8.GetBytes(WireProtocol.VesselStructure(bridge.LatestStructure));
                await WriteHttp(stream, 200, "OK", "application/json; charset=utf-8", vessel, token).ConfigureAwait(false);
                return;
            }

            if (request.Path == "/api/v1/vessel-image.jpg")
            {
                VesselImageSnapshot image = bridge.LatestVesselImage;
                if (image == null || image.Jpeg.Length == 0)
                {
                    await WriteHttp(stream, 503, "Service Unavailable", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Vessel image is not ready"), token).ConfigureAwait(false);
                    return;
                }
                await WriteHttp(stream, 200, "OK", "image/jpeg", image.Jpeg, token).ConfigureAwait(false);
                return;
            }

            if (request.Path == "/api/v1/recorder")
            {
                byte[] recorder = Encoding.UTF8.GetBytes(WireProtocol.RecorderHistory(bridge.GetRecorderHistory()));
                await WriteHttp(stream, 200, "OK", "application/json; charset=utf-8", recorder, token).ConfigureAwait(false);
                return;
            }

            if (request.Path == "/api/v1/recorder.csv")
            {
                byte[] recorderCsv = Encoding.UTF8.GetBytes(WireProtocol.RecorderCsv(bridge.GetRecorderHistory()));
                await WriteHttp(stream, 200, "OK", "text/csv; charset=utf-8", recorderCsv, token).ConfigureAwait(false);
                return;
            }

            if (request.Path == "/api/v1/porkchop")
            {
                byte[] porkchop = Encoding.UTF8.GetBytes(WireProtocol.Porkchop(bridge.LatestPorkchop));
                await WriteHttp(stream, 200, "OK", "application/json; charset=utf-8", porkchop, token).ConfigureAwait(false);
                return;
            }

            string relative = request.Path == "/" ? "index.html" : Uri.UnescapeDataString(request.Path.TrimStart('/'));
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(webRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch
            {
                await WriteHttp(stream, 400, "Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Invalid path"), token).ConfigureAwait(false);
                return;
            }

            if (!candidate.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
            {
                await WriteHttp(stream, 404, "Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"), token).ConfigureAwait(false);
                return;
            }

            byte[] content = await ReadAllBytes(candidate).ConfigureAwait(false);
            await WriteHttp(stream, 200, "OK", ContentType(candidate), content, token).ConfigureAwait(false);
        }

        private async Task HandleWebSocket(TcpClient socket, NetworkStream stream, HttpRequest request, CancellationToken serverToken)
        {
            string key;
            if (!request.Headers.TryGetValue("Sec-WebSocket-Key", out key) || string.IsNullOrWhiteSpace(key))
            {
                await WriteHttp(stream, 400, "Bad Request", "text/plain", Encoding.ASCII.GetBytes("Missing WebSocket key"), serverToken).ConfigureAwait(false);
                return;
            }

            string accept;
            using (SHA1 sha1 = SHA1.Create())
            {
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key.Trim() + WebSocketMagic)));
            }
            string response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n";
            byte[] responseBytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(responseBytes, 0, responseBytes.Length, serverToken).ConfigureAwait(false);

            string clientId = Guid.NewGuid().ToString("N");
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken))
            using (var client = new ClientSession(clientId, socket, stream, linked))
            {
                if (!clients.TryAdd(clientId, client))
                {
                    return;
                }

                client.EnqueueText(WireProtocol.Hello(clientId));
                client.PublishTelemetry(WireProtocol.Telemetry(bridge.LatestSnapshot));
                client.PublishRegular(WireProtocol.RegularTelemetry(bridge.LatestRegularSnapshot));
                FlightRecorderSample latestRecorder = bridge.LatestRecorder;
                if (latestRecorder != null) client.PublishRecorder(WireProtocol.RecorderSample(latestRecorder));
                FlightPanelSnapshot latestPanel = bridge.LatestFlightPanel;
                if (latestPanel != null) client.PublishFlightPanel(WireProtocol.FlightPanel(latestPanel));
                AutomationSnapshot latestAutomation = bridge.LatestAutomation;
                if (latestAutomation != null) client.PublishAutomation(WireProtocol.Automation(latestAutomation));
                VesselStructureSnapshot latestStructure = bridge.LatestStructure;
                if (latestStructure != null && latestStructure.Revision > 0) client.PublishStructure(WireProtocol.VesselStructure(latestStructure));
                Task sender = client.RunSender();
                try
                {
                    await ReceiveLoop(client, serverToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                }
                catch (IOException)
                {
                }
                catch (Exception exception)
                {
                    RecordError("WebSocket receive failed: " + exception.Message);
                }
                finally
                {
                    ClientSession removed;
                    clients.TryRemove(clientId, out removed);
                    linked.Cancel();
                    try { await sender.ConfigureAwait(false); } catch { }
                }
            }
        }

        private async Task HandleVesselImageWebSocket(NetworkStream stream, HttpRequest request, CancellationToken token)
        {
            if (!await AcceptWebSocket(stream, request, token).ConfigureAwait(false)) return;
            Interlocked.Increment(ref vesselImageClients);
            long lastRevision = -1;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    VesselImageSnapshot image = bridge.LatestVesselImage;
                    if (image != null && image.Jpeg.Length > 0 && image.Revision != lastRevision)
                    {
                        await WriteRawFrame(stream, 2, image.Jpeg, token).ConfigureAwait(false);
                        lastRevision = image.Revision;
                    }
                    await DelayQuietly(50, token).ConfigureAwait(false);
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                Interlocked.Decrement(ref vesselImageClients);
            }
        }

        private static async Task<bool> AcceptWebSocket(NetworkStream stream, HttpRequest request, CancellationToken token)
        {
            string key;
            if (!request.Headers.TryGetValue("Sec-WebSocket-Key", out key) || string.IsNullOrWhiteSpace(key))
            {
                await WriteHttp(stream, 400, "Bad Request", "text/plain", Encoding.ASCII.GetBytes("Missing WebSocket key"), token).ConfigureAwait(false);
                return false;
            }
            string accept;
            using (SHA1 sha1 = SHA1.Create())
            {
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key.Trim() + WebSocketMagic)));
            }
            string response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n";
            byte[] responseBytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(responseBytes, 0, responseBytes.Length, token).ConfigureAwait(false);
            return true;
        }

        private static async Task WriteRawFrame(NetworkStream stream, int opcode, byte[] payload, CancellationToken token)
        {
            int length = payload == null ? 0 : payload.Length;
            var header = new List<byte>(10) { (byte)(0x80 | (opcode & 0x0f)) };
            if (length < 126)
            {
                header.Add((byte)length);
            }
            else if (length <= ushort.MaxValue)
            {
                header.Add(126);
                header.Add((byte)(length >> 8));
                header.Add((byte)length);
            }
            else
            {
                header.Add(127);
                ulong value = (ulong)length;
                for (int shift = 56; shift >= 0; shift -= 8) header.Add((byte)(value >> shift));
            }
            byte[] headerBytes = header.ToArray();
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, token).ConfigureAwait(false);
            if (length > 0) await stream.WriteAsync(payload, 0, length, token).ConfigureAwait(false);
        }

        private async Task ReceiveLoop(ClientSession client, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                WebSocketFrame frame = await ReadFrame(client.Stream, token).ConfigureAwait(false);
                if (!frame.Final)
                {
                    client.EnqueueClose(1003, "Fragmented messages are not supported.");
                    return;
                }
                if (frame.Opcode == 8)
                {
                    client.EnqueueClose(1000, "Closing");
                    return;
                }
                if (frame.Opcode == 9)
                {
                    client.EnqueueFrame(10, frame.Payload);
                    continue;
                }
                if (frame.Opcode != 1)
                {
                    client.EnqueueText(WireProtocol.Error("unsupported_frame", "Only UTF-8 text messages are accepted."));
                    continue;
                }

                string json = Encoding.UTF8.GetString(frame.Payload);
                IncomingMessage message;
                if (!WireProtocol.TryParseIncoming(json, out message))
                {
                    client.EnqueueText(WireProtocol.Error("invalid_message", "Malformed protocol message."));
                    continue;
                }
                if (message.Type == "ping")
                {
                    client.EnqueueText(WireProtocol.Pong(message.Sequence));
                    continue;
                }
                if (message.Type != "command" || string.IsNullOrWhiteSpace(message.CommandId) || string.IsNullOrWhiteSpace(message.Name))
                {
                    client.EnqueueText(WireProtocol.Error("invalid_command", "Command requires commandId and name."));
                    continue;
                }

                bridge.Enqueue(client.Id, message, result => client.EnqueueText(WireProtocol.Ack(result)));
            }
        }

        private async Task BroadcastLoop(CancellationToken token)
        {
            long lastSequence = -1;
            long lastRegularSequence = -1;
            long lastRecorderSequence = -1;
            long lastRecorderRevision = -1;
            long lastFlightPanelSequence = -1;
            long lastAutomationSequence = -1;
            long lastStructureRevision = -1;
            string lastScene = null;
            string lastVesselId = null;
            RegularTelemetrySnapshot previousRegular = RegularTelemetrySnapshot.Empty;
            while (!token.IsCancellationRequested)
            {
                TelemetrySnapshot snapshot = bridge.LatestSnapshot;
                if (snapshot.Sequence != lastSequence)
                {
                    if (!string.Equals(lastScene, snapshot.Scene, StringComparison.Ordinal)
                        || !string.Equals(lastVesselId, snapshot.VesselId, StringComparison.Ordinal))
                    {
                        string context = WireProtocol.ContextChanged(snapshot);
                        foreach (ClientSession client in clients.Values)
                        {
                            client.EnqueueText(context);
                        }
                        lastScene = snapshot.Scene;
                        lastVesselId = snapshot.VesselId;
                    }
                    long serializationStarted = Stopwatch.GetTimestamp();
                    string json = WireProtocol.Telemetry(snapshot);
                    RecordFastSerialization(Stopwatch.GetTimestamp() - serializationStarted);
                    foreach (ClientSession client in clients.Values)
                    {
                        client.PublishTelemetry(json);
                    }
                    lastSequence = snapshot.Sequence;
                }
                RegularTelemetrySnapshot regular = bridge.LatestRegularSnapshot;
                if (regular.Sequence != lastRegularSequence)
                {
                    if (previousRegular.Sequence > 0
                        && string.Equals(previousRegular.VesselId, regular.VesselId, StringComparison.Ordinal))
                    {
                        string vesselEvent = WireProtocol.VesselChanged(previousRegular, regular);
                        if (vesselEvent != null)
                        {
                            foreach (ClientSession client in clients.Values) client.EnqueueText(vesselEvent);
                        }
                    }
                    string regularJson = WireProtocol.RegularTelemetry(regular);
                    foreach (ClientSession client in clients.Values)
                    {
                        client.PublishRegular(regularJson);
                    }
                    lastRegularSequence = regular.Sequence;
                    previousRegular = regular;
                }
                long recorderRevision = bridge.RecorderRevision;
                if (recorderRevision != lastRecorderRevision)
                {
                    string reset = WireProtocol.RecorderReset(recorderRevision);
                    foreach (ClientSession client in clients.Values) client.EnqueueText(reset);
                    lastRecorderRevision = recorderRevision;
                }
                FlightRecorderSample recorder = bridge.LatestRecorder;
                if (recorder != null && recorder.Sequence != lastRecorderSequence)
                {
                    string recorderJson = WireProtocol.RecorderSample(recorder);
                    foreach (ClientSession client in clients.Values) client.PublishRecorder(recorderJson);
                    lastRecorderSequence = recorder.Sequence;
                }
                FlightPanelSnapshot panel = bridge.LatestFlightPanel;
                if (panel != null && panel.Sequence != lastFlightPanelSequence)
                {
                    string panelJson = WireProtocol.FlightPanel(panel);
                    foreach (ClientSession client in clients.Values) client.PublishFlightPanel(panelJson);
                    lastFlightPanelSequence = panel.Sequence;
                }
                AutomationSnapshot automation = bridge.LatestAutomation;
                if (automation != null && automation.Sequence != lastAutomationSequence)
                {
                    string automationJson = WireProtocol.Automation(automation);
                    foreach (ClientSession client in clients.Values) client.PublishAutomation(automationJson);
                    lastAutomationSequence = automation.Sequence;
                }
                VesselStructureSnapshot structure = bridge.LatestStructure;
                if (structure != null && structure.Revision != lastStructureRevision)
                {
                    string structureJson = WireProtocol.VesselStructure(structure);
                    foreach (ClientSession client in clients.Values) client.PublishStructure(structureJson);
                    lastStructureRevision = structure.Revision;
                }
                await DelayQuietly(10, token).ConfigureAwait(false);
            }
        }

        private void RecordFastSerialization(long elapsedTicks)
        {
            Interlocked.Add(ref fastSerializationTicks, elapsedTicks);
            Interlocked.Increment(ref fastSerializationSamples);
            long maximum = Volatile.Read(ref fastSerializationMaximumTicks);
            while (elapsedTicks > maximum)
            {
                long observed = Interlocked.CompareExchange(ref fastSerializationMaximumTicks, elapsedTicks, maximum);
                if (observed == maximum) break;
                maximum = observed;
            }
        }

        private double SerializationAverageMilliseconds()
        {
            long samples = Volatile.Read(ref fastSerializationSamples);
            return samples == 0 ? 0 : Milliseconds(Volatile.Read(ref fastSerializationTicks)) / samples;
        }

        private static double Milliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static async Task<HttpRequest> ReadRequest(NetworkStream stream, CancellationToken token)
        {
            var bytes = new List<byte>(1024);
            var one = new byte[1];
            while (bytes.Count < MaximumHeaderBytes)
            {
                int read = await stream.ReadAsync(one, 0, 1, token).ConfigureAwait(false);
                if (read == 0) return null;
                bytes.Add(one[0]);
                int count = bytes.Count;
                if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10 && bytes[count - 2] == 13 && bytes[count - 1] == 10) break;
            }
            if (bytes.Count >= MaximumHeaderBytes) throw new InvalidDataException("HTTP header too large.");

            string header = Encoding.ASCII.GetString(bytes.ToArray());
            string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) throw new InvalidDataException("Invalid request line.");
            string rawPath = first[1];
            int query = rawPath.IndexOf('?');
            var request = new HttpRequest {
                Method = first[0],
                Path = query >= 0 ? rawPath.Substring(0, query) : rawPath,
                Query = query >= 0 ? rawPath.Substring(query + 1) : string.Empty
            };
            for (int index = 1; index < lines.Length; index++)
            {
                int separator = lines[index].IndexOf(':');
                if (separator <= 0) continue;
                request.Headers[lines[index].Substring(0, separator).Trim()] = lines[index].Substring(separator + 1).Trim();
            }
            string upgrade;
            request.IsWebSocket = request.Headers.TryGetValue("Upgrade", out upgrade) && string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase);
            return request;
        }

        private bool IsAuthorized(HttpRequest request)
        {
            if (accessToken.Length == 0) return true;
            string supplied = QueryValue(request.Query, "token");
            byte[] expectedBytes = Encoding.UTF8.GetBytes(accessToken);
            byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied ?? string.Empty);
            int difference = expectedBytes.Length ^ suppliedBytes.Length;
            int maximum = Math.Max(expectedBytes.Length, suppliedBytes.Length);
            for (int index = 0; index < maximum; index++)
            {
                byte expected = index < expectedBytes.Length ? expectedBytes[index] : (byte)0;
                byte actual = index < suppliedBytes.Length ? suppliedBytes[index] : (byte)0;
                difference |= expected ^ actual;
            }
            return difference == 0;
        }

        private static string QueryValue(string query, string name)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (string item in query.Split('&'))
            {
                int separator = item.IndexOf('=');
                string key = separator < 0 ? item : item.Substring(0, separator);
                if (!string.Equals(Uri.UnescapeDataString(key.Replace('+', ' ')), name, StringComparison.Ordinal)) continue;
                string value = separator < 0 ? string.Empty : item.Substring(separator + 1);
                return Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            return null;
        }

        private static async Task<WebSocketFrame> ReadFrame(NetworkStream stream, CancellationToken token)
        {
            byte[] prefix = new byte[2];
            await ReadExact(stream, prefix, 0, 2, token).ConfigureAwait(false);
            bool final = (prefix[0] & 0x80) != 0;
            int opcode = prefix[0] & 0x0f;
            bool masked = (prefix[1] & 0x80) != 0;
            ulong length = (ulong)(prefix[1] & 0x7f);
            if (length == 126)
            {
                byte[] extended = new byte[2];
                await ReadExact(stream, extended, 0, 2, token).ConfigureAwait(false);
                length = (ulong)((extended[0] << 8) | extended[1]);
            }
            else if (length == 127)
            {
                byte[] extended = new byte[8];
                await ReadExact(stream, extended, 0, 8, token).ConfigureAwait(false);
                length = 0;
                for (int index = 0; index < 8; index++) length = (length << 8) | extended[index];
            }
            if (!masked) throw new InvalidDataException("Client WebSocket frames must be masked.");
            if (length > MaximumFrameBytes) throw new InvalidDataException("WebSocket frame too large.");
            byte[] mask = new byte[4];
            await ReadExact(stream, mask, 0, 4, token).ConfigureAwait(false);
            byte[] payload = new byte[(int)length];
            await ReadExact(stream, payload, 0, payload.Length, token).ConfigureAwait(false);
            for (int index = 0; index < payload.Length; index++) payload[index] ^= mask[index % 4];
            return new WebSocketFrame { Final = final, Opcode = opcode, Payload = payload };
        }

        private static async Task ReadExact(Stream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            while (count > 0)
            {
                int read = await stream.ReadAsync(buffer, offset, count, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
                count -= read;
            }
        }

        private static async Task WriteHttp(NetworkStream stream, int status, string reason, string contentType, byte[] body, CancellationToken token)
        {
            string headers = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + reason
                + "\r\nContent-Type: " + contentType
                + "\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture)
                + "\r\nCache-Control: no-cache"
                + "\r\nConnection: close"
                + "\r\nX-Content-Type-Options: nosniff"
                + "\r\nX-Frame-Options: DENY"
                + "\r\nReferrer-Policy: no-referrer"
                + "\r\nPermissions-Policy: camera=(), microphone=(), geolocation=()"
                + "\r\nContent-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self' ws: wss:; object-src 'none'; frame-ancestors 'none'; base-uri 'none'"
                + "\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
        }

        private static Task<byte[]> ReadAllBytes(string path)
        {
            return Task.Run(() => File.ReadAllBytes(path));
        }

        private static string ContentType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".ico": return "image/x-icon";
                case ".json": return "application/json; charset=utf-8";
                default: return "application/octet-stream";
            }
        }

        private static string EnsureDirectorySeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? path : path + Path.DirectorySeparatorChar;
        }

        private static async Task DelayQuietly(int milliseconds, CancellationToken token)
        {
            try { await Task.Delay(milliseconds, token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        private void RecordError(string message)
        {
            Volatile.Write(ref lastError, message);
        }

        private sealed class HttpRequest
        {
            internal readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal string Method;
            internal string Path;
            internal string Query;
            internal bool IsWebSocket;
        }

        private sealed class WebSocketFrame
        {
            internal bool Final;
            internal int Opcode;
            internal byte[] Payload;
        }

        private sealed class ClientSession : IDisposable
        {
            private readonly TcpClient socket;
            private readonly CancellationTokenSource cancellation;
            private readonly ConcurrentQueue<OutboundFrame> reliable = new ConcurrentQueue<OutboundFrame>();
            private readonly SemaphoreSlim signal = new SemaphoreSlim(0, int.MaxValue);
            private string latestTelemetry;
            private string latestRegular;
            private string latestRecorder;
            private string latestFlightPanel;
            private string latestAutomation;
            private string latestStructure;

            internal ClientSession(string id, TcpClient socket, NetworkStream stream, CancellationTokenSource cancellation)
            {
                Id = id;
                this.socket = socket;
                Stream = stream;
                this.cancellation = cancellation;
            }

            internal string Id { get; private set; }
            internal NetworkStream Stream { get; private set; }

            internal void PublishTelemetry(string json)
            {
                Interlocked.Exchange(ref latestTelemetry, json);
                Signal();
            }

            internal void PublishRegular(string json)
            {
                Interlocked.Exchange(ref latestRegular, json);
                Signal();
            }

            internal void PublishRecorder(string json)
            {
                Interlocked.Exchange(ref latestRecorder, json);
                Signal();
            }

            internal void PublishFlightPanel(string json)
            {
                Interlocked.Exchange(ref latestFlightPanel, json);
                Signal();
            }

            internal void PublishAutomation(string json)
            {
                Interlocked.Exchange(ref latestAutomation, json);
                Signal();
            }

            internal void PublishStructure(string json)
            {
                Interlocked.Exchange(ref latestStructure, json);
                Signal();
            }

            internal void EnqueueText(string json)
            {
                EnqueueFrame(1, Encoding.UTF8.GetBytes(json));
            }

            internal void EnqueueClose(ushort code, string reason)
            {
                byte[] reasonBytes = Encoding.UTF8.GetBytes(reason ?? string.Empty);
                byte[] payload = new byte[reasonBytes.Length + 2];
                payload[0] = (byte)(code >> 8);
                payload[1] = (byte)(code & 0xff);
                Buffer.BlockCopy(reasonBytes, 0, payload, 2, reasonBytes.Length);
                EnqueueFrame(8, payload);
            }

            internal void EnqueueFrame(int opcode, byte[] payload)
            {
                reliable.Enqueue(new OutboundFrame { Opcode = opcode, Payload = payload });
                Signal();
            }

            internal async Task RunSender()
            {
                CancellationToken token = cancellation.Token;
                while (!token.IsCancellationRequested)
                {
                    try { await signal.WaitAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }

                    OutboundFrame frame;
                    while (reliable.TryDequeue(out frame))
                    {
                        await WriteFrame(Stream, frame.Opcode, frame.Payload, token).ConfigureAwait(false);
                    }
                    string telemetry = Interlocked.Exchange(ref latestTelemetry, null);
                    if (telemetry != null)
                    {
                        await WriteFrame(Stream, 1, Encoding.UTF8.GetBytes(telemetry), token).ConfigureAwait(false);
                    }
                    string regular = Interlocked.Exchange(ref latestRegular, null);
                    if (regular != null)
                    {
                        await WriteFrame(Stream, 1, Encoding.UTF8.GetBytes(regular), token).ConfigureAwait(false);
                    }
                    string recorder = Interlocked.Exchange(ref latestRecorder, null);
                    if (recorder != null)
                    {
                        await WriteFrame(Stream, 1, Encoding.UTF8.GetBytes(recorder), token).ConfigureAwait(false);
                    }
                    string panel = Interlocked.Exchange(ref latestFlightPanel, null);
                    if (panel != null)
                    {
                        await WriteFrame(Stream, 1, Encoding.UTF8.GetBytes(panel), token).ConfigureAwait(false);
                    }
                    string automation = Interlocked.Exchange(ref latestAutomation, null);
                    if (automation != null)
                    {
                        await WriteFrame(Stream, 1, Encoding.UTF8.GetBytes(automation), token).ConfigureAwait(false);
                    }
                    string structure = Interlocked.Exchange(ref latestStructure, null);
                    if (structure != null)
                    {
                        await WriteFrame(Stream, 1, Encoding.UTF8.GetBytes(structure), token).ConfigureAwait(false);
                    }
                }
            }

            public void Dispose()
            {
                try { cancellation.Cancel(); } catch { }
                try { socket.Close(); } catch { }
                signal.Dispose();
            }

            private void Signal()
            {
                try { signal.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
            }

            private static async Task WriteFrame(NetworkStream stream, int opcode, byte[] payload, CancellationToken token)
            {
                int length = payload == null ? 0 : payload.Length;
                var header = new List<byte>(10) { (byte)(0x80 | (opcode & 0x0f)) };
                if (length < 126)
                {
                    header.Add((byte)length);
                }
                else if (length <= ushort.MaxValue)
                {
                    header.Add(126);
                    header.Add((byte)(length >> 8));
                    header.Add((byte)length);
                }
                else
                {
                    header.Add(127);
                    ulong value = (ulong)length;
                    for (int shift = 56; shift >= 0; shift -= 8) header.Add((byte)(value >> shift));
                }
                byte[] headerBytes = header.ToArray();
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length, token).ConfigureAwait(false);
                if (length > 0) await stream.WriteAsync(payload, 0, length, token).ConfigureAwait(false);
            }

            private sealed class OutboundFrame
            {
                internal int Opcode;
                internal byte[] Payload;
            }
        }
    }
}
