using AVALegacyLens.Core.Config;
using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Sql;

namespace AVALegacyLens.Core.Scanning;

/// <summary>What a scan should do. Defaults suit a folder of source.</summary>
public sealed class ScanOptions
{
    /// <summary>Told what the scan is doing ("Reading ClinicApp (8 files)…").</summary>
    public IProgress<string>? Progress { get; init; }

    /// <summary>Live SQL Server databases whose schema to read too (connection strings; never kept in the map).</summary>
    public IReadOnlyList<string> Databases { get; init; } = [];
}

/// <summary>
/// Scans a folder into a <see cref="SystemMap"/>: finds the projects and files, reads each project's code with Roslyn
/// (<see cref="DotNetReader"/>), adds a node for every file with its line counts, then matches by name what the
/// compiler couldn't resolve. Deterministic: the same folder gives the same map.
/// </summary>
public static class Scanner
{
    public static SystemMap Scan(string folder, ScanOptions? options = null, CancellationToken cancel = default)
    {
        options ??= new ScanOptions();
        // No folder: a database alone.
        var databaseOnly = string.IsNullOrEmpty(folder);
        if (databaseOnly && options.Databases.Count == 0) throw new ArgumentException("Give a folder, a database, or both.");
        var root = databaseOnly ? "" : Path.GetFullPath(File.Exists(folder) ? Path.GetDirectoryName(folder)! : folder).TrimEnd(Path.DirectorySeparatorChar);
        if (!databaseOnly && !Directory.Exists(root)) throw new DirectoryNotFoundException($"There's no folder at {folder}.");
        var map = new SystemMap { Root = root, ScannedAt = DateTimeOffset.Now };

        options.Progress?.Report("Finding files…");
        var files = databaseOnly ? [] : SourceFiles.Find(root);
        var projectPaths = databaseOnly ? [] : SourceFiles.FindProjects(root);
        var projects = projectPaths.Select(p => SourceFiles.ReadProject(p, projectPaths)).ToList();
        var inProject = projects.SelectMany(p => p.Sources).ToHashSet(StringComparer.Ordinal);

        // Source files no project compiles: a loose group per language, so they're still read.
        foreach (var language in new[] { "VB.NET", "C#" })
        {
            var ext = language == "VB.NET" ? ".vb" : ".cs";
            var loose = files.Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && !inProject.Contains(f)).ToList();
            if (loose.Count > 0)
                projects.Add(new ProjectInfo { Path = Path.Combine(root, $"(loose {language} files)"), Name = $"Loose {language} files", Language = language, Sources = loose, Loose = true });
        }

