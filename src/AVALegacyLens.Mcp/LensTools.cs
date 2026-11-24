using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVALegacyLens.Core;
using AVALegacyLens.Core.Findings;
using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Scanning;
using AVALegacyLens.Core.Sql;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVALegacyLens.Mcp;

/// <summary>
/// The MCP tools: what an agent can do with a legacy system's map. They read code and schema only (never a table's
/// rows), through the scan the window shows (or the headless server's own); the only things they write are new files
/// in the working folder (reports, .lens files, steps files), and the only thing they change is the marks on findings.
/// Every piece of source or SQL they return has passwords masked. Failures come back as MCP tool errors
/// (<see cref="McpException"/>) with a message that says what to do instead.
/// </summary>
/// <remarks>The parameter names are the JSON names agents see, hence snake_case. Lines count from 1.</remarks>
public sealed partial class LensTools(IExplorerHost host)
{
    private readonly Workspace _files = new(() => host.Root);

    /// <summary>The most items, links or lines a call returns.</summary>
    public const int MaxResults = 300;

    private const string ItemHelp = "The item: its id (from find or get_node), or a name such as frmPatient, frmPatient.btnSave, BillingRules.PostCharge, Patient, Patient.LastName or usp_SavePatient.";

    // ───────────────────────────── the scan ─────────────────────────────

