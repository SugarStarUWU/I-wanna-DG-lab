namespace IWCoyoteBridge.Core;
public enum DeathTriggerMode { TitleCounter, Hotkey, Both }
public enum DeathTriggerSource { TitleCounter, Hotkey, Manual }
public sealed class DeathDetectedEventArgs(int previous, int current,
    DeathTriggerSource source = DeathTriggerSource.TitleCounter, string? key = null) : EventArgs
{
    public int Previous { get; } = previous;
    public int Current { get; } = current;
    public DeathTriggerSource Source { get; } = source;
    public string? Key { get; } = key;
}
