namespace IWCoyoteBridge.Input;
public sealed class HotkeyConfig
{
    public string Key { get; set; } = "R";
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    public bool OnlyWhenGameForeground { get; set; } = true;
    public static bool IsAllowed(Keys key) => key != Keys.None && key is not (Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin) && (int)key is > 0 and < 256;
    public bool TryKey(out Keys key) => Enum.TryParse(Key, true, out key) && IsAllowed(key);
    public bool IsEmergency => TryKey(out var key) && key == Keys.F12 && Ctrl && Shift;
    public override string ToString() => (Ctrl ? "Ctrl + " : "") + (Shift ? "Shift + " : "") + (Alt ? "Alt + " : "") + Key;
}
