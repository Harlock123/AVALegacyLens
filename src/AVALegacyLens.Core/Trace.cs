using AVALegacyLens.Core.Model;

namespace AVALegacyLens.Core;

/// <summary>
/// One step of a trace: a node, how it was reached from the step above (the link and a verb, "on Click", "calls",
/// "writes"), and the steps it leads on to. <see cref="Repeat"/> marks a node already on the path (a cycle) or already
/// shown elsewhere in the trace; <see cref="Stop"/> says why a step isn't followed further.
/// </summary>
public sealed class TraceStep
{
    public required Node Node { get; init; }
    public Edge? Edge { get; init; }
    public string Verb { get; init; } = "";
    public List<TraceStep> Next { get; } = [];
    public bool Repeat { get; init; }
    public string? Stop { get; set; }

    /// <summary>Every step below this one, depth first.</summary>
    public IEnumerable<TraceStep> All()
    {
        yield return this;
        foreach (var n in Next)
            foreach (var s in n.All()) yield return s;
    }

    public override string ToString() => $"{Verb} {Node.Name}".Trim();
}

/// <summary>
/// Follows the map from a starting point. Forward goes the way the program runs: a screen to its controls, a control
/// to the methods that handle its events, a method to what it calls, creates, runs and reads or writes (and, once SQL
/// is read, a procedure to its tables). Backward goes the other way: from a table, procedure or method up to the
/// screens and controls whose events reach it. A screen opened along the way is a stop (trace it on its own).
/// </summary>
public static class Trace
{
    public const int DefaultDepth = 10;
    public const int DefaultMaxSteps = 600;

    private static readonly EdgeKind[] Onward = [EdgeKind.Calls, EdgeKind.Creates, EdgeKind.Executes, EdgeKind.Reads, EdgeKind.Writes];

    public static TraceStep Forward(SystemMap map, string id, int depth = DefaultDepth, int maxSteps = DefaultMaxSteps) =>
        Build(map, id, depth, maxSteps, forward: true);

    public static TraceStep Backward(SystemMap map, string id, int depth = DefaultDepth, int maxSteps = DefaultMaxSteps) =>
        Build(map, id, depth, maxSteps, forward: false);

    private static TraceStep Build(SystemMap map, string id, int depth, int maxSteps, bool forward)
    {
        var start = map[id] ?? throw new KeyNotFoundException($"There's nothing with the id {id}.");
        var root = new TraceStep { Node = start };
        var shown = new HashSet<string>(StringComparer.Ordinal) { id };
        var count = 1;
        var queue = new Queue<(TraceStep Step, int Depth, HashSet<string> Path)>();
        queue.Enqueue((root, 0, [id]));
        // Breadth first, so the nearest steps are kept when the trace is cut short.
        while (queue.Count > 0)
        {
            var (step, d, path) = queue.Dequeue();
            var next = forward ? NextForward(map, step) : NextBackward(map, step.Node);
            if (next.Count == 0) continue;
            if (d >= depth)
            {
                step.Stop = $"deeper than {depth} steps";
                continue;
            }
            foreach (var (node, edge, verb) in next)
            {
                if (count >= maxSteps)
                {
                    step.Stop = $"trace cut at {maxSteps} steps";
                    break;
                }
                var onPath = path.Contains(node.Id);
                var repeat = onPath || !shown.Add(node.Id);
                var child = new TraceStep { Node = node, Edge = edge, Verb = verb, Repeat = repeat };
                if (onPath) child.Stop = "already on this path";
                else if (repeat) child.Stop = "shown above";
                step.Next.Add(child);
                count++;
                if (repeat) continue;
                if (forward && step.Node.Kind != NodeKind.Form && node.Kind == NodeKind.Form && edge?.Kind == EdgeKind.Creates)
                {
                    child.Stop = "opens another screen";
                    continue;
                }
                queue.Enqueue((child, d + 1, [.. path, node.Id]));
            }
        }
        return root;
    }

    private static List<(Node Node, Edge? Edge, string Verb)> NextForward(SystemMap map, TraceStep step)
    {
        var node = step.Node;
        var list = new List<(Node, Edge?, string)>();
        switch (node.Kind)
        {
            case NodeKind.Form:
                // Events of the form itself (Load), then each control with a handler, in tab order.
                foreach (var e in map.To(node.Id).Where(e => e.Kind == EdgeKind.Handles).OrderBy(e => e.Line))
                    if (map[e.From] is { } h) list.Add((h, e, $"on {e.Note}"));
                foreach (var c in Controls(map, node.Id).Where(c => map.To(c.Id).Any(e => e.Kind == EdgeKind.Handles)))
                    list.Add((c, null, "control"));
                break;
            case NodeKind.Control:
                foreach (var e in map.To(node.Id).Where(e => e.Kind == EdgeKind.Handles).OrderBy(e => e.Note, StringComparer.Ordinal))
                    if (map[e.From] is { } h) list.Add((h, e, $"on {e.Note}"));
                break;
            case NodeKind.Table:
                // Writing a table fires its triggers.
                if (step.Edge?.Kind == EdgeKind.Writes)
                    foreach (var e in map.To(node.Id).Where(e => e.Kind == EdgeKind.Handles))
                        if (map[e.From] is { } trigger && (e.Note ?? "").Contains(VerbOf(step.Edge), StringComparison.OrdinalIgnoreCase))
                            list.Add((trigger, e, "fires"));
                break;
            case NodeKind.Column or NodeKind.Setting or NodeKind.Connection or NodeKind.External:
                break;
            default:
                var edges = map.From(node.Id).Where(e => Onward.Contains(e.Kind) || e.Kind == EdgeKind.Uses && IsOutside(map[e.To])).OrderBy(e => e.Line).ToList();
                var tables = edges.Select(e => e.To).ToHashSet(StringComparer.Ordinal);
                foreach (var e in edges)
                {
                    if (map[e.To] is not { } to) continue;
                    // Columns are detail: shown only when their table isn't a step already.
                    if (to.Kind == NodeKind.Column && to.Parent != null && tables.Contains(to.Parent)) continue;
                    list.Add((to, e, Verb(e.Kind, forward: true)));
                }
                break;
        }
        // One step per node: a method called on three lines is one step (its first call).
        return [.. list.GroupBy(x => x.Item1.Id).Select(g => g.First())];
    }

