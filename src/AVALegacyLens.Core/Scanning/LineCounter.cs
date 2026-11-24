namespace AVALegacyLens.Core.Scanning;

/// <summary>Lines in a file: all of them, the blank ones, the comments, and the rest (code).</summary>
public readonly record struct LineCount(int Total, int Blank, int Comment)
{
    public int Code => Total - Blank - Comment;
}

/// <summary>Counts lines by language: VB and VB6 comments start with ' or REM; C# and T-SQL have line and block comments.</summary>
public static class LineCounter
{
    public static LineCount Count(string text, string language)
    {
        var lines = text.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
        int blank = 0, comment = 0;
        var inBlock = false;
        var (line, blockOpen, blockClose) = language switch
        {
            "C#" => ("//", "/*", "*/"),
            "T-SQL" => ("--", "/*", "*/"),
            "Config" => ((string?)null, "<!--", "-->"),
            _ => ((string?)null, (string?)null, (string?)null),
        };
        foreach (var raw in lines)
        {
            var t = raw.Trim();
            if (inBlock)
            {
                comment++;
                if (blockClose != null && t.Contains(blockClose, StringComparison.Ordinal)) inBlock = false;
                continue;
            }
            if (t.Length == 0)
            {
                blank++;
                continue;
            }
            if (language is "VB.NET" or "VB6")
            {
                if (t.StartsWith('\'') || t.StartsWith("REM ", StringComparison.OrdinalIgnoreCase) || t.Equals("REM", StringComparison.OrdinalIgnoreCase)) comment++;
                continue;
            }
            if (line != null && t.StartsWith(line, StringComparison.Ordinal))
            {
                comment++;
                continue;
            }
            if (blockOpen != null && t.StartsWith(blockOpen, StringComparison.Ordinal))
            {
                comment++;
                var rest = t[blockOpen.Length..];
                if (!rest.Contains(blockClose!, StringComparison.Ordinal)) inBlock = true;
            }
        }
        return new LineCount(lines.Length, blank, comment);
    }
}
