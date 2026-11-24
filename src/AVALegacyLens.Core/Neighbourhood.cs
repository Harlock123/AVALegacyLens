using AVALegacyLens.Core.Model;

namespace AVALegacyLens.Core;

/// <summary>One item in a neighbourhood: where it sits (its column: negative drives the centre, positive is driven by it) and the links drawn.</summary>
public sealed record Placed(Node Node, int Column, int Row);

/// <summary>A link drawn between two placed items, from the driving one to the driven one.</summary>
public sealed record Drawn(Placed From, Placed To, EdgeKind Kind, Certainty Certainty);

/// <summary>
/// The items around one, laid out in columns for a picture: what drives it on the left (the controls whose events run
/// it, the code that calls it, the code that reads or writes a table), what it drives on the right (what it calls,
/// runs, reads and writes, the handlers a control runs), one or two steps out. A table stands for its columns, and code
/// touching a screen's own controls is left out, so the picture stays readable; a column holds at most
/// <see cref="MaxPerColumn"/> items, the rest counted.
/// </summary>
public sealed class Neighbourhood
{
    public const int MaxPerColumn = 14;

    public required Placed Centre { get; init; }
    public required IReadOnlyList<Placed> Items { get; init; }
    public required IReadOnlyList<Drawn> Links { get; init; }
    /// <summary>Items left out of each column for room, by column.</summary>
    public required IReadOnlyDictionary<int, int> More { get; init; }

    private static readonly EdgeKind[] Drive = [EdgeKind.Calls, EdgeKind.Creates, EdgeKind.Executes, EdgeKind.Reads, EdgeKind.Writes, EdgeKind.Uses, EdgeKind.Inherits];

    public static Neighbourhood Of(SystemMap map, string id, int depth = 1)
    {
        var centreNode = map[id] ?? throw new KeyNotFoundException(id);
        var columns = new Dictionary<string, int>(StringComparer.Ordinal) { [id] = 0 };
        var links = new List<(string From, string To, EdgeKind Kind, Certainty C)>();

        string Show(string nodeId) => map[nodeId] is { Kind: NodeKind.Column, Parent: { } table } ? table : nodeId;
        bool Worth(Node n) => n.Kind is not (NodeKind.File or NodeKind.Project);

        // What a node drives (to its right) and what drives it (to its left).
        IEnumerable<(string Other, EdgeKind Kind, Certainty C)> Driven(string n)
        {
            foreach (var e in map.From(n).Where(e => Drive.Contains(e.Kind)))
                if (map[e.To] is { } t && t.Kind != NodeKind.Control && Worth(t)) yield return (Show(e.To), e.Kind, e.Certainty);
            foreach (var e in map.To(n).Where(e => e.Kind == EdgeKind.Handles)) yield return (e.From, e.Kind, e.Certainty);
            if (map[n] is { Kind: NodeKind.Form })
                foreach (var c in Trace.Controls(map, n).Where(c => map.To(c.Id).Any(e => e.Kind == EdgeKind.Handles))) yield return (c.Id, EdgeKind.Contains, Certainty.Resolved);
        }
        IEnumerable<(string Other, EdgeKind Kind, Certainty C)> Drivers(string n)
        {
            var ids = map[n] is { Kind: NodeKind.Table } ? map.Children(n).Where(c => c.Kind == NodeKind.Column).Select(c => c.Id).Append(n) : [n];
            foreach (var target in ids)
                foreach (var e in map.To(target).Where(e => Drive.Contains(e.Kind)))
                    if (map[e.From] is { } f && Worth(f) && f.Kind != NodeKind.Column) yield return (e.From, e.Kind, e.Certainty);
            foreach (var e in map.From(n).Where(e => e.Kind == EdgeKind.Handles)) yield return (e.To, e.Kind, e.Certainty);
            if (map[n] is { Kind: NodeKind.Control, Parent: { } holder } && map[holder]?.Kind == NodeKind.Form) yield return (holder, EdgeKind.Contains, Certainty.Resolved);
        }

        var frontier = new List<string> { id };
        for (var d = 1; d <= depth; d++)
        {
            var next = new List<string>();
            foreach (var n in frontier.Where(f => columns[f] >= 0))
                foreach (var (other, kind, c) in Driven(n))
                {
                    if (!columns.ContainsKey(other)) { columns[other] = d; next.Add(other); }
                    if (columns[other] == columns[n] + 1) links.Add((n, other, kind, c));
                }
            foreach (var n in frontier.Where(f => columns[f] <= 0))
                foreach (var (other, kind, c) in Drivers(n))
                {
                    if (!columns.ContainsKey(other)) { columns[other] = -d; next.Add(other); }
                    if (columns[other] == columns[n] - 1) links.Add((other, n, kind, c));
                }
            frontier = next;
        }

        var placed = new Dictionary<string, Placed>(StringComparer.Ordinal);
        var more = new Dictionary<int, int>();
        foreach (var col in columns.GroupBy(c => c.Value))
        {
            var nodes = col.Select(c => map[c.Key]!).OrderBy(n => n.Kind).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (nodes.Count > MaxPerColumn)
            {
                more[col.Key] = nodes.Count - MaxPerColumn;
                nodes = nodes.Take(MaxPerColumn).ToList();
            }
            for (var i = 0; i < nodes.Count; i++) placed[nodes[i].Id] = new Placed(nodes[i], col.Key, i);
        }
        var drawn = links.Distinct().Where(l => placed.ContainsKey(l.From) && placed.ContainsKey(l.To))
            .Select(l => new Drawn(placed[l.From], placed[l.To], l.Kind, l.C)).ToList();
        return new Neighbourhood { Centre = placed[id], Items = [.. placed.Values], Links = drawn, More = more };
    }
}
