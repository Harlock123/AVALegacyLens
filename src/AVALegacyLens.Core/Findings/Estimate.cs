using System.Globalization;
using System.Text;
using AVALegacyLens.Core.Model;

namespace AVALegacyLens.Core.Findings;

/// <summary>One line of an estimate: what's counted, how many, the days for each band, and the days in all.</summary>
public sealed record EstimateLine(string Area, string What, int Count, double DaysEach, double Days);

/// <summary>
/// A rough size for rebuilding the live part of the system (dead code left out), in person-days, from counts the scan
/// can be sure of: screens by how many controls they have, methods and procedures by complexity, tables, and the work
/// the high risks add. It's a starting point to argue with, not a quote: the weights are stated with it, and the range
/// is shown, not just one number.
/// </summary>
public sealed class Estimate
{
    public required IReadOnlyList<EstimateLine> Lines { get; init; }

    public double Days => Lines.Sum(l => l.Days);
    public double Low => Math.Round(Days * 0.7, 0);
    public double High => Math.Round(Days * 1.5, 0);

    /// <summary>The weights, in person-days, so the estimate can be checked and changed.</summary>
    public static readonly IReadOnlyDictionary<string, double> Weights = new Dictionary<string, double>
    {
        ["screen, up to 10 controls"] = 1.0,
        ["screen, 11 to 25 controls"] = 2.0,
        ["screen, over 25 controls"] = 4.0,
        ["method, complexity up to 5"] = 0.05,
        ["method, complexity 6 to 10"] = 0.25,
        ["method, complexity 11 to 20"] = 0.75,
        ["method, complexity over 20"] = 1.5,
        ["procedure, view or function, up to 30 lines"] = 0.25,
        ["procedure, view or function, 31 to 100 lines"] = 0.75,
        ["procedure, view or function, over 100 lines"] = 2.0,
        ["trigger"] = 0.5,
        ["table"] = 0.1,
        ["high risk"] = 0.5,
        ["business rule to confirm and test"] = 0.1,
    };

    public static Estimate Of(SystemMap map)
    {
        var lines = new List<EstimateLine>();
        void Add(string area, string what, int count)
        {
            if (count == 0) return;
            var each = Weights[what];
            lines.Add(new EstimateLine(area, what, count, each, Math.Round(count * each, 2)));
        }
        bool Live(Node n) => n.Prop("dead") != "true" && !(n.Parent != null && map[n.Parent]?.Prop("dead") == "true");

        var screens = map.OfKind(NodeKind.Form).Where(Live).Select(f => Trace.Controls(map, f.Id).Count()).ToList();
        Add("Screens", "screen, up to 10 controls", screens.Count(c => c <= 10));
        Add("Screens", "screen, 11 to 25 controls", screens.Count(c => c is > 10 and <= 25));
        Add("Screens", "screen, over 25 controls", screens.Count(c => c > 25));

        var methods = map.OfKind(NodeKind.Method).Where(m => Live(m) && m.Name != "InitializeComponent" && m.Prop("subkind") != "declare").Select(m => m.IntProp("complexity")).ToList();
        Add("Code", "method, complexity up to 5", methods.Count(c => c <= 5));
        Add("Code", "method, complexity 6 to 10", methods.Count(c => c is > 5 and <= 10));
        Add("Code", "method, complexity 11 to 20", methods.Count(c => c is > 10 and <= 20));
        Add("Code", "method, complexity over 20", methods.Count(c => c > 20));

        var modules = map.Nodes.Where(n => n.Kind is NodeKind.Procedure or NodeKind.View or NodeKind.Function && n.Prop("defined") != "false" && Live(n))
            .Select(n => n.IntProp("lines") > 0 ? n.IntProp("lines") : (n.Prop("sql")?.Split('\n').Length ?? 1)).ToList();
        Add("Database", "procedure, view or function, up to 30 lines", modules.Count(l => l <= 30));
        Add("Database", "procedure, view or function, 31 to 100 lines", modules.Count(l => l is > 30 and <= 100));
        Add("Database", "procedure, view or function, over 100 lines", modules.Count(l => l > 100));
        Add("Database", "trigger", map.OfKind(NodeKind.Trigger).Count(Live));
        Add("Database", "table", map.OfKind(NodeKind.Table).Count(t => t.Prop("defined") != "false" && Live(t)));

        Add("Risks and rules", "high risk", map.Findings.Count(f => f.Kind == FindingKind.Risk && f.Severity == Severity.High && map.MarkOf(f)?.State is not (MarkState.Wrong or MarkState.Noise)));
        Add("Risks and rules", "business rule to confirm and test", map.Findings.Count(f => f.Kind == FindingKind.Rule && map.MarkOf(f)?.State is not (MarkState.Wrong or MarkState.Noise)));
        return new Estimate { Lines = lines };
    }

    public string ToText()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(ci, $"About {Days:0} person-days to rebuild the live part (a range of {Low:0} to {High:0}); dead code left out.");
        foreach (var l in Lines) sb.AppendLine(ci, $"  {l.Area}: {l.Count} × {l.What} at {l.DaysEach:0.##} days = {l.Days:0.##}");
        sb.AppendLine("A rough size from counts the scan is sure of, not a quote: adjust the weights to your team.");
        return sb.ToString();
    }
}
