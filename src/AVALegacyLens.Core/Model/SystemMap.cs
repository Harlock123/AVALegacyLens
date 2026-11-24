using System.Text.Json.Serialization;

namespace AVALegacyLens.Core.Model;

/// <summary>What a node in the system map is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NodeKind>))]
public enum NodeKind
{
    Project,
    File,
    /// <summary>A screen: a WinForms form or user control, or a VB6 form.</summary>
    Form,
    /// <summary>A control on a screen.</summary>
    Control,
    /// <summary>A class, module, structure or interface.</summary>
    Type,
    /// <summary>A method, constructor, property or event handler.</summary>
    Method,
    Procedure,
    View,
    Function,
    Trigger,
    Table,
    Column,
    /// <summary>A setting in a config file (app.config, web.config).</summary>
    Setting,
    /// <summary>A connection string from a config file (its password masked).</summary>
    Connection,
    /// <summary>Something outside the system: a file share, a web address, a COM object, a mail server.</summary>
    External,
}

/// <summary>How one node depends on another.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EdgeKind>))]
public enum EdgeKind
{
    /// <summary>A project holds a file, a file a type, a type a method, a form a control, a table a column.</summary>
    Contains,
    Calls,
    /// <summary>A method handles a control's (or the form's) event.</summary>
    Handles,
    /// <summary>Code creates an instance of a type (opening a form, for one).</summary>
    Creates,
    Inherits,
    /// <summary>Code or SQL reads a table, view or column.</summary>
    Reads,
    /// <summary>Code or SQL inserts, updates or deletes rows of a table (or a column).</summary>
    Writes,
    /// <summary>Code or SQL runs a stored procedure or function.</summary>
    Executes,
    /// <summary>A plain use of something by name: a setting, a constant, a project reference.</summary>
    Uses,
}

/// <summary>How sure the scan is of an edge.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Certainty>))]
public enum Certainty
{
    /// <summary>The compiler resolved it.</summary>
    Resolved,
    /// <summary>Matched by name only (across projects or languages, or where the compiler couldn't resolve it).</summary>
    ByName,
    /// <summary>A guess: dynamic SQL, late binding, reflection, a string built at run time.</summary>
    Guessed,
}

/// <summary>
/// A thing in the legacy system. <see cref="Id"/> is stable across scans of the same code (a kind prefix and a
/// qualified name, e.g. <c>M:ClinicApp.BillingRules.PostCharge(System.Int32,…)</c> or <c>table:dbo.Patient</c>), so
/// marks and notes kept in a .lens file survive a rescan. <see cref="File"/> is relative to the scanned folder, with
/// forward slashes; lines count from 1.
/// </summary>
public sealed class Node
{
    public required string Id { get; init; }
    public required NodeKind Kind { get; init; }
    public required string Name { get; init; }
    /// <summary>The containing node (project for a file, type for a method, form for a control, table for a column).</summary>
    public string? Parent { get; set; }
    public string? File { get; set; }
    public int Line { get; set; }
    public int EndLine { get; set; }
    /// <summary>Language for files and code ("VB.NET", "C#", "T-SQL", "VB6", "Config").</summary>
    public string? Language { get; set; }
    /// <summary>Anything else worth knowing, by name: "subkind", "signature", "lines", "complexity", "text", "type"…</summary>
    public Dictionary<string, string> Props { get; init; } = [];

    public string? Prop(string name) => Props.TryGetValue(name, out var v) ? v : null;

    public int IntProp(string name) => int.TryParse(Prop(name), out var v) ? v : 0;

    public override string ToString() => $"{Kind} {Name}";
}

/// <summary>A dependency: <see cref="From"/> calls, reads, writes… <see cref="To"/>, at a place in the code.</summary>
public sealed class Edge
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required EdgeKind Kind { get; init; }
    public Certainty Certainty { get; init; } = Certainty.Resolved;
    public string? File { get; init; }
    public int Line { get; init; }
    /// <summary>What was seen there, briefly: the call's text, the SQL statement's first line.</summary>
    public string? Note { get; init; }

    public override string ToString() => $"{From} {Kind} {To}";
}

/// <summary>Something the scan couldn't do: a file it couldn't read or parse, and why. Not a finding about the code.</summary>
public sealed record ScanNote(string File, int Line, string Message);

/// <summary>
/// Everything one scan found in a legacy system: nodes (projects, files, screens, types, methods, tables…), the edges
/// between them, and notes about what couldn't be read. Built by <see cref="Scanning.Scanner"/>, saved as a .lens file
/// by <see cref="LensFile"/>. Lookups are by id; the indexes are rebuilt when a map is loaded.
/// </summary>
public sealed class SystemMap
{
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly List<Edge> _edges = [];
    private readonly HashSet<(string, string, EdgeKind, int)> _edgeKeys = [];
    private Dictionary<string, List<Edge>>? _out, _in;
    private Dictionary<string, List<Node>>? _children;

    /// <summary>The group node outside systems sit under (file shares, web addresses, native libraries).</summary>
    public const string OutsideGroup = "outside";

