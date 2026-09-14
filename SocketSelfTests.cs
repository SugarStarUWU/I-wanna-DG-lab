using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using IWCoyoteBridge.Config;
using IWCoyoteBridge.Core;
using IWCoyoteBridge.Coyote;
namespace IWCoyoteBridge;
internal static class SocketSelfTests
{
    internal sealed class MockApp : IAsyncDisposable
    {
        private WebSocket socket = null!;
        private static readonly ConcurrentDictionary<int, Task<WebSocket>> Sessions = new();
        public static void PrepareRelay(CoyoteController controller, int port, bool sendIdentity = true, Action<string>? logger = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
            Sessions[port] = AcceptRelay(listener, sendIdentity);
            controller.ClientFactory = () => new DglabV3SocketClient(logger ?? (_ => { }), $"ws://127.0.0.1:{port}/");
        }
        internal static Task<WebSocket> RelaySocket(int port) => Sessions[port];
        private static async Task<WebSocket> AcceptRelay(TcpListener listener, bool sendIdentity)
        {
            using var timeout = new CancellationTokenSource(10000);
            var tcp = await listener.AcceptTcpClientAsync(timeout.Token); listener.Stop();
            var stream = tcp.GetStream(); var request = new List<byte>(); var one = new byte[1];
            while (request.Count < 16384)
            {
                if (await stream.ReadAsync(one, timeout.Token) == 0) throw new IOException("upgrade closed");
                request.Add(one[0]);
                if (request.Count >= 4 && Encoding.ASCII.GetString(request.TakeLast(4).ToArray()) == "\r\n\r\n") break;
            }
            var key = Encoding.ASCII.GetString(request.ToArray()).Split("\r\n").Single(x => x.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            var accept = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), timeout.Token);
            var ws = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30));
            if (sendIdentity) await ws.SendAsync(Encoding.UTF8.GetBytes(CoyoteProtocol.Serialize(new("bind", Guid.NewGuid().ToString("D"), "", "targetId"))), WebSocketMessageType.Text, true, timeout.Token);
            return ws;
        }
        private readonly SemaphoreSlim sendGate = new(1, 1);
        private readonly CancellationTokenSource lifetime = new();
        private Task? worker;
        private string controllerId = "", appId = "";
        private int actualA, actualB, limitA = 40, limitB = 25;
        public int? IgnoreNextStrength;
        public readonly ConcurrentQueue<SocketMessage> Received = new();
        public async Task Connect(CoyoteController controller, int port)
        {
            socket = await Sessions[port];
            controllerId = controller.ControllerId;
            appId = Guid.NewGuid().ToString("D"); // Test relay, never production identity.
            await Send("bind", "200");
            worker = Listen();
        }
        public Task Feedback(int a = 0, int b = 0, int maxA = 40, int maxB = 25)
        {
            actualA = a; actualB = b; limitA = maxA; limitB = maxB;
            return Send("msg", $"strength-{a}+{b}+{maxA}+{maxB}");
        }
        public async Task Send(string type, string message)
        {
            await sendGate.WaitAsync(lifetime.Token);
            // Bind/break are relay notifications; msg is APP -> controller, not bind order.
            bool upstream = type is "msg" or "4";
            try { await socket.SendAsync(Encoding.UTF8.GetBytes(CoyoteProtocol.Serialize(new(type, upstream ? appId : controllerId, upstream ? controllerId : appId, message))), WebSocketMessageType.Text, true, lifetime.Token); }
            finally { sendGate.Release(); }
        }
        private async Task<SocketMessage> Receive()
        {
            var buffer = new byte[7800]; int length = 0;
            // Idle APP sockets stay connected: only lifetime cancellation ends this listener.
            // A per-read 4s timeout aborts a healthy WebSocket and corrupts UI emergency tests.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) throw new IOException("closed");
                length += result.Count;
            } while (!result.EndOfMessage);
            using var doc = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, length));
            var root = doc.RootElement;
            var type = root.GetProperty("type").ToString();
            if (root.GetProperty("clientId").GetString() != controllerId || root.GetProperty("targetId").GetString() != appId)
                throw new FormatException("outgoing relay command IDs reversed");
            var msg = root.GetProperty("message").GetString()!;
            if (type == "3") msg = $"strength-{root.GetProperty("channel").GetInt32()}+2+{root.GetProperty("strength").GetInt32()}";
            if (type == "4") msg = $"clear-{root.GetProperty("channel").GetInt32()}";
            if (type == "clientMsg") msg = "pulse-" + msg;
            return new(type, controllerId, appId, msg);
        }
        private async Task Listen()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var command = await Receive(); Received.Enqueue(command);
                    if (command.type == "3")
                    {
                        var parts = command.message[9..].Split('+');
                        if (IgnoreNextStrength == int.Parse(parts[2])) { IgnoreNextStrength = null; continue; }
                        if (parts[0] == "1") actualA = int.Parse(parts[2]); else actualB = int.Parse(parts[2]);
                        await Feedback(actualA, actualB, limitA, limitB);
                    }
                }
            }
            catch (Exception) when (lifetime.IsCancellationRequested || socket.State != WebSocketState.Open) { }
            catch (OperationCanceledException) { }
        }
        public int Pulses(char channel) => Received.Count(x => x.message.StartsWith($"pulse-{channel}:", StringComparison.Ordinal));
        public void Clear() { while (Received.TryDequeue(out _)) { } }
        public ValueTask DisposeAsync() { lifetime.Cancel(); socket.Abort(); socket.Dispose(); _ = worker; return ValueTask.CompletedTask; }
    }
    public static void Run(Action<bool, string> check, Action<Func<bool>> pump)
    {
        var task = Task.Run(() => RunAsync(check));
        pump(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
    private static async Task Wait(Func<bool> condition)
    {
        var end = Environment.TickCount64 + 3000;
        while (!condition() && Environment.TickCount64 < end) await Task.Delay(10);
        if (!condition()) throw new Exception("Socket condition timeout");
    }
    private static async Task RunAsync(Action<bool, string> check)
    {
        await ProtocolDirectionRegression(check);
        using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var library = new WaveformLibrary(Path.Combine(Path.GetTempPath(), "IW-Coyote-nonexistent-tests"));
        check(library.Items.Count == 16 && library.Get("breathing").Name == "呼吸", "official 16 V3 waveforms validated");
        var decoded = PulseFileParser.Parse("Dungeonlab+pulse:0,1,16=0,20,0,1,1/0-1,100-1");
        check(decoded.SequenceEqual(new[] { "0A0A0A0A00000000", "0A0A0A0A64646464" }), "pulse fixed shape decodes to V3 frames");
        var fast = PulseFileParser.Parse("Dungeonlab+pulse:0,2,16=0,20,0,1,1/0-1,100-1");
        check(fast.Single() == "0A0A0A0A00006464", "pulse 2x speed packs two 50ms bars");
        var gradient = PulseFileParser.Parse("Dungeonlab+pulse:0,1,16=0,20,0,3,1/0-1,100-1");
        check(gradient[1] == "1E1E1E1E64646464", "pulse element frequency gradient");
        var officialShape = PulseFileParser.Parse("Dungeonlab+pulse:35,1,8=0,20,0,1,1/0-1,20-0,40-0,60-0,80-0,100-1,100-1,100-1");
        check(officialShape.Take(8).SequenceEqual(library.Get("breathing").Frames.Take(8)), "pulse decoder cross-check official breathing active shape");
        foreach (var malformed in new[] { "unknown", "Dungeonlab+pulse:0,3,16=0,20,0,1,1/0-1,100-1", "Dungeonlab+pulse:0,1,16=0,20,0,1,1/999-1,100-1" })
        {
            try { PulseFileParser.Parse(malformed); throw new Exception("pulse accepted"); }
            catch (FormatException) { check(true, "malformed pulse fails closed"); }
        }
        check(DglabV3Protocol.QrPayload(DglabV3Protocol.PairUrl(DglabV3Protocol.OfficialRelayUrl, Guid.Empty.ToString("D"))).Contains("#DGLAB-SOCKET#wss://ws.dungeon-lab.cn/"), "official relay QR content");
        try { DglabV3Protocol.PairUrl(DglabV3Protocol.OfficialRelayUrl, "invalid"); throw new Exception("invalid ID accepted"); }
        catch (FormatException) { check(true, "QR rejects invalid server identity"); }
        foreach (var frame in new[] { "NOTHEX", "0000000064646464", "0A0A0A0AFFFFFFFF" })
        {
            try { CoyoteWaveform.ValidateFrame(frame); throw new Exception("frame accepted"); }
            catch (FormatException) { check(true, "malformed V3 frame rejected"); }
        }
        var cfg = new AppConfig { ChannelA = new() { BaseStrength = 10, MaxStrength = 30, DeathIncrement = 5, DurationMs = 100 },
            ChannelB = new() { Enabled = true, BaseStrength = 8, MaxStrength = 20, DeathIncrement = 10, DurationMs = 200, WaveformId = "combo" } };
        var connectionLogs = new ConcurrentQueue<string>();
        using var controller = new CoyoteController(cfg, connectionLogs.Enqueue);
        var transitions = new ConcurrentQueue<CoyoteConnectionStatus>();
        controller.StateChanged += state => transitions.Enqueue(state.Status);
        long now = 0;
        var penalty = new PenaltyController(controller, library, cfg, () => now);
        check(!controller.LinkEnabled && !controller.State.Ready, "startup fail closed");
        check(controller.ControllerId == "" && controller.PairUrl == "", "no manufactured identity or QR before relay handshake");
        MockApp.PrepareRelay(controller, port, logger: connectionLogs.Enqueue); await controller.ConnectAsync();
        check(transitions.Contains(CoyoteConnectionStatus.ConnectingRelay) && transitions.Contains(CoyoteConnectionStatus.WaitingForTargetId) &&
            controller.State.Status == CoyoteConnectionStatus.WaitingForApp && !controller.State.Ready && !controller.SetLinkEnabled(true), "relay connect and identity still wait for APP without output");
        await using var app = new MockApp(); await app.Connect(controller, port);
        await Wait(() => controller.State.Status == CoyoteConnectionStatus.Paired);
        check(controller.State.Status == CoyoteConnectionStatus.Paired && !controller.State.Ready && !controller.SetLinkEnabled(true), "bind without APP limits cannot arm");
        check(app.Received.IsEmpty, "connection and bind produce no hardware output");
        check(!controller.SocketDebugLogging && !connectionLogs.Any(x => x.StartsWith("[WS IN]", StringComparison.Ordinal)), "raw socket debug is off by default");
        check(transitions.Contains(CoyoteConnectionStatus.Binding) && transitions.Contains(CoyoteConnectionStatus.Paired), "official bind 200 transitions binding to paired");
        using (var extra = new ClientWebSocket())
        {
            try { await extra.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/{controller.ControllerId}"), CancellationToken.None); throw new Exception("extra APP accepted"); }
            catch (WebSocketException) { check(controller.State.Status == CoyoteConnectionStatus.Paired, "second APP rejected without disturbing binding"); }
        }
        await app.Feedback(13, 8, 40, 25);
        await Wait(() => controller.State.Ready);
        check(controller.State.ActualA == 13 && controller.State.ActualB == 8 && controller.EffectiveMax('A') == 30 && controller.EffectiveMax('B') == 20, "APP feedback actual values and independent effective limits");
        penalty.Reset();
        await penalty.HandleDeathAsync(new(10, 11));
        check(app.Received.IsEmpty && penalty.TotalTriggers == 0, "death while link disabled produces no output");
        controller.SocketDebugLogging = true;
        controller.Play([new('A', 10, library.Get("breathing"), 100)], true);
        await Wait(() => app.Pulses('A') == 1); await Task.Delay(180);
        check(app.Pulses('A') == 1 && app.Pulses('B') == 0, "manual A only outputs A once");
        check(connectionLogs.Any(x => x.StartsWith("[WS OUT]", StringComparison.Ordinal) && x.Contains("\"type\":3")), "raw debug records actual V3 outgoing strength frame");
        app.Clear();
        controller.Play([new('B', 8, library.Get("combo"), 100)], true);
        await Wait(() => app.Pulses('B') == 1); await Task.Delay(180);
        check(app.Pulses('B') == 1 && app.Pulses('A') == 0, "manual B only outputs B once");
        app.Clear(); controller.SetLinkEnabled(true); penalty.Reset();
        var death = new DeathDetectedEventArgs(10, 11);
        await penalty.HandleDeathAsync(death); await penalty.HandleDeathAsync(death);
        await Wait(() => app.Pulses('A') == 1 && app.Pulses('B') == 1); await Task.Delay(250);
        check(app.Pulses('A') == 1 && app.Pulses('B') == 1 && penalty.TotalTriggers == 1 &&
            penalty.CurrentPenaltyA == 10 && penalty.CurrentPenaltyB == 8, "fixed death A/B once and duplicate suppressed");
        check(app.Received.Any(x => x.message == "strength-1+2+10") && app.Received.Any(x => x.message == "strength-2+2+8"), "independent A/B absolute strengths");
        check(app.Received.Where(x => x.message.StartsWith("pulse-")).All(x => CoyoteProtocol.Serialize(x).Length <= 1950), "complete pulse JSON within protocol limit");
        cfg.Penalty.Mode = PenaltyMode.Permanent; controller.UpdateSettings(cfg); penalty.UpdateSettings(cfg);
        foreach (var target in new[] { 15, 20, 25, 30, 30 })
        {
            now += 400; await penalty.HandleDeathAsync(new(1, 2));
            check(penalty.CurrentPenaltyA == target, $"permanent independent A target {target}");
        }
        check(penalty.CurrentPenaltyB == 20, "independent B increment clamps software max");
        await app.Feedback(0, 0, 20, 25); await Wait(() => controller.EffectiveMax('A') == 20);
        now += 400; await penalty.HandleDeathAsync(new(2, 3));
        check(penalty.CurrentPenaltyA == 20, "APP lowered A limit clamps subsequent penalty");
        await controller.SetStrengthAsync('A', 100); await controller.AddStrengthAsync('A', int.MaxValue);
        await Wait(() => app.Received.Count(x => x.message == "strength-1+2+20") >= 2);
        check(!app.Received.Any(x => x.message.StartsWith("strength-1+2+") && int.Parse(x.message.Split('+')[2]) > 30), "strength overflow and APP caps never exceeded");
        await controller.StopAsync();
        await app.Feedback(0, 0, 40, 25); await Wait(() => controller.EffectiveMax('A') == 30);
        cfg.ChannelA.MaxStrength = 40; cfg.ChannelA.DeathIncrement = 10;
        cfg.Penalty.Mode = PenaltyMode.Temporary; cfg.Penalty.TemporaryRecoverySeconds = 5;
        controller.UpdateSettings(cfg); penalty.UpdateSettings(cfg); controller.SetLinkEnabled(true);
        now += 400; await penalty.HandleDeathAsync(new(10, 11));
        check(penalty.CurrentPenaltyA == 20, "temporary base10 plus10");
        now += 400; await penalty.HandleDeathAsync(new(11, 12));
        check(penalty.CurrentPenaltyA == 20, "temporary stacking off remains20");
        await Task.Delay(350); var pulses = app.Pulses('A'); now += 5000; penalty.Tick(); await Task.Delay(20);
        check(penalty.CurrentPenaltyA == 10 && penalty.CurrentPenaltyB == 8 && app.Pulses('A') == pulses, "temporary inactivity restore without pulse");
        await Wait(() => app.Received.Any(x => x.message == "strength-1+2+10") && app.Received.Any(x => x.message == "strength-2+2+8"));
        check(true, "temporary timer physically restores both enabled channel bases");
        cfg.Penalty.TemporaryStacking = true; penalty.UpdateSettings(cfg); controller.UpdateSettings(cfg);
        foreach (var target in new[] { 20, 30, 40, 40 }) { now += 400; await penalty.HandleDeathAsync(new(1, 2)); check(penalty.CurrentPenaltyA == target, $"temporary stacked A target {target}"); }
        await Wait(() => app.Received.Any(x => x.message == "strength-1+2+40"));
        await Task.Delay(350); app.Clear();
        now += 4999; penalty.Tick(); await Task.Delay(30);
        check(app.Received.IsEmpty && penalty.CurrentPenaltyA == 40, "stacked temporary strength holds until last death plus five seconds");
        now += 1; penalty.Tick();
        await Wait(() => app.Received.Any(x => x.message == "strength-1+2+10") && app.Received.Any(x => x.message == "strength-2+2+8"));
        check(penalty.CurrentPenaltyA == 10 && app.Pulses('A') == 0 && app.Pulses('B') == 0, "stacked recovery sends actual base strength without new waveform");
        app.Clear(); penalty.Tick(); await Task.Delay(30);
        check(app.Received.IsEmpty, "expired recovery runs once only");
        var count = penalty.TotalTriggers; await penalty.HandleDeathAsync(new(20, 0));
        check(penalty.TotalTriggers == count, "death counter decrease no output");
        await controller.StopAsync(); await Task.Delay(30); app.Clear();
        controller.Play([new('A', 25, library.Get("breathing"), 3000)], true);
        await Wait(() => app.Pulses('A') == 1);
        await controller.StopAsync(); await Task.Delay(100);
        check(!controller.LinkEnabled && app.Received.Any(x => x.message == "strength-1+2+0") &&
            app.Received.Any(x => x.message == "strength-2+2+0") && app.Received.Any(x => x.message == "clear-1") &&
            app.Received.Any(x => x.message == "clear-2"), "emergency disables and zeros clears both channels");
        var stoppedPulses = app.Pulses('A'); await Task.Delay(100);
        check(app.Pulses('A') == stoppedPulses && penalty.TemporaryRemaining == 0, "emergency cancels pending waveform and temporary timer");
        await controller.StopAsync(); await Task.Delay(30); app.Clear();
        cfg.Penalty.Mode = PenaltyMode.Fixed; cfg.ChannelB.Enabled = false; cfg.ChannelA.DurationMs = 200;
        cfg.ChannelA.WaveformMode = WaveformMode.Sequence; cfg.ChannelA.WaveformIds = ["breathing", "tide"];
        controller.UpdateSettings(cfg); penalty.UpdateSettings(cfg); controller.SetLinkEnabled(true);
        now += 400; await penalty.HandleDeathAsync(new(30, 31));
        await Wait(() => app.Pulses('A') == 1); await Task.Delay(250);
        check(penalty.Preview('A').Contains("潮汐"), "sequence preview advances to next selected waveform");
        now += 400; await penalty.HandleDeathAsync(new(31, 32));
        await Wait(() => app.Pulses('A') == 2); await Task.Delay(250);
        check(penalty.Preview('A').Contains("呼吸") && app.Received.Any(x => x.message.Contains("0B0B0B0B10101010")), "sequence cycles and sends official tide frames");
        var beforeDebounce = penalty.TotalTriggers;
        now += 50; await penalty.HandleDeathAsync(new(32, 33));
        check(penalty.TotalTriggers == beforeDebounce, "minimum trigger interval skips without replay");
        cfg.ChannelA.WaveformMode = WaveformMode.Random;
        controller.UpdateSettings(cfg); penalty.UpdateSettings(cfg);
        now += 400; await penalty.HandleDeathAsync(new(33, 34));
        await Wait(() => app.Pulses('A') == 3); await Task.Delay(250);
        var randomPulse = app.Received.Where(x => x.message.StartsWith("pulse-A:", StringComparison.Ordinal)).Last().message;
        check(randomPulse.Contains("0B0B0B0B10101010") || randomPulse.Contains("0A0A0A0A14141414"), "random mode only sends selected official waveform");
        await controller.StopAsync(); await Task.Delay(30);
        app.Clear(); controller.SetLinkEnabled(true);
        await app.Send("heartbeat", "200"); await Task.Delay(30);
        check(connectionLogs.Any(x => x.StartsWith("[WS IN]", StringComparison.Ordinal) && x.Contains("heartbeat")), "raw debug records incoming relay frame");
        check(controller.LinkEnabled && app.Received.IsEmpty, "official heartbeat is passive and emits no control");
        var identityBeforeBreak = controller.ControllerId;
        await app.Send("break", "209"); await Wait(() => controller.State.Status == CoyoteConnectionStatus.WaitingForApp);
        check(!controller.LinkEnabled && !controller.State.Ready && controller.ControllerId == identityBeforeBreak && controller.PairUrl.Length > 0,
            "APP break clears pair and limits but keeps live relay identity");
        await app.Send("bind", "200"); await Wait(() => controller.State.Status == CoyoteConnectionStatus.Paired);
        check(!controller.State.Ready && !controller.LinkEnabled, "APP rebind cannot reuse stale strength or arm automatically");
        await app.Feedback(); await Wait(() => controller.State.Ready);
        check(!controller.LinkEnabled && app.Received.IsEmpty, "APP strength resync emits nothing");
        controller.SetLinkEnabled(true);
        await app.Send("msg", "strength-999+0+40+25");
        await Wait(() => !controller.LinkEnabled && controller.State.Status == CoyoteConnectionStatus.Disconnected);
        check(true, "protocol anomaly fails closed and disconnects");
        check(controller.PairUrl == "" && controller.ControllerId == "", "relay loss immediately invalidates QR identity");
        check(app.Received.IsEmpty, "invalid strength state cannot produce output");
        var oldId = controller.ControllerId;
        MockApp.PrepareRelay(controller, port); await controller.ConnectAsync();
        await using (var reconnected = new MockApp())
        {
            await reconnected.Connect(controller, port); await reconnected.Feedback();
            await Wait(() => controller.State.Ready);
            check(!controller.LinkEnabled && controller.ControllerId != oldId, "reconnect receives fresh relay identity and stays disarmed");
            await Task.Delay(50);
            check(reconnected.Received.IsEmpty, "old connection cleanup cannot output on new APP");
            controller.SetLinkEnabled(true);
            await reconnected.DisposeAsync();
            await Wait(() => !controller.LinkEnabled && !controller.State.Ready);
            check(true, "APP disconnect auto disables link and invalidates limits");
        }
        var bad = new AppConfig { ChannelA = new() { BaseStrength = int.MaxValue, MaxStrength = int.MaxValue, DurationMs = int.MaxValue }, Penalty = new() { MinimumTriggerIntervalMs = -1 } };
        check(ConfigManager.Normalize(bad) && bad.ChannelA.BaseStrength == 100 && bad.ChannelA.DurationMs == 3000 && bad.Penalty.MinimumTriggerIntervalMs == 100, "unsafe preset values clamped with detectable warning");
        var serialized = System.Text.Json.JsonSerializer.Serialize(cfg, ConfigManager.JsonOptions);
        check(!serialized.Contains("LinkEnabled") && !serialized.Contains("HttpOutput") && serialized.Contains("Detection"), "config has nested settings and no saved arm or HTTP");
        check(!serialized.Contains("\"Port\"") && serialized.Contains("RelayUrl") && cfg.Socket.Protocol == "V3", "config removes local server and uses official V3 relay");
        using var canceledController = new CoyoteController(new(), _ => { });
        using var cancel = new CancellationTokenSource();
        MockApp.PrepareRelay(canceledController, port, false);
        var connecting = canceledController.ConnectAsync(cancel.Token);
        using var silentRelay = await MockApp.RelaySocket(port);
        await Wait(() => canceledController.State.Status == CoyoteConnectionStatus.WaitingForTargetId);
        check(canceledController.PairUrl == "" && canceledController.ControllerId == "", "silent relay cannot create QR before initial bind");
        cancel.Cancel();
        try { await connecting; throw new Exception("identity cancellation ignored"); }
        catch (OperationCanceledException) { check(true, "identity receive wait cancels and terminates connection task"); }
        await canceledController.StopAsync(true);
        check(!canceledController.State.Ready && canceledController.PairUrl == "", "cancel and dispose clear all pairing state");
        MockApp.PrepareRelay(canceledController, port); await canceledController.ConnectAsync();
        await using var replacementApp = new MockApp(); await replacementApp.Connect(canceledController, port); await replacementApp.Feedback();
        await Wait(() => canceledController.State.Ready);
        check(!canceledController.LinkEnabled && replacementApp.Received.IsEmpty, "reconnect after canceled receive has no residual tasks or output");
        await canceledController.StopAsync(true);
        await TemporaryBRegression(check, port);
    }

    private static async Task TemporaryBRegression(Action<bool, string> check, int port)
    {
        var cfg = new AppConfig
        {
            ChannelA = new() { Enabled = false },
            ChannelB = new() { Enabled = true, BaseStrength = 20, DeathIncrement = 10, MaxStrength = 80, DurationMs = 100 },
            Penalty = new() { Mode = PenaltyMode.Temporary, TemporaryStacking = true, TemporaryRecoverySeconds = 5, MinimumTriggerIntervalMs = 100 }
        };
        var recoveryLogs = new ConcurrentQueue<string>();
        using var controller = new CoyoteController(cfg, recoveryLogs.Enqueue);
        MockApp.PrepareRelay(controller, port); await controller.ConnectAsync();
        await using var app = new MockApp(); await app.Connect(controller, port); await app.Feedback(0, 20, 80, 81);
        await Wait(() => controller.State.Ready);
        long now = 0;
        var penalty = new PenaltyController(controller, new WaveformLibrary(), cfg, () => now);
        controller.SetLinkEnabled(true); penalty.Tick(); await Task.Delay(30);
        check(app.Received.IsEmpty, "B-only baseline and idle timer never output");
        await penalty.HandleDeathAsync(new(20, 21));
        await Wait(() => app.Received.Any(x => x.message == "strength-2+2+30") && app.Pulses('B') == 1);
        await Task.Delay(150);
        check(!app.Received.Any(x => x.message == "strength-2+2+0"), "temporary B stays30 after waveform duration instead of zeroing");
        now += 400; await penalty.HandleDeathAsync(new(21, 22));
        await Wait(() => app.Received.Any(x => x.message == "strength-2+2+40") && app.Pulses('B') == 2);
        await Task.Delay(150); app.Clear();
        now += 4999; penalty.Tick(); await Task.Delay(30);
        check(penalty.CurrentPenaltyB == 40 && app.Received.IsEmpty, "B20 to30 to40 holds until five seconds after latest death");
        now += 1; penalty.Tick();
        await Wait(() => app.Received.Any(x => x.message == "strength-2+2+20"));
        check(penalty.CurrentPenaltyB == 20 && app.Pulses('B') == 0 && !app.Received.Any(x => x.message.StartsWith("strength-1", StringComparison.Ordinal) || x.message == "clear-1"),
            "five-second B recovery restores20 without A commands or pulse");
        cfg.Penalty.TemporaryRecoverySeconds = 1;
        cfg.ChannelB.DurationMs = 100;
        controller.UpdateSettings(cfg);
        using (var autonomous = new PenaltyController(controller, new WaveformLibrary(), cfg))
        {
            app.Clear();
            await autonomous.HandleDeathAsync(new(22, 23));
            await Wait(() => app.Received.Any(x => x.message == "strength-2+2+30"));
            app.IgnoreNextStrength = 20;
            app.Clear();
            // No Tick, UI message pump, or successor death. Recovery also cancels
            // a waveform whose duration exceeds the punishment deadline.
            await Wait(() => controller.State.ActualB == 20 && app.Received.Count(x => x.message == "strength-2+2+20") >= 2);
            check(autonomous.CurrentPenaltyB == 20 && app.Pulses('B') == 0,
                "autonomous recovery lowers actual strength without UI tick or another death");
            await Wait(() => recoveryLogs.Any(x => x.Contains("APP 已确认恢复")));
            check(true, "lost first recovery is retried and success requires APP strength feedback");
            await Task.Delay(250); app.Clear(); await Task.Delay(250);
            check(app.Received.IsEmpty, "autonomous expiry sends recovery only once");
            cfg.ChannelB.DurationMs = 2000; autonomous.UpdateSettings(cfg); controller.UpdateSettings(cfg);
            await autonomous.HandleDeathAsync(new(23, 24));
            await Wait(() => controller.State.ActualB == 30);
            await Wait(() => controller.State.ActualB == 20);
            check(true, "long active waveform cleanup cannot overwrite confirmed expiry base strength");
        }
        await controller.StopAsync(true);
    }

    private static async Task ProtocolDirectionRegression(Action<bool, string> check)
    {
        await using var client = new DglabV3SocketClient(_ => { });
        const string controller = "ca117623-8c97-4af4-a161-21ccf3edeb53";
        const string app = "256839fe-8a45-4454-aab6-42ff80edb79e";
        client.HandleProtocolMessage($"{{\"type\":\"bind\",\"clientId\":\"{controller}\",\"targetId\":\"\",\"message\":\"targetId\"}}");
        client.HandleProtocolMessage($"{{\"type\":\"bind\",\"clientId\":\"{controller}\",\"targetId\":\"{app}\",\"message\":\"200\"}}");
        client.HandleProtocolMessage("{\"type\":\"msg\",\"clientId\":\"256839fe-8a45-4454-aab6-42ff80edb79e\",\"targetId\":\"ca117623-8c97-4af4-a161-21ccf3edeb53\",\"message\":\"strength-0\\u002B20\\u002B80\\u002B81\"}");
        check(client.State.Ready && client.State.ActualA == 0 && client.State.ActualB == 20 && client.State.LimitA == 80 && client.State.LimitB == 81,
            "real APP upstream log with escaped plus signs synchronizes A0 B20 limits80 81");
        client.HandleProtocolMessage(CoyoteProtocol.Serialize(new("msg", controller, app, "strength-1+2+80+81")));
        check(client.State.Ready && client.State.ActualA == 1 && client.State.ActualB == 2, "legacy same-pair forwarding orientation still supported");
        client.HandleProtocolMessage(CoyoteProtocol.Serialize(new("4", app, controller, "strength-3+4+80+81")));
        check(client.State.ActualA == 3 && client.State.ActualB == 4, "V3 alternate forwarded type supports APP upstream IDs");
        foreach (var ids in new[] { (app, Guid.Empty.ToString("D")), (Guid.Empty.ToString("D"), controller), (app, app), (controller, controller), ("", controller) })
        {
            try { client.HandleProtocolMessage(CoyoteProtocol.Serialize(new("msg", ids.Item1, ids.Item2, "strength-99+99+200+200"))); throw new Exception("unrelated pair accepted"); }
            catch (FormatException) { check(client.State.ActualA == 3 && client.State.LimitA == 80, "unrelated/missing pair ID rejected without changing strength"); }
        }
        client.HandleProtocolMessage(CoyoteProtocol.Serialize(new("break", controller, app, "209")));
        try { client.HandleProtocolMessage(CoyoteProtocol.Serialize(new("msg", app, controller, "strength-99+99+200+200"))); throw new Exception("stale APP accepted"); }
        catch (FormatException) { check(!client.State.Ready && client.PairedClientId == "", "disconnected APP stale upstream frame rejected"); }
    }
}
