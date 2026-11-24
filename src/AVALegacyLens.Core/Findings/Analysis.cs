using System.Globalization;
using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Sql;

namespace AVALegacyLens.Core.Findings;

/// <summary>
/// Works out, once a scan is done, what nothing reaches (dead code) and what makes the system risky or hard to
/// rebuild (risks). Reachability starts from the entry points: each project's startup form (or every form, when the
/// project doesn't say), Main, overrides and interface members the framework calls, the public members of libraries no
/// scanned project uses, and (when no code touches the database) the database itself. From there it follows calls,
/// creations, handlers, SQL and settings. What's left is dead: <c>unused</c> when nothing at all refers to it,
/// <c>unreachable</c> when only dead code does.
/// </summary>
public static class Analysis
{
    private static readonly EdgeKind[] Follow = [EdgeKind.Calls, EdgeKind.Creates, EdgeKind.Executes, EdgeKind.Reads, EdgeKind.Writes, EdgeKind.Uses, EdgeKind.Inherits];

    public static void Run(SystemMap map)
    {
        map.Findings.RemoveAll(f => f.Kind != FindingKind.Rule);
        var reachable = Reachable(map);
        foreach (var n in map.Nodes) n.Props.Remove("dead");
        DeadCode(map, reachable);
        Risks(map);
    }

    /// <summary>Every node reached from the entry points.</summary>
    public static HashSet<string> Reachable(SystemMap map)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        void Reach(string id)
        {
            if (seen.Add(id)) queue.Enqueue(id);
        }