    /// <summary>The group for outside systems, made the first time something needs it.</summary>
    public string Outside()
    {
        if (!_nodes.ContainsKey(OutsideGroup))
            Add(new Node { Id = OutsideGroup, Kind = NodeKind.Project, Name = "Outside the system", Props = { ["subkind"] = "outside" } });
        return OutsideGroup;
    }

    /// <summary>The folder that was scanned (absolute); file paths in the map are relative to it.</summary>
    public string Root { get; set; } = "";
    public DateTimeOffset ScannedAt { get; set; } = DateTimeOffset.Now;
    public List<ScanNote> Notes { get; init; } = [];

    public IReadOnlyCollection<Node> Nodes => _nodes.Values;
    public IReadOnlyList<Edge> Edges => _edges;

    public Node? this[string id] => _nodes.GetValueOrDefault(id);

    public bool Contains(string id) => _nodes.ContainsKey(id);

    /// <summary>Adds a node, or returns the one already there with that id (the first one wins).</summary>
    public Node Add(Node node)
    {
        if (_nodes.TryGetValue(node.Id, out var existing)) return existing;
        _nodes[node.Id] = node;
        _children = null;
        return node;
    }

    /// <summary>Puts a node in place of the one with its id (a stand-in replaced by the real definition), keeping links.</summary>
    public void Replace(Node node)
    {
        _nodes[node.Id] = node;
        _children = null;
    }

    /// <summary>Removes a node that nothing links to and nothing sits under (an empty group).</summary>
    public bool RemoveIfEmpty(string id)
    {
        if (!_nodes.ContainsKey(id) || Children(id).Count > 0 || From(id).Count > 0 || To(id).Count > 0) return false;
        _nodes.Remove(id);
        _children = null;
        return true;
    }

    /// <summary>Adds an edge, once per (from, to, kind, line).</summary>
    public void Link(Edge edge)
    {
        if (edge.From == edge.To && edge.Kind != EdgeKind.Calls) return;
        if (!_edgeKeys.Add((edge.From, edge.To, edge.Kind, edge.Line))) return;
        _edges.Add(edge);
        _out = _in = null;
    }

    public void Link(string from, string to, EdgeKind kind, string? file = null, int line = 0, Certainty certainty = Certainty.Resolved, string? note = null) =>
        Link(new Edge { From = from, To = to, Kind = kind, File = file, Line = line, Certainty = certainty, Note = note });

    /// <summary>The edges going out of a node (what it calls, reads, writes…).</summary>
    public IReadOnlyList<Edge> From(string id)
    {
        Index();
        return _out!.TryGetValue(id, out var list) ? list : [];
    }

    /// <summary>The edges coming into a node (what calls, reads, writes… it).</summary>
    public IReadOnlyList<Edge> To(string id)
    {
        Index();
        return _in!.TryGetValue(id, out var list) ? list : [];
    }

    /// <summary>The nodes whose parent is this one, in file and line order.</summary>
    public IReadOnlyList<Node> Children(string? id)
    {
        if (_children == null)
        {
            _children = new(StringComparer.Ordinal);
            foreach (var n in _nodes.Values)
            {
                var key = n.Parent ?? "";
                if (!_children.TryGetValue(key, out var list)) _children[key] = list = [];
                list.Add(n);
            }
            foreach (var list in _children.Values)
                list.Sort((a, b) =>
                {
                    var c = string.CompareOrdinal(a.File, b.File);
                    if (c != 0) return c;
                    c = a.Line.CompareTo(b.Line);
                    return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
        }
        return _children.TryGetValue(id ?? "", out var found) ? found : [];
    }

    /// <summary>Drops edges whose ends aren't nodes (a reference to something no reader made a node for). Returns how many.</summary>
    public int RemoveDangling()
    {
        var removed = _edges.RemoveAll(e => !_nodes.ContainsKey(e.From) || !_nodes.ContainsKey(e.To));
        if (removed > 0)
        {
            _edgeKeys.Clear();
            foreach (var e in _edges) _edgeKeys.Add((e.From, e.To, e.Kind, e.Line));
            _out = _in = null;
        }
        return removed;
    }

    public IEnumerable<Node> OfKind(NodeKind kind) => _nodes.Values.Where(n => n.Kind == kind);

    /// <summary>The chain of parents from the top down to this node, the node included.</summary>
    public IReadOnlyList<Node> PathTo(string id)
    {
        var path = new List<Node>();
        for (var n = this[id]; n != null && path.Count < 32; n = n.Parent == null ? null : this[n.Parent]) path.Add(n);
        path.Reverse();
        return path;
    }

    private void Index()
    {
        if (_out != null) return;
        _out = new(StringComparer.Ordinal);
        _in = new(StringComparer.Ordinal);
        foreach (var e in _edges)
        {
            if (!_out.TryGetValue(e.From, out var o)) _out[e.From] = o = [];
            o.Add(e);
            if (!_in!.TryGetValue(e.To, out var i)) _in[e.To] = i = [];
            i.Add(e);
        }
    }
}
