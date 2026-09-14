using System.Media;

namespace IWCoyoteBridge.Output;

public sealed class DebugOutputController(Action<string> log, Action showDeath, Func<bool> sound) : IOutputController
{
    public void OnDeath()
    {
        log("DEATH EVENT");
        showDeath();
        if (sound())
        {
            try { SystemSounds.Beep.Play(); }
            catch (Exception ex) { log($"提示音失败：{ex.Message}"); }
        }
    }
}
