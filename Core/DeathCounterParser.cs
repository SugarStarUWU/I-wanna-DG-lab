using System.Globalization;
using System.Text.RegularExpressions;

namespace IWCoyoteBridge.Core;

public enum DeathRecognitionMode { Unknown, Standard, Custom }

public static class DeathCounterParser
{
    private static readonly Regex Pattern = new(
        @"(?<![\p{L}\p{N}_])deaths?(?![\p{L}_])\s*[:：=]?\s*([0-9]+)(?![\p{L}\p{N}_.])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    public static bool TryParseDeathCount(string? title, out int deaths)
        => TryMatch(Pattern, title, out deaths);

    public static string NormalizeKeyword(string? keyword)
        => string.IsNullOrWhiteSpace(keyword) ? "Death" : keyword.Trim();

    public static bool TryParseDeathCount(string? title, string? customKeyword, out int deaths)
        => TryParseDeathCount(title, customKeyword, out deaths, out _);

    public static bool TryParseDeathCount(string? title, string? customKeyword,
        out int deaths, out DeathRecognitionMode mode)
    {
        mode = DeathRecognitionMode.Unknown;
        deaths = 0;
        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(customKeyword))
        {
            var customPattern = new Regex(
                @"(?<![\p{L}\p{N}_])" + Regex.Escape(customKeyword.Trim()) +
                @"\s*[:：=]?\s*([0-9]+)(?![\p{L}\p{N}_.])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            if (TryMatch(customPattern, title, out deaths))
            {
                mode = DeathRecognitionMode.Custom;
                return true;
            }
        }
        if (!TryParseDeathCount(title, out deaths)) return false;
        mode = DeathRecognitionMode.Standard;
        return true;
    }

    private static bool TryMatch(Regex pattern, string? title, out int deaths)
    {
        deaths = 0;
        if (string.IsNullOrWhiteSpace(title)) return false;
        try
        {
            var matches = pattern.Matches(title);
            // Multiple counters are ambiguous: do not guess.
            return matches.Count == 1 && int.TryParse(matches[0].Groups[1].Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out deaths);
        }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
