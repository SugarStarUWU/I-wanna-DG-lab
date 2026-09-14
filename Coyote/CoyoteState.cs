namespace IWCoyoteBridge.Coyote;
public enum CoyoteConnectionStatus { Stopped, ConnectingRelay, WaitingForTargetId, WaitingForApp, Binding, Paired, Disconnected, Error }
public sealed record CoyoteState(CoyoteConnectionStatus Status = CoyoteConnectionStatus.Stopped,
    int? ActualA = null, int? ActualB = null, int? LimitA = null, int? LimitB = null,
    DateTimeOffset? ConnectedSince = null, string? Detail = null)
{
    public bool Ready => Status == CoyoteConnectionStatus.Paired && ActualA.HasValue && ActualB.HasValue && LimitA.HasValue && LimitB.HasValue;
}
