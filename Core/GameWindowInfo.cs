namespace IWCoyoteBridge.Core;

public sealed record GameWindowInfo(nint Hwnd, uint ProcessId, string Title, string ProcessName = "未知进程")
{
    public override string ToString() => $"{Title}    [{ProcessName}]";
    public string Tooltip => $"{Title}\n{ProcessName}\nPID: {ProcessId}";
}
