using System.Runtime.InteropServices;

namespace IWCoyoteBridge;

internal static class ComboBoxSelfTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ComboInfo
    {
        public int Size;
        public Rect Item, Button;
        public uint ButtonState;
        public nint Combo, Edit, List;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(nint hwnd, ref ComboInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    public static void Run(Form form, ComboBox combo, Action<bool, string> check)
    {
        var oldSize = form.Size;
        combo.Items.Add(new Core.GameWindowInfo(123, 123, "I Wanna " + new string('X', 400) + " | Deaths: 123"));
        void Verify(string name, bool preview)
        {
            form.Activate(); combo.DroppedDown = true; Application.DoEvents();
            try
            {
                var info = new ComboInfo { Size = Marshal.SizeOf<ComboInfo>() };
                check(combo.DropDownWidth == combo.Width, $"{name} managed dropdown width equals body");
                check(GetComboBoxInfo(combo.Handle, ref info) && GetWindowRect(info.List, out var list) &&
                    GetWindowRect(combo.Handle, out var body) && list.Right - list.Left == body.Right - body.Left,
                    $"{name} actual expanded native list width equals body");
                if (preview)
                {
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    using var graphics = Graphics.FromImage(bitmap);
                    graphics.CopyFromScreen(form.Location, Point.Empty, form.Size);
                    bitmap.Save(Path.Combine(AppContext.BaseDirectory, "dropdown-preview.png"));
                }
            }
            finally { combo.DroppedDown = false; }
        }
        try
        {
            Verify("ComboBox long title", preview: true);
            form.Width += 100; Application.DoEvents(); Verify("ComboBox after resize", preview: false);
        }
        finally { form.Size = oldSize; }
    }
}
