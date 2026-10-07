using System.Text.RegularExpressions;

namespace EplanEdzManager.Core.Text;

public static class EplanMultilingualText
{
    private static readonly Regex LanguageMarker = new Regex(
        @"(?<language>(?:[A-Za-z]{2,3}|\?\?)_(?:[A-Za-z]{2,3}|\?\?))@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] PreferredLanguages = { "zh_CN", "zh_TW", "??_??", "en_US", "en_GB" };

    public static string? ToDisplayText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var source = value!.Trim();
        var matches = LanguageMarker.Matches(source).Cast<Match>().ToArray();
        if (matches.Length == 0 || matches[0].Index != 0) return source;

        var values = new List<(string Language, string Text)>();
        for (var index = 0; index < matches.Length; index++)
        {
            var match = matches[index];
            var start = match.Index + match.Length;
            var end = index + 1 < matches.Length ? matches[index + 1].Index : source.Length;
            var text = source.Substring(start, end - start).Trim().TrimEnd(';').Trim();
            if (text.Length > 0) values.Add((match.Groups["language"].Value, text));
        }

        foreach (var language in PreferredLanguages)
        {
            var preferred = values.FirstOrDefault(item => string.Equals(item.Language, language, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(preferred.Text)) return preferred.Text;
        }

        return values.Count == 0 ? null : values[0].Text;
    }
}
