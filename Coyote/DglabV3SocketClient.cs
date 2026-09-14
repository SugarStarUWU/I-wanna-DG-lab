using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
namespace IWCoyoteBridge.Coyote;
public sealed class DglabV3SocketClient : IDglabSocketClient
{
    private readonly Uri relay;
    private readonly Action<string> log;
    private readonly SemaphoreSlim lifecycle = new(1, 1), sendGate = new(1, 1);
    private ClientWebSocket? socket;
    private CancellationTokenSource? lifetime;
    private Task? receiver;
    private TaskCompletionSource<string>? identity;
    private CoyoteState state = new();
    private string controllerId = "", pairedClientId = "";
    public CoyoteState State => Volatile.Read(ref state);
    public string ControllerId => Volatile.Read(ref controllerId);
    public string PairedClientId => Volatile.Read(ref pairedClientId);
    public string PairUrl => ControllerId.Length == 0 ? "" : DglabV3Protocol.PairUrl(relay.AbsoluteUri, ControllerId);
    public bool DebugLogging { get; set; }
    public event Action<CoyoteState>? StateChanged;
    public DglabV3SocketClient(Action<string> logger, string relayUrl = DglabV3Protocol.OfficialRelayUrl)
    {
        relay = new Uri(relayUrl); log = logger;
        if (relay.Scheme != "wss" && !(relay.Scheme == "ws" && relay.IsLoopback)) throw new ArgumentException("中继必须使用 wss");
    }
    private void Set(CoyoteState next) { Volatile.Write(ref state, next); StateChanged?.Invoke(next); }
    public async Task ConnectAsync(CancellationToken token = default)
    {
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await CleanupAsync().ConfigureAwait(false);
            identity = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lifetime = new CancellationTokenSource();
            socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            Set(new(CoyoteConnectionStatus.ConnectingRelay)); log("Connecting relay: " + relay);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await socket.ConnectAsync(relay, timeout.Token).ConfigureAwait(false);
            log("Relay websocket connected"); Set(new(CoyoteConnectionStatus.WaitingForTargetId)); log("Waiting for targetId");
            receiver = ReceiveLoopAsync(socket, lifetime.Token);
            using var idTimeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            idTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            await identity.Task.WaitAsync(idTimeout.Token).ConfigureAwait(false);
            // No APP-wait timer: follow official relay behavior, not the 8-second connect deadline.
        }
        catch (Exception ex)
        {
            log("Relay connection failed: " + ex);
            await CleanupAsync().ConfigureAwait(false);
            Set(new(CoyoteConnectionStatus.Error, Detail: "连接官方中继失败：" + ex.Message));
            throw;
        }
        finally { lifecycle.Release(); }
    }
    // dglab-kit base/index.ts handleSocketMessage/handleSocketClose and socketGeneration:
    // each reconnect awaits cancellation of this receiver before starting its successor.
    private async Task ReceiveLoopAsync(ClientWebSocket current, CancellationToken token)
    {
        try
        {
            var bytes = new byte[8192];
            while (!token.IsCancellationRequested)
            {
                int length = 0; WebSocketReceiveResult result;
                do
                {
                    result = await current.ReceiveAsync(new ArraySegment<byte>(bytes, length, bytes.Length - length), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) throw new IOException($"中继服务器已断开：{result.CloseStatus} {result.CloseStatusDescription}");
                    if (result.MessageType != WebSocketMessageType.Text || length + result.Count >= bytes.Length) throw new FormatException("Socket 消息类型或长度无效");
                    length += result.Count;
                } while (!result.EndOfMessage);
                var text = new UTF8Encoding(false, true).GetString(bytes, 0, length);
                if (DebugLogging) log("[WS IN] " + SafeDebug(text));
                HandleProtocolMessage(text);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log("Socket receive failed: " + ex);
            identity?.TrySetException(ex);
            controllerId = pairedClientId = "";
            Set(new(CoyoteConnectionStatus.Disconnected, Detail: "中继服务器已断开：" + ex.Message));
            current.Abort();
        }
    }
    // Exact V3 mapping: src/socket/v3/index.ts handleProtocolMessage,
    // handleInitialBind(clientId), handlePairBind(targetId), parseDeviceMessage.
    internal void HandleProtocolMessage(string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { log("Relay data: " + text[..Math.Min(200, text.Length)]); return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("无效 Socket 帧");
            string Field(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string type = root.TryGetProperty("type", out var t) ? t.ToString() : "";
            var client = Field("clientId"); var target = Field("targetId"); var message = Field("message");
            if (type == "heartbeat") return; // Official V3 only receives heartbeats; no invented reply.
            if (type == "bind")
            {
                if (target.Length == 0)
                {
                    if (!Guid.TryParseExact(client, "D", out _) || ControllerId.Length != 0) throw new FormatException("无效或重复初始 bind");
                    controllerId = client; log("targetId received: " + client); log("Pair URL: " + PairUrl);
                    Set(new(CoyoteConnectionStatus.WaitingForApp)); identity?.TrySetResult(client); return;
                }
                if (client != ControllerId || !Guid.TryParseExact(target, "D", out _) || PairedClientId.Length != 0) throw new FormatException("绑定 ID 不匹配");
                log("APP connection received"); Set(new(CoyoteConnectionStatus.Binding)); log("Binding APP...");
                if (message != "200") throw new FormatException("Bind failed: code=" + message);
                pairedClientId = target; log("Bind success"); log("Paired client: " + target); log("Waiting for strength state");
                Set(new(CoyoteConnectionStatus.Paired, ConnectedSince: DateTimeOffset.Now)); return;
            }
            if (type == "error") throw new FormatException("Relay error: code=" + message);
            if (type == "break")
            {
                if (target != PairedClientId) return;
                pairedClientId = ""; log("APP disconnected: " + message);
                Set(new(CoyoteConnectionStatus.WaitingForApp, Detail: "APP 已断开，等待 APP 扫码")); return;
            }
            if (type is "msg" or "4")
            {
                // APP -> controller uses clientId=APP and targetId=controller (real V3
                // relay capture). Legacy relay forwarding may retain the bind orientation.
                // Accept only these two orientations of the CURRENT pair, never unrelated IDs.
                bool currentPair = (client == PairedClientId && target == ControllerId) ||
                    (client == ControllerId && target == PairedClientId);
                if (State.Status != CoyoteConnectionStatus.Paired || !currentPair)
                    throw new FormatException($"APP 消息与当前配对不匹配：clientId={client}, targetId={target}");
                if (!message.StartsWith("strength-", StringComparison.Ordinal)) return; // feedback and other data never trigger output.
                if (!CoyoteProtocol.TryStrength(message, out var a, out var b, out var la, out var lb)) throw new FormatException("APP 强度状态无效");
                log($"Strength: A={a} B={b} LimitA={la} LimitB={lb}");
                Set(State with { ActualA = a, ActualB = b, LimitA = la, LimitB = lb });
            }
        }
    }
    public async Task SendAsync(string command, CancellationToken token)
    {
        await sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (State.Status != CoyoteConnectionStatus.Paired || socket?.State != WebSocketState.Open) throw new IOException("APP 未配对");
            var json = DglabV3Protocol.Command(ControllerId, PairedClientId, command);
            if (DebugLogging) log("[WS OUT] " + SafeDebug(json));
            // Once committed, finish the send rather than aborting the socket on job cancellation.
            using var deadline = new CancellationTokenSource(1000);
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
        }
        finally { sendGate.Release(); }
    }
    private static string SafeDebug(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var safe = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                safe[prop.Name] = prop.Name is "type" or "clientId" or "targetId" or "message" or "channel" or "strength" or "time" ? prop.Value.Clone() : "[redacted]";
            return JsonSerializer.Serialize(safe);
        }
        catch { return "[non-protocol data omitted]"; }
    }
    private async Task CleanupAsync()
    {
        lifetime?.Cancel(); socket?.Abort();
        if (receiver is not null) await receiver.ConfigureAwait(false);
        receiver = null; socket?.Dispose(); socket = null; lifetime?.Dispose(); lifetime = null;
        controllerId = pairedClientId = "";
    }
    public async Task DisconnectAsync()
    {
        // Cancels an in-flight connect before acquiring its lifecycle lock.
        lifetime?.Cancel(); socket?.Abort();
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { await CleanupAsync().ConfigureAwait(false); Set(new(CoyoteConnectionStatus.Disconnected)); }
        finally { lifecycle.Release(); }
    }
    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
