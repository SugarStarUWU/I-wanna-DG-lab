using System.Text.Json;
namespace IWCoyoteBridge.Config;
public sealed record Preset(string Name, AppConfig Settings)
{
    public override string ToString() => Name;
}
public sealed class PresetManager
{
    private readonly string directory;
    public PresetManager(string? path = null) => directory = path ?? Path.Combine(AppContext.BaseDirectory, "presets");
    public IEnumerable<(string Path, Preset Value)> Load(Action<string> log)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Take(100))
        {
            Preset? preset = null;
            try { preset = Import(path, out _); } catch (Exception ex) { log("预设跳过：" + ex.Message); }
            if (preset is not null) yield return (path, preset);
        }
    }
    public Preset Import(string path, out bool limited)
    {
        if (new FileInfo(path).Length > 65536) throw new FormatException("预设文件不得超过 64 KB");
        var preset = JsonSerializer.Deserialize<Preset>(File.ReadAllText(path), ConfigManager.JsonOptions) ?? throw new FormatException("空预设");
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 80 || preset.Settings is null) throw new FormatException("预设名称 / 参数无效");
        limited = ConfigManager.Normalize(preset.Settings); return preset;
    }
    public string Save(Preset preset, string? existing = null)
    {
        Directory.CreateDirectory(directory);
        var path = existing ?? Path.Combine(directory, Guid.NewGuid().ToString("D") + ".json");
        if (Path.GetDirectoryName(Path.GetFullPath(path)) != Path.GetFullPath(directory)) throw new InvalidOperationException("预设路径超出目录");
        Export(path, preset); return path;
    }
    public static void Export(string path, Preset preset) => File.WriteAllText(path, JsonSerializer.Serialize(preset, ConfigManager.JsonOptions));
    public void Delete(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) != Path.GetFullPath(directory)) throw new InvalidOperationException("预设路径无效");
        File.Delete(path);
    }
}