        foreach (var root in Roots(map)) Reach(root);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (map[id] is not { } n) continue;
            foreach (var e in map.From(id).Where(e => Follow.Contains(e.Kind))) Reach(e.To);
            switch (n.Kind)
            {
                case NodeKind.Form or NodeKind.Type:
                    // A live type: its constructors, setup and field initializers; a live form: its controls and events.
                    foreach (var c in map.Children(id))
                        if (c.Kind == NodeKind.Control || c.Prop("subkind") == "constructor" || c.Name is "InitializeComponent" or "Dispose" or "Class_Initialize" or "Class_Terminate" || c.Prop("override") == "true" || c.Prop("implements") == "true")
                            Reach(c.Id);
                    foreach (var e in map.To(id).Where(e => e.Kind == EdgeKind.Handles)) Reach(e.From);
                    break;
                case NodeKind.Control:
                    foreach (var e in map.To(id).Where(e => e.Kind == EdgeKind.Handles)) Reach(e.From);
                    foreach (var c in map.Children(id)) Reach(c.Id);
                    break;
                case NodeKind.Method:
                    if (n.Parent != null) Reach(n.Parent);
                    break;
                case NodeKind.Table:
                    // A table something writes fires its triggers.
                    if (map.To(id).Any(e => e.Kind == EdgeKind.Writes && seen.Contains(e.From)))
                        foreach (var e in map.To(id).Where(e => e.Kind == EdgeKind.Handles)) Reach(e.From);
                    break;
                case NodeKind.Column:
                    if (n.Parent != null) Reach(n.Parent);
                    break;
            }
            // Something reached that's written later (a table a trigger handles, reached before its writer): recheck.
            foreach (var e in map.From(id).Where(e => e.Kind == EdgeKind.Writes))
                if (map[e.To] is { Kind: NodeKind.Table } t)
                    foreach (var h in map.To(t.Id).Where(h => h.Kind == EdgeKind.Handles)) Reach(h.From);
        }
        return seen;
    }

    private static IEnumerable<string> Roots(SystemMap map)
    {
        var projects = map.OfKind(NodeKind.Project).Where(p => p.Prop("key") != null).ToList();
        var referenced = map.Edges.Where(e => e.Kind == EdgeKind.Uses && e.From.StartsWith("project:", StringComparison.Ordinal) && e.To.StartsWith("project:", StringComparison.Ordinal)).Select(e => e.To).ToHashSet();
        foreach (var p in projects)
        {
            var key = p.Prop("key")!;
            var types = map.Nodes.Where(n => n.Kind is NodeKind.Form or NodeKind.Type && n.Prop("project") == key).ToList();
            var isLibrary = p.Prop("outputType") is not ("WinExe" or "Exe" or "Exe32") && !string.Equals(p.Language, "VB6", StringComparison.Ordinal);
            if (p.Prop("loose") == "true" || isLibrary && !referenced.Contains(p.Id))
            {
                // Loose files, or a library nothing scanned uses: something outside may use any of it.
                foreach (var t in types) yield return t.Id;
                foreach (var m in map.OfKind(NodeKind.Method).Where(m => m.Prop("project") == key && m.Prop("access") is "Public" or "Friend" or "Internal")) yield return m.Id;
                continue;
            }
            var startup = p.Prop("startup");
            var start = startup == null ? null : types.FirstOrDefault(t => string.Equals(t.Prop("fullName") ?? t.Name, startup, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, startup, StringComparison.OrdinalIgnoreCase));
            if (start != null) yield return start.Id;
            else if (!isLibrary)
                foreach (var f in types.Where(t => t.Kind == NodeKind.Form)) yield return f.Id;
            if (startup != null && startup.Contains("Main", StringComparison.OrdinalIgnoreCase) || start == null)
                foreach (var m in map.OfKind(NodeKind.Method).Where(m => m.Prop("project") == key && (m.Name == "Main" || m.Prop("entry") == "true"))) yield return m.Id;
        }
        foreach (var m in map.OfKind(NodeKind.Method).Where(m => m.Prop("entry") == "true")) yield return m.Id;

        // No code touches the database: the database is all there is, so all of it counts.
        var codeTouchesDb = map.Edges.Any(e => e.Kind is EdgeKind.Reads or EdgeKind.Writes or EdgeKind.Executes && map[e.From]?.Kind == NodeKind.Method);
        if (!codeTouchesDb)
            foreach (var n in map.Nodes.Where(n => n.Kind is NodeKind.Table or NodeKind.View or NodeKind.Procedure or NodeKind.Function or NodeKind.Trigger)) yield return n.Id;
        // Scripts that run their own statements (seed data, migrations) are entry points too.
        foreach (var e in map.Edges.Where(e => e.From.StartsWith("file:", StringComparison.Ordinal))) yield return e.From;
    }

    private static void DeadCode(SystemMap map, HashSet<string> reachable)
    {
        var dead = map.Nodes.Where(n => !reachable.Contains(n.Id) && Counts(n)).ToList();
        var deadIds = dead.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var n in dead)
        {
            // A dead type's members aren't listed one by one: the type is.
            if (n.Kind == NodeKind.Method && n.Parent != null && deadIds.Contains(n.Parent)) continue;
            n.Props["dead"] = "true";
            var refs = map.To(n.Id).Where(e => e.Kind is not EdgeKind.Contains && !(e.Kind == EdgeKind.Uses && map[e.From]?.Kind == NodeKind.Column)).ToList();
            var unused = refs.Count == 0;
            var users = refs.Select(e => map[e.From]).Where(u => u != null).Select(u => Name(u!)).Distinct().Take(3).ToList();
            var (title, detail) = n.Kind switch
            {
                NodeKind.Method when unused => ($"{Name(n)}: nothing calls it", "No code in the scan calls this."),
                NodeKind.Form when unused => ($"{n.Name}: nothing opens this screen", "No code in the scan opens or creates it."),
                NodeKind.Type when unused => ($"{n.Name}: nothing uses this {n.Prop("subkind") ?? "type"}", "No code in the scan creates it or calls into it."),
                NodeKind.Procedure or NodeKind.Function when unused => ($"{n.Name}: nothing runs it", "No code or SQL in the scan runs it."),
                NodeKind.View when unused => ($"{n.Name}: nothing reads this view", "No code or SQL in the scan reads it; a report outside the scan might."),
                NodeKind.Table when unused => ($"{n.Name}: no code reads or writes this table", "No code or SQL in the scan touches it."),
                NodeKind.Trigger => ($"{n.Name}: its table is never written", "The trigger only runs when its table is written, and nothing live writes it."),
                NodeKind.Setting when unused => ($"{n.Name}: no code reads this setting", "A value kept in the config file that no code reads."),
                NodeKind.Connection when unused => ($"{n.Name}: no code uses this connection", "A connection string in the config file that no code reads."),
                _ => ($"{Name(n)}: only dead code uses it", "Used only by " + string.Join(", ", users) + ", which nothing live reaches."),
            };
            map.Find(new Finding
            {
                Kind = FindingKind.DeadCode,
                Category = unused ? "unused" : "unreachable",
                NodeId = n.Id,
                Title = title,
                Detail = detail,
                File = n.File,
                Line = n.Line,
                Severity = Severity.Low,
                Confidence = Confidence(map, n, unused),
            });
        }
    }

    /// <summary>The kinds dead-code analysis reports on.</summary>
    private static bool Counts(Node n) => n.Kind switch
    {
        NodeKind.Method => n.Prop("subkind") is not ("constructor" or "abstract") && n.Name is not ("InitializeComponent" or "Dispose"),
        NodeKind.Form or NodeKind.Type => n.Prop("subkind") is not ("interface" or "enum" or "delegate" or "structure"),
        NodeKind.Procedure or NodeKind.Function or NodeKind.View or NodeKind.Table or NodeKind.Trigger => n.Prop("defined") != "false",
        NodeKind.Setting or NodeKind.Connection => n.Prop("hardCoded") != "true",
        _ => false,
    };

    private static Severity Confidence(SystemMap map, Node n, bool unused)
    {
        var c = n.Kind switch
        {
            NodeKind.Method when n.Prop("subkind") is { } sk && sk.StartsWith("property", StringComparison.Ordinal) => Severity.Low,
            NodeKind.Method when n.Prop("access") == "Private" => Severity.High,
            NodeKind.View => Severity.Low,
            _ => Severity.Medium,
        };
        return unused ? c : (Severity)Math.Max((int)Severity.Low, (int)c - 1);
    }

    private static string Name(Node n) => n.Kind == NodeKind.Method && n.Prop("type") is { } t ? $"{t}.{n.Name}" : n.Name;

    private static void Risks(SystemMap map)
    {
        void Risk(Node n, string category, Severity severity, string title, string detail, string? code = null, int line = 0) =>
            map.Find(new Finding { Kind = FindingKind.Risk, Category = category, NodeId = n.Id, Title = title, Detail = detail, Code = code, File = n.File, Line = line > 0 ? line : n.Line, Severity = severity });

        string? SqlNote(Node n) => map.From(n.Id).FirstOrDefault(e => e.Kind is EdgeKind.Reads or EdgeKind.Writes or EdgeKind.Executes && e.Certainty == Certainty.Guessed)?.Note;

        foreach (var n in map.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            if (n.Prop("sqlBuiltByHand") == "true")
            {
                var e = map.From(n.Id).FirstOrDefault(e => e.Certainty == Certainty.Guessed && e.Kind is EdgeKind.Reads or EdgeKind.Writes);
                Risk(n, "sql-built-by-hand", Severity.High, $"{Name(n)} builds SQL by joining strings",
                    "Values pasted into SQL text: open to SQL injection, and the tables it touches can only be guessed. Rebuild it with parameters.", SqlNote(n), e?.Line ?? 0);
            }
            if (n.Prop("dynamicSql") == "true")
                Risk(n, "dynamic-sql", Severity.Medium, $"{n.Name} runs SQL it builds at run time", "EXEC of a string: what it touches can only be guessed from the pieces.", SqlNote(n));
            if (n.Prop("hardCodedPassword") == "true")
                Risk(n, "hard-coded-password", Severity.High, $"{Name(n)} has a password written into the code", "Anyone with the code or the program has it. Move it to protected configuration and change it.");
            if (n.Prop("hardCodedOutside") == "true")
            {
                var e = map.From(n.Id).FirstOrDefault(x => x.Kind == EdgeKind.Uses && map[x.To]?.Kind == NodeKind.External);
                Risk(n, "hard-coded-location", Severity.Medium, $"{Name(n)} has a server or share written into the code", "A path or address that differs between environments: it belongs in configuration.", e?.Note, e?.Line ?? 0);
            }
            if (n.Prop("onErrorResumeNext") == "true")
                Risk(n, "on-error-resume-next", Severity.Medium, $"{Name(n)} ignores every error (On Error Resume Next)", "Failures pass silently; the rebuild must decide what each one should do.");
            if (n.Prop("goTo") == "true")
                Risk(n, "goto", Severity.Low, $"{Name(n)} uses GoTo", "Jumps make the flow hard to follow and to port.");
            if (n.Prop("globals") is { } globals)
                Risk(n, "global-state", Severity.Low, $"{n.Name} keeps {globals} global variable{(globals == "1" ? "" : "s")}", "State any code can change: find who sets and reads each before porting.");
            if (n.Kind == NodeKind.Method && n.Prop("subkind") == "declare")
                Risk(n, "native-call", Severity.Medium, $"{Name(n)} calls Windows directly", "A Declare of a native library: it won't work off Windows, so find what it does and replace it.", n.Prop("signature"));
            if (n.Kind == NodeKind.Method && n.Name != "InitializeComponent")
            {
                var complexity = n.IntProp("complexity");
                if (complexity >= 10)
                    Risk(n, "complex", complexity >= 20 ? Severity.High : Severity.Medium, $"{Name(n)} is complex (complexity {complexity})", "Many paths through it: write tests for each before rebuilding it.");
                if (n.IntProp("lines") > 150)
                    Risk(n, "long", Severity.Low, $"{Name(n)} is {n.Prop("lines")} lines long", "Long methods usually do several jobs: split them as they're rebuilt.");
            }
            if (n.Kind is NodeKind.Procedure or NodeKind.Function && n.IntProp("complexity") >= 10)
                Risk(n, "complex", Severity.Medium, $"{n.Name} is complex (complexity {n.Prop("complexity")})", "Many paths through it: test each before rebuilding it.");
            if (n.Kind == NodeKind.Table && n.Prop("defined") == "false")
                Risk(n, "undefined-table", Severity.Info, $"{n.Name} is used but not defined in the scan", "Add the database's scripts or the live database to see its columns and who else uses it.");
            if (n.Prop("alsoDefinedIn") is { } also)
                Risk(n, "defined-twice", Severity.Low, $"{n.Name} is defined in more than one place", $"Also in {also.Replace("|", ", ")}: check which one is live.");
            if (n.Kind == NodeKind.Setting && n.Prop("secret") == "true")
                Risk(n, "secret-in-config", Severity.Medium, $"{n.Name}: a secret kept in a config file", "Kept as plain text next to the program. Move it to a secret store.");
            if (n.Kind == NodeKind.Connection && n.Prop("password") != null && n.Prop("hardCoded") != "true")
                Risk(n, "secret-in-config", Severity.Medium, $"{n.Name}: a connection string with a password", "Kept as plain text in the config file. Use integrated sign-in or a secret store.");
            if (n.Kind == NodeKind.Project && n.Prop("controls (OCX)") is { } ocx)
                Risk(n, "activex", Severity.Medium, $"{n.Name} uses ActiveX controls: {ocx}", "Each needs a modern replacement, and their behaviour must be matched.");
            if (n.Kind == NodeKind.Method && map.From(n.Id).Any(e => e.Kind is EdgeKind.Reads or EdgeKind.Writes && e.Note?.Contains("SELECT *", StringComparison.OrdinalIgnoreCase) == true))
                Risk(n, "select-star", Severity.Low, $"{Name(n)} uses SELECT *", "It takes whatever columns the table has: a schema change breaks it quietly.", map.From(n.Id).First(e => e.Note?.Contains("SELECT *", StringComparison.OrdinalIgnoreCase) == true).Note);
            if (n.Kind == NodeKind.Method && map[n.Parent ?? ""]?.Kind == NodeKind.Form && map.From(n.Id).Any(e => e.Kind is EdgeKind.Reads or EdgeKind.Writes or EdgeKind.Executes))
                Risk(n, "data-access-in-screen", Severity.Low, $"{Name(n)} reaches the database from screen code", "Screen and data access are mixed: separate them as it's rebuilt.");
        }
    }

    public static string Plural(int n, string one, string many) => n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
}
