namespace IWCoyoteBridge.Core;
// Preserve the existing monitor's baseline/decrease/recovery semantics.
public sealed class TitleDeathDetector(IWindowReader reader)
{
    public DeathMonitor Monitor { get; } = new(reader);
}