    private static List<(Node Node, Edge? Edge, string Verb)> NextBackward(SystemMap map, Node node)
    {
        var list = new List<(Node, Edge?, string)>();
        switch (node.Kind)
        {
            case NodeKind.Control:
                if (node.Parent != null && map[node.Parent] is { } holder) list.Add((holder, null, "on screen"));
                break;
            case NodeKind.Form:
                foreach (var e in map.To(node.Id).Where(e => e.Kind == EdgeKind.Creates))
                    if (map[e.From] is { } opener) list.Add((opener, e, "opened by"));
                break;
            default:
                // A handler is reached through the control (or form) whose event it handles.
                foreach (var e in map.From(node.Id).Where(e => e.Kind == EdgeKind.Handles))
                    if (map[e.To] is { } c) list.Add((c, e, $"when {e.Note}"));
                foreach (var e in map.To(node.Id).Where(e => Onward.Contains(e.Kind) || e.Kind == EdgeKind.Uses && IsOutside(node)).OrderBy(e => e.File, StringComparer.Ordinal).ThenBy(e => e.Line))
                    if (map[e.From] is { } from) list.Add((from, e, Verb(e.Kind, forward: false)));
                break;
        }
        return [.. list.GroupBy(x => x.Item1.Id).Select(g => g.First())];
    }

    /// <summary>INSERT, UPDATE or DELETE from a write's note ("UPDATE: UPDATE dbo.Patient…"); a trigger on any fires for a MERGE.</summary>
    private static string VerbOf(Edge write)
    {
        var verb = (write.Note ?? "").Split(':')[0].Trim();
        return verb is "INSERT" or "UPDATE" or "DELETE" ? verb : "";
    }

    /// <summary>The controls of a screen, nested ones included, in tab order then position.</summary>
    public static IEnumerable<Node> Controls(SystemMap map, string formId)
    {
        var all = new List<Node>();
        var stack = new Stack<string>([formId]);
        while (stack.Count > 0)
            foreach (var c in map.Children(stack.Pop()).Where(c => c.Kind == NodeKind.Control))
            {
                all.Add(c);
                stack.Push(c.Id);
            }
        return all.OrderBy(c => int.TryParse(c.Prop("TabIndex"), out var t) ? t : int.MaxValue).ThenBy(c => c.Line);
    }

    private static bool IsOutside(Node? n) => n?.Kind is NodeKind.Setting or NodeKind.Connection or NodeKind.External;

    public static string Verb(EdgeKind kind, bool forward) => (kind, forward) switch
    {
        (EdgeKind.Calls, true) => "calls",
        (EdgeKind.Calls, false) => "called by",
        (EdgeKind.Creates, true) => "creates",
        (EdgeKind.Creates, false) => "created by",
        (EdgeKind.Executes, true) => "runs",
        (EdgeKind.Executes, false) => "run by",
        (EdgeKind.Reads, true) => "reads",
        (EdgeKind.Reads, false) => "read by",
        (EdgeKind.Writes, true) => "writes",
        (EdgeKind.Writes, false) => "written by",
        (EdgeKind.Uses, true) => "uses",
        (EdgeKind.Uses, false) => "used by",
        (EdgeKind.Handles, true) => "handles",
        (EdgeKind.Handles, false) => "handled by",
        (EdgeKind.Inherits, true) => "inherits",
        (EdgeKind.Inherits, false) => "inherited by",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>A trace as indented text, one step a line, with where each link is: for agents and for copying.</summary>
    public static string ToText(TraceStep root)
    {
        var sb = new System.Text.StringBuilder();
        void Write(TraceStep s, int indent)
        {
            sb.Append(new string(' ', indent * 2));
            if (s.Verb.Length > 0) sb.Append(s.Verb).Append(' ');
            sb.Append(Describe(s.Node));
            if (s.Edge is { Line: > 0 } e) sb.Append("  (").Append(e.File).Append(':').Append(e.Line).Append(')');
            if (s.Edge is { Certainty: not Certainty.Resolved } c) sb.Append(c.Certainty == Certainty.ByName ? " [by name]" : " [guessed]");
            if (s.Stop != null) sb.Append("  — ").Append(s.Stop);
            sb.AppendLine();
            foreach (var n in s.Next) Write(n, indent + 1);
        }
        Write(root, 0);
        return sb.ToString();
    }

    /// <summary>A node as "kind Name", with its type for methods: "method BillingRules.PostCharge".</summary>
    public static string Describe(Node n) => n.Kind switch
    {
        NodeKind.Method => $"method {n.Prop("type")}.{n.Name}",
        NodeKind.Control => $"{n.Prop("type") ?? "control"} {n.Name}" + (n.Prop("Text") is { Length: > 0 } t ? $" \"{t.Replace("&", "", StringComparison.Ordinal)}\"" : ""),
        NodeKind.Form => $"screen {n.Name}" + (n.Prop("title") is { Length: > 0 } title ? $" \"{title}\"" : ""),
        NodeKind.Column => $"column {n.Prop("table")}.{n.Name}",
        _ => $"{n.Kind.ToString().ToLowerInvariant()} {n.Name}",
    };
}
