using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Scanning;

namespace AVALegacyLens.Tests;

/// <summary>The samples folder, found from the test's output folder, and the sample system scanned once.</summary>
public static class Samples
{
    public static string Folder { get; } = Find();

    public static string Clinic => Path.Combine(Folder, "ClinicLegacy");

    private static readonly Lazy<SystemMap> s_clinic = new(() => Scanner.Scan(Clinic));

    /// <summary>The ClinicLegacy sample, scanned once for every test that only reads the map.</summary>
    public static SystemMap ClinicMap => s_clinic.Value;

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples");
            if (Directory.Exists(Path.Combine(candidate, "ClinicLegacy"))) return candidate;
        }
        throw new DirectoryNotFoundException("Couldn't find the samples folder.");
    }

    /// <summary>A copy of the samples in a new temporary folder (agents write into it).</summary>
    public static string Copy()
    {
        var root = Directory.CreateTempSubdirectory("lens-samples-").FullName;
        foreach (var f in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(root, Path.GetRelativePath(Folder, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return root;
    }

    /// <summary>The node of a method, by type and name (the first overload).</summary>
    public static Node Method(this SystemMap map, string type, string name) =>
        map.OfKind(NodeKind.Method).Where(m => m.Prop("type") == type && m.Name == name).OrderBy(m => m.Line).FirstOrDefault()
        ?? throw new KeyNotFoundException($"No method {type}.{name}.");

    public static Node Type(this SystemMap map, string name) =>
        map.Nodes.FirstOrDefault(n => n.Kind is NodeKind.Type or NodeKind.Form && n.Name == name) ?? throw new KeyNotFoundException($"No type {name}.");

    public static IEnumerable<string> CalleeNames(this SystemMap map, Node from, EdgeKind kind = EdgeKind.Calls) =>
        map.From(from.Id).Where(e => e.Kind == kind).Select(e => map[e.To]).Where(n => n != null).Select(n => $"{n!.Prop("type") ?? ""}.{n.Name}".TrimStart('.'));
}
