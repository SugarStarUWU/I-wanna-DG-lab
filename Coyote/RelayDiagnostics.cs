using System.Net;
namespace IWCoyoteBridge.Coyote;
public static class RelayDiagnostics
{
    // Fresh outbound socket; never pairs, sends commands, or touches controller/link state.
    public static async Task<string> RunAsync(Action<string> log, CancellationToken token = default)
    {
        var lines = new List<string>();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(new Uri(DglabV3Protocol.OfficialRelayUrl).Host, token).WaitAsync(TimeSpan.FromSeconds(8), token);
            if (addresses.Length == 0) throw new IOException("DNS 未返回地址");
            lines.Add("DNS：✓");
        }
        catch (Exception ex) { return "DNS：✕\n错误：" + ex; }
        await using var probe = new DglabV3SocketClient(log);
        bool opened = false;
        probe.StateChanged += state => { if (state.Status == CoyoteConnectionStatus.WaitingForTargetId) opened = true; };
        try
        {
            await probe.ConnectAsync(token);
            lines.Add("WebSocket：✓"); lines.Add("协议握手：✓"); lines.Add("Target ID：✓");
        }
        catch (Exception ex)
        {
            lines.Add("WebSocket：" + (opened ? "✓" : "✕"));
            lines.Add("协议握手：✕"); lines.Add("Target ID：✕"); lines.Add("错误：" + ex);
        }
        return string.Join(Environment.NewLine, lines);
    }
}
