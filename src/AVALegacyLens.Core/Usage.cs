using AVALegacyLens.Core.Model;

namespace AVALegacyLens.Core;

/// <summary>Who touches a table or column: the direct writers and readers (procedures, views, triggers, methods).</summary>
public static class Usage
{
    public static IReadOnlyList<(Node Node, Edge Edge)> Writers(SystemMap map, string id) => Direct(map, id, EdgeKind.Writes);

    public static IReadOnlyList<(Node Node, Edge Edge)> Readers(SystemMap map, string id) => Direct(map, id, EdgeKind.Reads);

    private static List<(Node, Edge)> Direct(SystemMap map, string id, EdgeKind kind) =>
        [.. map.To(id).Where(e => e.Kind == kind).Select(e => (map[e.From], e)).Where(x => x.Item1 != null)
            .GroupBy(x => x.Item1!.Id).Select(g => (g.First().Item1!, g.First().e))
            .OrderBy(x => x.Item1.Name, StringComparer.OrdinalIgnoreCase)];
}
