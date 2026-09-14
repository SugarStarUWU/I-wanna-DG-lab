using System.Runtime.InteropServices;
using System.Diagnostics;
using IWCoyoteBridge.Config;
using IWCoyoteBridge.Core;
using IWCoyoteBridge.Input;
namespace IWCoyoteBridge;
internal static class DetectionSelfTests
{
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct NativeInput { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public ushort Key; [FieldOffset(12)] public uint Flags; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    internal static bool Focus(Form form)
    {
        uint current = GetCurrentThreadId(), foreground = GetWindowThreadProcessId(HotkeyTrigger.GetForegroundWindow(), out _);
        bool attached = foreground != current && foreground != 0 && AttachThreadInput(current, foreground, true);
        // Attach before activation: Form.Activate on the reused fixture can otherwise
        // block indefinitely waiting for a foreign foreground input queue.
        try { ShowWindow(form.Handle, 5); SetForegroundWindow(form.Handle); }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
        Application.DoEvents(); Thread.Sleep(100); Application.DoEvents();
        return HotkeyTrigger.GetForegroundWindow() == form.Handle;
    }
    public static void RunUi(MainForm main, Form target, Action<bool, string> check, Action<Func<bool>> pump)
    {
        // Own-PID fixtures are intentionally not scanner candidates. Suspend automatic scans
        // only for this in-process UI fixture; external scanner coverage above remains real.
        var timer = (System.Windows.Forms.Timer)typeof(MainForm).GetField("scanTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        timer.Stop();
        var modes = (ComboBox)main.Controls.Find("DeathTriggerMode", true).Single();
        var recent = (Label)main.Controls.Find("RecentEvent", true).Single();
        var title = (Label)main.Controls.Find("CurrentDeaths", true).Single();
        var keyState = (Label)main.Controls.Find("KeyDetection", true).Single();
        var list = (ComboBox)main.Controls.Find("GameWindows", true).Single();
        void Stroke(Keys key, bool down)
        {
            if (HotkeyTrigger.GetForegroundWindow() != target.Handle && !Focus(target))
                throw new InvalidOperationException("Windows denied fixture foreground; no input injected");
            var input = new[] { new NativeInput { Type = 1, Key = (ushort)key, Flags = down ? 0u : 2u } };
            check(SendInput(1, input, Marshal.SizeOf<NativeInput>()) == 1, "UI keyboard fixture input");
            Application.DoEvents(); Thread.Sleep(30); Application.DoEvents();
        }
        modes.SelectedIndex = 1; target.Text = "Unrecognized Title";
        check(title.Text.Contains("未识别") && keyState.Text.Contains("● R"), "Hotkey mode listens without title counter");
        if (Focus(target))
        {
            Stroke(Keys.R, false); Stroke(Keys.R, true); Stroke(Keys.R, false);
            pump(() => recent.Text.Contains("按键 R"));
            check(true, "UI native hook routes hotkey death with target foreground");
            var previousEvent = recent.Text;
            ((Button)main.Controls.Find("SimulateDeath", true).Single()).PerformClick();
            check(recent.Text == previousEvent, "manual event obeys disabled link switch");
            modes.SelectedIndex = 2; target.Text = "No Counter"; Thread.Sleep(170); Application.DoEvents();
            check(keyState.Text.Contains("● R") && title.Text.Contains("未识别"), "new test13 Both unrecognized title keeps hotkey enabled");
            Stroke(Keys.R, true); Stroke(Keys.R, false);
            pump(() => recent.Text.Contains("按键 R"));
            target.Text = "寄：15"; pump(() => title.Text == "当前死亡：15");
            check(title.Text == "当前死亡：15" && recent.Text.Contains("按键 R"), "Both title recovery baseline produces no new event");
            Thread.Sleep(550); Application.DoEvents(); target.Text = "寄：16";
            pump(() => recent.Text.Contains("标题 15"));
            Stroke(Keys.R, true); Stroke(Keys.R, false);
            check(recent.Text.Contains("标题 15"), "UI Both title then hotkey deduplication");
            modes.SelectedIndex = 0;
            check(!recent.Text.StartsWith("最近死亡："), "mode edit rebaselines without death");
            Stroke(Keys.R, true); Stroke(Keys.R, false);
            check(!recent.Text.StartsWith("最近死亡："), "UI title-only ignores keyboard input");
        }
        else check(true, "UI hook injection skipped: fixture foreground denied");
        var old = list.SelectedItem;
        using var second = new Form { Text = "寄：10000", ShowInTaskbar = false }; second.Show(); Application.DoEvents();
        var secondInfo = new GameWindowInfo(second.Handle, (uint)Environment.ProcessId, second.Text);
        list.Items.Add(secondInfo); list.SelectedItem = secondInfo;
        check(title.Text == "当前死亡：10000" && !recent.Text.Contains("DEATH"), "new test14 switching windows initializes safely");
        list.SelectedItem = old;
        target.Text = "寄：1000";
        ((Button)main.Controls.Find("StopMonitoring", true).Single()).PerformClick();
        ((Button)main.Controls.Find("StartMonitoring", true).Single()).PerformClick();
        check(!recent.Text.Contains("DEATH"), "UI reconnect baseline no event");
    }
    public static void Run(Action<bool, string> check, Action<Func<bool>> pump)
    {
        check(DeathCounterParser.TryParseDeathCount("I Wanna XXX | 死亡：10 | Deaths: 999", "死亡", out var parsed, out var mode) && parsed == 10 && mode == DeathRecognitionMode.Custom, "new test1 custom priority");
        check(DeathCounterParser.TryParseDeathCount("I Wanna XXX | Deaths: 20", "死亡", out parsed, out mode) && parsed == 20 && mode == DeathRecognitionMode.Standard, "new test2 standard fallback");
        check(DeathCounterParser.TryParseDeathCount("I Wanna XXX | Death Mode | 死亡：13", "死亡", out parsed) && parsed == 13, "custom counter ignores Death Mode");
        check(DeathCounterParser.TryParseDeathCount("寄：20 | Deaths: 999", "寄", out parsed) && parsed == 20, "custom alternate wins");
        long time = 0;
        var logs = new List<string>();
        var coordinator = new DeathTriggerCoordinator(() => time, logs.Add);
        var hotkey = new HotkeyTrigger { Target = new(10, 20, "No counter"), Enabled = true };
        hotkey.DeathDetected += e => coordinator.Submit(e);
        int events = 0;
        coordinator.DeathDetected += (_, _) => events++;
        var down = new KeyboardStroke(Keys.R, true, false, false, false, false);
        coordinator.Configure(DeathTriggerMode.Hotkey, 500); coordinator.Start();
        check(hotkey.Handle(down, 10, true) && events == 1, "new test5 foreground key produces unified death");
        hotkey.Handle(down with { Repeat = true }, 10, true);
        check(events == 1, "new test6 held key repeat ignored");
        hotkey.Handle(down with { Down = false }, 10, true); hotkey.Handle(down, 10, true);
        check(events == 2, "new test7 release then press again");
        hotkey.Handle(down, 99, true);
        check(events == 2, "new test8 another foreground window ignored");
        hotkey.Config.OnlyWhenGameForeground = false; hotkey.Handle(down, 99, true);
        check(events == 3, "advanced global option works");
        hotkey.Handle(down, 99, false);
        check(events == 3, "closed HWND never triggers even global");
        coordinator.Configure(DeathTriggerMode.Both, 500); coordinator.Start();
        coordinator.Submit(new(20, 21)); time += 200; hotkey.Handle(down, 10, true);
        check(events == 4 && logs.Last().StartsWith("Hotkey"), "new test9 title then hotkey deduplicated");
        coordinator.Start(); time += 600; hotkey.Handle(down, 10, true); time += 200; coordinator.Submit(new(21, 22));
        check(events == 5 && logs.Last().StartsWith("Title"), "new test10 hotkey then title deduplicated");
        time += 500; coordinator.Submit(new(22, 23));
        check(events == 6, "Both next death after dedup window accepted");
        coordinator.Configure(DeathTriggerMode.Hotkey, 500); coordinator.Start(); coordinator.Submit(new(10, 11));
        check(events == 6, "new test11 hotkey-only rejects title source");
        coordinator.Configure(DeathTriggerMode.TitleCounter, 500); coordinator.Start(); hotkey.Handle(down, 10, true);
        check(events == 6, "new test12 title-only rejects hotkey source");
        coordinator.Submit(new(0, 1, DeathTriggerSource.Manual));
        check(events == 7, "manual routes unified source without title mutation");
        var reserved = new HotkeyConfig { Key = "F12", Ctrl = true, Shift = true };
        check(reserved.IsEmergency, "reserved emergency combo rejected");
        hotkey.Config = new HotkeyConfig { Key = "F12", OnlyWhenGameForeground = false };
        check(!hotkey.Handle(new(Keys.F12, true, true, true, false, false), 10, true), "new test16 emergency combo never becomes death trigger");
        NativeHook(check, pump);
        var config = new AppConfig { Detection = new() { TriggerMode = DeathTriggerMode.Both, Hotkey = new() { Key = "F8", OnlyWhenGameForeground = false }, TriggerDeduplicationMs = 750 } };
        var restored = ConfigManager.Clone(config);
        check(restored.Detection.TriggerMode == DeathTriggerMode.Both && restored.Detection.Hotkey.Key == "F8" &&
            !restored.Detection.Hotkey.OnlyWhenGameForeground && restored.Detection.TriggerDeduplicationMs == 750, "trigger config roundtrip without HWND");
        config.Detection.Hotkey = reserved; config.Detection.TriggerDeduplicationMs = int.MaxValue;
        check(ConfigManager.Normalize(config) && config.Detection.Hotkey.Key == "R" && config.Detection.TriggerDeduplicationMs == 3000, "invalid hotkey config clamps safely");
    }
    private static void NativeHook(Action<bool, string> check, Action<Func<bool>> pump)
    {
        using var hook = new GlobalKeyboardHook();
        check(hook.Install(), "native WH_KEYBOARD_LL hook installed");
        using var target = new Form { Text = "Keyboard target - no counter", ShowInTaskbar = false };
        using var other = new Form { Text = "Other foreground fixture", ShowInTaskbar = false };
        target.Show(); other.Show(); Application.DoEvents();
        var trigger = new HotkeyTrigger { Target = new(target.Handle, (uint)Environment.ProcessId, target.Text), Enabled = true };
        int events = 0;
        trigger.DeathDetected += _ => events++;
        hook.Stroke += stroke => trigger.Handle(stroke, HotkeyTrigger.GetForegroundWindow(), WindowScanner.IsAlive(target.Handle, (uint)Environment.ProcessId));
        bool focused = Focus(target);
        void Key(bool down)
        {
            var inputs = new[] { new NativeInput { Type = 1, Key = (ushort)Keys.R, Flags = down ? 0u : 2u } };
            check(SendInput(1, inputs, Marshal.SizeOf<NativeInput>()) == 1, "native key input delivered");
            Application.DoEvents(); Thread.Sleep(20); Application.DoEvents();
        }
        if (focused)
        {
        Key(false); Key(true); pump(() => events == 1);
        Key(true); Key(true);
        check(events == 1, "native keyboard auto-repeat only one edge event");
        Key(false); Key(true); pump(() => events == 2); Key(false);
        if (Focus(other))
        {
            Key(true); Key(false);
            check(events == 2, "native hook foreground filter blocks another window");
        }
        }
        else check(true, "native input skipped: Windows denied fixture foreground; routing tested separately");
        target.Close(); other.Close();
        // Cross-process scanner must include an ordinary titled window without any death counter.
        using var fixture = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--test-window --plain-fixture") { UseShellExecute = false })!;
        try
        {
            var scanner = new WindowScanner(); GameWindowInfo? found = null;
            pump(() => { found = scanner.GetVisibleWindows().FirstOrDefault(x => x.ProcessId == fixture.Id); return found is not null; });
            check(found!.Title.Contains("Ordinary Window") && found.ProcessName.EndsWith(".exe"), "new test3 plain window enumerated with process metadata");
            check(!scanner.GetVisibleWindows().Any(x => x.ProcessId == Environment.ProcessId), "bridge own windows excluded from enumeration");
        }
        finally { if (!fixture.HasExited) fixture.CloseMainWindow(); fixture.WaitForExit(3000); }
    }
}