        // Projects, with unique keys (the key prefixes every code node's id).
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in projects)
        {
            var key = p.Name;
            for (var i = 2; !used.Add(key); i++) key = $"{p.Name}~{i}";
            keys[p.Path] = key;
            var rel = SourceFiles.Relative(root, p.Path);
            var node = map.Add(new Node { Id = "project:" + rel, Kind = NodeKind.Project, Name = p.Name, File = p.Loose ? null : rel, Language = p.Language });
            node.Props["key"] = key;
            if (p.Loose) node.Props["loose"] = "true";
            if (p.RootNamespace != null) node.Props["rootNamespace"] = p.RootNamespace;
            if (p.OutputType != null) node.Props["outputType"] = p.OutputType;
            if (p.Framework != null) node.Props["framework"] = p.Framework;
            if (p.References.Count > 0) node.Props["references"] = string.Join(", ", p.References);
        }
        foreach (var p in projects)
            foreach (var r in p.ProjectReferences)
            {
                var target = "project:" + SourceFiles.Relative(root, r);
                if (map.Contains(target)) map.Link("project:" + SourceFiles.Relative(root, p.Path), target, EdgeKind.Uses, SourceFiles.Relative(root, p.Path), note: "project reference");
            }

        // Files: one node each, under its project (or the folder, for files no project lists), with line counts.
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in projects)
            foreach (var s in p.Sources) owner.TryAdd(s, "project:" + SourceFiles.Relative(root, p.Path));
        foreach (var f in files)
        {
            cancel.ThrowIfCancellationRequested();
            var rel = SourceFiles.Relative(root, f);
            var language = SourceFiles.Languages[Path.GetExtension(f)];
            var node = map.Add(new Node { Id = "file:" + rel, Kind = NodeKind.File, Name = Path.GetFileName(f), File = rel, Line = 1, Language = language });
            node.Parent = owner.GetValueOrDefault(f) ?? OwningProject(map, root, f, projects);
            try
            {
                var count = LineCounter.Count(SourceFiles.ReadText(f), language);
                node.EndLine = count.Total;
                node.Props["lines"] = count.Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
                node.Props["code"] = count.Code.ToString(System.Globalization.CultureInfo.InvariantCulture);
                node.Props["comments"] = count.Comment.ToString(System.Globalization.CultureInfo.InvariantCulture);
                node.Props["blank"] = count.Blank.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (IOException e)
            {
                map.Notes.Add(new ScanNote(rel, 0, "Couldn't read: " + e.Message));
            }
            if (DotNetReader.IsDesignerFile(rel)) node.Props["designer"] = "true";
        }

        // SQL definitions first, from scripts and live databases, so code and bodies resolve against them.
        var sql = new SqlReader(map);
        foreach (var f in files.Where(f => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)))
        {
            cancel.ThrowIfCancellationRequested();
            options.Progress?.Report($"Reading {Path.GetFileName(f)}…");
            var rel = SourceFiles.Relative(root, f);
            try
            {
                sql.ReadScript(rel, SourceFiles.ReadText(f), "file:" + rel);
            }
            catch (IOException e)
            {
                map.Notes.Add(new ScanNote(rel, 0, "Couldn't read: " + e.Message));
            }
        }
        foreach (var cs in options.Databases)
        {
            try
            {
                DatabaseReader.Read(map, sql, cs, options.Progress, cancel);
            }
            catch (Exception e) when (e is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or ArgumentException)
            {
                map.Notes.Add(new ScanNote("database", 0, $"Couldn't read the database: {e.Message}"));
            }
        }

        // Config files: connection strings and settings.
        var config = new ConfigReader(map);
        foreach (var f in files.Where(f => f.EndsWith(".config", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Equals("packages.config", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                config.ReadFile(SourceFiles.Relative(root, f), SourceFiles.ReadText(f));
            }
            catch (IOException e)
            {
                map.Notes.Add(new ScanNote(SourceFiles.Relative(root, f), 0, "Couldn't read: " + e.Message));
            }
        }

        // Code: each project through Roslyn, then what only matches by name.
        var reader = new DotNetReader(map, root);
        new WinFormsReader().Attach(reader);
        new EmbeddedSqlReader(sql).Attach(reader);
        config.Attach(reader);
        foreach (var p in projects)
        {
            cancel.ThrowIfCancellationRequested();
            options.Progress?.Report($"Reading {p.Name} ({p.Sources.Count} files)…");
            try
            {
                reader.Read(p, "project:" + SourceFiles.Relative(root, p.Path), keys[p.Path], cancel);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                map.Notes.Add(new ScanNote(SourceFiles.Relative(root, p.Path), 0, $"Couldn't read the project: {e.Message}"));
            }
        }
        options.Progress?.Report("Reading procedures, views, functions and triggers…");
        sql.AnalyzeModules();
        options.Progress?.Report("Matching calls across projects…");
        reader.ResolvePending();
        map.RemoveDangling();
        map.RemoveIfEmpty(SqlReader.ReferencedGroup);
        options.Progress?.Report($"Scanned {map.Nodes.Count:N0} items and {map.Edges.Count:N0} links.");
        return map;
    }

    /// <summary>For a file no project compiles (a SQL script, a config file): the project whose folder holds it, if any.</summary>
    private static string? OwningProject(SystemMap map, string root, string file, List<ProjectInfo> projects)
    {
        var best = projects.Where(p => !p.Loose)
            .Select(p => (p, dir: Path.GetDirectoryName(p.Path)!))
            .Where(x => file.StartsWith(x.dir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderByDescending(x => x.dir.Length)
            .Select(x => x.p)
            .FirstOrDefault();
        if (best != null) return "project:" + SourceFiles.Relative(root, best.Path);
        // Otherwise a folder node, so SQL scripts and the like have a home.
        var dir = SourceFiles.Relative(root, Path.GetDirectoryName(file)!);
        var id = "folder:" + dir;
        if (!map.Contains(id))
            map.Add(new Node { Id = id, Kind = NodeKind.Project, Name = dir == "." ? "(top folder)" : dir, Props = { ["subkind"] = "folder" } });
        return id;
    }
}
