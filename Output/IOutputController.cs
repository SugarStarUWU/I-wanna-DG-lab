using IWCoyoteBridge.Core;

namespace IWCoyoteBridge.Output;

public interface IOutputController
{
    void OnDeath();
    // Backwards compatible: existing outputs need not implement event-aware dispatch.
    void OnDeath(DeathDetectedEventArgs death) => OnDeath();
}
