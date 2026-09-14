using System.Text.Json;
using System.Text.RegularExpressions;
namespace IWCoyoteBridge.Coyote;
public sealed record SocketMessage(string type, string clientId, string targetId, string message);
public static class CoyoteProtocol
{
    public const int MaximumTextLength = 1950;
    private static readonly Regex StrengthPattern = new(@"^strength-([0-9]{1,3})\+([0-9]{1,3})\+([0-9]{1,3})\+([0-9]{1,3})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    public static string Serialize(SocketMessage message)
    {
        var json = JsonSerializer.Serialize(message);
        if (json.Length > MaximumTextLength) throw new FormatException("Socket 消息超过 1950 字符");
        return json;
    }
    public static SocketMessage Parse(string text)
    {
        if (text.Length > MaximumTextLength) throw new FormatException("Socket 消息过长");
        var message = JsonSerializer.Deserialize<SocketMessage>(text) ?? throw new FormatException("空消息");
        if (string.IsNullOrWhiteSpace(message.type) || !Guid.TryParseExact(message.clientId, "D", out _) ||
            message.targetId is null || message.message is null || message.message.Length == 0)
            throw new FormatException("Socket 字段无效");
        return message;
    }
    public static bool TryStrength(string message, out int a, out int b, out int limitA, out int limitB)
    {
        a = b = limitA = limitB = 0;
        var match = StrengthPattern.Match(message);
        if (!match.Success) return false;
        var values = Enumerable.Range(1, 4).Select(i => int.Parse(match.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (values.Any(x => x > 200)) return false;
        (a, b, limitA, limitB) = (values[0], values[1], values[2], values[3]);
        return true;
    }
    public static string Strength(char channel, int value) => $"strength-{(channel == 'A' ? 1 : 2)}+2+{Math.Clamp(value, 0, 200)}";
    public static string Clear(char channel) => channel == 'A' ? "clear-1" : "clear-2";
    public static string Pulse(char channel, IEnumerable<string> frames)
    {
        var data = frames.ToArray();
        if (data.Length is < 1 or > 100) throw new FormatException("每个 pulse 需要 1～100 帧");
        foreach (var frame in data) CoyoteWaveform.ValidateFrame(frame);
        return $"pulse-{channel}:" + JsonSerializer.Serialize(data);
    }
}
