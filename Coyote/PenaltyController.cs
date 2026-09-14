using System.Runtime.CompilerServices;
using IWCoyoteBridge.Config;
using IWCoyoteBridge.Core;
namespace IWCoyoteBridge.Coyote;
public sealed class PenaltyController : IDisposable
{
    private readonly CoyoteController coyote;
    private readonly WaveformLibrary library;
    private readonly Func<long> clock;
    private readonly ConditionalWeakTable<DeathDetectedEventArgs, object> seen = new();
    private readonly object gate = new();
    private readonly System.Threading.Timer? recoveryTimer;
    private bool disposed;
    private AppConfig settings;
    private long? lastTrigger, temporaryDeadline;
    private int sequenceA, sequenceB;
    public int CurrentPenaltyA { get; private set; }
    public int CurrentPenaltyB { get; private set; }
    public int LinkedDeaths { get; private set; }
    public int TotalTriggers { get; private set; }
    public int MaximumPenaltyA { get; private set; }
    public int MaximumPenaltyB { get; private set; }
    public double TemporaryRemaining => Math.Max(0, ((temporaryDeadline ?? clock()) - clock()) / 1000.0);
    public PenaltyController(CoyoteController controller, WaveformLibrary waveforms, AppConfig config, Func<long>? time = null)
    {
        coyote = controller; library = waveforms; settings = ConfigManager.Clone(config); clock = time ?? (() => Environment.TickCount64);
        Reset();
        coyote.SafetyStopped += Reset;
        // Recovery must not depend on WM_TIMER, an open dropdown, a blocked UI,
        // or another death event. Injected clocks remain manually driven in tests.
        if (time is null) recoveryTimer = new(_ => Tick(), null, 100, 100);
    }
    public void UpdateSettings(AppConfig config) { lock (gate) { settings = ConfigManager.Clone(config); Reset(); } }
    public void Reset()
    {
        lock (gate)
        {
            CurrentPenaltyA = coyote.Clamp('A', settings.ChannelA.BaseStrength);
            CurrentPenaltyB = coyote.Clamp('B', settings.ChannelB.BaseStrength);
            temporaryDeadline = null; lastTrigger = null; sequenceA = sequenceB = 0;
        }
    }
    public void Tick()
    {
        lock (gate)
        {
            if (disposed) return;
            if (!lastTrigger.HasValue)
            {
                CurrentPenaltyA = coyote.Clamp('A', settings.ChannelA.BaseStrength);
                CurrentPenaltyB = coyote.Clamp('B', settings.ChannelB.BaseStrength);
            }
            CurrentPenaltyA = coyote.Clamp('A', CurrentPenaltyA);
            CurrentPenaltyB = coyote.Clamp('B', CurrentPenaltyB);
            if (temporaryDeadline.HasValue && clock() >= temporaryDeadline)
            {
                temporaryDeadline = null;
                CurrentPenaltyA = coyote.Clamp('A', settings.ChannelA.BaseStrength);
                CurrentPenaltyB = coyote.Clamp('B', settings.ChannelB.BaseStrength);
                var restore = new List<(char Channel, int Strength)>();
                if (settings.ChannelA.Enabled) restore.Add(('A', CurrentPenaltyA));
                if (settings.ChannelB.Enabled) restore.Add(('B', CurrentPenaltyB));
                _ = coyote.RestoreStrengthsAsync(restore);
            }
        }
    }
    private int Next(char channel, ChannelConfig config, int current) => coyote.Clamp(channel, settings.Penalty.Mode switch
    {
        PenaltyMode.Fixed => config.BaseStrength,
        PenaltyMode.Permanent => (long)current + config.DeathIncrement,
        PenaltyMode.Temporary when settings.Penalty.TemporaryStacking && temporaryDeadline.HasValue => (long)current + config.DeathIncrement,
        _ => (long)config.BaseStrength + config.DeathIncrement
    });
    private CoyoteWaveform SelectWave(ChannelConfig config, ref int position, bool advance)
    {
        if (config.WaveformMode == WaveformMode.Single) return library.Get(config.WaveformId);
        var ids = config.WaveformIds.Where(id => library.Items.Any(x => x.Id == id)).ToArray();
        if (ids.Length == 0) throw new FormatException("顺序 / 随机模式至少选择一个有效波形");
        var index = config.WaveformMode == WaveformMode.Random && advance ? Random.Shared.Next(ids.Length) : position % ids.Length;
        if (advance && config.WaveformMode == WaveformMode.Sequence) position = (position + 1) % ids.Length;
        return library.Get(ids[index]);
    }
    public string Preview(char channel)
    {
        lock (gate)
        {
            var config = channel == 'A' ? settings.ChannelA : settings.ChannelB;
            if (!config.Enabled) return $"{channel}：关闭";
            var current = channel == 'A' ? CurrentPenaltyA : CurrentPenaltyB;
            int position = channel == 'A' ? sequenceA : sequenceB;
            var strength = Next(channel, config, current);
            var range = config.RandomEnabled ? config.RandomRange : 0;
            var level = range > 0 ? $"{coyote.Clamp(channel, strength - range)}～{coyote.Clamp(channel, (long)strength + range)}" : strength.ToString();
            string name;
            try { name = config.WaveformMode == WaveformMode.Random ? "随机所选波形" : SelectWave(config, ref position, false).Name; }
            catch (FormatException) { name = "波形未配置"; }
            return $"{channel}：{level} / {name} / {config.DurationMs}ms";
        }
    }
    public Task HandleDeathAsync(DeathDetectedEventArgs e)
    {
        lock (gate)
        {
            if (seen.TryGetValue(e, out _)) return Task.CompletedTask;
            seen.Add(e, new object());
            if (!coyote.LinkEnabled || !coyote.State.Ready || e.Current <= e.Previous) return Task.CompletedTask;
            LinkedDeaths++;
            var now = clock();
            if (lastTrigger.HasValue && now - lastTrigger < settings.Penalty.MinimumTriggerIntervalMs) return Task.CompletedTask;
            Tick();
            // Newly synchronized limits establish the configured base, never a guessed 200.
            if (!lastTrigger.HasValue) { CurrentPenaltyA = coyote.Clamp('A', settings.ChannelA.BaseStrength); CurrentPenaltyB = coyote.Clamp('B', settings.ChannelB.BaseStrength); }
            var nextA = Next('A', settings.ChannelA, CurrentPenaltyA);
            var nextB = Next('B', settings.ChannelB, CurrentPenaltyB);
            var outputs = new List<ChannelOutput>();
            void Add(char channel, ChannelConfig config, int strength, ref int sequence)
            {
                if (!config.Enabled) return;
                var waveform = SelectWave(config, ref sequence, true);
                if (config.RandomEnabled) strength = coyote.Clamp(channel, (long)strength + Random.Shared.Next(-config.RandomRange, config.RandomRange + 1));
                outputs.Add(new(channel, strength, waveform, config.DurationMs,
                    KeepStrength: settings.Penalty.Mode != PenaltyMode.Fixed,
                    RestoreStrength: settings.Penalty.Mode == PenaltyMode.Fixed ? config.BaseStrength : null));
            }
            Add('A', settings.ChannelA, nextA, ref sequenceA);
            Add('B', settings.ChannelB, nextB, ref sequenceB);
            if (!coyote.Play(outputs)) return Task.CompletedTask;
            CurrentPenaltyA = settings.ChannelA.Enabled ? nextA : coyote.Clamp('A', settings.ChannelA.BaseStrength);
            CurrentPenaltyB = settings.ChannelB.Enabled ? nextB : coyote.Clamp('B', settings.ChannelB.BaseStrength);
            lastTrigger = now;
            if (settings.Penalty.Mode == PenaltyMode.Temporary) temporaryDeadline = now + settings.Penalty.TemporaryRecoverySeconds * 1000L;
            MaximumPenaltyA = Math.Max(MaximumPenaltyA, CurrentPenaltyA);
            MaximumPenaltyB = Math.Max(MaximumPenaltyB, CurrentPenaltyB);
            TotalTriggers++;
        }
        return Task.CompletedTask;
    }
    public void ClearStatistics() { lock (gate) { LinkedDeaths = TotalTriggers = MaximumPenaltyA = MaximumPenaltyB = 0; } }
    public void Dispose()
    {
        lock (gate) { disposed = true; temporaryDeadline = null; }
        recoveryTimer?.Dispose();
        coyote.SafetyStopped -= Reset;
    }
}
