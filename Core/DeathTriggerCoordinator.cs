namespace IWCoyoteBridge.Core;
public sealed class DeathTriggerCoordinator(Func<long>? time = null, Action<string>? logger = null)
{
    private readonly Func<long> now = time ?? (() => Environment.TickCount64);
    private long? lastTitle, lastHotkey;
    public DeathTriggerMode Mode { get; private set; }
    public int DeduplicationMs { get; private set; } = 500;
    public bool IsListening { get; private set; }
    public event EventHandler<DeathDetectedEventArgs>? DeathDetected;
    public void Configure(DeathTriggerMode mode, int milliseconds)
    { Mode = mode; DeduplicationMs = Math.Clamp(milliseconds, 100, 3000); Reset(); }
    public void Start() { Reset(); IsListening = true; }
    public void Stop() { IsListening = false; Reset(); }
    public void Reset() { lastTitle = lastHotkey = null; }
    public bool Submit(DeathDetectedEventArgs e)
    {
        if (!IsListening && e.Source != DeathTriggerSource.Manual) return false;
        if ((e.Source == DeathTriggerSource.TitleCounter && Mode == DeathTriggerMode.Hotkey) ||
            (e.Source == DeathTriggerSource.Hotkey && Mode == DeathTriggerMode.TitleCounter)) return false;
        long clock = now();
        if (Mode == DeathTriggerMode.Both && e.Source != DeathTriggerSource.Manual)
        {
            var other = e.Source == DeathTriggerSource.TitleCounter ? lastHotkey : lastTitle;
            if (other.HasValue && clock - other.Value < DeduplicationMs)
            {
                logger?.Invoke($"{(e.Source == DeathTriggerSource.Hotkey ? "Hotkey" : "Title")} trigger ignored: duplicate within {DeduplicationMs}ms");
                return false;
            }
        }
        if (e.Source == DeathTriggerSource.TitleCounter) lastTitle = clock;
        if (e.Source == DeathTriggerSource.Hotkey) lastHotkey = clock;
        DeathDetected?.Invoke(this, e);
        return true;
    }
}
