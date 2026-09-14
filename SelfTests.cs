using IWCoyoteBridge.Core;
using IWCoyoteBridge.Config;
using IWCoyoteBridge.Output;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;

namespace IWCoyoteBridge;

internal static class SelfTests
{
    private sealed class FakeReader : IWindowReader
    {
        public string Title = "Deaths: 10";
        public bool Alive = true;
        public bool Throw;
        public bool TryRead(nint hwnd, uint processId, out string title)
        {
            title = Title;
            if (Throw) throw new InvalidOperationException("test");
            return Alive;
        }
    }

    public static int Run()
    {
        var report = new List<string>();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test-progress.txt"), "START\n");
        try
        {
            void Check(bool condition, string name)
            {
                if (!condition) throw new Exception(name);
                report.Add($"PASS {name}");
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "self-test-progress.txt"), $"PASS {name}\n");
            }
            foreach (var title in new[] { "Deaths: 123", "Deaths 123", "Deaths：123", "Death: 123", "Death 123", "death:123", "deaths = 123" })
                Check(DeathCounterParser.TryParseDeathCount(title, out var count) && count == 123, title);
            foreach (var title in new[] { "Undeaths: 123", "Deaths: -1", "Deaths: 2147483648", "Time: 01:22:43", "Deaths: 12.5", "Deaths: 1 Death: 2", "DeathRate 123", "Deaths: 123abc" })
                Check(!DeathCounterParser.TryParseDeathCount(title, out _), $"reject {title}");
            Check(DeathCounterParser.TryParseDeathCount("I Wanna Test | Deaths: 137 | Time: 02:15:35", out var parsed) && parsed == 137, "test 8 time ignored");
            void ParseCustom(string title, string customKeyword, int expected, DeathRecognitionMode expectedMode, string name)
                => Check(DeathCounterParser.TryParseDeathCount(title, customKeyword, out var count, out var mode) &&
                    count == expected && mode == expectedMode, name);
            ParseCustom("I Wanna Test | Deaths: 15", "死亡", 15, DeathRecognitionMode.Standard, "custom test 1 standard priority");
            ParseCustom("I Wanna Test | 死亡：15", "死亡", 15, DeathRecognitionMode.Custom, "custom test 2 Chinese keyword");
            ParseCustom("I Wanna Test | 死亡：15 | 时间：01:24:33", "死亡", 15, DeathRecognitionMode.Custom, "custom test 3 time ignored");
            ParseCustom("I Wanna Test | 寄：88", "寄", 88, DeathRecognitionMode.Custom, "custom test 4 alternate keyword");
            Check(!DeathCounterParser.TryParseDeathCount("I Wanna Test | 寄：88", "死亡", out _, out var missingMode) &&
                missingMode == DeathRecognitionMode.Unknown, "custom test 5 unmatched keyword");
            ParseCustom("I Wanna Test | Deaths: 50 | 死亡：999", "死亡", 999, DeathRecognitionMode.Custom, "custom priority conflicting counters");
            foreach (var title in new[] { "死亡: 13", "死亡：13", "死亡 13", "死亡=13", "死亡 = 13", "死亡： 13" })
                ParseCustom(title, "死亡", 13, DeathRecognitionMode.Custom, $"custom separators {title}");
            ParseCustom("Deaths(+): 123", "Deaths(+)", 123, DeathRecognitionMode.Custom, "Regex.Escape literal punctuation");
            ParseCustom("[.*]：23", "[.*]", 23, DeathRecognitionMode.Custom, "Regex.Escape metacharacters");
            ParseCustom("Deaths: 4", "   ", 4, DeathRecognitionMode.Standard, "empty keyword defaults to Death");
            ParseCustom("死了：137 | 时间：01:20:33", " 死了 ", 137, DeathRecognitionMode.Custom, "keyword trimmed");
            Check(!DeathCounterParser.TryParseDeathCount("Deathssss: 12", "Death", out _), "custom Death does not swallow suffix");
            Check(!DeathCounterParser.TryParseDeathCount("死亡：2147483648", "死亡", out _), "custom overflow rejected");
            Check(!DeathCounterParser.TryParseDeathCount("死亡：-13 | 时间：01:20:33", "死亡", out _), "custom does not search later numbers");
            var customReader = new FakeReader { Title = "死亡：20 | 寄：999" };
            long customTime = 0;
            var customMonitor = new DeathMonitor(customReader, () => customTime);
            int customEvents = 0;
            customMonitor.DeathDetected += (_, _) => customEvents++;
            customMonitor.Start(new(3, 3, customReader.Title), "死亡");
            Check(customEvents == 0 && customMonitor.LastDeaths == 20, "custom baseline initializes safely");
            customMonitor.UpdateCustomKeyword("寄");
            Check(customEvents == 0 && customMonitor.LastDeaths == 999 && customMonitor.CurrentDeaths == 999 &&
                customMonitor.RecognitionMode == DeathRecognitionMode.Custom, "custom test 7 keyword edit rebaselines without event");
            customTime += 400; customReader.Title = "寄：1000"; customMonitor.Poll();
            Check(customEvents == 1, "custom increase after edit emits once");
            customMonitor.UpdateCustomKeyword("死亡");
            Check(customEvents == 1 && customMonitor.LastDeaths is null && customMonitor.RecognitionMode == DeathRecognitionMode.Unknown,
                "unmatched edited keyword clears parsing state");
            customReader.Title = "死亡：5000"; customMonitor.Poll();
            Check(customEvents == 1 && customMonitor.LastDeaths == 5000, "edited keyword recovery establishes baseline");
            customReader.Title = "Deaths: 50"; customMonitor.Poll();
            Check(customEvents == 1 && customMonitor.LastDeaths == 50 && customMonitor.RecognitionMode == DeathRecognitionMode.Standard,
                "standard recognition switch rebaselines");
            customTime += 400; customReader.Title = "死亡：999"; customMonitor.Poll();
            Check(customEvents == 1 && customMonitor.LastDeaths == 999, "standard to custom switch does not compare unrelated counts");
            var restored = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(
                new AppConfig { CustomDeathKeyword = "死亡" }));
            Check(restored?.CustomDeathKeyword == "死亡", "config keyword JSON round trip");
            Check(System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"PollInterval\":150}")?.CustomDeathKeyword == "Death",
                "legacy config default keyword");
            long time = 0;
            var reader = new FakeReader();
            var monitor = new DeathMonitor(reader, () => time);
            int events = 0, disconnects = 0;
            monitor.DeathDetected += (_, _) => events++;
            monitor.Disconnected += (_, _) => disconnects++;
            monitor.Start(new(1, 1, reader.Title));
            Check(events == 0 && monitor.LastDeaths == 10, "test 1 baseline");
            void Change(string title, long advance = 400) { time += advance; reader.Title = title; monitor.Poll(); }
            Change("Deaths: 11"); Check(events == 1, "test 2 +1");
            Change("Deaths: 13"); Check(events == 2, "test 3 +2 single event");
            Change("Deaths: 0"); Check(events == 2 && monitor.LastDeaths == 0, "test 4 reset");
            Change("Deaths: 1"); Check(events == 3, "test 5 after reset");
            Change("Deaths: 1"); Check(events == 3, "repeated number");
            Change("Deaths: 2"); Change("Deaths: 3", 100); Change("Deaths: 3", 400);
            Check(events == 4 && monitor.LastDeaths == 3, "debounce drops without replay");
            Change("invalid"); Change("Deaths: 100"); Check(events == 4, "parse recovery baseline");
            reader.Throw = true; monitor.Poll(); reader.Throw = false; Change("Deaths: 200");
            Check(events == 4, "read exception recovery baseline");
            reader.Alive = false; monitor.Poll(); Check(!monitor.IsMonitoring && disconnects == 1, "test 6 disconnect");
            reader.Alive = true; reader.Title = "Deaths: 1000"; monitor.Start(new(2, 2, reader.Title));
            Check(events == 4 && monitor.LastDeaths == 1000, "test 7 new HWND baseline");
            Check(ConfigManager.IsValid(new AppConfig()), "safe default config");

            // Real top-level HWND + real Win32 title reads, not a fake reader.
            using var window = new Form { Text = "I Wanna Bridge Test | Deaths: 10", ShowInTaskbar = false };
            window.Show(); Application.DoEvents();
            var scanner = new WindowScanner();
            var live = new DeathMonitor(scanner, () => time);
            int liveEvents = 0;
            live.DeathDetected += (_, _) => liveEvents++;
            live.Start(new(window.Handle, (uint)Environment.ProcessId, window.Text));
            Check(liveEvents == 0 && live.CurrentDeaths == 10, "Win32 initial title");
            window.Text = "I Wanna Bridge Test | Deaths: 11"; time += 400; live.Poll();
            Check(liveEvents == 1, "Win32 changed title");
            window.Close(); Application.DoEvents(); live.Poll();
            Check(!live.IsMonitoring && liveEvents == 1, "Win32 destroyed HWND");
            using (var fixture = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--test-window") { UseShellExecute = false })!)
            {
                try
                {
                    var deadline = Stopwatch.StartNew();
                    GameWindowInfo? found = null;
                    while (found is null && deadline.ElapsedMilliseconds < 5000)
                    {
                        Application.DoEvents(); Thread.Sleep(20);
                        found = scanner.Scan().FirstOrDefault(w => w.ProcessId == fixture.Id && w.Title.Contains("Scanner Fixture"));
                    }
                    Check(found is not null, "EnumWindows finds external visible window");
                    var external = new DeathMonitor(scanner);
                    external.Start(found!);
                    Check(external.CurrentDeaths == 42, "external process title baseline");
                    fixture.CloseMainWindow();
                    Check(fixture.WaitForExit(3000), "fixture closes normally");
                    external.Poll();
                    Check(!external.IsMonitoring, "external process disconnect");
                }
                finally { if (!fixture.HasExited) fixture.CloseMainWindow(); }
            }
            void PumpUntil(Func<bool> done)
            {
                var deadline = Stopwatch.StartNew();
                while (!done() && deadline.ElapsedMilliseconds < 15000)
                { Application.DoEvents(); Thread.Sleep(5); }
                Check(done(), "async operation completes");
            }
            DetectionSelfTests.Run(Check, PumpUntil);
            SocketSelfTests.Run(Check, PumpUntil);
            using var main = new MainForm(new AppConfig());
            main.Show(); Application.DoEvents();
            Check(main.Visible && main.Text == "I wanna DG-LAB", "MainForm runtime startup");
            var keywordInput = (TextBox)main.Controls.Find("CustomDeathKeyword", true).Single();
            var gameList = (ComboBox)main.Controls.Find("GameWindows", true).Single();
            var recognitionLabel = (Label)main.Controls.Find("RecognitionMode", true).Single();
            var currentLabel = (Label)main.Controls.Find("CurrentDeaths", true).Single();
            var eventLabel = (Label)main.Controls.Find("RecentEvent", true).Single();
            var scanButton = (Button)main.Controls.Find("Rescan", true).Single();
            using (var fixture = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--test-window --custom-fixture") { UseShellExecute = false })!)
            {
                try
                {
                    fixture.WaitForInputIdle(5000);
                    keywordInput.Text = "死亡"; scanButton.PerformClick();
                    PumpUntil(() => gameList.Items.Cast<GameWindowInfo>().Any(w => w.ProcessId == fixture.Id));
                    Check(gameList.Items.Cast<GameWindowInfo>().Any(w => w.ProcessId == fixture.Id && w.Title.Contains("死亡：42")),
                        "UI rescan finds custom title in another process");
                    keywordInput.Text = "寄"; scanButton.PerformClick();
                    PumpUntil(() => gameList.Items.Cast<GameWindowInfo>().Any(w => w.ProcessId == fixture.Id));
                    Check(true, "all-window scan independent of keyword");
                }
                finally
                {
                    if (!fixture.HasExited) fixture.CloseMainWindow();
                    fixture.WaitForExit(3000);
                }
            }
            using var customWindow = new Form { Text = "I Wanna UI Test | 死亡：20 | 寄：999", ShowInTaskbar = false };
            customWindow.Show(); Application.DoEvents();
            keywordInput.Text = "死亡";
            var customTarget = new GameWindowInfo(customWindow.Handle, (uint)Environment.ProcessId, customWindow.Text);
            gameList.Items.Add(customTarget); gameList.SelectedItem = customTarget;
            ((Button)main.Controls.Find("StartMonitoring", true).Single()).PerformClick();
            Check(currentLabel.Text == "当前死亡：20" && recognitionLabel.Text == "识别方式：自定义：死亡" && !eventLabel.Text.StartsWith("最近死亡："),
                "UI custom connection displays recognition mode without output");
            keywordInput.Text = "寄";
            Check(currentLabel.Text == "当前死亡：999" && recognitionLabel.Text == "识别方式：自定义：寄" && !eventLabel.Text.StartsWith("最近死亡："),
                "UI live keyword edit does not emit death");
            customWindow.Text = "I Wanna UI Test | 寄：1000";
            PumpUntil(() => eventLabel.Text.StartsWith("最近死亡："));
            Check(currentLabel.Text == "当前死亡：1000", "UI polling continues after keyword edit");
            ComboBoxSelfTests.Run(main, gameList, Check);
            DetectionSelfTests.RunUi(main, customWindow, Check, PumpUntil);
            SocketUiSelfTests.Run(main, customWindow, Check, PumpUntil);

            using (var preview = new Bitmap(main.Width, main.Height))
            {
                main.DrawToBitmap(preview, new Rectangle(Point.Empty, main.Size));
                preview.Save(Path.Combine(AppContext.BaseDirectory, "main-form-preview.png"));
            }
            main.Close(); Application.DoEvents();
            report.Add($"ALL {report.Count} TESTS PASSED");
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "self-test-results.txt"), report);
            return 0;
        }
        catch (Exception ex)
        {
            report.Add($"FAIL {ex}");
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "self-test-results.txt"), report);
            return 1;
        }
    }
}
