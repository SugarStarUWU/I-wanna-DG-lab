using System.Globalization;
namespace IWCoyoteBridge.Coyote;

// Independent bounded implementation of DG-LAB's exported Dungeonlab+pulse format.
// Slider maps and section/element modes are documented by the export-format analysis:
// https://forum.dg-bbs.com/d/18-344-xin-ban-bo-xing-shu-ju-ge-shi-jie-xi
// Speed grouping is cross-checked against the primary open-source pulse parser:
// MofuNadenade/DG-LAB-VRCOSC/src/core/official/pulse_file_parser.py.
public static class PulseFileParser
{
    private static readonly int[] Frequencies = [10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,
        50,52,54,56,58,60,62,64,66,68,70,72,74,76,78,80,85,90,95,100,110,120,130,140,150,160,170,180,190,200,233,266,300,333,366,400,450,500,550,600,700,800,900,1000];
    private static readonly double[] Durations = [0.1,0.2,0.3,0.4,0.5,0.6,0.7,0.8,0.9,1,1.1,1.2,1.3,1.4,1.5,1.6,1.7,1.8,1.9,2,2.1,2.2,2.3,2.4,2.5,2.6,2.7,2.8,2.9,3,3.1,3.2,3.3,3.4,3.5,3.6,3.7,3.8,3.9,4,4.1,4.2,4.3,4.4,4.5,4.6,4.7,4.8,4.9,
        5,5.2,5.4,5.6,5.8,6,6.2,6.4,6.6,6.8,7,7.2,7.4,7.6,7.8,8,8.5,9,9.5,10,11,12,13,14,15,16,17,18,19,20,23.4,26.6,30,33.4,36.6,40,45,50,55,60,70,80,90,100,120,140,160,180,200,250,300];
    private static int Integer(string value, int minimum, int maximum)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum)
            throw new FormatException($"波形参数须为 {minimum}～{maximum} 的整数");
        return number;
    }
    private static byte Frequency(double input) => (byte)Math.Clamp((int)(input <= 100 ? input : input <= 600 ? (input - 100) / 5 + 100 : (input - 600) / 10 + 200), 10, 240);
    public static string[] Parse(string text)
    {
        text = text.Trim();
        const string prefix = "Dungeonlab+pulse:";
        if (text.Length > 65536 || !text.StartsWith(prefix, StringComparison.Ordinal)) throw new FormatException("不是有效的 Dungeonlab+pulse 文件");
        var pieces = text[prefix.Length..].Split('=', StringSplitOptions.None);
        if (pieces.Length != 2) throw new FormatException("波形需要一个 '=' 分隔符");
        var header = pieces[0].Split(',');
        if (header.Length != 3) throw new FormatException("波形头部须为休息时间、速度、元数据三个整数");
        int rest = Integer(header[0], 0, 600), speed = Integer(header[1], 1, 4);
        int metadata = Integer(header[2], 0, 65535);
        if (metadata is not (8 or 16)) throw new FormatException("不支持此 .pulse 元数据版本（只接受已验证的 8 / 16）");
        if (speed is not (1 or 2 or 4)) throw new FormatException("速度只能为 1、2、4");
        var sections = pieces[1].Split("+section+", StringSplitOptions.None);
        if (sections.Length is < 1 or > 10) throw new FormatException("波形须有 1～10 个小节");
        var steps = new List<(byte Frequency, byte Amplitude)>();
        foreach (var section in sections)
        {
            var parts = section.Split('/');
            if (parts.Length != 2) throw new FormatException("小节需要一个 '/' 分隔符");
            var parameters = parts[0].Split(',');
            if (parameters.Length != 5) throw new FormatException("小节描述须有五个参数");
            int start = Frequencies[Integer(parameters[0], 0, Frequencies.Length - 1)], end = Frequencies[Integer(parameters[1], 0, Frequencies.Length - 1)];
            double duration = Durations[Integer(parameters[2], 0, Durations.Length - 1)];
            int mode = Integer(parameters[3], 1, 4), enabled = Integer(parameters[4], 0, 1);
            var items = parts[1].Split(',');
            if (items.Length is < 2 or > 500) throw new FormatException("每个脉冲元须有 2～500 个形状点");
            var amplitudes = new byte[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i].Split('-');
                if (item.Length != 2 || !decimal.TryParse(item[0].Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value < 0 || value > 100)
                    throw new FormatException("形状点须为 0～100 的幅度及 0 / 1 的锚点类型");
                _ = Integer(item[1], 0, 1); amplitudes[i] = (byte)value;
            }
            if (enabled == 0) continue;
            int cycles = Math.Max(1, (int)Math.Ceiling(duration * 10 * speed / items.Length - 1e-9));
            int totalItems = checked(cycles * items.Length);
            if (steps.Count + (long)totalItems * (4 / speed) > 4000) throw new FormatException("展开后的波形不得超过 1000 帧（100 秒）");
            for (int cycle = 0; cycle < cycles; cycle++)
                for (int item = 0; item < items.Length; item++)
                {
                    double progress = mode switch
                    {
                        2 => totalItems > 1 ? (cycle * items.Length + item) / (double)(totalItems - 1) : 0,
                        3 => item / (double)(items.Length - 1),
                        4 => cycles > 1 ? cycle / (double)(cycles - 1) : 0,
                        _ => 0
                    };
                    byte frequency = Frequency(start + (end - start) * progress);
                    for (int repeat = 0; repeat < 4 / speed; repeat++) steps.Add((frequency, amplitudes[item]));
                }
        }
        if (steps.Count == 0) throw new FormatException("波形没有启用的小节");
        if (steps.Count + rest * 4L > 4000) throw new FormatException("展开后的波形含休息时间不得超过 1000 帧");
        for (int i = 0; i < rest * 4; i++) steps.Add((10, 0));
        while (steps.Count % 4 != 0) steps.Add((10, 0));
        return steps.Chunk(4).Select(chunk => Convert.ToHexString(chunk.Select(x => x.Frequency).Concat(chunk.Select(x => x.Amplitude)).ToArray())).ToArray();
    }
}
