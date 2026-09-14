using IWCoyoteBridge.Config;
using IWCoyoteBridge.Core;
using IWCoyoteBridge.Coyote;
using IWCoyoteBridge.Output;
using IWCoyoteBridge.Input;
using System.Runtime.InteropServices;
namespace IWCoyoteBridge;
public partial class MainForm : Form
{
    private readonly WindowScanner scanner = new();
    private readonly DeathMonitor monitor;
    private readonly GlobalKeyboardHook keyboard = new();
    private readonly HotkeyTrigger hotkey = new();
    private readonly DeathTriggerCoordinator coordinator;
    private GameWindowInfo? listeningWindow;
    private bool capturingKey;
    private bool outputAuthorized;
    private long authorizationRevision, settingsRevision;
    private bool selectedWindowClosed;
    private long inputGeneration;
    private AppConfig config;
    private readonly DebugOutputController debugOutput;
    private readonly CoyoteController coyote;
    private readonly PenaltyController penalty;
    private readonly WaveformLibrary library;
    private readonly PresetManager presetManager = new();
    private readonly System.Windows.Forms.Timer scanTimer = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer pollTimer = new();
    private readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 200 };
    private readonly Queue<string> logLines = new();
    private bool scanning, updating, closing, rescanPending, loading, shutdownComplete, suppressConfirmation;
    private QrConnectionForm? qr;
    private StrengthOverlayForm? strengthOverlay;
    private void ShowStrengthOverlay()
    {
        if (strengthOverlay is { IsDisposed: false }) { strengthOverlay.Show(); strengthOverlay.WindowState = FormWindowState.Normal; strengthOverlay.Activate(); return; }
        strengthOverlay = new StrengthOverlayForm(() => coyote.State);
        // Unowned: OBS capture remains visible when the settings window is minimized.
        strengthOverlay.Show();
    }
    internal CoyoteController DeviceController => coyote;
    private sealed record PresetItem(string? Path, Preset Value) { public override string ToString() => Value.Name; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
    public MainForm() : this(null) { }
    internal MainForm(AppConfig? configuration)
    {
        InitializeComponent();
        Icon = AppIcon.Load();
        string? warning = null;
        config = configuration ?? ConfigManager.Load(out warning);
        ConfigManager.Normalize(config);
        library = new WaveformLibrary(log: Log);
        coyote = new CoyoteController(config, Log);
        penalty = new PenaltyController(coyote, library, config);
        monitor = new TitleDeathDetector(scanner).Monitor;
        coordinator = new DeathTriggerCoordinator(logger: Log);
        debugOutput = new DebugOutputController(Log, () => { }, () => sound.Checked);
        LoadUi();
        RefreshPresets();
        monitor.DeathDetected += (_, e) => coordinator.Submit(e);
        coordinator.DeathDetected += (_, e) =>
        {
            Log(e.Source == DeathTriggerSource.Manual ? "手动试用死亡效果" : $"检测到死亡（{(e.Source == DeathTriggerSource.Hotkey ? "按键" : "窗口标题")}）");
            recent.Text = e.Source switch
            {
                DeathTriggerSource.Hotkey => $"最近死亡：按键 {e.Key}",
                DeathTriggerSource.Manual => "最近死亡：手动试用",
                _ => $"最近死亡：标题 {e.Previous} → {e.Current}"
            };
            previous.Text = $"上一次死亡（最近事件）：{e.Previous}";
            ((IOutputController)debugOutput).OnDeath(e);
            try { _ = penalty.HandleDeathAsync(e); }
            catch (Exception ex) { Log("惩罚配置错误：" + ex.Message); _ = coyote.StopAsync(true); }
        };
        monitor.Disconnected += (_, _) => WindowClosed();
        pollTimer.Tick += (_, _) => PollDetection();
        keyboard.Stroke += OnKeyboardStroke;
        hotkey.DeathDetected += e => coordinator.Submit(e);
        setTriggerKey.Click += (_, _) =>
        {
            capturingKey = !capturingKey; inputGeneration++;
            triggerKeyLabel.Text = capturingKey ? "请按下一个按键…" : config.Detection.Hotkey.ToString();
            setTriggerKey.Text = capturingKey ? "取消设置" : "设置按键";
        };
        simulateDeath.Click += (_, _) =>
        {
            if (!coyote.LinkEnabled) { Log("模拟死亡忽略：死亡联动未启用"); return; }
            coordinator.Submit(new(0, 1, DeathTriggerSource.Manual));
        };
        deathTriggerMode.SelectedIndexChanged += (_, _) => DetectionSettingsChanged();
        onlyForeground.CheckedChanged += (_, _) => DetectionSettingsChanged();
        deduplication.ValueChanged += (_, _) => DetectionSettingsChanged();
        scanTimer.Tick += async (_, _) => await ScanAsync();
        statusTimer.Tick += (_, _) => RefreshDevice();
        rescan.Click += async (_, _) => await ScanAsync();
        start.Click += (_, _) => StartMonitoring();
        stop.Click += (_, _) => StopMonitoring();
        windows.SelectedIndexChanged += (_, _) =>
        {
            if (!updating && coordinator.IsListening)
            {
                StopMonitoring();
                if (windows.SelectedItem is GameWindowInfo) StartMonitoring();
            }
            if (!updating) RefreshStatus();
            titleTip.SetToolTip(windows, (windows.SelectedItem as GameWindowInfo)?.Tooltip ?? "");
        };
        keyword.TextChanged += (_, _) =>
        {
            if (loading) return;
            monitor.UpdateCustomKeyword(keyword.Text); coordinator.Reset(); inputGeneration++; penalty.Reset();
            previous.Text = $"上次读取的死亡数：{monitor.LastDeaths?.ToString() ?? "—"}";
            recent.Text = "最近事件：—";
            if (scanning) rescanPending = true;
            RefreshStatus();
        };
        interval.ValueChanged += (_, _) => { pollTimer.Interval = (int)interval.Value; };
        save.Click += (_, _) => SaveSettings();
        connect.Click += async (_, _) => await ConnectRelayAsync();
        reconnect.Click += async (_, _) => { await SafeStopAsync(true); await ConnectRelayAsync(); };
        socketDebug.CheckedChanged += (_, _) => coyote.SocketDebugLogging = socketDebug.Checked;
        diagnose.Click += async (_, _) =>
        {
            diagnose.Enabled = false;
            try { Log("连接诊断开始（不会控制设备）"); var result = await RelayDiagnostics.RunAsync(Log); Log(result); MessageBox.Show(this, result, "连接诊断"); }
            catch (Exception ex) { Log("连接诊断异常：" + ex); }
            finally { if (!IsDisposed) diagnose.Enabled = true; }
        };
        disconnect.Click += async (_, _) => await SafeStopAsync(true);
        emergency.Click += async (_, _) => { Log("紧急停止"); await SafeStopAsync(false); };
        viewA.Test.Click += async (_, _) => await TestAsync('A');
        viewB.Test.Click += async (_, _) => await TestAsync('B');
        resetPenalty.Click += (_, _) => { penalty.Reset(); Log("惩罚已重置；不会播放波形"); RefreshDevice(); };
        clearStatistics.Click += (_, _) => { penalty.ClearStatistics(); RefreshDevice(); };
        foreach (var view in new[] { viewA, viewB })
        {
            foreach (var numeric in new[] { view.Base, view.Max, view.Increment, view.Duration, view.RandomRange }) numeric.ValueChanged += (_, _) => SettingsChanged();
            foreach (var check in new[] { view.Enabled, view.Random }) check.CheckedChanged += (_, _) => SettingsChanged();
            view.Wave.SelectedIndexChanged += (_, _) => SettingsChanged();
            view.Mode.SelectedIndexChanged += (_, _) => SettingsChanged();
            view.Sequence.ItemCheck += (_, _) => { if (!loading) BeginInvoke((Action)SettingsChanged); };
        }
        penaltyMode.SelectedIndexChanged += (_, _) => SettingsChanged();
        recovery.ValueChanged += (_, _) => SettingsChanged();
        triggerInterval.ValueChanged += (_, _) => SettingsChanged();
        stacking.CheckedChanged += (_, _) => SettingsChanged();
        presets.SelectedIndexChanged += (_, _) =>
        {
            if (loading || presets.SelectedItem is not PresetItem item) return;
            _ = SafeStopAsync(false);
            var currentDetection = config.Detection; var currentSocket = config.Socket;
            config = ConfigManager.Clone(item.Value.Settings);
            config.Detection = currentDetection; config.Socket = currentSocket;
            LoadUi(); ReadSettings(); penalty.UpdateSettings(config);
            Log("已加载预设；联动已关闭，惩罚已重置。");
        };
        coyote.StateChanged += state => Ui(() =>
        {
            if (coyote.State != state) return;
            if (state.Status is CoyoteConnectionStatus.Disconnected or CoyoteConnectionStatus.Error ||
                (state.Status == CoyoteConnectionStatus.WaitingForApp && state.Detail is not null)) outputAuthorized = false;
            if (state.Ready && outputAuthorized && coordinator.IsListening && !coyote.LinkEnabled) coyote.SetLinkEnabled(true);
            RefreshDevice();
        });
        coyote.SafetyStopped += () => Ui(RefreshDevice);
        Shown += async (_, _) =>
        {
            if (!keyboard.Install()) Log("按键检测启动失败；仍可使用标题死亡数检测。");
            ApplyDetectionSettings();
            if (!RegisterHotKey(Handle, 1920, 0x4000 | 0x0002 | 0x0004, 0x7B)) Log("全局 Ctrl+Shift+F12 注册失败；请使用紧急停止按钮。");
            scanTimer.Start(); statusTimer.Start(); await ScanAsync();
        };
        FormClosing += async (_, e) =>
        {
            if (shutdownComplete) return;
            e.Cancel = true;
            if (closing) return;
            closing = true; Enabled = false;
            scanTimer.Stop(); pollTimer.Stop(); statusTimer.Stop(); monitor.Stop(); coordinator.Stop(); hotkey.Enabled = false; keyboard.Dispose();
            UnregisterHotKey(Handle, 1920); qr?.Close(); strengthOverlay?.Close();
            await coyote.StopAsync(true);
            penalty.Dispose(); coyote.Dispose(); titleTip.Dispose(); scanTimer.Dispose(); pollTimer.Dispose(); statusTimer.Dispose();
            shutdownComplete = true; Close();
        };
        if (warning is not null) Log(warning);
        RefreshStatus(); RefreshDevice();
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam == 1920) { Log("全局紧急停止"); _ = SafeStopAsync(false); }
        base.WndProc(ref message);
    }
    private void Ui(Action action)
    {
        if (closing || IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(action); } catch (InvalidOperationException) { } }
        else action();
    }
    private async Task SafeStopAsync(bool disconnectDevice)
    {
        authorizationRevision++;
        outputAuthorized = false; penalty.Reset();
        await coyote.StopAsync(disconnectDevice);
        if (!closing) { if (disconnectDevice) qr?.Close(); RefreshDevice(); }
    }
    private void LoadUi()
    {
        loading = true;
        try
        {
            interval.Value = config.PollInterval; pollTimer.Interval = config.PollInterval;
            keyword.Text = config.CustomDeathKeyword;
            deathTriggerMode.SelectedIndex = (int)config.Detection.TriggerMode;
            triggerKeyLabel.Text = config.Detection.Hotkey.ToString();
            onlyForeground.Checked = config.Detection.Hotkey.OnlyWhenGameForeground;
            deduplication.Value = config.Detection.TriggerDeduplicationMs;
            viewA.Load(config.ChannelA, library); viewB.Load(config.ChannelB, library);
            penaltyMode.SelectedIndex = (int)config.Penalty.Mode; recovery.Value = config.Penalty.TemporaryRecoverySeconds;
            triggerInterval.Value = config.Penalty.MinimumTriggerIntervalMs; stacking.Checked = config.Penalty.TemporaryStacking;

        }
        finally { loading = false; }
    }
    private void ReadSettings()
    {
        config.Detection.PollIntervalMs = (int)interval.Value;
        config.CustomDeathKeyword = DeathCounterParser.NormalizeKeyword(keyword.Text);
        config.Detection.TriggerMode = (DeathTriggerMode)Math.Max(0, deathTriggerMode.SelectedIndex);
        config.Detection.Hotkey.OnlyWhenGameForeground = onlyForeground.Checked;
        config.Detection.TriggerDeduplicationMs = (int)deduplication.Value;
        config.ChannelA = viewA.Read(); config.ChannelB = viewB.Read();
        config.Penalty = new() { Mode = (PenaltyMode)Math.Max(0, penaltyMode.SelectedIndex), TemporaryRecoverySeconds = (int)recovery.Value,
            TemporaryStacking = stacking.Checked, MinimumTriggerIntervalMs = (int)triggerInterval.Value };
        if (ConfigManager.Normalize(config)) Log("部分参数超过安全范围，已自动限制。");
        coyote.UpdateSettings(config);
    }
    private async void SettingsChanged()
    {
        if (loading || closing) return;
        long revision = ++settingsRevision, authorization = authorizationRevision;
        try
        {
            // Editing is not emergency-stop authorization. Cancel old playback, then
            // resume only an already-authorized listener, never an idle/stopped one.
            var stopping = coyote.StopAsync(false);
            ReadSettings(); penalty.UpdateSettings(config);
            loading = true;
            try
            {
                viewA.Base.Value = config.ChannelA.BaseStrength;
                viewB.Base.Value = config.ChannelB.BaseStrength;
            }
            finally { loading = false; }
            await stopping;
            if (closing || revision != settingsRevision || authorization != authorizationRevision) return;
            if (outputAuthorized && coordinator.IsListening && coyote.State.Ready) coyote.SetLinkEnabled(true);
            RefreshDevice();
        }
        catch (Exception ex) { Log("更新设置失败：" + ex); await SafeStopAsync(true); }
    }
    private void SaveSettings() { ReadSettings(); Log(ConfigManager.Save(config, out var error) ? "设置已保存；联动开关不保存。" : error!); }
    private async Task TestAsync(params char[] channels)
    {
        if (!coyote.State.Ready) { Log("设备未同步，不能测试。"); return; }
        ReadSettings();
        var outputs = new List<ChannelOutput>();
        try
        {
            foreach (var channel in channels)
            {
                var cfg = channel == 'A' ? config.ChannelA : config.ChannelB;
                outputs.Add(new(channel, coyote.Clamp(channel, cfg.BaseStrength), library.Get(cfg.WaveformId), cfg.DurationMs));
            }
            if (!suppressConfirmation && outputs.Any(x => x.Strength > 5))
            {
                using var confirmation = new Form { Text = "实际设备输出确认", ClientSize = new Size(390, 140), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog };
                var label = new Label { Text = "测试输出会控制实际设备，确认继续？", Dock = DockStyle.Top, Height = 40 };
                var suppress = new CheckBox { Text = "本次运行不再提示", Dock = DockStyle.Top, Height = 32 };
                var ok = new Button { Text = "确认测试", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom };
                confirmation.Controls.AddRange([suppress, label, ok]); confirmation.AcceptButton = ok;
                if (confirmation.ShowDialog(this) != DialogResult.OK) return;
                suppressConfirmation = suppress.Checked;
            }
            coyote.Play(outputs, manual: true);
        }
        catch (Exception ex) { Log("测试失败：" + ex.Message); await coyote.StopAsync(true); }
    }
    private async Task ConnectRelayAsync()
    {
        connect.Enabled = reconnect.Enabled = false;
        try
        {
            ReadSettings();
            qr?.Close(); qr = null;
            await coyote.ConnectAsync();
            if (closing || coyote.PairUrl.Length == 0) return;
            qr = new QrConnectionForm(coyote); qr.Show(this);
        }
        catch (Exception ex) { Log("连接官方中继失败：" + ex); }
        finally { if (!IsDisposed) RefreshDevice(); }
    }
    private void RefreshDevice()
    {
        var state = coyote.State;
        if (qr is { IsDisposed: false }) qr.UpdateState(state);
        if (outputAuthorized && coordinator.IsListening && state.Ready && !coyote.IsStopping && !coyote.LinkEnabled)
            coyote.SetLinkEnabled(true);
        // Rendering must not tear down an open popup. Recovery/output timers continue
        // independently; render the latest snapshot once the user closes the dropdown.
        if (AnyDropDownOpen(this)) return;
        deviceStatus.Text = QrConnectionForm.ConnectionText(state);
        deviceStatus.ForeColor = state.Ready ? Color.DarkGreen : Color.DimGray;
        var serverActive = state.Status is CoyoteConnectionStatus.ConnectingRelay or CoyoteConnectionStatus.WaitingForTargetId or CoyoteConnectionStatus.WaitingForApp or CoyoteConnectionStatus.Binding or CoyoteConnectionStatus.Paired;
        connect.Enabled = !serverActive && !coyote.IsConnecting; disconnect.Enabled = serverActive; reconnect.Enabled = !coyote.IsConnecting && state.Status is not (CoyoteConnectionStatus.ConnectingRelay or CoyoteConnectionStatus.WaitingForTargetId);

        viewA.Test.Enabled = viewB.Test.Enabled = state.Ready;
        viewA.State.Text = $"实时强度：{state.ActualA?.ToString() ?? "—"}\n最高允许：{coyote.EffectiveMax('A')}（同时受手机上限限制）";
        viewB.State.Text = $"实时强度：{state.ActualB?.ToString() ?? "—"}\n最高允许：{coyote.EffectiveMax('B')}（同时受手机上限限制）";
        RefreshStatus();
        dashboard.Text = $"本局死亡：{monitor.CurrentDeaths?.ToString() ?? "—"}    当前惩罚 A：{penalty.CurrentPenaltyA} / B：{penalty.CurrentPenaltyB}\n" +
            $"临时剩余：{penalty.TemporaryRemaining:F1} 秒    下一次死亡：\n{penalty.Preview('A')}\n{penalty.Preview('B')}\n" +
            $"联动死亡 {penalty.LinkedDeaths} / 总触发 {penalty.TotalTriggers} / 最高 A:{penalty.MaximumPenaltyA} B:{penalty.MaximumPenaltyB}\n" +
            $"连接时间：{(state.ConnectedSince.HasValue ? (DateTimeOffset.Now - state.ConnectedSince.Value).ToString(@"hh\:mm\:ss") : "—")}";
    }
    private async Task ScanAsync()
    {
        if (closing || AnyDropDownOpen(this)) return;
        if (scanning) { rescanPending = true; return; }
        scanning = true;
        try
        {
            var found = await Task.Run(scanner.GetVisibleWindows);
            if (closing) return;
            var selected = windows.SelectedItem as GameWindowInfo;
            var oldHandles = windows.Items.Cast<GameWindowInfo>().Select(w => (w.Hwnd, w.ProcessId)).ToHashSet();
            updating = true;
            windows.BeginUpdate();
            try
            {
                windows.Items.Clear();
                foreach (var item in found)
                {
                    windows.Items.Add(item);
                    if (!oldHandles.Contains((item.Hwnd, item.ProcessId))) Log($"找到游戏窗口：{item.Title}");
                }
                var match = found.FirstOrDefault(w => w.Hwnd == selected?.Hwnd && w.ProcessId == selected?.ProcessId);
                if (match is not null) windows.SelectedItem = match;
                else
                {
                    windows.SelectedIndex = -1;
                    if (selected is not null)
                    {
                        if (!WindowScanner.IsAlive(selected.Hwnd, selected.ProcessId)) WindowClosed();
                        else if (coordinator.IsListening) StopMonitoring();
                    }
                }
            }
            finally { windows.EndUpdate(); updating = false; }
            RefreshStatus();
        }
        catch (Exception ex) { if (!closing) Log($"扫描失败：{ex.Message}"); }
        finally
        {
            scanning = false;
            if (rescanPending && !closing)
            {
                rescanPending = false;
                await ScanAsync();
            }
        }
    }


    private void StartMonitoring()
    {
        if (windows.SelectedItem is not GameWindowInfo window) return;
        if (!WindowScanner.IsAlive(window.Hwnd, window.ProcessId)) { WindowClosed(); return; }
        selectedWindowClosed = false; inputGeneration++;
        penalty.Reset(); recent.Text = "最近事件：—";
        listeningWindow = window;
        coordinator.Configure(config.Detection.TriggerMode, config.Detection.TriggerDeduplicationMs);
        coordinator.Start(); ApplyDetectionSettings();
        monitor.Stop();
        if (config.Detection.TriggerMode != DeathTriggerMode.Hotkey) monitor.Start(window, keyword.Text);
        previous.Text = $"上次读取的死亡数：{monitor.LastDeaths?.ToString() ?? "—"}";
        if (coordinator.IsListening) { authorizationRevision++; outputAuthorized = true; coyote.SetLinkEnabled(true); pollTimer.Start(); Log($"开始监听并允许游戏事件输出：{window.Title}"); }
        RefreshStatus();
    }
    private void StopMonitoring()
    {
        pollTimer.Stop(); monitor.Stop(); coordinator.Stop(); hotkey.Enabled = false; listeningWindow = null; inputGeneration++; penalty.Reset(); _ = SafeStopAsync(true);
        previous.Text = "上次读取的死亡数：—"; recent.Text = "最近事件：—";
        Log("停止监听"); RefreshStatus();
    }
    private void RefreshStatus()
    {
        if (AnyDropDownOpen(this)) return;
        connection.Text = coordinator.IsListening ? "状态：● 正在监听" : selectedWindowClosed ? "游戏窗口：已关闭 / 等待重新选择" : "状态：等待选择 / 开始";
        connection.ForeColor = coordinator.IsListening ? Color.DarkGreen : Color.DimGray;
        current.Text = $"当前死亡：{monitor.CurrentDeaths?.ToString() ?? "未识别"}";
        detection.Text = $"标题检测：{(config.Detection.TriggerMode == DeathTriggerMode.Hotkey ? "未启用" : monitor.RecognitionMode == DeathRecognitionMode.Unknown ? "○ 未识别" : "● 已识别")} / {monitor.Status}";
        keyDetection.Text = $"按键检测：{(hotkey.Enabled ? "● " + hotkey.Config : config.Detection.TriggerMode == DeathTriggerMode.TitleCounter ? "未启用" : !keyboard.IsInstalled ? "按键检测不可用" : "等待开始监听")}";
        recognition.Text = monitor.RecognitionMode switch
        {
            DeathRecognitionMode.Standard => "识别方式：标准 Death",
            DeathRecognitionMode.Custom => $"识别方式：自定义：{monitor.CustomDeathKeyword}",
            _ => "识别方式：—"
        };
        start.Enabled = windows.SelectedItem is GameWindowInfo && (!coordinator.IsListening || !outputAuthorized) && !coyote.IsStopping;
        stop.Enabled = coordinator.IsListening;
    }

    private static bool AnyDropDownOpen(Control control)
    {
        if (control is ComboBox combo && combo.DroppedDown) return true;
        foreach (Control child in control.Controls) if (AnyDropDownOpen(child)) return true;
        return false;
    }



    private void OnKeyboardStroke(KeyboardStroke stroke)
    {
        if (!stroke.Down || stroke.Repeat || closing) return;
        bool emergencyKey = stroke.Key == Keys.F12 && stroke.Ctrl && stroke.Shift;
        long generation = inputGeneration;
        var foreground = HotkeyTrigger.GetForegroundWindow();
        bool capture = capturingKey;
        if (!capture && !emergencyKey &&
            (!hotkey.Enabled || !hotkey.Config.TryKey(out var configuredKey) || stroke.Key != configuredKey)) return;
        // LL callback only posts to the UI queue; no regex, device I/O, dialogs or logging here.
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (closing || generation != inputGeneration) return;
                if (emergencyKey)
                {
                    _ = SafeStopAsync(false);
                    if (capture) MessageBox.Show(this, "该快捷键已用于紧急停止，请选择其他按键。");
                    return;
                }
                if (capture)
                {
                    if (!HotkeyConfig.IsAllowed(stroke.Key)) return;
                    config.Detection.Hotkey.Key = stroke.Key.ToString();
                    config.Detection.Hotkey.Ctrl = stroke.Ctrl; config.Detection.Hotkey.Shift = stroke.Shift; config.Detection.Hotkey.Alt = stroke.Alt;
                    capturingKey = false; setTriggerKey.Text = "设置按键"; triggerKeyLabel.Text = config.Detection.Hotkey.ToString();
                    DetectionSettingsChanged(); return;
                }
                var target = listeningWindow;
                hotkey.Handle(stroke, foreground, target is not null && WindowScanner.IsAlive(target.Hwnd, target.ProcessId));
                RefreshStatus();
            }));
        }
        catch (InvalidOperationException) { }
    }
    private void ApplyDetectionSettings()
    {
        hotkey.Config = config.Detection.Hotkey;
        hotkey.Target = listeningWindow;
        hotkey.Enabled = coordinator.IsListening && config.Detection.TriggerMode != DeathTriggerMode.TitleCounter && keyboard.IsInstalled;
    }
    private void DetectionSettingsChanged()
    {
        if (loading || closing) return;
        ReadSettings(); inputGeneration++; capturingKey = false;
        setTriggerKey.Text = "设置按键"; triggerKeyLabel.Text = config.Detection.Hotkey.ToString();
        bool active = coordinator.IsListening;
        var target = listeningWindow;
        coordinator.Configure(config.Detection.TriggerMode, config.Detection.TriggerDeduplicationMs);
        monitor.Stop();
        if (active && target is not null)
        {
            coordinator.Start();
            if (config.Detection.TriggerMode != DeathTriggerMode.Hotkey) monitor.Start(target, keyword.Text);
        }
        ApplyDetectionSettings(); penalty.Reset(); recent.Text = "最近事件：—"; RefreshStatus();
    }
    private void PollDetection()
    {
        if (!coordinator.IsListening || listeningWindow is null) return;
        if (!WindowScanner.IsAlive(listeningWindow.Hwnd, listeningWindow.ProcessId)) { WindowClosed(); return; }
        if (config.Detection.TriggerMode != DeathTriggerMode.Hotkey) monitor.Poll();
        RefreshStatus();
    }
    private void WindowClosed()
    {
        pollTimer.Stop(); monitor.Stop(); coordinator.Stop(); hotkey.Enabled = false; listeningWindow = null; inputGeneration++;
        selectedWindowClosed = true;
        bool oldUpdating = updating; updating = true;
        try { windows.SelectedIndex = -1; } finally { updating = oldUpdating; }
        RefreshStatus();
        Log("游戏窗口已关闭；等待重新选择");
        connection.Text = "游戏窗口：已关闭 / 等待重新选择"; recent.Text = "最近事件：—";
        _ = SafeStopAsync(true);
    }

    private void RefreshPresets(string? selectedPath = null)
    {
        var previousLoading = loading; loading = true;
        try
        {
            presets.Items.Clear(); presets.Items.Add(new PresetItem(null, new Preset("默认（保守）", new AppConfig())));
            foreach (var item in presetManager.Load(Log)) presets.Items.Add(new PresetItem(item.Path, item.Value));
            presets.SelectedItem = presets.Items.Cast<PresetItem>().FirstOrDefault(x => x.Path == selectedPath) ?? presets.Items[0];
        }
        finally { loading = previousLoading; }
    }
    private void SavePreset(bool saveAs)
    {
        try
        {
            ReadSettings(); var selected = presets.SelectedItem as PresetItem;
            var name = selected?.Value.Name ?? "自定义";
            if (saveAs || selected?.Path is null)
            {
                using var form = new Form { Text = "预设名称", ClientSize = new Size(300, 90), StartPosition = FormStartPosition.CenterParent };
                var input = new TextBox { Dock = DockStyle.Top, Text = "自定义", MaxLength = 80 };
                var ok = new Button { Text = "保存", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
                form.Controls.AddRange([input, ok]); form.AcceptButton = ok;
                if (form.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(input.Text)) return;
                name = input.Text.Trim();
            }
            var path = presetManager.Save(new Preset(name, ConfigManager.Clone(config)), saveAs ? null : selected?.Path);
            RefreshPresets(path); Log("预设已保存。");
        }
        catch (Exception ex) { Log("预设保存失败：" + ex.Message); }
    }
    private void DeletePreset()
    {
        if (presets.SelectedItem is not PresetItem { Path: not null } selected) return;
        if (MessageBox.Show(this, "删除所选预设？", "删除预设", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try { presetManager.Delete(selected.Path); RefreshPresets(); Log("所选预设已删除；无自动恢复副本。"); } catch (Exception ex) { Log(ex.Message); }
    }
    private void ImportPreset()
    {
        using var file = new OpenFileDialog { Filter = "预设 JSON|*.json" };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var preset = presetManager.Import(file.FileName, out var limited);
            foreach (var channel in new[] { preset.Settings.ChannelA, preset.Settings.ChannelB })
            {
                if (!library.Items.Any(x => x.Id == channel.WaveformId)) { channel.WaveformId = "breathing"; limited = true; }
                var valid = channel.WaveformIds.Where(id => library.Items.Any(x => x.Id == id)).ToList();
                if (valid.Count != channel.WaveformIds.Count) { channel.WaveformIds = valid; limited = true; }
                if (channel.WaveformMode != WaveformMode.Single && channel.WaveformIds.Count == 0) { channel.WaveformIds = ["breathing"]; limited = true; }
            }
            var path = presetManager.Save(preset); RefreshPresets(path);
            if (limited) MessageBox.Show(this, "部分参数超过安全范围，已自动限制。");
            Log("预设已导入；请选择预设以加载。");
        }
        catch (Exception ex) { MessageBox.Show(this, "导入失败：" + ex.Message); }
    }
    private void ExportPreset()
    {
        using var file = new SaveFileDialog { Filter = "预设 JSON|*.json", FileName = "ExportPreset.json" };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        try { ReadSettings(); PresetManager.Export(file.FileName, new Preset((presets.SelectedItem as PresetItem)?.Value.Name ?? "自定义", ConfigManager.Clone(config))); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }
    private void ImportWaveform()
    {
        using var file = new OpenFileDialog { Filter = "波形|*.pulse;*.json" };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ReadSettings(); var waveform = library.Import(file.FileName); SettingsChanged(); LoadUi();
            Log("已导入波形：" + waveform.Name);
        }
        catch (Exception ex) { MessageBox.Show(this, "导入失败：" + ex.Message); }
    }
    private void Log(string message)
    {
        if (closing || IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke((Action)(() => Log(message))); }
            catch (InvalidOperationException) { }
            return;
        }
        message = message.Replace('\r', ' ').Replace('\n', ' ');
        if (message.Length > 1500) message = message[..1500] + "…";
        logLines.Enqueue($"{DateTime.Now:HH:mm:ss} {message}");
        while (logLines.Count > 300) logLines.Dequeue();
        logs.Lines = logLines.ToArray();
        logs.SelectionStart = logs.TextLength;
        logs.ScrollToCaret();
    }

}
