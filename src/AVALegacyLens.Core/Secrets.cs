using System.Text.RegularExpressions;

namespace AVALegacyLens.Core;

/// <summary>
/// Keeps secrets out of the map: every piece of code or SQL text the map keeps (a link's note, a snippet) goes
/// through <see cref="Mask"/>, which hides the value of anything that looks like a password in a connection string or
/// an assignment. A hard-coded password is reported as a risk, never copied.
/// </summary>
public static partial class Secrets
{
    public const string Masked = "(masked)";

    [GeneratedRegex(@"\b(password|passwd|pwd|secret|apikey|api_key|token)(\s*=\s*)(""[^""]*""|'[^']*'|[^;'""\s,)]*)", RegexOptions.IgnoreCase)]
    private static partial Regex Assignment();

    /// <summary>A config entry whose name says it's a secret: key="SmtpPassword" value="…" (or name=… value=…).</summary>
    [GeneratedRegex(@"\b((?:key|name)\s*=\s*""[^""]*(?:password|passwd|pwd|secret|token|apikey|api_key|credential)[^""]*""\s+value\s*=\s*"")[^""]*("")", RegexOptions.IgnoreCase)]
    private static partial Regex ConfigEntry();

    public static string Mask(string text)
    {
        if (text.Length == 0) return text;
        var masked = Assignment().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Masked);
        return ConfigEntry().Replace(masked, m => m.Groups[1].Value + Masked + m.Groups[2].Value);
    }

    /// <summary>Whether text has a password written into it (a connection string with one, say).</summary>
    public static bool HasPassword(string text) => Regex.IsMatch(text, @"\b(password|pwd)\s*=\s*[^;'""\s]+", RegexOptions.IgnoreCase);
}
