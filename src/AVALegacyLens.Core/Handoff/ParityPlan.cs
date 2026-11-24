using System.Text.Json;
using System.Text.Json.Nodes;
using AVALegacyLens.Core.Findings;
using AVALegacyLens.Core.Model;

namespace AVALegacyLens.Core.Handoff;

/// <summary>
/// A parity plan: the checks a rebuild must pass before cutover, as an AVAParityCheck suite (.parity) to open and fill
/// in. One check for each table the live code writes (old database against new, keyed on the primary key, with
/// tolerances for money, dates compared as dates, text trimmed, and the columns the database stamps itself ignored),
/// and one for each procedure the live code runs that returns rows. The two databases are named connections, "old"
/// and "new", set up in AVAParityCheck: no connection string is written. Each check's note says why it's there.
/// </summary>
public static class ParityPlan
{
    public const string Format = "avaparitycheck.suite";
    public const string Extension = ".parity";

    public static JsonObject Make(SystemMap map)
    {
        var checks = new JsonArray();
        bool Live(Node n) => n.Prop("dead") != "true";
        string Paths(Node n)
        {
            var controls = Trace.Backward(map, n.Id, 12).All().Where(s => s.Node.Kind == NodeKind.Control).Select(s => $"{s.Node.Prop("form")}.{s.Node.Name}").Distinct().Take(6).ToList();
            return controls.Count == 0 ? "" : $" Reached from {string.Join(", ", controls)}.";
        }

        // Tables the live system writes: their rows after a test run must match.
        foreach (var table in map.OfKind(NodeKind.Table).Where(t => t.Prop("defined") != "false" && Live(t)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var writers = Usage.Writers(map, table.Id).Where(w => Live(w.Node)).ToList();
            if (writers.Count == 0) continue;
            var columns = map.Children(table.Id).Where(c => c.Kind == NodeKind.Column).OrderBy(c => c.Line).ToList();
            var keys = columns.Where(c => c.Prop("key") == "primary").Select(c => c.Name).ToList();
            var stamped = columns.Where(c => c.Prop("default") is { } d && d.Contains("GETDATE", StringComparison.OrdinalIgnoreCase) || c.Prop("default")?.Contains("SYSDATETIME", StringComparison.OrdinalIgnoreCase) == true).Select(c => c.Name).ToList();
            var schema = table.Prop("schema") ?? "dbo";
            var bare = table.Name.Contains('.') ? table.Name[(table.Name.IndexOf('.') + 1)..] : table.Name;
            var list = columns.Count > 0 ? string.Join(", ", columns.Select(c => $"[{c.Name}]")) : "*";
            var sql = $"SELECT {list} FROM [{schema}].[{bare}]" + (keys.Count > 0 ? $" ORDER BY {string.Join(", ", keys.Select(k => $"[{k}]"))}" : "");
            var rules = new JsonArray();
            foreach (var c in columns.Where(c => !stamped.Contains(c.Name)))
            {
                var type = (c.Prop("type") ?? "").ToLowerInvariant();
                if (type.StartsWith("decimal", StringComparison.Ordinal) || type.StartsWith("numeric", StringComparison.Ordinal) || type.Contains("money", StringComparison.Ordinal) || type is "float" or "real")
                    rules.Add(new JsonObject { ["column"] = c.Name, ["abs_tolerance"] = 0.01 });
                else if (type.StartsWith("date", StringComparison.Ordinal) || type.Contains("time", StringComparison.Ordinal))
                    rules.Add(new JsonObject { ["column"] = c.Name, ["date"] = true });
                else if (type.Contains("char", StringComparison.Ordinal) || type.Contains("text", StringComparison.Ordinal))
                {
                    var rule = new JsonObject { ["column"] = c.Name, ["trim"] = true };
                    if (c.Prop("nullable") == "true") rule["null_equals_empty"] = true;
                    rules.Add(rule);
                }
            }
            var identity = columns.Where(c => keys.Contains(c.Name) && c.Prop("identity") == "true").Select(c => c.Name).ToList();
            var note = $"{table.Name} is written by {string.Join(", ", writers.Select(w => w.Node.Kind == NodeKind.Method ? $"{w.Node.Prop("type")}.{w.Node.Name}" : w.Node.Name))}.{Paths(table)}";
            if (identity.Count > 0) note += $" Its key ({string.Join(", ", identity)}) is an identity: if the rebuild numbers rows differently, key on a business column instead.";
            if (stamped.Count > 0) note += $" Ignored: {string.Join(", ", stamped)}, which the database stamps with the time.";
            if (writers.Any(w => w.Edge.Certainty == Certainty.Guessed)) note += " Some writes are guessed from SQL built at run time: check what else writes it.";
            var rulesHere = map.Findings.Count(f => f.Kind == FindingKind.Rule && (f.NodeId == table.Id || writers.Any(w => w.Node.Id == f.NodeId)));
            if (rulesHere > 0) note += $" {rulesHere} business rule{(rulesHere == 1 ? "" : "s")} guard it: test each with rows that break it.";
            checks.Add(Check($"table {table.Name}", Side("old", sql), Side("new", sql), keys, stamped, rules, note));
        }

        // Procedures the live code runs: what they return (or do) must match for the same arguments.
        foreach (var proc in map.Nodes.Where(n => n.Kind is NodeKind.Procedure && n.Prop("defined") != "false" && Live(n)).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            var callers = map.To(proc.Id).Where(e => e.Kind == EdgeKind.Executes && map[e.From] is { } c && Live(c)).ToList();
            if (callers.Count == 0) continue;
            var returnsRows = map.From(proc.Id).Any(e => e.Kind == EdgeKind.Reads) && !map.From(proc.Id).Any(e => e.Kind == EdgeKind.Writes);
            if (!returnsRows) continue;
            var schema = proc.Prop("schema") ?? "dbo";
            var bare = proc.Name.Contains('.') ? proc.Name[(proc.Name.IndexOf('.') + 1)..] : proc.Name;
            var args = (proc.Prop("parameters") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(' ')[0]).Where(p => p.StartsWith('@')).ToList();
            var sql = $"EXEC [{schema}].[{bare}]" + (args.Count > 0 ? " " + string.Join(", ", args.Select(a => $"{a} = NULL")) : "");
            var note = $"Run by {string.Join(", ", callers.Select(e => map[e.From]!).Select(c => c.Kind == NodeKind.Method ? $"{c.Prop("type")}.{c.Name}" : c.Name).Distinct())}.{Paths(proc)}" +
                       (args.Count > 0 ? $" Put test values in place of NULL for {string.Join(", ", args)}, and add a check for each case worth testing." : "");
            checks.Add(Check($"procedure {proc.Name}", Side("old", sql), Side("new", sql), [], [], [], note));
        }

        return new JsonObject
        {
            ["format"] = Format,
            ["version"] = 1,
            ["checks"] = checks,
        };
    }

    public static void Save(SystemMap map, string path) =>
        File.WriteAllText(path, Make(map).ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

    private static JsonObject Side(string connection, string sql) => new() { ["kind"] = "sql", ["connection"] = connection, ["sql"] = sql };

    private static JsonObject Check(string name, JsonObject old, JsonObject @new, List<string> keys, List<string> ignored, JsonArray rules, string note) => new()
    {
        ["name"] = name,
        ["old"] = old,
        ["new"] = @new,
        ["keys"] = new JsonArray(keys.Select(k => (JsonNode)k).ToArray()),
        ["mapping"] = new JsonObject(),
        ["ignored"] = new JsonArray(ignored.Select(k => (JsonNode)k).ToArray()),
        ["rules"] = rules,
        ["accept"] = new JsonArray(),
        ["note"] = note,
    };
}
