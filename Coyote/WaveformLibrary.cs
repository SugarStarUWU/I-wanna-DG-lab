using System.Reflection;
using System.Text.Json;
namespace IWCoyoteBridge.Coyote;
public sealed class WaveformLibrary
{
    private sealed record Official(string Id, string Name, string[] Frames, string Raw);
    private readonly List<CoyoteWaveform> waveforms = [];
    private readonly Dictionary<string, CoyoteWaveform> knownPulse = new(StringComparer.Ordinal);
    public IReadOnlyList<CoyoteWaveform> Items => waveforms;
    public string DirectoryPath { get; }
    public WaveformLibrary(string? directory = null, Action<string>? log = null)
    {
        DirectoryPath = directory ?? Path.Combine(AppContext.BaseDirectory, "waveforms");
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("IW-Coyote-Bridge.Coyote.official-waveforms.json") ??
            Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(x => x.EndsWith("official-waveforms.json", StringComparison.Ordinal)).Select(x => Assembly.GetExecutingAssembly().GetManifestResourceStream(x)).First()!;
        foreach (var item in JsonSerializer.Deserialize<Official[]>(resource)!)
        {
            var waveform = new CoyoteWaveform(item.Id, item.Name, item.Frames);
            waveform.Validate(); waveforms.Add(waveform); knownPulse.Add(item.Raw.Trim(), waveform);
        }
        if (!Directory.Exists(DirectoryPath)) return;
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json").Take(100))
        {
            try { var wave = ReadJson(file); if (waveforms.All(x => x.Id != wave.Id)) waveforms.Add(wave); }
            catch (Exception ex) { log?.Invoke("波形跳过：" + Path.GetFileName(file) + " / " + ex.Message); }
        }
    }
    public CoyoteWaveform Get(string id) => waveforms.FirstOrDefault(x => x.Id == id) ?? throw new FormatException("找不到波形：" + id);
    private static CoyoteWaveform ReadJson(string file)
    {
        if (new FileInfo(file).Length > 65536) throw new FormatException("波形文件不得超过 64 KB");
        var waveform = JsonSerializer.Deserialize<CoyoteWaveform>(File.ReadAllText(file), Config.ConfigManager.JsonOptions) ?? throw new FormatException("空波形");
        waveform.Validate(); return waveform;
    }
    public CoyoteWaveform Import(string file)
    {
        if (waveforms.Count >= 116) throw new FormatException("自定义波形最多 100 个");
        if (new FileInfo(file).Length > 65536) throw new FormatException("波形文件不得超过 64 KB");
        CoyoteWaveform waveform;
        if (Path.GetExtension(file).Equals(".pulse", StringComparison.OrdinalIgnoreCase))
        {
            var raw = File.ReadAllText(file).Trim();
            var frames = knownPulse.TryGetValue(raw, out var official) ? official.Frames : PulseFileParser.Parse(raw);
            waveform = new(Guid.NewGuid().ToString("D"), Path.GetFileNameWithoutExtension(file), frames);
        }
        else if (Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase))
            waveform = ReadJson(file) with { Id = Guid.NewGuid().ToString("D") };
        else throw new FormatException("只接受 .pulse 或 .json");
        waveform.Validate();
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, waveform.Id + ".json"), JsonSerializer.Serialize(waveform, Config.ConfigManager.JsonOptions));
        waveforms.Add(waveform); return waveform;
    }
}