    [McpServerTool(Name = "list_files", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Lists the files a scan reads in the working folder (and its subfolders): VB.NET, C#, VB6, T-SQL, config and project files, and saved scans (.lens). Start here if nothing is scanned yet.")]
    public Task<string> ListFiles(
        [Description("A folder inside the working folder to list instead. Optional.")] string? folder = null,
        CancellationToken cancel = default) =>
        Plain("listed the files", () =>
        {
            var (list, more) = _files.List(folder);
            var byKind = list.GroupBy(f => Path.GetExtension(f).ToLowerInvariant()).OrderByDescending(g => g.Count()).Select(g => (JsonNode)$"{g.Key} ×{g.Count()}").ToArray();
            var result = new JsonObject { ["folder"] = _files.Root, ["count"] = list.Count, ["kinds"] = new JsonArray(byKind), ["files"] = new JsonArray(list.Select(f => (JsonNode)f).ToArray()) };
            if (more) result["note"] = $"Only the first {Workspace.MaxListed} are listed. Name a folder to see the rest.";
            return result;
        }, cancel);

    [McpServerTool(Name = "scan", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Scans the working folder (or a folder in it) into a system map: projects, files, screens and their controls, types, methods, tables, columns, procedures, settings, and the calls, reads, writes and handlers between them; then the rules, dead code and risks. Live databases the person added are read too (their schema only). In the open window, the window shows the new scan. People's marks on findings carry over when the same folder is scanned again. Takes seconds for most systems.")]
    public async Task<string> Scan(
        [Description("A folder inside the working folder. Default: the working folder itself.")] string? folder = null,
        CancellationToken cancel = default)
    {
        try
        {
            var full = string.IsNullOrWhiteSpace(folder) || folder.Trim() == "." ? _files.Root : _files.Resolve(folder);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"There's no folder \"{folder}\" in the working folder.");
            var databases = await Run("read the settings", s => s.Databases.ToList(), cancel).ConfigureAwait(false);
            var map = await Task.Run(() => Scanner.Scan(full, new ScanOptions { Databases = databases }, cancel), cancel).ConfigureAwait(false);
            return await Run("scanned " + Shown(full), s =>
            {
                if (s.Map is { } old && old.Root == map.Root)
                    foreach (var (id, mark) in old.Marks) map.Marks.TryAdd(id, mark);
                s.Map = map;
                s.Show(ViewState.Empty with { Scan = map.Root });
                var result = Summary(map);
                result["scanned"] = Shown(full);
                if (databases.Count > 0) result["databases"] = databases.Count;
                return result.ToJsonString(Json.Options);
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    [McpServerTool(Name = "open_lens", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Opens a saved scan (.lens file) from the working folder instead of scanning, with the marks people made on its findings. Source is shown if the scanned folder is still there.")]
    public async Task<string> OpenLens(
        [Description("The .lens file, relative to the working folder.")] string file,
        CancellationToken cancel = default)
    {
        try
        {
            var full = _files.Existing(file);
            if (!full.EndsWith(LensFile.Extension, StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException($"{file} isn't a .lens file.");
            var map = await Task.Run(() => LensFile.Load(full), cancel).ConfigureAwait(false);
            return await Run("opened " + file, s =>
            {
                s.Map = map;
                s.Show(ViewState.Empty with { Scan = full });
                var result = Summary(map);
                result["opened"] = file;
                result["source_available"] = map.Root.Length > 0 && Directory.Exists(map.Root);
                return result.ToJsonString(Json.Options);
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    [McpServerTool(Name = "save_lens", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Saves the scan, with the marks on its findings, as a new .lens file in the working folder, so it can be opened again (open_lens) or compared with a later scan (compare_scans).")]
    public Task<string> SaveLens(
        [Description("The new .lens file, relative to the working folder.")] string output,
        CancellationToken cancel = default) =>
        Write("saved the scan", output, "The scan", [LensFile.Extension], (map, full) =>
        {
            LensFile.Save(map, full);
            return new JsonObject { ["items"] = map.Nodes.Count, ["links"] = map.Edges.Count, ["marks"] = map.Marks.Count };
        }, cancel);

    [McpServerTool(Name = "get_inventory", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The system at a glance: lines of code by language, how many screens, types, methods, tables and procedures, each project's size, the most complex methods, how many rules, dead items and risks were found, and the rebuild estimate.")]
    public Task<string> GetInventory(CancellationToken cancel = default) =>
        OnMap("read the inventory", (_, map) =>
        {
            var inv = Inventory.Of(map);
            var result = Summary(map);
            result["languages"] = new JsonArray(inv.Languages.Select(l => (JsonNode)new JsonObject { ["language"] = l.Language, ["files"] = l.Files, ["code_lines"] = l.Code, ["comment_lines"] = l.Comments }).ToArray());
            result["projects"] = new JsonArray(inv.Projects.Select(p => (JsonNode)new JsonObject { ["project"] = p.Name, ["id"] = p.Id, ["language"] = p.Language ?? "folder", ["files"] = p.Files, ["screens"] = p.Screens, ["types"] = p.Types, ["methods"] = p.Methods, ["code_lines"] = p.Code }).ToArray());
            result["most_complex"] = new JsonArray(inv.MostComplex.Select(m => (JsonNode)new JsonObject { ["method"] = $"{m.Type}.{m.Name}", ["id"] = m.Id, ["complexity"] = m.Complexity, ["lines"] = m.Lines, ["at"] = $"{m.File}:{m.Line}" }).ToArray());
            result["links"] = inv.Links;
            result["links_by_name"] = inv.LinksByName;
            if (map.Notes.Count > 0) result["could_not_read"] = new JsonArray(map.Notes.Take(50).Select(n => (JsonNode)$"{n.File}{(n.Line > 0 ? ":" + n.Line : "")}: {n.Message}").ToArray());
            return result;
        }, cancel);

    // ───────────────────────────── items ─────────────────────────────

    [McpServerTool(Name = "find", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Finds items by name (part of it, any case): screens, controls, types, methods, tables, columns, procedures, settings… Exact names first. Each with its id, kind and where it is.")]
    public Task<string> Find(
        [Description("Part of the name to look for.")] string query,
        [Description("Only this kind: project, file, form, control, type, method, procedure, view, function, trigger, table, column, setting, connection or external. Optional.")] string? kind = null,
        [Description("The most to return, up to 300. Default 50.")] int limit = 50,
        CancellationToken cancel = default) =>
        OnMap("found items", (_, map) =>
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is part of a name to look for.");
            var k = ParseKind(kind);
            var q = query.Trim();
            var found = map.Nodes.Where(n => (k == null || n.Kind == k) && (n.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || Qualified(n).Contains(q, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(n => !n.Name.Equals(q, StringComparison.OrdinalIgnoreCase) && !Qualified(n).Equals(q, StringComparison.OrdinalIgnoreCase))
                .ThenBy(n => !n.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenBy(n => n.Kind).ThenBy(n => Qualified(n), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var take = Math.Clamp(limit, 1, MaxResults);
            return new JsonObject { ["query"] = q, ["count"] = found.Count, ["items"] = new JsonArray(found.Take(take).Select(n => (JsonNode)Brief(n)).ToArray()), ["more"] = found.Count > take ? found.Count - take : null };
        }, cancel);

    [McpServerTool(Name = "get_node", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("One item in full: what it is, where it is (file and lines), its properties (a method's signature and complexity, a control's text and limits, a column's type and key…), what it uses and what uses it (each link with its kind, place, the code there, and how sure the scan is of it), and the findings on it.")]
    public Task<string> GetNode(
        [Description(ItemHelp)] string item,
        CancellationToken cancel = default) =>
        OnMap("read an item", (_, map) =>
        {
            var n = Resolve(map, item);
            var o = Brief(n);
            o["path"] = string.Join(" › ", map.PathTo(n.Id).Select(p => p.Name));
            o["properties"] = Props(n);
            var uses = map.From(n.Id).Take(MaxResults).Select(e => (JsonNode)Link(map, e, true)).ToArray();
            var usedBy = map.To(n.Id).Take(MaxResults).Select(e => (JsonNode)Link(map, e, false)).ToArray();
            o["uses"] = new JsonArray(uses);
            o["used_by"] = new JsonArray(usedBy);
            var children = map.Children(n.Id);
            if (children.Count > 0) o["holds"] = children.GroupBy(c => c.Kind).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}{(g.Count() == 1 ? "" : "s")}").Aggregate((a, b) => a + ", " + b);
            var findings = map.Findings.Where(f => f.NodeId == n.Id).ToList();
            if (findings.Count > 0) o["findings"] = new JsonArray(findings.Select(f => (JsonNode)FindingJson(map, f)).ToArray());
            return o;
        }, cancel);

    [McpServerTool(Name = "list_children", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Browses the system's tree as the window shows it: without an item, the top (projects, the database, outside systems); with one, what's inside it (a project's files, a file's types, a type's methods, a screen's controls, a table's columns).")]
    public Task<string> ListChildren(
        [Description(ItemHelp + " Optional: the top when not given.")] string? item = null,
        CancellationToken cancel = default) =>
        OnMap("browsed the tree", (_, map) =>
        {
            var parent = string.IsNullOrWhiteSpace(item) ? null : Resolve(map, item);
            var kids = map.Children(parent?.Id).Take(MaxResults).ToList();
            return new JsonObject
            {
                ["item"] = parent?.Id,
                ["children"] = new JsonArray(kids.Select(c => { var b = Brief(c); var count = map.Children(c.Id).Count; if (count > 0) b["holds"] = count; return (JsonNode)b; }).ToArray()),
            };
        }, cancel);

    [McpServerTool(Name = "get_source", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Source lines, numbered: an item's own lines (a method, a procedure, a control's declaration…), or lines of a file. Passwords are masked. At most 400 lines a call.")]
    public Task<string> GetSource(
        [Description(ItemHelp + " Or give file instead.")] string? item = null,
        [Description("A file, relative to the scanned folder (as items' file shows it). Optional if item is given.")] string? file = null,
        [Description("The first line, from 1. Default: the item's first line, or 1.")] int? from = null,
        [Description("The last line. Default: the item's last line, or 400 lines on.")] int? to = null,
        CancellationToken cancel = default) =>
        OnMap("read source", (_, map) =>
        {
            Node? n = string.IsNullOrWhiteSpace(item) ? null : Resolve(map, item);
            var lines = SourceLines(map, n, file, out var shownFile);
            var first = Math.Max(1, from ?? (n != null && n.Line > 0 && n.Kind != NodeKind.File ? n.Line : 1));
            var last = Math.Min(lines.Length, to ?? (n != null && n.EndLine >= first && n.Kind != NodeKind.File ? n.EndLine : first + 399));
            if (last - first + 1 > 400) last = first + 399;
            if (first > lines.Length) throw new ArgumentException($"{shownFile} has {lines.Length} lines.");
            var text = new System.Text.StringBuilder();
            for (var i = first; i <= last; i++) text.Append(i.ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append("  ").Append(Secrets.Mask(lines[i - 1].TrimEnd('\r'))).Append('\n');
            return new JsonObject { ["file"] = shownFile, ["from"] = first, ["to"] = last, ["lines_in_file"] = lines.Length, ["source"] = text.ToString() };
        }, cancel);

    // ───────────────────────────── tracing and usage ─────────────────────────────

    [McpServerTool(Name = "trace", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Follows the map from an item. Forward goes the way the program runs: a screen to its controls, a control to its event handlers, a method to what it calls, creates, runs, reads and writes, a table write to the triggers it fires. Backward goes from code or a table up to the buttons and screens whose events reach it. Each step with its file and line, and how sure the link is (\"by name\" or \"guessed\" when not resolved by a compiler).")]
    public Task<string> TraceItem(
        [Description(ItemHelp)] string item,
        [Description("\"forward\" (the default) or \"backward\".")] string direction = "forward",
        [Description("How many steps deep, 1 to 20. Default 10.")] int depth = Core.Trace.DefaultDepth,
        CancellationToken cancel = default) =>
        OnMap("traced " + item, (_, map) =>
        {
            var n = Resolve(map, item);
            var forward = direction.Trim().ToLowerInvariant() switch { "forward" or "" => true, "backward" or "back" => false, _ => throw new ArgumentException("direction is forward or backward.") };
            var trace = forward ? Core.Trace.Forward(map, n.Id, Math.Clamp(depth, 1, 20)) : Core.Trace.Backward(map, n.Id, Math.Clamp(depth, 1, 20));
            var steps = trace.All().Skip(1).ToList();
            return new JsonObject
            {
                ["from"] = Core.Trace.Describe(n),
                ["direction"] = forward ? "forward" : "backward",
                ["steps"] = steps.Count,
                ["trace"] = Core.Trace.ToText(trace),
                ["screens"] = new JsonArray(steps.Where(s => s.Node.Kind == NodeKind.Form).Select(s => (JsonNode)s.Node.Name).Distinct().ToArray()),
                ["controls"] = new JsonArray(steps.Where(s => s.Node.Kind == NodeKind.Control).Select(s => (JsonNode)$"{s.Node.Prop("form")}.{s.Node.Name}").Distinct().ToArray()),
                ["tables_written"] = new JsonArray(steps.Where(s => s.Node.Kind == NodeKind.Table && s.Edge?.Kind == EdgeKind.Writes).Select(s => (JsonNode)s.Node.Name).Distinct().ToArray()),
                ["tables_read"] = new JsonArray(steps.Where(s => s.Node.Kind == NodeKind.Table && s.Edge?.Kind == EdgeKind.Reads).Select(s => (JsonNode)s.Node.Name).Distinct().ToArray()),
                ["procedures_run"] = new JsonArray(steps.Where(s => s.Node.Kind is NodeKind.Procedure or NodeKind.Function).Select(s => (JsonNode)s.Node.Name).Distinct().ToArray()),
            };
        }, cancel);

    [McpServerTool(Name = "who_writes", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What writes a table or a column: each procedure, trigger or method that inserts, updates or deletes it (with the statement and its place, and whether it's a guess from SQL built at run time), and the screens and buttons from which each is reached.")]
    public Task<string> WhoWrites(
        [Description("A table or column: Patient, Patient.LastName, dbo.Patient, or its id.")] string item,
        CancellationToken cancel = default) =>
        OnMap("looked up who writes " + item, (_, map) => Users(map, Resolve(map, item), writes: true), cancel);

    [McpServerTool(Name = "who_reads", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What reads a table or a column: each procedure, view, trigger or method that selects from it, with the statement and its place, and the screens and buttons from which each is reached.")]
    public Task<string> WhoReads(
        [Description("A table or column: Patient, Patient.LastName, dbo.Patient, or its id.")] string item,
        CancellationToken cancel = default) =>
        OnMap("looked up who reads " + item, (_, map) => Users(map, Resolve(map, item), writes: false), cancel);

    private static JsonObject Users(SystemMap map, Node n, bool writes)
    {
        if (n.Kind is not (NodeKind.Table or NodeKind.Column or NodeKind.View)) throw new ArgumentException($"{n.Name} is a {n.Kind.ToString().ToLowerInvariant()}: give a table or a column.");
        var list = writes ? Usage.Writers(map, n.Id) : Usage.Readers(map, n.Id);
        var users = new JsonArray();
        foreach (var (user, edge) in list.Take(MaxResults))
        {
            var reached = Core.Trace.Backward(map, user.Id, 12).All().Where(s => s.Node.Kind == NodeKind.Control).Select(s => $"{s.Node.Prop("form")}.{s.Node.Name}").Distinct().ToList();
            users.Add(new JsonObject
            {
                ["by"] = Core.Trace.Describe(user),
                ["id"] = user.Id,
                ["statement"] = edge.Note,
                ["at"] = edge.File != null ? $"{edge.File}:{edge.Line}" : null,
                ["certainty"] = edge.Certainty.ToString().ToLowerInvariant(),
                ["dead"] = user.Prop("dead") == "true" ? true : null,
                ["reached_from"] = new JsonArray(reached.Select(r => (JsonNode)r).ToArray()),
            });
        }
        return new JsonObject { ["item"] = Core.Trace.Describe(n), ["id"] = n.Id, [writes ? "writers" : "readers"] = users, ["note"] = list.Count == 0 ? (writes ? "Nothing in the scan writes it." : "Nothing in the scan reads it.") : null };
    }

    [McpServerTool(Name = "list_screens", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The screens (WinForms forms and user controls, VB6 forms): each with its title, project, file, how many controls and event handlers it has, and whether anything opens it.")]
    public Task<string> ListScreens(CancellationToken cancel = default) =>
        OnMap("listed the screens", (_, map) => new JsonObject
        {
            ["screens"] = new JsonArray(map.OfKind(NodeKind.Form).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => (JsonNode)new JsonObject
            {
                ["screen"] = f.Name,
                ["id"] = f.Id,
                ["title"] = f.Prop("title"),
                ["language"] = f.Language,
                ["project"] = f.Prop("project"),
                ["file"] = f.File,
                ["controls"] = Core.Trace.Controls(map, f.Id).Count(),
                ["handlers"] = map.To(f.Id).Count(e => e.Kind == EdgeKind.Handles) + Core.Trace.Controls(map, f.Id).Sum(c => map.To(c.Id).Count(e => e.Kind == EdgeKind.Handles)),
                ["opened_by"] = new JsonArray(map.To(f.Id).Where(e => e.Kind == EdgeKind.Creates).Select(e => (JsonNode)(map[e.From]?.Name ?? e.From)).Distinct().ToArray()),
                ["dead"] = f.Prop("dead") == "true" ? true : null,
            }).ToArray()),
        }, cancel);

    [McpServerTool(Name = "describe_screen", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("A screen end to end: its controls in tab order (type, text, position and size in pixels, limits such as MaxLength), the event handlers of each, and what each handler leads to: methods called, procedures run, tables read and written, other screens opened, and the rules checked on the way.")]
    public Task<string> DescribeScreen(
        [Description("The screen: its name (frmPatient) or id.")] string screen,
        CancellationToken cancel = default) =>
        OnMap("described a screen", (_, map) =>
        {
            var f = Resolve(map, screen);
            if (f.Kind != NodeKind.Form) throw new ArgumentException($"{f.Name} is a {f.Kind.ToString().ToLowerInvariant()}, not a screen. list_screens shows them.");
            JsonObject Effects(Node handler)
            {
                var steps = Core.Trace.Forward(map, handler.Id, 12).All().Skip(1).ToList();
                var ids = steps.Select(s => s.Node.Id).Append(handler.Id).ToHashSet(StringComparer.Ordinal);
                return new JsonObject
                {
                    ["handler"] = $"{handler.Prop("type")}.{handler.Name}",
                    ["id"] = handler.Id,
                    ["at"] = $"{handler.File}:{handler.Line}",
                    ["calls"] = new JsonArray(steps.Where(s => s.Node.Kind == NodeKind.Method).Select(s => (JsonNode)$"{s.Node.Prop("type")}.{s.Node.Name}").Distinct().ToArray()),
                    ["runs"] = new JsonArray(steps.Where(s => s.Node.Kind is NodeKind.Procedure or NodeKind.Function or NodeKind.Trigger).Select(s => (JsonNode)s.Node.Name).Distinct().ToArray()),
                    ["writes"] = new JsonArray(steps.Where(s => s.Node.Kind == NodeKind.Table && s.Edge?.Kind == EdgeKind.Writes).Select(s => (JsonNode)s.Node.Name).Distinct().ToArray()),
                    ["reads"] = new JsonArray(steps.Where(s => s.Node.Kind == NodeKind.Table && s.Edge?.Kind == EdgeKind.Reads).Select(s => (JsonNode)s.Node.Name).Distinct().ToArray()),
                    ["opens"] = new JsonArray(steps.Where(s => s.Node.Kind == NodeKind.Form).Select(s => (JsonNode)s.Node.Name).Distinct().ToArray()),
                    ["rules"] = new JsonArray(map.Findings.Where(r => r.Kind == FindingKind.Rule && ids.Contains(r.NodeId)).Select(r => (JsonNode)$"{r.Title} ({Report.Place(r)})").ToArray()),
                };
            }
            var controls = new JsonArray();
            foreach (var c in Core.Trace.Controls(map, f.Id))
            {
                var o = new JsonObject { ["control"] = c.Name, ["type"] = c.Prop("type") };
                foreach (var k in new[] { "Text", "Location", "Size", "TabIndex", "MaxLength", "ReadOnly", "Enabled", "Visible", "DropDownStyle", "Items", "Index" })
                    if (c.Prop(k) is { } v) o[k.ToLowerInvariant()] = v;
                if (c.Parent != f.Id && map[c.Parent ?? ""] is { } holder) o["inside"] = holder.Name;
                var handlers = map.To(c.Id).Where(e => e.Kind == EdgeKind.Handles).ToList();
                if (handlers.Count > 0)
                    o["events"] = new JsonArray(handlers.Select(e => { var h = map[e.From]!; var eff = Effects(h); eff["event"] = e.Note; return (JsonNode)eff; }).ToArray());
                var users = map.To(c.Id).Where(e => e.Kind == EdgeKind.Uses).Select(e => map[e.From]).Where(m => m != null).Select(m => (JsonNode)m!.Name).Distinct().ToArray();
                if (users.Length > 0) o["used_by"] = new JsonArray(users);
                controls.Add(o);
            }
            return new JsonObject
            {
                ["screen"] = f.Name,
                ["id"] = f.Id,
                ["title"] = f.Prop("title"),
                ["size"] = f.Prop("ClientSize"),
                ["file"] = f.File,
                ["language"] = f.Language,
                ["opened_by"] = new JsonArray(map.To(f.Id).Where(e => e.Kind == EdgeKind.Creates).Select(e => (JsonNode)(map[e.From] is { } m ? $"{m.Prop("type")}.{m.Name}" : e.From)).Distinct().ToArray()),
                ["form_events"] = new JsonArray(map.To(f.Id).Where(e => e.Kind == EdgeKind.Handles).Select(e => { var eff = Effects(map[e.From]!); eff["event"] = e.Note; return (JsonNode)eff; }).ToArray()),
                ["controls"] = controls,
            };
        }, cancel);

    // ───────────────────────────── findings ─────────────────────────────

    [McpServerTool(Name = "list_rules", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The business rules found: checks that stop with a message (validation), messages that don't stop (notice), decision tables (decision), limits written into the code (limit), the database's CHECK constraints (constraint), calculations in SQL (calculation), and guards in check functions (guard). Each in the original's own words where it has them, with the condition, the code, its place, how sure the scan is, and any mark a person made. Each has an id for mark_finding.")]
    public Task<string> ListRules(
        [Description("Only this category: validation, notice, decision, limit, constraint, calculation or guard. Optional.")] string? category = null,
        [Description("Only these marks: confirmed, wrong, noise, or unmarked. Optional.")] string? mark = null,
        [Description("Only rules in or under this item (a screen, a type, a table, a project). Optional.")] string? item = null,
        CancellationToken cancel = default) =>
        OnMap("listed the rules", (_, map) => Findings(map, FindingKind.Rule, category, mark, item, null), cancel);

    [McpServerTool(Name = "list_dead_code", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What nothing live reaches from the system's entry points (the startup screen, Main, framework callbacks): methods, screens, types, procedures, views, tables, triggers, settings and connections. Category unused (nothing refers to it at all) or unreachable (only dead code does), and a confidence: high for private code nothing calls; medium or low where something outside the scan (a report, a job, reflection, data binding) might still use it.")]
    public Task<string> ListDeadCode(
        [Description("Only this confidence or higher: low, medium or high. Optional.")] string? confidence = null,
        [Description("Only these marks: confirmed, wrong, noise, or unmarked. Optional.")] string? mark = null,
        CancellationToken cancel = default) =>
        OnMap("listed the dead code", (_, map) => Findings(map, FindingKind.DeadCode, null, mark, null, confidence), cancel);

    [McpServerTool(Name = "list_risks", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What makes the system risky or the rebuild harder, worst first: SQL built by joining strings (injection), dynamic SQL, passwords or servers written into code, ignored errors (On Error Resume Next), native Windows calls, ActiveX controls, global state, secrets in config, SELECT *, data access in screen code, very complex or long methods. Each with why it matters and its place.")]
    public Task<string> ListRisks(
        [Description("Only this severity or worse: info, low, medium or high. Optional.")] string? severity = null,
        [Description("Only these marks: confirmed, wrong, noise, or unmarked. Optional.")] string? mark = null,
        CancellationToken cancel = default) =>
        OnMap("listed the risks", (_, map) => Findings(map, FindingKind.Risk, null, mark, null, severity), cancel);

    [McpServerTool(Name = "mark_finding", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Marks a finding as a person judged it (only when they've said so): confirmed (it's right), wrong (it isn't so) or noise (true but not worth keeping), with an optional note; clear takes the mark off. Marks show in the window, change the estimate, are saved with the scan (.lens) and carried to a rescan.")]
    public Task<string> MarkFinding(
        [Description("The finding's id, as list_rules, list_dead_code or list_risks give it.")] string finding,
        [Description("confirmed, wrong, noise or clear.")] string state,
        [Description("Why, in a few words. Optional.")] string? note = null,
        CancellationToken cancel = default) =>
        OnMap("marked a finding", (s, map) =>
        {
            var f = map.Findings.FirstOrDefault(x => x.Id == finding.Trim()) ?? throw new KeyNotFoundException($"There's no finding \"{finding}\". list_rules, list_dead_code and list_risks give their ids.");
            switch (state.Trim().ToLowerInvariant())
            {
                case "clear" or "none" or "":
                    map.Marks.Remove(f.Id);
                    break;
                case var st when Enum.TryParse<MarkState>(st, true, out var m):
                    map.Marks[f.Id] = new Mark(m, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), DateTimeOffset.Now);
                    break;
                default:
                    throw new ArgumentException("state is confirmed, wrong, noise or clear.");
            }
            s.Show(s.View with { Tab = f.Kind switch { FindingKind.Rule => "rules", FindingKind.DeadCode => "dead code", _ => "problems" } });
            return FindingJson(map, f);
        }, cancel);

    [McpServerTool(Name = "get_estimate", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("A rough size for rebuilding the live part of the system, in person-days, with a range: screens by how many controls they have, methods and procedures by complexity, tables, high risks and rules to confirm, each with its weight, so it can be argued with. Dead code is left out, and findings marked wrong or noise don't count. Not a quote.")]
    public Task<string> GetEstimate(CancellationToken cancel = default) =>
        OnMap("read the estimate", (_, map) =>
        {
            var est = Estimate.Of(map);
            return new JsonObject
            {
                ["person_days"] = Math.Round(est.Days, 1),
                ["low"] = est.Low,
                ["high"] = est.High,
                ["lines"] = new JsonArray(est.Lines.Select(l => (JsonNode)new JsonObject { ["area"] = l.Area, ["what"] = l.What, ["count"] = l.Count, ["days_each"] = l.DaysEach, ["days"] = l.Days }).ToArray()),
                ["note"] = "A rough size from counts the scan is sure of, not a quote: the weights are a team's to adjust.",
            };
        }, cancel);

    [McpServerTool(Name = "explain_sql", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Reads a piece of T-SQL (a query from a log, a statement from a report) against the scan's schema: the tables and columns it reads and writes, the procedures and functions it runs, and whether each is in the scan. Doesn't run it.")]
    public Task<string> ExplainSql(
        [Description("The SQL.")] string sql,
        CancellationToken cancel = default) =>
        OnMap("read some SQL", (_, map) =>
        {
            if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("sql is the T-SQL to read.");
            var scratch = new SystemMap();
            var parsed = new SqlReader(scratch).AnalyzeSql("sql", sql, null, 1, Certainty.Resolved);
            JsonNode Entry(Edge e)
            {
                var known = map[e.To];
                var o = new JsonObject { ["name"] = scratch[e.To]?.Name ?? e.To, ["kind"] = (scratch[e.To]?.Kind ?? NodeKind.Table).ToString().ToLowerInvariant(), ["in_scan"] = known != null && known.Prop("defined") != "false" };
                if (known != null) o["id"] = known.Id;
                if (e.Certainty != Certainty.Resolved) o["certainty"] = e.Certainty.ToString().ToLowerInvariant();
                return o;
            }
            var edges = scratch.From("sql");
            return new JsonObject
            {
                ["parsed"] = parsed,
                ["reads"] = new JsonArray(edges.Where(e => e.Kind == EdgeKind.Reads).Select(Entry).ToArray()),
                ["writes"] = new JsonArray(edges.Where(e => e.Kind == EdgeKind.Writes).Select(e => { var o = (JsonObject)Entry(e); o["how"] = e.Note?.Split(':')[0]; return (JsonNode)o; }).ToArray()),
                ["runs"] = new JsonArray(edges.Where(e => e.Kind is EdgeKind.Executes or EdgeKind.Calls).Select(Entry).ToArray()),
                ["note"] = parsed ? null : "It doesn't parse as T-SQL, so its table names were picked out roughly.",
            };
        }, cancel);

    [McpServerTool(Name = "compare_scans", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What changed between two saved scans (.lens files), or a saved scan and the one open now: items added and removed by kind, methods and procedures whose size or complexity changed, and findings that appeared or went away.")]
    public Task<string> CompareScans(
        [Description("The earlier scan, a .lens file in the working folder.")] string before,
        [Description("The later scan, a .lens file in the working folder. Default: the scan open now.")] string? after = null,
        CancellationToken cancel = default) =>
        OnMap("compared scans", (_, current) =>
        {
            var a = LensFile.Load(_files.Existing(before));
            var b = string.IsNullOrWhiteSpace(after) ? current : LensFile.Load(_files.Existing(after));
            var aIds = a.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            var bIds = b.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            JsonArray Group(IEnumerable<Node> nodes) => new(nodes.GroupBy(n => n.Kind).OrderBy(g => g.Key).Select(g => (JsonNode)new JsonObject { ["kind"] = g.Key.ToString().ToLowerInvariant(), ["count"] = g.Count(), ["items"] = new JsonArray(g.Take(50).Select(n => (JsonNode)Qualified(n)).ToArray()) }).ToArray());
            var changed = bIds.Values.Where(n => aIds.TryGetValue(n.Id, out var o) && n.Kind is NodeKind.Method or NodeKind.Procedure or NodeKind.Function or NodeKind.View && (o.IntProp("lines") != n.IntProp("lines") || o.IntProp("complexity") != n.IntProp("complexity")))
                .Take(MaxResults).Select(n => (JsonNode)new JsonObject { ["item"] = Qualified(n), ["lines"] = $"{aIds[n.Id].IntProp("lines")} → {n.IntProp("lines")}", ["complexity"] = $"{aIds[n.Id].IntProp("complexity")} → {n.IntProp("complexity")}" }).ToArray();
            var aFind = a.Findings.Select(f => f.Id).ToHashSet();
            var bFind = b.Findings.Select(f => f.Id).ToHashSet();
            return new JsonObject
            {
                ["before"] = before,
                ["after"] = after ?? "the scan open now",
                ["added"] = Group(bIds.Values.Where(n => !aIds.ContainsKey(n.Id))),
                ["removed"] = Group(aIds.Values.Where(n => !bIds.ContainsKey(n.Id))),
                ["changed"] = new JsonArray(changed),
                ["new_findings"] = new JsonArray(b.Findings.Where(f => !aFind.Contains(f.Id)).Take(MaxResults).Select(f => (JsonNode)$"{f.Kind}: {f.Title} ({Report.Place(f)})").ToArray()),
                ["gone_findings"] = new JsonArray(a.Findings.Where(f => !bFind.Contains(f.Id)).Take(MaxResults).Select(f => (JsonNode)$"{f.Kind}: {f.Title} ({Report.Place(f)})").ToArray()),
            };
        }, cancel);

    [McpServerTool(Name = "export_report", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Writes the scan as a report, a new file in the working folder: .html (one page, no outside links) or .xlsx (a sheet each for the summary, estimate, rules, dead code, risks, screens, tables and methods). With people's marks. Nothing in it comes from data.")]
    public Task<string> ExportReport(
        [Description("The new file, .html or .xlsx, relative to the working folder.")] string output,
        CancellationToken cancel = default) =>
        Write("wrote a report", output, "The report", [".html", ".htm", ".xlsx"], (map, full) =>
        {
            Report.Save(map, full);
            return new JsonObject { ["rules"] = map.Findings.Count(f => f.Kind == FindingKind.Rule), ["dead"] = map.Findings.Count(f => f.Kind == FindingKind.DeadCode), ["risks"] = map.Findings.Count(f => f.Kind == FindingKind.Risk) };
        }, cancel);

    // ───────────────────────────── hand-offs ─────────────────────────────

    [McpServerTool(Name = "export_parity_plan", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Writes the parity plan for the rebuild as an AVAParityCheck suite (.parity), a new file in the working folder: a check for each table the live code writes (old database against new, keyed on its primary key, money within 0.01, dates as dates, text trimmed, time stamps ignored) and for each procedure it runs that returns rows, each with a note on why it's there and what reaches it. The databases are named connections \"old\" and \"new\", set up in AVAParityCheck: no connection string is written.")]
    public Task<string> ExportParityPlan(
        [Description("The new .parity file, relative to the working folder.")] string output,
        CancellationToken cancel = default) =>
        Write("wrote a parity plan", output, "The parity plan", [Core.Handoff.ParityPlan.Extension], (map, full) =>
        {
            var plan = Core.Handoff.ParityPlan.Make(map);
            File.WriteAllText(full, plan.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            var checks = (JsonArray)plan["checks"]!;
            return new JsonObject { ["checks"] = new JsonArray(checks.Select(c => (JsonNode)(string)c!["name"]!).ToArray()), ["connections"] = "old and new: set them up in AVAParityCheck" };
        }, cancel);

    [McpServerTool(Name = "export_axaml", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Drafts old screens as Avalonia .axaml windows for AVAUIDesigner, new files in the working folder: the same controls at the same places and sizes, each mapped to its Avalonia counterpart (limits and items kept; frames and panels become bordered canvases), and comments naming the old handler behind each event and the rules it checked. One screen to a file, or every live screen into a folder.")]
    public Task<string> ExportAxaml(
        [Description("A new .axaml file for one screen, or a folder for every live screen (each named after its screen).")] string output,
        [Description("The screen (frmPatient). Leave out to draft every live screen into the output folder.")] string? screen = null,
        CancellationToken cancel = default) =>
        OnMap("drafted screens", (_, map) =>
        {
            var forms = string.IsNullOrWhiteSpace(screen)
                ? map.OfKind(NodeKind.Form).Where(f => f.Prop("dead") != "true").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : [Resolve(map, screen)];
            if (forms.Any(f => f.Kind != NodeKind.Form)) throw new ArgumentException($"{forms[0].Name} isn't a screen. list_screens shows them.");
            var single = !string.IsNullOrWhiteSpace(screen);
            var written = new JsonArray();
            // Two screens with one name (a VB and a C# version, say): each file is named after its project too.
            var twice = forms.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in forms)
            {
                var name = twice.Contains(f.Name) ? $"{f.Prop("project")}-{f.Name}" : f.Name;
                var full = single ? _files.Resolve(output, "The draft", Core.Handoff.AxamlDraft.Extension) : _files.Resolve(Path.Combine(output, name + Core.Handoff.AxamlDraft.Extension));
                _files.CheckWritable(full);
                File.WriteAllText(full, Core.Handoff.AxamlDraft.Make(map, f.Id));
                _files.Wrote(full);
                written.Add(_files.Relative(full));
            }
            return new JsonObject { ["wrote"] = written, ["screens"] = forms.Count, ["note"] = "Open them in AVAUIDesigner; events are listed in comments, to rebuild behind them." };
        }, cancel);

    // ───────────────────────────── the view ─────────────────────────────

    [McpServerTool(Name = "get_view", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What the window shows: the scan open, the tab, and the item selected (with its name and kind).")]
    public Task<string> GetView(CancellationToken cancel = default) =>
        Run("looked at the view", s => new JsonObject
        {
            ["scan"] = s.Map == null ? null : Shown(s.Map),
            ["tab"] = s.View.Tab,
            ["item"] = s.View.Node,
            ["item_name"] = s.View.Node != null && s.Map?[s.View.Node] is { } n ? Core.Trace.Describe(n) : null,
            ["line"] = s.View.Line > 0 ? s.View.Line : null,
            ["live"] = host.Live,
        }.ToJsonString(Json.Options), cancel);

    [McpServerTool(Name = "show_in_view", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Selects an item in the person's window and shows it on a tab: item (its source and links, a line highlighted), trace (forward or backward from it), graph (what drives it and what it drives), or the inventory, rules, dead code or problems tab. Headless, it only remembers it (render_view draws it).")]
    public Task<string> ShowInView(
        [Description(ItemHelp + " Optional for the inventory, rules, dead code and problems tabs.")] string? item = null,
        [Description("item (the default), trace, trace backward, graph, inventory, rules, dead code or problems.")] string tab = "item",
        [Description("A line to highlight in the item's source. Optional.")] int line = 0,
        CancellationToken cancel = default) =>
        OnMap("showed something", (s, map) =>
        {
            var t = tab.Trim().ToLowerInvariant();
            if (t is not ("item" or "trace" or "trace backward" or "graph" or "inventory" or "rules" or "dead code" or "problems")) throw new ArgumentException("tab is item, trace, trace backward, graph, inventory, rules, dead code or problems.");
            var n = string.IsNullOrWhiteSpace(item) ? null : Resolve(map, item);
            if (n == null && t is "item" or "trace" or "trace backward" or "graph") throw new ArgumentException($"The {t} tab needs an item.");
            s.Show(new ViewState(map.Root, n?.Id, t, line));
            return new JsonObject { ["showing"] = t, ["item"] = n?.Id, ["name"] = n == null ? null : Core.Trace.Describe(n) };
        }, cancel);

    [McpServerTool(Name = "render_view", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("A picture (PNG) to show the person: an item's source with its lines (or one line) highlighted, or a trace from it. Without an item, what the window shows now.")]
    public async Task<CallToolResult> RenderView(
        [Description(ItemHelp + " Optional.")] string? item = null,
        [Description("A line to highlight instead of the item's own lines. Optional.")] int line = 0,
        [Description("Draw a trace instead of source: forward or backward. Optional.")] string? trace = null,
        [Description("Width in pixels, from 200 to 2000. Default 1000.")] int width = 1000,
        CancellationToken cancel = default)
    {
        try
        {
            if (width is < 200 or > 2000) throw new ArgumentException("width is from 200 to 2000 pixels.");
            await EnsureMap(cancel).ConfigureAwait(false);
            var picture = await Run("drew the view", s =>
            {
                var map = s.Map!;
                var view = string.IsNullOrWhiteSpace(item) ? s.View : new ViewState(map.Root, Resolve(map, item).Id, trace is { Length: > 0 } t ? (t.Trim().StartsWith("back", StringComparison.OrdinalIgnoreCase) ? "trace backward" : "trace") : "item", line);
                return PictureOf(map, view);
            }, cancel).ConfigureAwait(false);
            var png = await Run("drew the view", _ => host.Painter.Picture(picture, width / 1000.0), cancel).ConfigureAwait(false);
            return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = picture.Title + (picture.Subtitle.Length > 0 ? " — " + picture.Subtitle : "") }] };
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    /// <summary>What a picture of a view shows: the item's source (its lines highlighted) or a trace from it, or the inventory.</summary>
    internal ViewPicture PictureOf(SystemMap? map, ViewState view)
    {
        if (map == null) return new ViewPicture("Nothing scanned", "", [], 1, 0, 0, false, []);
        var n = view.Node == null ? null : map[view.Node];
        if (n == null || view.Tab is "inventory" or "rules" or "dead code" or "problems")
        {
            var text = view.Tab switch
            {
                "rules" or "dead code" or "problems" => map.Findings.Where(f => f.Kind == (view.Tab == "rules" ? FindingKind.Rule : view.Tab == "dead code" ? FindingKind.DeadCode : FindingKind.Risk))
                    .Select(f => $"{(f.Kind == FindingKind.Risk ? f.Severity.ToString().ToUpperInvariant() : f.Category.ToUpperInvariant()),-12} {f.Title}   {Report.Place(f)}").ToList(),
                _ => Inventory.Of(map).ToText().Split('\n').ToList(),
            };
            return new ViewPicture(map.Root.Length > 0 ? Path.GetFileName(map.Root) : "database", view.Tab, text, 1, 0, 0, false, []);
        }
        if (view.Tab is "trace" or "trace backward")
        {
            var forward = view.Tab == "trace";
            var t = forward ? Core.Trace.Forward(map, n.Id) : Core.Trace.Backward(map, n.Id);
            return new ViewPicture((forward ? "What " : "What reaches ") + Core.Trace.Describe(n) + (forward ? " leads to" : ""), $"{t.All().Count() - 1} steps", Core.Trace.ToText(t).TrimEnd().Split('\n'), 1, 0, 0, false, []);
        }
        string[] lines;
        try
        {
            lines = SourceLines(map, n, null, out _);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return new ViewPicture(Core.Trace.Describe(n), e.Message, [], 1, 0, 0, false, []);
        }
        var from = view.Line > 0 ? view.Line : Math.Max(1, n.Line);
        var to = view.Line > 0 ? view.Line : Math.Max(from, n.EndLine);
        var first = Math.Max(1, from - 4);
        var last = Math.Min(lines.Length, Math.Max(to + 4, first + 30));
        if (last - first > 60) last = first + 60;
        var shown = lines.Skip(first - 1).Take(last - first + 1).Select(l => Secrets.Mask(l.TrimEnd('\r'))).ToList();
        return new ViewPicture(Core.Trace.Describe(n), n.File != null ? $"{n.File}:{from}" + (to > from ? $"–{to}" : "") : "", shown, first, from, to, true, []);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>A scan's headline numbers.</summary>
    private JsonObject Summary(SystemMap map)
    {
        var inv = Inventory.Of(map);
        var est = Estimate.Of(map);
        return new JsonObject
        {
            ["scan"] = Shown(map),
            ["scanned_at"] = map.ScannedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["code_lines"] = inv.TotalCode,
            ["files"] = inv.Counts[NodeKind.File],
            ["projects"] = inv.Projects.Count,
            ["screens"] = inv.Counts[NodeKind.Form],
            ["types"] = inv.Counts[NodeKind.Type],
            ["methods"] = inv.Counts[NodeKind.Method],
            ["tables"] = inv.Counts[NodeKind.Table],
            ["procedures"] = inv.Counts[NodeKind.Procedure],
            ["rules"] = map.Findings.Count(f => f.Kind == FindingKind.Rule),
            ["dead_items"] = map.Findings.Count(f => f.Kind == FindingKind.DeadCode),
            ["risks"] = map.Findings.Count(f => f.Kind == FindingKind.Risk),
            ["high_risks"] = map.Findings.Count(f => f.Kind == FindingKind.Risk && f.Severity == Severity.High),
            ["estimate_person_days"] = $"{est.Low:0}–{est.High:0}",
            ["could_not_read"] = map.Notes.Count > 0 ? map.Notes.Count : null,
        };
    }

    private string Shown(SystemMap map) => map.Root.Length == 0 ? "a database" : InRoot(map.Root) || map.Root == _files.Root ? (map.Root == _files.Root ? "the working folder" : _files.Relative(map.Root)) : Path.GetFileName(map.Root);

    private string Shown(string full) => full == _files.Root ? "the working folder" : InRoot(full) ? _files.Relative(full) : Path.GetFileName(full);

    private bool InRoot(string full) => full.StartsWith(_files.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static NodeKind? ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        var k = kind.Trim().ToLowerInvariant() switch { "screen" or "form" => "Form", "class" or "module" => "Type", "proc" or "stored procedure" => "Procedure", var x => x };
        return Enum.TryParse<NodeKind>(k, true, out var parsed) ? parsed : throw new ArgumentException($"kind is project, file, form, control, type, method, procedure, view, function, trigger, table, column, setting, connection or external, not \"{kind}\".");
    }

    /// <summary>A node's name with what it's in: BillingRules.PostCharge, frmPatient.btnSave, Patient.LastName.</summary>
    internal static string Qualified(Node n) => n.Kind switch
    {
        NodeKind.Method => $"{n.Prop("type")}.{n.Name}",
        NodeKind.Control => $"{n.Prop("form")}.{n.Name}",
        NodeKind.Column => $"{n.Prop("table")}.{n.Name}",
        _ => n.Name,
    };

    /// <summary>An item by id or by name; an ambiguous name lists the candidates.</summary>
    internal static Node Resolve(SystemMap map, string item)
    {
        if (string.IsNullOrWhiteSpace(item)) throw new ArgumentException("Name an item: its id (from find) or its name.");
        var q = item.Trim();
        if (map[q] is { } exact) return exact;
        if (map["db:" + q.ToLowerInvariant()] is { } db) return db;
        if (map["db:dbo." + q.ToLowerInvariant()] is { } dbo) return dbo;
        var matches = map.Nodes.Where(n => Qualified(n).Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) matches = [.. map.Nodes.Where(n => n.Name.Equals(q, StringComparison.OrdinalIgnoreCase))];
        if (matches.Count == 0 && q.Contains('.'))
        {
            // dbo.Patient.LastName, ClinicApp.BillingRules.PostCharge: the last two parts.
            var parts = q.Split('.');
            var tail = $"{parts[^2]}.{parts[^1]}";
            matches = [.. map.Nodes.Where(n => Qualified(n).Equals(tail, StringComparison.OrdinalIgnoreCase))];
        }
        // A screen and its file share a name: prefer what's not a file; a method and its overloads: the first.
        if (matches.Count > 1) matches = [.. matches.Where(n => n.Kind != NodeKind.File).DefaultIfEmpty(matches[0])];
        if (matches.Count > 1 && matches.All(m => m.Kind == NodeKind.Method && Qualified(m) == Qualified(matches[0]))) matches = [matches.OrderBy(m => m.Line).First()];
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new KeyNotFoundException($"There's nothing called \"{item}\" in the scan. find looks for names."),
            _ => throw new ArgumentException($"\"{item}\" could be any of: {string.Join("; ", matches.Take(10).Select(m => $"{m.Kind.ToString().ToLowerInvariant()} {Qualified(m)} (id {m.Id})"))}. Give the id."),
        };
    }

    internal static JsonObject Brief(Node n)
    {
        var o = new JsonObject { ["id"] = n.Id, ["kind"] = n.Kind.ToString().ToLowerInvariant(), ["name"] = Qualified(n) };
        if (n.Prop("subkind") is { } sk && n.Kind != NodeKind.Project) o["subkind"] = sk;
        if (n.Language != null) o["language"] = n.Language;
        if (n.File != null) o["at"] = n.Line > 0 ? $"{n.File}:{n.Line}" + (n.EndLine > n.Line ? $"-{n.EndLine}" : "") : n.File;
        if (n.Prop("dead") == "true") o["dead"] = true;
        if (n.Prop("defined") == "false") o["defined"] = false;
        return o;
    }

    private static JsonObject Props(Node n)
    {
        var o = new JsonObject();
        foreach (var (k, v) in n.Props.OrderBy(p => p.Key, StringComparer.Ordinal))
            if (k is not ("sql" or "dead" or "defined" or "subkind")) o[k] = Secrets.Mask(v);
        return o;
    }

    private static JsonObject Link(SystemMap map, Edge e, bool outgoing)
    {
        var other = map[outgoing ? e.To : e.From];
        var o = new JsonObject
        {
            ["link"] = Core.Trace.Verb(e.Kind, outgoing),
            [outgoing ? "to" : "from"] = other == null ? (outgoing ? e.To : e.From) : Qualified(other),
            ["id"] = outgoing ? e.To : e.From,
            ["kind"] = other?.Kind.ToString().ToLowerInvariant(),
        };
        if (e.File != null && e.Line > 0) o["at"] = $"{e.File}:{e.Line}";
        if (e.Note is { Length: > 0 } note) o["code"] = note;
        if (e.Certainty != Certainty.Resolved) o["certainty"] = e.Certainty.ToString().ToLowerInvariant();
        return o;
    }

    internal static JsonObject FindingJson(SystemMap map, Finding f)
    {
        var o = new JsonObject
        {
            ["id"] = f.Id,
            ["kind"] = f.Kind switch { FindingKind.Rule => "rule", FindingKind.DeadCode => "dead code", _ => "risk" },
            ["category"] = f.Category,
            ["title"] = f.Title,
            ["detail"] = f.Detail,
            ["code"] = f.Code,
            ["at"] = Report.Place(f) is { Length: > 0 } p ? p : null,
            ["item"] = map[f.NodeId] is { } n ? Qualified(n) : f.NodeId,
            ["confidence"] = f.Confidence.ToString().ToLowerInvariant(),
        };
        if (f.Kind == FindingKind.Risk) o["severity"] = f.Severity.ToString().ToLowerInvariant();
        if (map.MarkOf(f) is { } m)
        {
            o["mark"] = m.State.ToString().ToLowerInvariant();
            if (m.Note != null) o["mark_note"] = m.Note;
        }
        return o;
    }

    private static JsonObject Findings(SystemMap map, FindingKind kind, string? category, string? mark, string? item, string? atLeast)
    {
        IEnumerable<Finding> list = map.Findings.Where(f => f.Kind == kind);
        if (!string.IsNullOrWhiteSpace(category)) list = list.Where(f => f.Category.Equals(category.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(mark))
        {
            var m = mark.Trim().ToLowerInvariant();
            if (m is "unmarked" or "none") list = list.Where(f => map.MarkOf(f) == null);
            else if (Enum.TryParse<MarkState>(m, true, out var state)) list = list.Where(f => map.MarkOf(f)?.State == state);
            else throw new ArgumentException("mark is confirmed, wrong, noise or unmarked.");
        }
        if (!string.IsNullOrWhiteSpace(item))
        {
            var under = Resolve(map, item);
            var ids = Inventory.Below(map, under.Id).Select(n => n.Id).Append(under.Id).ToHashSet(StringComparer.Ordinal);
            list = list.Where(f => ids.Contains(f.NodeId));
        }
        if (!string.IsNullOrWhiteSpace(atLeast))
        {
            if (!Enum.TryParse<Severity>(atLeast.Trim(), true, out var floor)) throw new ArgumentException("That's info, low, medium or high.");
            list = list.Where(f => (kind == FindingKind.Risk ? f.Severity : f.Confidence) >= floor);
        }
        var shown = list.OrderByDescending(f => kind == FindingKind.Risk ? f.Severity : f.Confidence).ThenBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line).ToList();
        return new JsonObject
        {
            ["count"] = shown.Count,
            ["marked"] = shown.Count(f => map.MarkOf(f) != null),
            ["findings"] = new JsonArray(shown.Take(MaxResults).Select(f => (JsonNode)FindingJson(map, f)).ToArray()),
        };
    }

    /// <summary>The lines of an item's file (or of <paramref name="file"/>), or a live database module's text.</summary>
    private string[] SourceLines(SystemMap map, Node? n, string? file, out string shown)
    {
        if (n != null && n.File == null && n.Prop("sql") is { } sql)
        {
            shown = $"{n.Name} (from the database)";
            return sql.Replace("\r\n", "\n").Split('\n');
        }
        var rel = file ?? n?.File ?? throw new ArgumentException(n == null ? "Give an item or a file." : $"{Qualified(n)} has no source.");
        if (map.Root.Length == 0) throw new IOException("This scan is of a database alone: there are no files.");
        var full = Path.GetFullPath(Path.Combine(map.Root, rel));
        if (!full.StartsWith(map.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException($"{rel} is outside the scanned folder.");
        if (!File.Exists(full)) throw new FileNotFoundException(Directory.Exists(map.Root) ? $"There's no file {rel} in the scanned folder." : $"The scanned folder ({map.Root}) isn't here, so its source can't be read.");
        shown = rel;
        return SourceFiles.ReadText(full).Replace("\r\n", "\n").Split('\n');
    }

    /// <summary>Scans the working folder if nothing is open yet, so every tool works from the first call.</summary>
    private async Task EnsureMap(CancellationToken cancel)
    {
        if (await Run("read the scan", s => s.Map != null, cancel).ConfigureAwait(false)) return;
        var databases = await Run("read the settings", s => s.Databases.ToList(), cancel).ConfigureAwait(false);
        var root = _files.Root;
        var map = await Task.Run(() => Scanner.Scan(root, new ScanOptions { Databases = databases }, cancel), cancel).ConfigureAwait(false);
        await Run("scanned the working folder", s =>
        {
            s.Map ??= map;
            return true;
        }, cancel).ConfigureAwait(false);
    }

    /// <summary>Runs a call that reads the map (on the window's thread) and answers in JSON.</summary>
    private async Task<string> OnMap(string action, Func<IExplorerSession, SystemMap, JsonObject> work, CancellationToken cancel)
    {
        try
        {
            await EnsureMap(cancel).ConfigureAwait(false);
            return await host.RunAsync(action, s => work(s, s.Map!).ToJsonString(Json.Options), cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    /// <summary>Runs a call that needs no map (listing files), off the window's thread.</summary>
    private async Task<string> Plain(string action, Func<JsonObject> work, CancellationToken cancel)
    {
        try
        {
            var result = await Task.Run(work, cancel).ConfigureAwait(false);
            await Run(action, _ => true, cancel).ConfigureAwait(false);
            return result.ToJsonString(Json.Options);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    /// <summary>Runs a call that writes one new file in the working folder from the map, and answers with where it went.</summary>
    private async Task<string> Write(string action, string output, string what, string[] extensions, Func<SystemMap, string, JsonObject> work, CancellationToken cancel)
    {
        try
        {
            await EnsureMap(cancel).ConfigureAwait(false);
            var full = _files.Resolve(output, what, extensions);
            _files.CheckWritable(full);
            var map = await Run("read the scan", s => s.Map!, cancel).ConfigureAwait(false);
            var result = await host.RunAsync(action, _ => work(map, full), cancel).ConfigureAwait(false);
            _files.Wrote(full);
            result["wrote"] = _files.Relative(full);
            return result.ToJsonString(Json.Options);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    private async Task<T> Run<T>(string action, Func<IExplorerSession, T> work, CancellationToken cancel)
    {
        try
        {
            return await host.RunAsync(action, work, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    internal static string Explain(Exception e) => e switch
    {
        ArgumentException or InvalidOperationException or FormatException or NotSupportedException or IOException
            or UnauthorizedAccessException or ExplorerBusyException or JsonException or KeyNotFoundException or InvalidDataException => e.Message,
        _ => $"Something went wrong ({e.GetType().Name}): {e.Message}",
    };
}
