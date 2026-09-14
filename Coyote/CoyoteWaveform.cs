namespace IWCoyoteBridge.Coyote;
public sealed record CoyoteWaveform(string Id, string Name, string[] Frames)
{
    public override string ToString() => Name;
    public static void ValidateFrame(string? frame)
    {
        if (frame is null || frame.Length != 16 || frame.Any(c => !Uri.IsHexDigit(c))) throw new FormatException("每帧必须为 16 个 HEX 字符");
        var bytes = Convert.FromHexString(frame);
        if (bytes.Take(4).Any(x => x is < 10 or > 240) || bytes.Skip(4).Any(x => x > 100))
            throw new FormatException("V3 频率须为 10～240，相对幅度须为 0～100");
    }
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 80 || string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Frames is null || Frames.Length is < 1 or > 1000)
            throw new FormatException("波形 ID / 名称无效，帧数须为 1～1000");
        foreach (var frame in Frames) ValidateFrame(frame);
    }
    public string[] ForDuration(int durationMs) => Enumerable.Range(0, (Math.Clamp(durationMs, 100, 3000) + 99) / 100).Select(i => Frames[i % Frames.Length]).ToArray();
}
