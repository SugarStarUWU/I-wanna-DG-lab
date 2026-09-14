using System.Runtime.InteropServices;
namespace IWCoyoteBridge.Input;
public sealed record KeyboardStroke(Keys Key, bool Down, bool Ctrl, bool Shift, bool Alt, bool Repeat);
public sealed class GlobalKeyboardHook : IDisposable
{
    private delegate nint HookProc(int code, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    private readonly HashSet<Keys> pressed = [];
    private readonly HookProc callback;
    private nint handle;
    public event Action<KeyboardStroke>? Stroke;
    public bool IsInstalled => handle != 0;
    public GlobalKeyboardHook() => callback = Hook;
    public bool Install()
    {
        if (IsInstalled) return true;
        for (int i = 1; i < 256; i++) if ((GetAsyncKeyState(i) & 0x8000) != 0) pressed.Add((Keys)i);
        handle = SetWindowsHookExW(13, callback, GetModuleHandleW(null), 0);
        return IsInstalled;
    }
    private bool Down(params Keys[] keys) => keys.Any(pressed.Contains);
    private nint Hook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && wParam is 0x0100 or 0x0101 or 0x0104 or 0x0105)
        {
            try
            {
                var key = (Keys)Marshal.PtrToStructure<KeyboardData>(lParam).Key;
                bool down = wParam is 0x0100 or 0x0104;
                bool repeat = down && !pressed.Add(key);
                if (!down) pressed.Remove(key);
                // Never suppress or modify game input. Subscribers must only enqueue short work.
                Stroke?.Invoke(new(key, down, Down(Keys.ControlKey, Keys.LControlKey, Keys.RControlKey),
                    Down(Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey), Down(Keys.Menu, Keys.LMenu, Keys.RMenu), repeat));
            }
            catch { /* Never let a callback exception cross the unmanaged hook boundary. */ }
        }
        return CallNextHookEx(handle, code, wParam, lParam);
    }
    public void Dispose() { if (handle != 0) { UnhookWindowsHookEx(handle); handle = 0; } pressed.Clear(); }
}
