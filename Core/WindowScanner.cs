using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

namespace IWCoyoteBridge.Core;

public interface IWindowReader
{
    bool TryRead(nint hwnd, uint processId, out string title);
}

public sealed class WindowScanner : IWindowReader
{
    private delegate bool EnumWindowsProc(nint hwnd, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int capacity);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    public IReadOnlyList<GameWindowInfo> Scan(string? customKeyword = null) => GetVisibleWindows();
    public IReadOnlyList<GameWindowInfo> GetVisibleWindows()
    {
        var result = new List<GameWindowInfo>();
        EnumWindows((hwnd, parameter) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            long style = Environment.Is64BitProcess ? GetWindowLongPtr(hwnd, -20).ToInt64() : GetWindowLongW(hwnd, -20);
            if ((style & 0x80) != 0) return true; // WS_EX_TOOLWINDOW, never filter process names.
            if (DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != Environment.ProcessId && TryRead(hwnd, pid, out var title))
            {
                string name;
                try { using var process = Process.GetProcessById((int)pid); name = process.ProcessName + ".exe"; }
                catch { name = $"未知进程 (PID {pid})"; }
                result.Add(new(hwnd, pid, title, name));
            }
            return true;
        }, 0);
        return result.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public static bool IsAlive(nint hwnd, uint processId) => IsWindow(hwnd) &&
        GetWindowThreadProcessId(hwnd, out var actual) != 0 && actual == processId;

    public bool TryRead(nint hwnd, uint processId, out string title)
    {
        title = "";
        if (!IsWindow(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var actualPid);
        if (actualPid != processId) return false;
        var length = GetWindowTextLengthW(hwnd);
        if (length <= 0 || length > 32767) return false;
        // Allow the title to grow between the two API calls.
        var buffer = new StringBuilder(32768);
        if (GetWindowTextW(hwnd, buffer, buffer.Capacity) <= 0 || !IsWindow(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out actualPid);
        if (actualPid != processId) return false;
        title = buffer.ToString();
        return true;
    }
}
