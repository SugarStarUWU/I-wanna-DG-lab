using System.Text.Json.Serialization;
using IWCoyoteBridge.Core;
using IWCoyoteBridge.Input;
namespace IWCoyoteBridge.Config;
public enum PenaltyMode { Fixed, Permanent, Temporary }
public enum WaveformMode { Single, Sequence, Random }
public sealed class DetectionConfig
{
    public int PollIntervalMs { get; set; } = 150;
    public string CustomDeathKeyword { get; set; } = "Death";
    public DeathTriggerMode TriggerMode { get; set; }
    public HotkeyConfig Hotkey { get; set; } = new();
    public int TriggerDeduplicationMs { get; set; } = 500;
}
public sealed class SocketConfig
{
    public string Protocol { get; set; } = "V3";
    public string RelayUrl { get; set; } = IWCoyoteBridge.Coyote.DglabV3Protocol.OfficialRelayUrl;
}
public sealed class ChannelConfig
{
    public bool Enabled { get; set; } = true;
    public int BaseStrength { get; set; } = 5;
    public int MaxStrength { get; set; } = 30;
    public int DeathIncrement { get; set; } = 5;
    public int DurationMs { get; set; } = 500;
    public string WaveformId { get; set; } = "breathing";
    public WaveformMode WaveformMode { get; set; }
    public List<string> WaveformIds { get; set; } = ["breathing"];
    public bool RandomEnabled { get; set; }
    public int RandomRange { get; set; }
}
public sealed class PenaltyConfig
{
    public PenaltyMode Mode { get; set; }
    public int TemporaryRecoverySeconds { get; set; } = 30;
    public bool TemporaryStacking { get; set; }
    public int MinimumTriggerIntervalMs { get; set; } = 300;
}
public sealed class AppConfig
{
    public DetectionConfig Detection { get; set; } = new();
    public SocketConfig Socket { get; set; } = new();
    public ChannelConfig ChannelA { get; set; } = new();
    public ChannelConfig ChannelB { get; set; } = new() { Enabled = false };
    public PenaltyConfig Penalty { get; set; } = new();
    [JsonIgnore] public int PollInterval { get => Detection.PollIntervalMs; set => Detection.PollIntervalMs = value; }
    [JsonIgnore] public string CustomDeathKeyword { get => Detection.CustomDeathKeyword; set => Detection.CustomDeathKeyword = value; }
}
