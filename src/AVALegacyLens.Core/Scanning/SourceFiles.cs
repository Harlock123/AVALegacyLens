using System.Text;
using System.Xml.Linq;

namespace AVALegacyLens.Core.Scanning;

/// <summary>A .vbproj or .csproj and what it says about itself: its source files, imports and references.</summary>
public sealed class ProjectInfo
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Language { get; init; }
    public string? RootNamespace { get; init; }
    public string? OutputType { get; init; }
    public string? Framework { get; init; }
    public string? StartupObject { get; init; }
    /// <summary>VB project-level imports (<c>&lt;Import Include="System.Data" /&gt;</c>).</summary>
    public List<string> Imports { get; init; } = [];
    /// <summary>Other projects it references (absolute paths).</summary>
    public List<string> ProjectReferences { get; init; } = [];
    /// <summary>Assemblies it references by name (System.Data, a vendor DLL…).</summary>
    public List<string> References { get; init; } = [];
    /// <summary>Its source files (absolute paths).</summary>
    public List<string> Sources { get; init; } = [];
    /// <summary>A loose group: files of one language not in any project file.</summary>
    public bool Loose { get; init; }
}

/// <summary>
/// Finds what a scan reads under a folder: project files and the source files each one compiles, plus every other file
/// a reader knows (SQL scripts, config files, VB6 files). Build output, packages and tool folders are skipped.
/// </summary>
public static class SourceFiles
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".svn", ".hg", ".vs", ".idea", "node_modules", "packages", "publish", "TestResults", ".claude",
    };

    /// <summary>Extensions of the files a scan looks at, and their language.</summary>
    public static readonly IReadOnlyDictionary<string, string> Languages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".vb"] = "VB.NET",
        [".cs"] = "C#",
        [".sql"] = "T-SQL",
        [".config"] = "Config",
        [".frm"] = "VB6",
        [".bas"] = "VB6",
        [".cls"] = "VB6",
        [".ctl"] = "VB6",
        [".vbp"] = "VB6",
    };

    public static bool Wanted(string path) => Languages.ContainsKey(System.IO.Path.GetExtension(path));

    /// <summary>Every wanted file under the folder, in a stable order.</summary>
    public static List<string> Find(string root)
    {
        var found = new List<string>();
        Walk(root, found, null);
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>Every .vbproj and .csproj under the folder.</summary>
    public static List<string> FindProjects(string root)
    {
        var found = new List<string>();
        Walk(root, found, p => p.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static void Walk(string dir, List<string> found, Func<string, bool>? keep)
    {
        IEnumerable<string> files, dirs;
        try
        {
            files = Directory.EnumerateFiles(dir);
            dirs = Directory.EnumerateDirectories(dir);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return;
        }
        foreach (var f in files)
            if (keep?.Invoke(f) ?? Wanted(f)) found.Add(f);
        foreach (var d in dirs)
        {
            var name = System.IO.Path.GetFileName(d);
            if (Skipped.Contains(name)) continue;
            if (new DirectoryInfo(d).LinkTarget != null) continue;
            Walk(d, found, keep);
        }
    }

    /// <summary>
    /// Reads a project file. Old-style projects list their sources (<c>&lt;Compile Include&gt;</c>); SDK-style ones
    /// compile every file of their language under their folder, except in folders of other projects.
    /// </summary>
    public static ProjectInfo ReadProject(string path, IReadOnlyCollection<string> allProjects)
    {
        var language = path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ? "VB.NET" : "C#";
        var ext = language == "VB.NET" ? ".vb" : ".cs";
        var dir = System.IO.Path.GetDirectoryName(path)!;
        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception e) when (e is System.Xml.XmlException or IOException)
        {
            doc = new XDocument(new XElement("Project"));
        }
        IEnumerable<XElement> All(string name) => doc.Descendants().Where(e => e.Name.LocalName == name);
        string? First(string name) => All(name).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
        string Resolve(string include) => System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, include.Replace('\\', '/')));

        var sources = new List<string>();
        var compiles = All("Compile").Select(e => (string?)e.Attribute("Include")).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (compiles.Count > 0 && compiles.All(c => !c!.Contains('*')))
        {
            foreach (var c in compiles)
            {
                var full = Resolve(c!);
                if (File.Exists(full) && full.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) sources.Add(full);
            }
        }
        else
        {
            var otherDirs = allProjects.Where(p => !string.Equals(p, path, StringComparison.Ordinal))
                .Select(p => System.IO.Path.GetDirectoryName(p)!)
                .Where(d => d.Length > dir.Length && d.StartsWith(dir + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .ToList();
            var list = new List<string>();
            Walk(dir, list, f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            sources.AddRange(list.Where(f => !otherDirs.Any(d => f.StartsWith(d + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))));
            var removed = All("Compile").Select(e => (string?)e.Attribute("Remove")).Where(v => !string.IsNullOrWhiteSpace(v) && !v!.Contains('*')).Select(v => Resolve(v!)).ToHashSet();
            sources.RemoveAll(removed.Contains);
        }
        sources = [.. sources.Distinct().OrderBy(s => s, StringComparer.Ordinal)];

        return new ProjectInfo
        {
            Path = path,
            Name = First("AssemblyName") ?? System.IO.Path.GetFileNameWithoutExtension(path),
            Language = language,
            RootNamespace = First("RootNamespace"),
            OutputType = First("OutputType"),
            Framework = First("TargetFrameworkVersion") ?? First("TargetFramework") ?? First("TargetFrameworks"),
            StartupObject = First("StartupObject"),
            Imports = [.. All("Import").Select(e => (string?)e.Attribute("Include")).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct()],
            ProjectReferences = [.. All("ProjectReference").Select(e => (string?)e.Attribute("Include")).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => Resolve(v!))],
            References = [.. All("Reference").Concat(All("PackageReference")).Select(e => (string?)e.Attribute("Include")).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Split(',')[0].Trim()).Distinct()],
            Sources = sources,
        };
    }

    /// <summary>
    /// Reads a source file's text. Legacy code is often Windows-1252, not UTF-8: a file that isn't valid UTF-8 is read
    /// as Latin-1, which keeps every byte and the line structure.
    /// </summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>A path relative to the scanned folder, with forward slashes.</summary>
    public static string Relative(string root, string path) => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
}
