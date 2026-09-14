using System.Text.Json;
using System.Text.Json.Serialization;
using IWCoyoteBridge.Core;
namespace IWCoyoteBridge.Config;
public static class ConfigManager
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "config.json");
    public static AppConfig Clone(AppConfig value) => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
    public static bool Normalize(AppConfig config)
    {
        var before = JsonSerializer.Serialize(config, JsonOptions);
        config.Detection ??= new(); config.Socket ??= new(); config.ChannelA ??= new(); config.ChannelB ??= new() { Enabled = false }; config.Penalty ??= new();
        config.PollInterval = Math.Clamp(config.PollInterval, 100, 200);
        config.CustomDeathKeyword = DeathCounterParser.NormalizeKeyword(config.CustomDeathKeyword);
        config.Detection.Hotkey ??= new();
        if (!config.Detection.Hotkey.TryKey(out _) || config.Detection.Hotkey.IsEmergency) config.Detection.Hotkey = new();
        if (!Enum.IsDefined(config.Detection.TriggerMode)) config.Detection.TriggerMode = DeathTriggerMode.TitleCounter;
        config.Detection.TriggerDeduplicationMs = Math.Clamp(config.Detection.TriggerDeduplicationMs, 100, 3000);
        if (config.CustomDeathKeyword.Length > 128) config.CustomDeathKeyword = config.CustomDeathKeyword[..128];
        config.Socket.Protocol = "V3";
        config.Socket.RelayUrl = IWCoyoteBridge.Coyote.DglabV3Protocol.OfficialRelayUrl;
        foreach (var channel in new[] { config.ChannelA, config.ChannelB })
        {
            channel.MaxStrength = Math.Clamp(channel.MaxStrength, 0, 100);
            channel.BaseStrength = Math.Clamp(channel.BaseStrength, 0, channel.MaxStrength);
            channel.DeathIncrement = Math.Clamp(channel.DeathIncrement, 0, 100);
            channel.DurationMs = Math.Clamp(channel.DurationMs, 100, 3000);
            channel.RandomRange = Math.Clamp(channel.RandomRange, 0, 20);
            if (!Enum.IsDefined(channel.WaveformMode)) channel.WaveformMode = WaveformMode.Single;
            channel.WaveformId = string.IsNullOrWhiteSpace(channel.WaveformId) ? "breathing" : channel.WaveformId[..Math.Min(80, channel.WaveformId.Length)];
            channel.WaveformIds = (channel.WaveformIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 80).Distinct().Take(100).ToList();
        }
        if (!Enum.IsDefined(config.Penalty.Mode)) config.Penalty.Mode = PenaltyMode.Fixed;
        config.Penalty.TemporaryRecoverySeconds = Math.Clamp(config.Penalty.TemporaryRecoverySeconds, 1, 600);
        config.Penalty.MinimumTriggerIntervalMs = Math.Clamp(config.Penalty.MinimumTriggerIntervalMs, 100, 5000);
        return before != JsonSerializer.Serialize(config, JsonOptions);
    }
    public static bool IsValid(AppConfig config) => !Normalize(Clone(config));
    public static AppConfig Load(out string? warning)
    {
        warning = null;
        try
        {
            if (File.Exists(FilePath))
            {
                if (new FileInfo(FilePath).Length > 65536) throw new FormatException("配置文件过大");
                var text = File.ReadAllText(FilePath);
                using var doc = JsonDocument.Parse(text);
                var config = JsonSerializer.Deserialize<AppConfig>(text, JsonOptions) ?? throw new FormatException("配置为空");
                if (!doc.RootElement.TryGetProperty("Detection", out _))
                {
                    File.Copy(FilePath, FilePath + ".pre-socket-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
                    if (doc.RootElement.TryGetProperty("PollInterval", out var poll)) config.PollInterval = poll.GetInt32();
                    if (doc.RootElement.TryGetProperty("CustomDeathKeyword", out var keyword)) config.CustomDeathKeyword = keyword.GetString() ?? "Death";
                    warning = "旧配置已备份并迁移；旧 HTTP / Game Hub 设置不再使用。";
                }
                if (Normalize(config)) warning = "部分参数超过安全范围，已自动限制。";
                Save(config, out _); return config;
            }
        }
        catch (Exception ex) { warning = "配置读取失败，使用安全默认值：" + ex.Message; }
        var defaults = new AppConfig();
        if (!File.Exists(FilePath)) Save(defaults, out _);
        return defaults;
    }
    public static bool Save(AppConfig config, out string? error)
    {
        error = null;
        try { Normalize(config); File.WriteAllText(FilePath, JsonSerializer.Serialize(config, JsonOptions)); return true; }
        catch (Exception ex) { error = "保存失败：" + ex.Message; return false; }
    }
}
