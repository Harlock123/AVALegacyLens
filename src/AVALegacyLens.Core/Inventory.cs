using System.Globalization;
using System.Text;
using AVALegacyLens.Core.Model;

namespace AVALegacyLens.Core;

/// <summary>Lines of one language across the system.</summary>
public sealed record LanguageLines(string Language, int Files, int Lines, int Code, int Comments);

/// <summary>A project's size: its files, types, screens, methods and lines of code.</summary>
public sealed record ProjectSize(string Id, string Name, string? Language, int Files, int Types, int Screens, int Methods, int Code);

/// <summary>A method and how complex it is, for the "hardest code" list.</summary>
public sealed record ComplexMethod(string Id, string Name, string Type, string? File, int Line, int Lines, int Complexity);

/// <summary>
/// The system at a glance: how many of each thing there are, lines by language, the size of each project and the most
/// complex methods. Worked out from a <see cref="SystemMap"/>; what the Inventory tab and get_inventory show.
/// </summary>
public sealed class Inventory
{
    public required IReadOnlyDictionary<NodeKind, int> Counts { get; init; }
    public required IReadOnlyList<LanguageLines> Languages { get; init; }
    public required IReadOnlyList<ProjectSize> Projects { get; init; }
    public required IReadOnlyList<ComplexMethod> MostComplex { get; init; }
    public required int Links { get; init; }
    public required int LinksByName { get; init; }
    public required int Notes { get; init; }

    public int TotalCode => Languages.Sum(l => l.Code);

    public static Inventory Of(SystemMap map, int complexCount = 15)
    {
        var counts = Enum.GetValues<NodeKind>().ToDictionary(k => k, _ => 0);
        foreach (var n in map.Nodes) counts[n.Kind]++;
        var files = map.OfKind(NodeKind.File).ToList();
        var languages = files.GroupBy(f => f.Language ?? "?")
            .Select(g => new LanguageLines(g.Key, g.Count(), g.Sum(f => f.IntProp("lines")), g.Sum(f => f.IntProp("code")), g.Sum(f => f.IntProp("comments"))))
            .OrderByDescending(l => l.Code).ToList();
        var projects = map.OfKind(NodeKind.Project)
            .Select(p =>
            {
                var below = Below(map, p.Id).ToList();
                return new ProjectSize(p.Id, p.Name, p.Language,
                    below.Count(n => n.Kind == NodeKind.File),
                    below.Count(n => n.Kind == NodeKind.Type),
                    below.Count(n => n.Kind == NodeKind.Form),
                    below.Count(n => n.Kind == NodeKind.Method),
                    below.Where(n => n.Kind == NodeKind.File).Sum(n => n.IntProp("code")));
            })
            .Where(p => p.Files > 0)
            .OrderByDescending(p => p.Code).ToList();
        var complex = map.OfKind(NodeKind.Method)
            .Where(m => m.IntProp("complexity") > 1)
            .OrderByDescending(m => m.IntProp("complexity")).ThenByDescending(m => m.IntProp("lines")).ThenBy(m => m.Id, StringComparer.Ordinal)
            .Take(complexCount)
            .Select(m => new ComplexMethod(m.Id, m.Name, m.Prop("type") ?? "", m.File, m.Line, m.IntProp("lines"), m.IntProp("complexity")))
            .ToList();
        return new Inventory
        {
            Counts = counts,
            Languages = languages,
            Projects = projects,
            MostComplex = complex,
            Links = map.Edges.Count(e => e.Kind != EdgeKind.Contains),
            LinksByName = map.Edges.Count(e => e.Certainty != Certainty.Resolved),
            Notes = map.Notes.Count,
        };
    }

    /// <summary>Every node under one, at any depth.</summary>
    public static IEnumerable<Node> Below(SystemMap map, string id)
    {
        var stack = new Stack<string>([id]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (stack.Count > 0)
            foreach (var c in map.Children(stack.Pop()))
                if (seen.Add(c.Id))
                {
                    yield return c;
                    stack.Push(c.Id);
                }
    }

    /// <summary>The inventory as plain text, for the status bar's "copy" and for agents.</summary>
    public string ToText()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(ci, $"{TotalCode:N0} lines of code in {Counts[NodeKind.File]:N0} files");
        foreach (var (k, v) in Counts.Where(c => c.Value > 0 && c.Key != NodeKind.File)) sb.AppendLine(ci, $"  {k}: {v:N0}");
        sb.AppendLine("By language:");
        foreach (var l in Languages) sb.AppendLine(ci, $"  {l.Language}: {l.Files} files, {l.Code:N0} code lines ({l.Lines:N0} in all, {l.Comments:N0} comments)");
        sb.AppendLine("Projects:");
        foreach (var p in Projects) sb.AppendLine(ci, $"  {p.Name} ({p.Language ?? "folder"}): {p.Files} files, {p.Screens} screens, {p.Types} types, {p.Methods} methods, {p.Code:N0} code lines");
        if (MostComplex.Count > 0)
        {
            sb.AppendLine("Most complex methods:");
            foreach (var m in MostComplex) sb.AppendLine(ci, $"  {m.Type}.{m.Name}: complexity {m.Complexity}, {m.Lines} lines ({m.File}:{m.Line})");
        }
        return sb.ToString();
    }
}
