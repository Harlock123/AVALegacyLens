using System.Text.Json;
using System.Text.Json.Serialization;

namespace AVALegacyLens.Core.Model;

/// <summary>
/// A .lens file: a saved system map, so a big system needn't be scanned again to be looked at, and two scans can be
/// compared. JSON, versioned; the scanned folder is kept so source can be shown when the code is still there.
/// </summary>
public static class LensFile
{
    public const int Version = 1;
    public const string Extension = ".lens";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class Dto
    {
        public int Version { get; set; }
        public string Root { get; set; } = "";
        public DateTimeOffset ScannedAt { get; set; }
        public List<Node> Nodes { get; set; } = [];
        public List<Edge> Edges { get; set; } = [];
        public List<ScanNote> Notes { get; set; } = [];
    }

    public static void Save(SystemMap map, string path)
    {
        var dto = new Dto
        {
            Version = Version,
            Root = map.Root,
            ScannedAt = map.ScannedAt,
            Nodes = [.. map.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal)],
            Edges = [.. map.Edges],
            Notes = map.Notes,
        };
        var temp = path + ".tmp";
        using (var stream = File.Create(temp)) JsonSerializer.Serialize(stream, dto, Options);
        File.Move(temp, path, overwrite: true);
    }

    public static SystemMap Load(string path)
    {
        using var stream = File.OpenRead(path);
        var dto = JsonSerializer.Deserialize<Dto>(stream, Options) ?? throw new InvalidDataException("Not a .lens file.");
        if (dto.Version is < 1 or > Version)
            throw new InvalidDataException($"This .lens file is version {dto.Version}; this program reads up to {Version}.");
        var map = new SystemMap { Root = dto.Root, ScannedAt = dto.ScannedAt, Notes = dto.Notes };
        foreach (var n in dto.Nodes) map.Add(n);
        foreach (var e in dto.Edges) map.Link(e);
        return map;
    }
}
