namespace IWCoyoteBridge.Coyote;
public interface IDglabSocketClient : IAsyncDisposable
{
    CoyoteState State { get; }
    string ControllerId { get; }
    string PairUrl { get; }
    bool DebugLogging { get; set; }
    event Action<CoyoteState>? StateChanged;
    Task ConnectAsync(CancellationToken token = default);
    Task SendAsync(string command, CancellationToken token);
    Task DisconnectAsync();
}
