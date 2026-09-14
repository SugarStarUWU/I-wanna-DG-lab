using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using IWCoyoteBridge.Config;
using IWCoyoteBridge.Core;
using IWCoyoteBridge.Coyote;
namespace IWCoyoteBridge;
internal static class SocketUiSelfTests
{
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    public static void Run(MainForm main, Form game, Action<bool, string> check, Action<Func<bool>> pump)
    {
        // The in-process fixture is intentionally excluded by the production scanner.
        // Keep its test-only selection stable; real cross-process enumeration is tested separately.
        var scannerTimer = (System.Windows.Forms.Timer)typeof(MainForm).GetField("scanTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(main)!;
        scannerTimer.Stop();
        var tabs = (TabControl)main.Controls.Find("MainTabs", true).Single();
        foreach (TabPage page in tabs.TabPages)
        {
            tabs.SelectedTab = page; Application.DoEvents();
            using var preview = new Bitmap(main.Width, main.Height);
            ShowWindow(main.Handle, 5); main.BringToFront(); SetForegroundWindow(main.Handle);
            Application.DoEvents(); main.Update(); Thread.Sleep(250); Application.DoEvents();
            using var graphics = Graphics.FromImage(preview);
            graphics.CopyFromScreen(main.Location, Point.Empty, main.Size);
            preview.Save(Path.Combine(AppContext.BaseDirectory, $"tab-{page.Text}.png"));
        }
        check(tabs.TabPages.Count == 5 && main.Height <= 900, "five tabs fit normal desktop");
        tabs.SelectedIndex = 1;
        using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        SocketSelfTests.MockApp.PrepareRelay(main.DeviceController, port);
        ((Button)main.Controls.Find("ConnectCoyote", true).Single()).PerformClick();
        pump(() => Application.OpenForms.OfType<QrConnectionForm>().Any());
        var qr = Application.OpenForms.OfType<QrConnectionForm>().Single();
        check(qr.CurrentQrContent == DglabV3Protocol.QrPayload(main.DeviceController.PairUrl), "QR uses actual relay-issued identity");
        check(main.Controls.Find("SocketPort", true).Length == 0 && qr.Controls.Find("ConnectionAddress", true).Length == 0, "no port or LAN selector in UI");
        check(!main.DeviceController.State.Ready && !main.DeviceController.LinkEnabled, "waiting APP cannot output");
        using (var preview = new Bitmap(qr.Width, qr.Height))
        {
            qr.DrawToBitmap(preview, new Rectangle(Point.Empty, qr.Size)); preview.Save(Path.Combine(AppContext.BaseDirectory, "qr-preview.png"));
        }
        var app = new SocketSelfTests.MockApp();
        var connect = Task.Run(async () => { await app.Connect(main.DeviceController, port); await app.Feedback(3, 4, 15, 12); });
        pump(() => connect.IsCompleted && main.DeviceController.State.Ready && main.DeviceController.LinkEnabled); connect.GetAwaiter().GetResult();
        check(main.DeviceController.LinkEnabled && app.Received.IsEmpty, "listening game authorizes output after APP sync without emitting");
        pump(() => qr.IsDisposed);
        check(main.DeviceController.State.Ready && !Application.OpenForms.OfType<QrConnectionForm>().Any(), "successful pairing auto closes only QR dialog without disconnect");
        check(main.Controls.Find("LinkEnabled", true).Length == 0, "no separate death linkage checkbox");
        check(main.Controls.Find("TestBoth", true).Length == 0, "redundant combined test button removed");
        var overlayButton = (Button)main.Controls.Find("ShowStrengthOverlay", true).Single();
        overlayButton.PerformClick(); Application.DoEvents();
        var overlay = Application.OpenForms.OfType<StrengthOverlayForm>().Single();
        check(overlay.Owner is null && overlay.TopMost && overlay.ShowInTaskbar, "OBS overlay is standalone and topmost");
        check(overlay.Controls.Find("OverlayStrengthA", true).Single().Text == "3" && overlay.Controls.Find("OverlayStrengthB", true).Single().Text == "4", "overlay shows APP actual strength not configured penalty targets");
        awaitFeedback();
        void awaitFeedback()
        {
            var feedback = app.Feedback(4, 7, 15, 12);
            pump(() => feedback.IsCompleted && overlay.Controls.Find("OverlayStrengthB", true).Single().Text == "7");
            feedback.GetAwaiter().GetResult();
        }
        using (var image = new Bitmap(overlay.Width, overlay.Height))
        { overlay.DrawToBitmap(image, new Rectangle(Point.Empty, overlay.Size)); image.Save(Path.Combine(AppContext.BaseDirectory, "strength-overlay.png")); }
        ShowWindow(main.Handle, 6); Application.DoEvents();
        check(overlay.Visible, "minimizing main window does not hide OBS overlay");
        ShowWindow(main.Handle, 5); Application.DoEvents(); overlay.Close(); Application.DoEvents();
        check(main.DeviceController.LinkEnabled && main.DeviceController.State.Ready && app.Received.IsEmpty, "closing overlay sends no control and preserves connection");
        CoyoteState disconnected = new();
        using (var preview = new StrengthOverlayForm(() => disconnected))
        {
            check(preview.Controls.Find("OverlayStrengthA", true).Single().Text == "—", "overlay disconnected strength is unknown not stale or zero");
        }
        void ResumeOutput()
        {
            var startButton = (Button)main.Controls.Find("StartMonitoring", true).Single();
            pump(() => startButton.Enabled);
            tabs.SelectedIndex = 0; Application.DoEvents(); startButton.PerformClick();
            pump(() => main.DeviceController.LinkEnabled);
            tabs.SelectedIndex = 1; Application.DoEvents();
        }
        tabs.SelectedIndex = 2; Application.DoEvents();
        var mode = (ComboBox)main.Controls.Find("PenaltyMode", true).Single();
        var seconds = (NumericUpDown)main.Controls.Find("RecoverySeconds", true).Single();
        var wave = (ComboBox)main.Controls.Find("WaveformA", true).Single();
        int waveChanges = 0; wave.SelectedIndexChanged += (_, _) => waveChanges++;
        mode.SelectedIndex = (int)PenaltyMode.Permanent;
        pump(() => main.DeviceController.LinkEnabled && !main.DeviceController.IsStopping);
        check(waveChanges == 0, "penalty mode edit does not reload waveform choices");
        mode.DroppedDown = true; seconds.Value += 1;
        pump(() => main.DeviceController.LinkEnabled && !main.DeviceController.IsStopping);
        Thread.Sleep(350); Application.DoEvents();
        check(mode.DroppedDown && waveChanges == 0, "seconds edit and timer ticks keep open dropdown and selections intact");
        mode.DroppedDown = false; app.Clear();
        game.Text = "寄：1001";
        pump(() => app.Pulses('A') >= 1 && app.Received.Any(x => x.message == "strength-1+2+10"));
        check(true, "real title death after live settings edit increases actual A strength");
        seconds.Value += 1;
        ((Button)main.Controls.Find("EmergencyStop", true).Single()).PerformClick();
        pump(() => !main.DeviceController.IsStopping);
        Thread.Sleep(250); Application.DoEvents();
        check(!main.DeviceController.LinkEnabled, "edit completion cannot reauthorize after emergency stop");
        ResumeOutput(); app.Clear();
        var deathLabel = (Label)main.Controls.Find("CurrentDeaths", true).Single();
        var savedDeath = deathLabel.Text;
        tabs.SelectedIndex = 0; Application.DoEvents();
        ((Button)main.Controls.Find("SimulateDeath", true).Single()).PerformClick();
        pump(() => app.Pulses('A') >= 1);
        tabs.SelectedIndex = 1; Application.DoEvents();
        check(deathLabel.Text == savedDeath, "manual death linked output leaves title baseline untouched");
        ((Button)main.Controls.Find("EmergencyStop", true).Single()).PerformClick();
        ResumeOutput();
        Thread.Sleep(30); Application.DoEvents(); app.Clear();
        // No real device is involved: these messages go only to the loopback MockApp.
        ((Button)main.Controls.Find("TestA", true).Single()).PerformClick();
        pump(() => app.Pulses('A') >= 1);
        check(app.Pulses('B') == 0, "UI manual A sends no B waveform");
        ((Button)main.Controls.Find("EmergencyStop", true).Single()).PerformClick();
        pump(() => !main.DeviceController.LinkEnabled && app.Received.Any(x => x.message == "clear-2"));
        ResumeOutput();
        // Native keyboard installation/edge/foreground routing is exercised separately.
        // SetForegroundWindow can block indefinitely for this reused fixture on Windows.
        PostMessage(main.Handle, 0x0312, 1920, 0);
        pump(() => !main.DeviceController.LinkEnabled);
        check(!main.DeviceController.LinkEnabled, "WM_HOTKEY emergency handler disables linked output");
        ResumeOutput();
        game.Close(); Application.DoEvents();
        pump(() => !main.DeviceController.LinkEnabled && !main.DeviceController.State.Ready);
        check(true, "UI game destroyed HWND safety stops device and link");
        app.DisposeAsync().GetAwaiter().GetResult();
        tabs.SelectedIndex = 0;
        TestFiles(check);
        scannerTimer.Start();
    }
    private static Input Key(ushort key, bool up = false) => new() { Type = 1, VirtualKey = key, Flags = up ? 2u : 0u };
    private static void TestFiles(Action<bool, string> check)
    {
        // Keep all test-generated files inside a unique test directory.
        var path = Path.Combine(Path.GetTempPath(), "IW-Coyote-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var library = new WaveformLibrary(Path.Combine(path, "waveforms"));
            var source = Path.Combine(path, "example.json");
            File.WriteAllText(source, JsonSerializer.Serialize(new CoyoteWaveform("source", "导入测试", ["0A0A0A0A14141414"])));
            var imported = library.Import(source);
            var restored = new WaveformLibrary(Path.Combine(path, "waveforms"));
            check(restored.Get(imported.Id).Name == "导入测试", "custom JSON waveform persists after library restart");
            var pulse = Path.Combine(path, "custom.pulse");
            File.WriteAllText(pulse, "Dungeonlab+pulse:0,1,16=0,20,0,1,1/0-1,100-1");
            var decoded = library.Import(pulse);
            check(decoded.Frames.Length == 2 && decoded.Name == "custom", "custom .pulse import validated and persisted");
            File.WriteAllText(source, "{\"Id\":\"evil\",\"Name\":\"bad\",\"Frames\":[\"FFFFFFFFFFFFFFFF\"]}");
            try { library.Import(source); throw new Exception("bad waveform accepted"); } catch (FormatException) { check(true, "invalid imported HEX byte ranges rejected"); }
            var presets = new PresetManager(Path.Combine(path, "presets"));
            var config = new AppConfig();
            config.ChannelA.BaseStrength = 7; config.ChannelB.DurationMs = 800;
            config.ChannelA.WaveformMode = WaveformMode.Sequence; config.ChannelA.WaveformIds = ["breathing", "tide"];
            var file = presets.Save(new Preset("测试", config));
            var loaded = presets.Import(file, out var limited);
            check(!limited && loaded.Settings.ChannelA.BaseStrength == 7 && loaded.Settings.ChannelB.DurationMs == 800 &&
                loaded.Settings.ChannelA.WaveformIds.Count == 2, "preset channel and waveform mode roundtrip");
            config.ChannelA.BaseStrength = int.MaxValue;
            PresetManager.Export(source, new Preset("超范围", config));
            check(presets.Import(source, out limited).Settings.ChannelA.BaseStrength == 30 && limited, "preset import clamps and reports warning");
            presets.Delete(file);
            check(!File.Exists(file), "preset delete only selected file");
        }
        finally
        {
            // This exact directory was created above, never a workspace or shared Temp root.
            Directory.Delete(path, true);
        }
    }
}
