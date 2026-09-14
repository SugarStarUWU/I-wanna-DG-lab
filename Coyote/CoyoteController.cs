using IWCoyoteBridge.Config;
namespace IWCoyoteBridge.Coyote;
public sealed record ChannelOutput(char Channel, int Strength, CoyoteWaveform Waveform, int DurationMs, bool KeepStrength = false, int? RestoreStrength = null);
public sealed class CoyoteController : IDisposable
{
    private readonly object gate = new();
    private readonly CoyoteOutputQueue queue;
    private readonly Action<string> log;
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly SemaphoreSlim stopGate = new(1, 1);
    
    private IDglabSocketClient? connection;
    public bool SocketDebugLogging { get => connection?.DebugLogging ?? false; set { if (connection is not null) connection.DebugLogging = value; debugLogging = value; } }
    private bool debugLogging;
    internal Func<IDglabSocketClient>? ClientFactory { get; set; }
    public string PairUrl => connection?.PairUrl ?? "";
    private AppConfig settings;
    private bool linked;
    private long epoch;
    private int stopsInProgress;
    private int connecting;
    public bool IsConnecting => Volatile.Read(ref connecting) != 0;
    public bool IsStopping => Volatile.Read(ref stopsInProgress) != 0;
    private CoyoteState idleState = new();
    public CoyoteState State => connection?.State ?? idleState;
    public string ControllerId => connection?.ControllerId ?? "";
    public bool LinkEnabled { get { lock (gate) return linked; } }
    public event Action<CoyoteState>? StateChanged;
    public event Action? SafetyStopped;
    public CoyoteController(AppConfig config, Action<string> logger)
    {
        settings = ConfigManager.Clone(config); ConfigManager.Normalize(settings); log = logger;
        queue = new(ex => { log("输出异常：" + ex.Message); _ = StopAsync(true); });
    }
    public void UpdateSettings(AppConfig config) { var copy = ConfigManager.Clone(config); ConfigManager.Normalize(copy); lock (gate) settings = copy; }
    public async Task ConnectAsync(CancellationToken token = default)
    {
        if (Interlocked.CompareExchange(ref connecting, 1, 0) != 0) throw new InvalidOperationException("连接正在进行，请勿重复连接");
        try
        {
        if (connection is not null) await StopAsync(true).ConfigureAwait(false);
        if (Volatile.Read(ref stopsInProgress) != 0) throw new InvalidOperationException("请等待安全停止完成");
        lock (gate) { linked = false; epoch++; }
        var manager = ClientFactory?.Invoke() ?? new DglabV3SocketClient(log, settings.Socket.RelayUrl);
        manager.DebugLogging = debugLogging;
        connection = manager;
        manager.StateChanged += state =>
        {
            if (!ReferenceEquals(connection, manager)) return;
            if (!state.Ready) DisableImmediately();
            StateChanged?.Invoke(state);
        };
        try { await manager.ConnectAsync(token).ConfigureAwait(false); }
        catch { if (ReferenceEquals(connection, manager)) DisableImmediately(); throw; }
        }
        finally { Volatile.Write(ref connecting, 0); }
    }
    public int EffectiveMax(char channel)
    {
        var state = State;
        lock (gate) return Math.Min(channel == 'A' ? settings.ChannelA.MaxStrength : settings.ChannelB.MaxStrength,
            (channel == 'A' ? state.LimitA : state.LimitB) ?? 0);
    }
    public int Clamp(char channel, long value) => (int)Math.Clamp(value, 0, EffectiveMax(channel));
    public bool SetLinkEnabled(bool value)
    {
        lock (gate)
        {
            if (value && (!State.Ready || Volatile.Read(ref stopsInProgress) != 0)) return false;
            linked = value; epoch++;
        }
        if (!value) queue.Clear();
        return true;
    }
    private void DisableImmediately()
    {
        lock (gate) { linked = false; epoch++; }
        queue.Clear(); SafetyStopped?.Invoke();
    }
    public bool Play(IReadOnlyList<ChannelOutput> outputs, bool manual = false)
    {
        long permit;
        lock (gate)
        {
            if (!State.Ready || Volatile.Read(ref stopsInProgress) != 0 || (!manual && !linked) || outputs.Count == 0) return false;
            permit = epoch;
            var manager = connection!;
            var snapshot = outputs.ToArray();
            if (snapshot.Any(x => x.Channel is not ('A' or 'B')) || snapshot.Select(x => x.Channel).Distinct().Count() != snapshot.Length)
                throw new FormatException("输出通道须为独立的 A / B");
            foreach (var output in snapshot) output.Waveform.Validate();
            _ = queue.Enqueue(token => PlayAsync(snapshot, manager, permit, manual, token));
        }
        return true;
    }
    private bool Permit(long permit, bool manual)
    {
        lock (gate) return epoch == permit && State.Ready && (manual || linked);
    }
    private static async Task CommandAsync(IDglabSocketClient? manager, string message, CancellationToken token)
    {
        if (manager is null) throw new IOException("未连接");
        await manager.SendAsync(message, token).ConfigureAwait(false);
    }
    private async Task SendOutputAsync(ChannelOutput output, IDglabSocketClient manager, long permit, bool manual, CancellationToken token)
    {
        await commandGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!Permit(permit, manual)) throw new OperationCanceledException(token);
            await CommandAsync(manager, CoyoteProtocol.Clear(output.Channel), token).ConfigureAwait(false);
            await Task.Delay(20, token).ConfigureAwait(false);
            if (!Permit(permit, manual)) throw new OperationCanceledException(token);
            await CommandAsync(manager, CoyoteProtocol.Strength(output.Channel, Clamp(output.Channel, output.Strength)), token).ConfigureAwait(false);
            // Segment under the complete JSON 1950-character limit, not only the pulse payload.
            foreach (var chunk in output.Waveform.ForDuration(output.DurationMs).Chunk(40))
            {
                if (!Permit(permit, manual)) throw new OperationCanceledException(token);
                await CommandAsync(manager, CoyoteProtocol.Pulse(output.Channel, chunk), token).ConfigureAwait(false);
            }
        }
        finally { commandGate.Release(); }
    }
    private async Task PlayAsync(IReadOnlyList<ChannelOutput> outputs, IDglabSocketClient manager, long permit, bool manual, CancellationToken token)
    {
        bool completed = false;
        try
        {
            var starts = new Dictionary<char, long>();
            foreach (var output in outputs) { await SendOutputAsync(output, manager, permit, manual, token).ConfigureAwait(false); starts[output.Channel] = Environment.TickCount64; }
            foreach (var output in outputs.OrderBy(x => x.DurationMs))
            {
                var remaining = output.DurationMs - (Environment.TickCount64 - starts[output.Channel]);
                if (remaining > 0) await Task.Delay((int)remaining, token).ConfigureAwait(false);
                if (!output.KeepStrength && !output.RestoreStrength.HasValue)
                    await ZeroAsync([output.Channel], manager).ConfigureAwait(false);
                else
                {
                    await commandGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (!Permit(permit, manual)) throw new OperationCanceledException(token);
                        if (output.RestoreStrength.HasValue)
                            await CommandAsync(manager, CoyoteProtocol.Strength(output.Channel, Clamp(output.Channel, output.RestoreStrength.Value)), token).ConfigureAwait(false);
                        await CommandAsync(manager, CoyoteProtocol.Clear(output.Channel), token).ConfigureAwait(false);
                    }
                    finally { commandGate.Release(); }
                }
            }
            completed = true;
            log("波形输出完成");
        }
        finally
        {
            // The single worker cannot start a successor until this cleanup completes.
            if (!completed) await ZeroAsync(outputs.Select(x => x.Channel), manager).ConfigureAwait(false);
        }
    }
    public Task RestoreStrengthsAsync(IReadOnlyList<(char Channel, int Strength)> targets)
    {
        lock (gate)
        {
            if (!linked || !State.Ready || targets.Count == 0 || Volatile.Read(ref stopsInProgress) != 0) return Task.CompletedTask;
            var manager = connection; var permit = epoch; var snapshot = targets.ToArray();
            return queue.Enqueue(async token =>
            {
                await commandGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    foreach (var (channel, _) in snapshot)
                    {
                        if (!Permit(permit, false)) return;
                        await CommandAsync(manager, CoyoteProtocol.Clear(channel), token).ConfigureAwait(false);
                        await Task.Delay(150, token).ConfigureAwait(false);
                    }
                    var beforeRestore = State;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        foreach (var (channel, strength) in snapshot)
                        {
                            if (!Permit(permit, false)) return;
                            int target = Clamp(channel, strength);
                            int? actual = channel == 'A' ? State.ActualA : State.ActualB;
                            // Retries must never raise strength after a user lowered it.
                            if (attempt > 0 && actual <= target) continue;
                            log($"临时恢复发送：{channel}={target}（第{attempt + 1}次）");
                            await CommandAsync(manager, CoyoteProtocol.Strength(channel, target), token).ConfigureAwait(false);
                            await Task.Delay(150, token).ConfigureAwait(false);
                        }
                        for (int wait = 0; wait < 10; wait++)
                        {
                            if (!Permit(permit, false)) return;
                            if (!ReferenceEquals(State, beforeRestore) && snapshot.All(x => (x.Channel == 'A' ? State.ActualA : State.ActualB) == Clamp(x.Channel, x.Strength)))
                            {
                                log($"临时惩罚结束：APP 已确认恢复 A={State.ActualA} B={State.ActualB}");
                                return;
                            }
                            await Task.Delay(50, token).ConfigureAwait(false);
                        }
                    }
                    log($"临时恢复未获 APP 确认：实际 A={State.ActualA} B={State.ActualB}；请检查设备/APP，发送成功不代表恢复成功");
                }
                finally { commandGate.Release(); }
            });
        }
    }
    private async Task ZeroAsync(IEnumerable<char> channels, IDglabSocketClient? manager)
    {
        using var deadline = new CancellationTokenSource(1200);
        try
        {
            await commandGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                foreach (var channel in channels.Distinct())
                {
                    await CommandAsync(manager, CoyoteProtocol.Strength(channel, 0), deadline.Token).ConfigureAwait(false);
                    await CommandAsync(manager, CoyoteProtocol.Clear(channel), deadline.Token).ConfigureAwait(false);
                }
            }
            finally { commandGate.Release(); }
        }
        catch (Exception ex) { log("安全归零未确认：" + ex.Message); }
    }
    public async Task SetStrengthAsync(char channel, int value)
    {
        if (!State.Ready) throw new InvalidOperationException("APP 状态尚未同步");
        if (channel is not ('A' or 'B')) throw new ArgumentOutOfRangeException(nameof(channel));
        var manager = connection;
        long permit; lock (gate) permit = epoch;
        await queue.Enqueue(async token =>
        {
            await commandGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!Permit(permit, true)) return;
                await CommandAsync(manager, CoyoteProtocol.Strength(channel, Clamp(channel, value)), token).ConfigureAwait(false);
            }
            finally { commandGate.Release(); }
        }).ConfigureAwait(false);
    }
    public Task AddStrengthAsync(char channel, int delta) => SetStrengthAsync(channel,
        Clamp(channel, (long)((channel == 'A' ? State.ActualA : State.ActualB) ?? 0) + delta));
    public async Task StopAsync(bool disconnect = false)
    {
        var manager = connection;
        
        Interlocked.Increment(ref stopsInProgress);
        DisableImmediately();
        await stopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(connection, manager)) return;
            await queue.DrainAsync().ConfigureAwait(false);
            if (manager?.State.Status == CoyoteConnectionStatus.Paired)
                await ZeroAsync(['A', 'B'], manager).ConfigureAwait(false);
            if (disconnect)
            {
                
                idleState = new(CoyoteConnectionStatus.Disconnected);
                connection = null;
                if (manager is not null) await manager.DisposeAsync().ConfigureAwait(false);
                StateChanged?.Invoke(idleState);
            }
        }
        finally { stopGate.Release(); Interlocked.Decrement(ref stopsInProgress); }
    }
    public void Dispose() { DisableImmediately(); connection?.DisposeAsync().AsTask().GetAwaiter().GetResult(); queue.Dispose(); }
}
