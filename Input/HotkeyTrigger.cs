using System.Runtime.InteropServices;
using IWCoyoteBridge.Core;
namespace IWCoyoteBridge.Input;
public sealed class HotkeyTrigger
{
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    public HotkeyConfig Config { get; set; } = new();
    public GameWindowInfo? Target { get; set; }
    public bool Enabled { get; set; }
    public event Action<DeathDetectedEventArgs>? DeathDetected;
    public bool Handle(KeyboardStroke stroke, nint foreground, bool alive)
    {
        if (!Enabled || !stroke.Down || stroke.Repeat || !alive || Target is null || !Config.TryKey(out var key)) return false;
        if (stroke.Key == Keys.F12 && stroke.Ctrl && stroke.Shift) return false;
        if (stroke.Key != key || stroke.Ctrl != Config.Ctrl || stroke.Shift != Config.Shift || stroke.Alt != Config.Alt) return false;
        if (Config.OnlyWhenGameForeground && foreground != Target.Hwnd) return false;
        DeathDetected?.Invoke(new(0, 1, DeathTriggerSource.Hotkey, Config.ToString()));
        return true;
    }
}
