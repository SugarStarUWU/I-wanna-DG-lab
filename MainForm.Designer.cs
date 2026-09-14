using IWCoyoteBridge.Config;
using IWCoyoteBridge.Coyote;
using IWCoyoteBridge.Core;
namespace IWCoyoteBridge;
partial class MainForm
{
    private ComboBox windows = null!, presets = null!;
    private Label connection = null!, current = null!, previous = null!, detection = null!, recent = null!, recognition = null!, deviceStatus = null!, dashboard = null!;
    private Button rescan = null!, start = null!, stop = null!, save = null!, connect = null!, disconnect = null!, emergency = null!, resetPenalty = null!, clearStatistics = null!;
    private CheckBox sound = null!, stacking = null!;
    private NumericUpDown interval = null!, recovery = null!, triggerInterval = null!;
    private ComboBox penaltyMode = null!, deathTriggerMode = null!;
    private Button setTriggerKey = null!, simulateDeath = null!;
    private Label triggerKeyLabel = null!, keyDetection = null!;
    private CheckBox onlyForeground = null!, socketDebug = null!;
    private Button reconnect = null!, diagnose = null!;
    private NumericUpDown deduplication = null!;
    private TextBox logs = null!, keyword = null!;
    private ChannelView viewA = null!, viewB = null!;
    private readonly ToolTip titleTip = new();
    private sealed class ChannelView
    {
        public readonly CheckBox Enabled = new() { Text = "启用通道", AutoSize = true };
        public readonly NumericUpDown Base = Number(0, 100, 5), Max = Number(0, 100, 30), Increment = Number(0, 100, 5), Duration = Number(100, 3000, 500), RandomRange = Number(0, 20, 0);
        public readonly CheckBox Random = new() { Text = "随机强度浮动 ±", AutoSize = true };
        public readonly ComboBox Wave = Combo(), Mode = Combo();
        public readonly CheckedListBox Sequence = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        public readonly Label State = MakeLabel("APP 当前 / 上限：—");
        public readonly Button Test = MakeButton("测试", "");
        public ChannelView(char channel) { Enabled.Text = $"启用 {channel}"; Test.Text = $"试用 {channel}（会输出）"; Test.Name = $"Test{channel}"; Wave.Name = $"Waveform{channel}"; }
        public Control MakePanel(char channel)
        {
            var box = new GroupBox { Text = $"{channel} 通道", Dock = DockStyle.Fill, Padding = new Padding(10) };
            var grid = MakeGrid(32, 36, 36, 36, 36, 36, 36, 58, 38);
            grid.ColumnStyles[0].Width = 125;
            box.Controls.Add(grid);
            AddWide(grid, 0, Enabled);
            AddField(grid, 1, "平时强度", Base); AddField(grid, 2, "最高强度", Max);
            AddField(grid, 3, "每次死亡增加", Increment); AddField(grid, 4, "输出时长(毫秒)", Duration);
            AddField(grid, 5, "波形", Wave);
            var flow = MakeFlow(); flow.Controls.AddRange([Random, RandomRange]); AddWide(grid, 6, flow);
            AddWide(grid, 7, State); AddWide(grid, 8, Test);
            return box;
        }
        public Control MakeWavePanel(char channel)
        {
            var box = new GroupBox { Text = $"{channel} 波形播放", Dock = DockStyle.Fill, Padding = new Padding(10) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Mode.Items.AddRange(["单个", "顺序", "随机"]);
            grid.Controls.Add(Mode); grid.Controls.Add(MakeLabel("顺序 / 随机：勾选下方波形")); grid.Controls.Add(Sequence);
            box.Controls.Add(grid); return box;
        }
        public ChannelConfig Read() => new()
        {
            Enabled = Enabled.Checked, BaseStrength = (int)Base.Value, MaxStrength = (int)Max.Value, DeathIncrement = (int)Increment.Value,
            DurationMs = (int)Duration.Value, WaveformId = (Wave.SelectedItem as CoyoteWaveform)?.Id ?? "breathing",
            WaveformMode = (WaveformMode)Math.Max(0, Mode.SelectedIndex),
            WaveformIds = Sequence.CheckedItems.Cast<CoyoteWaveform>().Select(x => x.Id).ToList(), RandomEnabled = Random.Checked, RandomRange = (int)RandomRange.Value
        };
        public void Load(ChannelConfig config, WaveformLibrary library)
        {
            Enabled.Checked = config.Enabled; Base.Value = config.BaseStrength; Max.Value = config.MaxStrength;
            Increment.Value = config.DeathIncrement; Duration.Value = config.DurationMs; Random.Checked = config.RandomEnabled; RandomRange.Value = config.RandomRange;
            Wave.Items.Clear(); Sequence.Items.Clear();
            foreach (var waveform in library.Items) { Wave.Items.Add(waveform); Sequence.Items.Add(waveform, config.WaveformIds.Contains(waveform.Id)); }
            Wave.SelectedItem = library.Items.FirstOrDefault(x => x.Id == config.WaveformId);
            Mode.SelectedIndex = (int)config.WaveformMode;
        }
    }
    private void InitializeComponent()
    {
        Text = "I wanna DG-LAB"; ClientSize = new Size(780, 700); MinimumSize = new Size(720, 680);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); Controls.Add(root);
        var tabs = new TabControl { Name = "MainTabs", Dock = DockStyle.Fill }; root.Controls.Add(tabs);
        TabPage Page(string title) { var page = new TabPage(title) { Padding = new Padding(10), AutoScroll = true }; tabs.TabPages.Add(page); return page; }
        var game = Page("游戏"); var device = Page("郊狼"); var penalty = Page("惩罚"); var wave = Page("波形"); var log = Page("日志");
        var footer = MakeFlow(); emergency = MakeButton("■ 紧急停止  Ctrl+Shift+F12", "EmergencyStop");
        emergency.BackColor = Color.Firebrick; emergency.ForeColor = Color.White; emergency.Font = new Font(Font, FontStyle.Bold);
        save = MakeButton("保存设置", "SaveSettings");
        var overlay = MakeButton("强度悬浮窗", "ShowStrengthOverlay"); overlay.Click += (_, _) => ShowStrengthOverlay();
        titleTip.SetToolTip(overlay, "显示实时 A / B 强度。OBS 添加“窗口捕获”，选择“实时强度”窗口；右键窗口可切换绿色背景。");
        footer.Controls.AddRange([emergency, save, overlay]); root.Controls.Add(footer);
        var gameGrid = MakeGrid(38, 38, 38, 28, 38, 32, 38, 30, 30, 30, 30, 30, 30, 38, 38); game.Controls.Add(gameGrid);
        windows = Combo(); windows.Name = "GameWindows";
        keyword = new TextBox { Name = "CustomDeathKeyword", Dock = DockStyle.Fill, MaxLength = 128 };
        deathTriggerMode = Combo(); deathTriggerMode.Name = "DeathTriggerMode";
        deathTriggerMode.Items.AddRange(["标题死亡数", "指定按键", "标题 + 指定按键"]);
        AddField(gameGrid, 0, "游戏窗口", windows); AddField(gameGrid, 1, "触发方式", deathTriggerMode);
        AddField(gameGrid, 2, "死亡计数关键字", keyword);
        AddWide(gameGrid, 3, MakeLabel("自定义关键字优先；未匹配时回退 Death / Deaths"));
        triggerKeyLabel = MakeLabel("R", "TriggerKey", 160);
        setTriggerKey = MakeButton("设置按键", "SetTriggerKey");
        var keyFlow = MakeFlow(); keyFlow.Controls.AddRange([triggerKeyLabel, setTriggerKey]); AddField(gameGrid, 4, "触发按键", keyFlow);
        onlyForeground = new CheckBox { Name = "OnlyGameForeground", Text = "仅游戏窗口处于前台时响应", Checked = true, AutoSize = true }; AddWide(gameGrid, 5, onlyForeground);
        deduplication = Number(100, 3000, 500); deduplication.Name = "TriggerDeduplicationMs";
        AddField(gameGrid, 6, "防止重复触发(毫秒)", deduplication);
        connection = MakeLabel(""); current = MakeLabel("当前死亡：—", "CurrentDeaths");
        gameGrid.Controls.Add(connection, 0, 7); gameGrid.Controls.Add(current, 1, 7);
        recognition = MakeLabel("", "RecognitionMode"); previous = MakeLabel(""); detection = MakeLabel(""); recent = MakeLabel("", "RecentEvent");
        keyDetection = MakeLabel("", "KeyDetection");
        AddWide(gameGrid, 8, detection); AddWide(gameGrid, 9, recognition); AddWide(gameGrid, 10, previous);
        AddWide(gameGrid, 11, keyDetection); AddWide(gameGrid, 12, recent);
        rescan = MakeButton("重新扫描", "Rescan"); start = MakeButton("开始监听 / 新一局", "StartMonitoring"); stop = MakeButton("停止监听", "StopMonitoring");
        simulateDeath = MakeButton("试一次死亡", "SimulateDeath");
        var buttons = MakeFlow(); buttons.Controls.AddRange([rescan, start, stop, simulateDeath]); AddWide(gameGrid, 13, buttons);
        interval = Number(100, 200, 150); sound = new CheckBox { Text = "提示音", AutoSize = true };
        var poll = MakeFlow(); poll.Controls.AddRange([MakeLabel("检查间隔(毫秒)", width:140), interval, sound]); AddWide(gameGrid, 14, poll);
        var deviceGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        foreach (var h in new[] { 40, 32, 32, 395, 34 }) deviceGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        device.Controls.Add(deviceGrid);
        reconnect = MakeButton("重新连接", "ReconnectCoyote"); diagnose = MakeButton("检查连接", "RelayDiagnostics");
        connect = MakeButton("连接郊狼", "ConnectCoyote"); disconnect = MakeButton("断开", "DisconnectCoyote");
        var connections = MakeFlow(); connections.Controls.AddRange([connect, reconnect, disconnect, diagnose]); deviceGrid.Controls.Add(connections);
        deviceStatus = MakeLabel("● 未启动", "DeviceStatus"); deviceGrid.Controls.Add(deviceStatus);
        deviceGrid.Controls.Add(MakeLabel("开始监听即允许游戏事件输出；急停后需重新开始监听。"));
        viewA = new('A'); viewB = new('B');
        deviceGrid.Controls.Add(TwoColumns(viewA.MakePanel('A'), viewB.MakePanel('B')));
        deviceGrid.Controls.Add(MakeLabel("连接方式：DG-LAB 官方服务器；手机可使用 4G / 5G / Wi-Fi。"));
        var penaltyGrid = MakeGrid(38, 38, 38, 34, 38, 145, 38, 38, 38);
        penalty.Controls.Add(penaltyGrid);
        penaltyMode = Combo(); penaltyMode.Name = "PenaltyMode"; penaltyMode.Items.AddRange(["每次使用固定强度", "每次死亡加强，不自动恢复", "死亡后暂时加强，到期恢复"]);
        recovery = Number(1, 600, 30); recovery.Name = "RecoverySeconds"; triggerInterval = Number(100, 5000, 300);
        stacking = new CheckBox { Text = "恢复前再次死亡，继续增加强度", AutoSize = true };
        AddField(penaltyGrid, 0, "惩罚模式", penaltyMode); AddField(penaltyGrid, 1, "恢复时间（秒）", recovery);
        AddField(penaltyGrid, 2, "两次惩罚间隔(毫秒)", triggerInterval); AddWide(penaltyGrid, 3, stacking);
        resetPenalty = MakeButton("重置惩罚", "ResetPenalty"); clearStatistics = MakeButton("清空本次统计", "ClearStatistics");
        var statsButtons = MakeFlow(); statsButtons.Controls.AddRange([resetPenalty, clearStatistics]); AddWide(penaltyGrid, 4, statsButtons);
        dashboard = MakeLabel("", "Dashboard"); AddWide(penaltyGrid, 5, dashboard);
        presets = Combo(); presets.Name = "Presets"; AddField(penaltyGrid, 6, "预设", presets);
        var presetButtons = MakeFlow(); var presetFiles = MakeFlow();
        foreach (var (caption, action) in new (string, Action)[] { ("保存预设", () => SavePreset(false)), ("另存为", () => SavePreset(true)), ("删除", DeletePreset) })
        { var button = MakeButton(caption, ""); button.Click += (_, _) => action(); presetButtons.Controls.Add(button); }
        foreach (var (caption, action) in new (string, Action)[] { ("导入预设", ImportPreset), ("导出预设", ExportPreset) })
        { var button = MakeButton(caption, ""); button.Click += (_, _) => action(); presetFiles.Controls.Add(button); }
        AddWide(penaltyGrid, 7, presetButtons); AddWide(penaltyGrid, 8, presetFiles);
        var waveGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        waveGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); waveGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var importWave = MakeButton("导入波形 (.pulse / .json)", "ImportWaveform"); importWave.Click += (_, _) => ImportWaveform();
        waveGrid.Controls.Add(importWave); waveGrid.Controls.Add(TwoColumns(viewA.MakeWavePanel('A'), viewB.MakeWavePanel('B'))); wave.Controls.Add(waveGrid);
        var logGrid = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        logGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); logGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        logs = new TextBox { Name = "Logs", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true };
        logGrid.Controls.Add(logs); var logButtons = MakeFlow();
        var clear = MakeButton("清空日志", ""); clear.Click += (_, _) => { logLines.Clear(); logs.Clear(); };
        var copy = MakeButton("复制日志", ""); copy.Click += (_, _) => { try { if (logs.TextLength > 0) Clipboard.SetText(logs.Text); } catch (Exception ex) { Log(ex.Message); } };
        socketDebug = new CheckBox { Name = "SocketDebugLogging", Text = "详细连接日志（排错用）", AutoSize = true };
        logButtons.Controls.AddRange([clear, copy, socketDebug]); logGrid.Controls.Add(logButtons); log.Controls.Add(logGrid);
    }
    private static NumericUpDown Number(int min, int max, int value) => new() { Minimum = min, Maximum = max, Value = value, Width = 85 };
    private static ComboBox Combo()
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, IntegralHeight = true };
        combo.SizeChanged += (_, _) => combo.DropDownWidth = Math.Max(1, combo.Width);
        combo.DropDown += (_, _) => combo.DropDownWidth = Math.Max(1, combo.Width);
        return combo;
    }
    private static Control TwoColumns(Control left, Control right)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.Controls.Add(left, 0, 0); grid.Controls.Add(right, 1, 0); return grid;
    }
    private static FlowLayoutPanel MakeFlow() => new() { Dock = DockStyle.Fill, WrapContents = false };
    private static Button MakeButton(string text, string name) => new() { Text = text, Name = name, AutoSize = true };
    private static Label MakeLabel(string text, string name = "", int width = 0) => new() { Text = text, Name = name, Dock = width == 0 ? DockStyle.Fill : DockStyle.None, Width = width == 0 ? 100 : width, AutoEllipsis = true, Padding = new Padding(0, 3, 0, 0) };
    private static TableLayoutPanel MakeGrid(params int[] heights)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = heights.Length };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in heights) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); return grid;
    }
    private static void AddWide(TableLayoutPanel grid, int row, Control control) { grid.Controls.Add(control, 0, row); grid.SetColumnSpan(control, 2); }
    private static void AddField(TableLayoutPanel grid, int row, string label, Control control) { grid.Controls.Add(MakeLabel(label), 0, row); grid.Controls.Add(control, 1, row); }
}
