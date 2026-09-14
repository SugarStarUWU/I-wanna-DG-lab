using System.Text.Json;
namespace IWCoyoteBridge.Coyote;
public static class DglabV3Protocol
{
    public const string OfficialRelayUrl = "wss://ws.dungeon-lab.cn/";
    public static string PairUrl(string relay, string targetId)
    {
        if (!Guid.TryParseExact(targetId, "D", out _)) throw new FormatException("无效的服务器配对 ID");
        return new Uri(new Uri(relay), targetId).AbsoluteUri;
    }
    // Official simple/socket/v2/frontend/wsConnection.js: QR app-download fragment wrapper.
    // The published example has a stale LAN literal; use dglab-kit README V3's relay/{targetId}.
    public static string QrPayload(string pairUrl) =>
        "https://www.dungeon-lab.com/app-download.php#DGLAB-SOCKET#" + pairUrl;
    // dglab-kit src/socket/v3/index.ts: setStrength, clearPulse, sendPulse, send.
    // Keep internal APP commands unchanged; translate only at the relay boundary.
    public static string Command(string controllerId, string pairedClientId, string command)
    {
        object frame;
        if (command.StartsWith("strength-", StringComparison.Ordinal))
        {
            var parts = command[9..].Split('+');
            if (parts.Length != 3 || parts[1] != "2" || !int.TryParse(parts[0], out var ch) || ch is < 1 or > 2 ||
                !int.TryParse(parts[2], out var strength) || strength is < 0 or > 200) throw new FormatException("无效强度指令");
            frame = new { type = 3, clientId = controllerId, targetId = pairedClientId, channel = ch, strength, message = "set channel" };
        }
        else if (command is "clear-1" or "clear-2")
            frame = new { type = 4, clientId = controllerId, targetId = pairedClientId, channel = command == "clear-1" ? 1 : 2, message = "clear" };
        else if (command.StartsWith("pulse-A:", StringComparison.Ordinal) || command.StartsWith("pulse-B:", StringComparison.Ordinal))
        {
            // One relay send, not a server-side repeating punishment timer. Duration and
            // cancellation remain owned by the existing serialized CoyoteOutputQueue.
            frame = new { type = "clientMsg", clientId = controllerId, targetId = pairedClientId, channel = command[6].ToString(), time = 1, message = command[6..] };
        }
        else throw new FormatException("不支持的控制指令");
        var json = JsonSerializer.Serialize(frame);
        if (json.Length > CoyoteProtocol.MaximumTextLength) throw new FormatException("Socket 消息过长");
        return json;
    }
}
