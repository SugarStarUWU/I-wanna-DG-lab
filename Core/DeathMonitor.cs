namespace IWCoyoteBridge.Core;

public sealed class DeathMonitor(IWindowReader reader, Func<long>? clock = null)
{
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private GameWindowInfo? target;
    private bool baseline;
    private long? lastEvent;
    public string CustomDeathKeyword { get; private set; } = "Death";
    public DeathRecognitionMode RecognitionMode { get; private set; }
    public bool IsMonitoring => target is not null;
    public int? CurrentDeaths { get; private set; }
    public int? LastDeaths { get; private set; }
    public string Status { get; private set; } = "未连接";
    public event EventHandler<DeathDetectedEventArgs>? DeathDetected;
    public event EventHandler? Disconnected;

    public void Start(GameWindowInfo window, string? customKeyword = "Death")
    {
        Stop();
        CustomDeathKeyword = DeathCounterParser.NormalizeKeyword(customKeyword);
        target = window;
        Status = "正在监听";
        Poll(); // Establish baseline immediately; never emit on connection.
    }

    public void Stop()
    {
        target = null;
        baseline = false;
        lastEvent = null;
        CurrentDeaths = LastDeaths = null;
        RecognitionMode = DeathRecognitionMode.Unknown;
        Status = "已停止 / 未连接";
    }

    public void UpdateCustomKeyword(string? keyword)
    {
        var normalized = DeathCounterParser.NormalizeKeyword(keyword);
        if (normalized == CustomDeathKeyword) return;
        CustomDeathKeyword = normalized;
        baseline = false;
        CurrentDeaths = LastDeaths = null;
        RecognitionMode = DeathRecognitionMode.Unknown;
        if (IsMonitoring) Poll(); // New parsing identity: initialize without output.
    }

    public void Poll()
    {
        if (target is null) return;
        string title;
        try
        {
            if (!reader.TryRead(target.Hwnd, target.ProcessId, out title))
            {
                Stop();
                Status = "游戏已关闭 / 未连接";
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
        catch
        {
            baseline = false;
            CurrentDeaths = LastDeaths = null;
            RecognitionMode = DeathRecognitionMode.Unknown;
            Status = "读取异常，等待重新建立基线";
            return;
        }
        if (!DeathCounterParser.TryParseDeathCount(title, CustomDeathKeyword, out var count, out var mode))
        {
            baseline = false;
            CurrentDeaths = LastDeaths = null;
            RecognitionMode = DeathRecognitionMode.Unknown;
            Status = "标题解析失败，等待重新建立基线";
            return;
        }
        Status = "正在监听";
        if (!baseline || RecognitionMode != mode)
        {
            CurrentDeaths = LastDeaths = count;
            baseline = true;
            RecognitionMode = mode;
            return;
        }
        var previous = CurrentDeaths!.Value;
        CurrentDeaths = count;
        LastDeaths = count;
        if (count <= previous) return;
        var time = now();
        if (lastEvent.HasValue && time - lastEvent.Value < 300) return;
        lastEvent = time;
        DeathDetected?.Invoke(this, new(previous, count));
    }
}
