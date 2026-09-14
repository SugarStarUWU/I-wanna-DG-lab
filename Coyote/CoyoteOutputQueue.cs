using System.Threading.Channels;
namespace IWCoyoteBridge.Coyote;

// One worker, one pending request. A new request supersedes active playback; the worker
// finishes its cancellation cleanup before starting the next request.
public sealed class CoyoteOutputQueue : IDisposable
{
    private readonly object gate = new();
    private sealed record Request(Func<CancellationToken, Task> Run, TaskCompletionSource<bool> Completion);
    private readonly Channel<Request> pending = Channel.CreateBounded<Request>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false });
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? active;
    private Request? activeRequest;
    private readonly Task worker;
    private readonly Action<Exception> fault;
    public CoyoteOutputQueue(Action<Exception> onFault) { fault = onFault; worker = WorkAsync(); }
    public Task<bool> Enqueue(Func<CancellationToken, Task> request)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (lifetime.IsCancellationRequested) { completion.SetResult(false); return completion.Task; }
            active?.Cancel();
            while (pending.Reader.TryRead(out var dropped)) dropped.Completion.TrySetResult(false);
            if (!pending.Writer.TryWrite(new(request, completion))) completion.TrySetResult(false);
        }
        return completion.Task;
    }
    public void Clear()
    {
        lock (gate) { active?.Cancel(); while (pending.Reader.TryRead(out var dropped)) dropped.Completion.TrySetResult(false); }
    }
    public Task DrainAsync()
    {
        lock (gate) return activeRequest?.Completion.Task ?? Task.CompletedTask;
    }
    private async Task WorkAsync()
    {
        try
        {
            while (await pending.Reader.WaitToReadAsync(lifetime.Token).ConfigureAwait(false))
            {
                Request? request;
                CancellationTokenSource token;
                lock (gate)
                {
                    if (!pending.Reader.TryRead(out request)) continue;
                    active = token = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    activeRequest = request;
                }
                try { await request.Run(token.Token).ConfigureAwait(false); request.Completion.TrySetResult(true); }
                catch (OperationCanceledException) { request.Completion.TrySetResult(false); }
                catch (Exception ex) { request.Completion.TrySetResult(false); fault(ex); }
                finally { lock (gate) { if (active == token) { active = null; activeRequest = null; } token.Dispose(); } }
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose() { Clear(); lifetime.Cancel(); pending.Writer.TryComplete(); _ = worker; }
}
