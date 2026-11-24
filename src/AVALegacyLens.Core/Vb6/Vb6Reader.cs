using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AVALegacyLens.Core.Config;
using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Scanning;
using AVALegacyLens.Core.Sql;

namespace AVALegacyLens.Core.Vb6;

/// <summary>
/// Reads Visual Basic 6: projects (.vbp) and their forms (.frm), modules (.bas), classes (.cls) and user controls
/// (.ctl). A form's controls come from its header (the <c>Begin VB.TextBox … End</c> blocks), with positions turned
/// from twips into pixels. Code is read shallowly, line by line: procedures, their size and complexity, event
/// handlers (VB6 binds <c>cmdSave_Click</c> to cmdSave by name), calls and object creation matched by name across the
/// project (<see cref="Certainty.ByName"/>: nothing here compiles VB6), the controls code touches, SQL in strings
/// (read by <see cref="SqlReader"/>), hard-coded connection strings and shares, and risks such as
/// <c>On Error Resume Next</c>.
/// </summary>
public sealed partial class Vb6Reader(SystemMap map, string root, SqlReader sql, ConfigReader config)
{
    private sealed class Module
    {
        public required string Key { get; init; }
        public required string VbName { get; init; }
        public required string Kind { get; init; }     // form, module, class, user control
        public required string File { get; init; }
        public required Node Node { get; init; }
        public required List<(int Line, string Text)> Code { get; init; }
        public Dictionary<string, Node> Controls { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Proc> Procs { get; } = [];
        public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record Proc(Node Node, string Name, string Kind, int Start, int End, List<(int Line, string Text)> Body);

    private readonly List<Module> _modules = [];

    public const int TwipsPerPixel = 15;

    public void Read(IReadOnlyList<string> files, CancellationToken cancel)
    {
        // Projects first: which files belong to which, and each project's key.
        var owner = new Dictionary<string, (string Key, string ProjectId)>(StringComparer.Ordinal);
        foreach (var vbp in files.Where(f => f.EndsWith(".vbp", StringComparison.OrdinalIgnoreCase)))
        {
            cancel.ThrowIfCancellationRequested();
            ReadProject(vbp, owner);
        }
        var loose = files.Where(f => IsMember(f) && !owner.ContainsKey(f)).ToList();
        if (loose.Count > 0)
        {
            var id = "project:(loose VB6 files)";
            map.Add(new Node { Id = id, Kind = NodeKind.Project, Name = "Loose VB6 files", Language = "VB6", Props = { ["key"] = "VB6", ["loose"] = "true" } });
            foreach (var f in loose) owner[f] = ("VB6", id);
        }

        // Every module's declarations, then every body: calls resolve against the whole project.
        foreach (var f in files.Where(IsMember))
        {
            cancel.ThrowIfCancellationRequested();
            var (key, projectId) = owner[f];
            var rel = SourceFiles.Relative(root, f);
            if (map["file:" + rel] is { } fileNode) fileNode.Parent = projectId;
            try
            {
                ReadModule(f, rel, key);
            }
            catch (IOException e)
            {
                map.Notes.Add(new ScanNote(rel, 0, "Couldn't read: " + e.Message));
            }
        }
        foreach (var m in _modules)
            foreach (var p in m.Procs)
            {
                cancel.ThrowIfCancellationRequested();
                ReadBody(m, p);
            }
    }

    private static bool IsMember(string f) =>
        Path.GetExtension(f).ToLowerInvariant() is ".frm" or ".bas" or ".cls" or ".ctl";

    private void ReadProject(string vbp, Dictionary<string, (string Key, string ProjectId)> owner)
    {
        var rel = SourceFiles.Relative(root, vbp);
        var dir = Path.GetDirectoryName(vbp)!;
        var lines = SourceFiles.ReadText(vbp).Replace("\r\n", "\n").Split('\n');
        string? Value(string key) => lines.Select(l => l.Split('=', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)).Select(p => p[1].Trim().Trim('"')).FirstOrDefault();
        var name = Value("Name") ?? Path.GetFileNameWithoutExtension(vbp);
        var id = "project:" + rel;
        var node = map.Add(new Node { Id = id, Kind = NodeKind.Project, Name = name, File = rel, Language = "VB6" });
        node.Props["key"] = name;
        if (Value("Type") is { } type) node.Props["outputType"] = type;
        if (Value("Startup") is { } startup) node.Props["startup"] = startup;
        var references = lines.Where(l => l.StartsWith("Reference=", StringComparison.OrdinalIgnoreCase)).Select(l => l.Split('#').Last().Trim()).ToList();
        if (references.Count > 0) node.Props["references"] = string.Join(", ", references);
        var objects = lines.Where(l => l.StartsWith("Object=", StringComparison.OrdinalIgnoreCase)).Select(l => l.Split(';').Last().Trim()).ToList();
        if (objects.Count > 0) node.Props["controls (OCX)"] = string.Join(", ", objects);
        if (map["file:" + rel] is { } fileNode) fileNode.Parent = id;
        foreach (var l in lines)
        {
            var parts = l.Split('=', 2);
            if (parts.Length != 2 || parts[0].Trim() is not ("Form" or "Module" or "Class" or "UserControl" or "Designer")) continue;
            var path = parts[1].Split(';').Last().Trim().Replace('\\', '/');
            var full = Path.GetFullPath(Path.Combine(dir, path));
            if (File.Exists(full)) owner.TryAdd(full, (name, id));
        }
    }

    [GeneratedRegex(@"^\s*Begin\s+(\S+)\s+(\w+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BeginLine();

    [GeneratedRegex(@"^\s*(\w+)\s*=\s*(.*)$")]
    private static partial Regex PropertyLine();

    private void ReadModule(string path, string rel, string key)
    {
        var lines = SourceFiles.ReadText(path).Replace("\r\n", "\n").Split('\n');
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var kind = ext switch { ".frm" => "form", ".ctl" => "user control", ".cls" => "class", _ => "module" };
        var vbName = lines.Select(l => Regex.Match(l, @"^Attribute\s+VB_Name\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)).FirstOrDefault(m => m.Success)?.Groups[1].Value
                     ?? Path.GetFileNameWithoutExtension(path);
        var typeId = $"{key}:T:{vbName}";
        var node = map.Add(new Node
        {
            Id = typeId,
            Kind = kind is "form" or "user control" ? NodeKind.Form : NodeKind.Type,
            Name = vbName,
            Parent = "file:" + rel,
            File = rel,
            Line = 1,
            EndLine = lines.Length,
            Language = "VB6",
        });
        node.Props["subkind"] = kind;
        node.Props["project"] = key;

        // The header: a form's controls (VERSION … Begin VB.Form … End), or a class's BEGIN … END block.
        var codeStart = 0;
        var module = new Module { Key = key, VbName = vbName, Kind = kind, File = rel, Node = node, Code = [] };
        if (kind is "form" or "user control")
            codeStart = ReadControls(lines, module);
        else if (kind == "class")
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Trim().Equals("END", StringComparison.OrdinalIgnoreCase))
                {
                    codeStart = i + 1;
                    break;
                }

        // The code, as logical lines (continuations joined, comments dropped), each with its first physical line.
        var logical = new StringBuilder();
        var first = 0;
        for (var i = codeStart; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (logical.Length == 0)
            {
                if (raw.TrimStart().StartsWith("Attribute ", StringComparison.OrdinalIgnoreCase)) continue;
                first = i + 1;
            }
            var text = StripComment(raw);
            if (text.TrimEnd().EndsWith(" _", StringComparison.Ordinal))
            {
                logical.Append(text.TrimEnd()[..^1]).Append(' ');
                continue;
            }
            logical.Append(text);
            var done = logical.ToString().Trim();
            logical.Clear();
            if (done.Length > 0)
                foreach (var statement in SplitStatements(done)) module.Code.Add((first, statement));
        }
        ReadDeclarations(module);
        _modules.Add(module);
    }

    /// <summary>Reads a form's Begin/End blocks into controls; returns the line the code starts after.</summary>
    private int ReadControls(string[] lines, Module module)
    {
        var stack = new Stack<(string Id, Dictionary<string, string> Props, string Type, string Name, int Line)>();
        var inProperty = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("BeginProperty", StringComparison.OrdinalIgnoreCase)) { inProperty++; continue; }
            if (t.StartsWith("EndProperty", StringComparison.OrdinalIgnoreCase)) { inProperty--; continue; }
            if (inProperty > 0) continue;
            if (BeginLine().Match(lines[i]) is { Success: true } b)
            {
                var type = b.Groups[1].Value;
                type = type.Contains('.') ? type[(type.LastIndexOf('.') + 1)..] : type;
                var name = b.Groups[2].Value;
                var id = stack.Count == 0 ? module.Node.Id : WinFormsReader.ControlId(module.Node.Id, name);
                stack.Push((id, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), type, name, i + 1));
                continue;
            }
            if (t.Equals("End", StringComparison.OrdinalIgnoreCase) && stack.Count > 0)
            {
                var (id, props, type, name, line) = stack.Pop();
                if (stack.Count == 0)
                {
                    // The form itself.
                    if (props.TryGetValue("Caption", out var caption)) module.Node.Props["title"] = caption;
                    if (Px(props, "ClientWidth") is { } w && Px(props, "ClientHeight") is { } h) module.Node.Props["ClientSize"] = $"{w}, {h}";
                    module.Node.Props["controls"] = module.Controls.Count.ToString(CultureInfo.InvariantCulture);
                    return i + 1;
                }
                AddControl(module, id, stack.Peek().Id, type, name, line, props);
                continue;
            }
            if (stack.Count > 0 && PropertyLine().Match(t) is { Success: true } p)
                stack.Peek().Props[p.Groups[1].Value] = PropertyValue(p.Groups[2].Value);
        }
        return 0;
    }

    private void AddControl(Module module, string id, string parentId, string type, string name, int line, Dictionary<string, string> props)
    {
        if (module.Controls.TryGetValue(name, out var existing))
        {
            // A control array: one node, its members' indexes and captions added.
            existing.Props["array"] = "true";
            if (props.TryGetValue("Index", out var index)) existing.Props["Index"] = existing.Prop("Index") is { } i ? $"{i}, {index}" : index;
            if (props.TryGetValue("Caption", out var c)) existing.Props["Text"] = existing.Prop("Text") is { } t ? $"{t} | {c}" : c;
            return;
        }
        var node = map.Add(new Node { Id = id, Kind = NodeKind.Control, Name = name, Parent = parentId, File = module.File, Line = line, EndLine = line, Language = "VB6" });
        node.Props["type"] = type;
        node.Props["form"] = module.VbName;
        if (props.TryGetValue("Caption", out var caption) || props.TryGetValue("Text", out caption)) node.Props["Text"] = caption;
        if (Px(props, "Left") is { } left && Px(props, "Top") is { } top) node.Props["Location"] = $"{left}, {top}";
        if (Px(props, "Width") is { } width && Px(props, "Height") is { } height) node.Props["Size"] = $"{width}, {height}";
        foreach (var k in new[] { "TabIndex", "MaxLength", "Index", "Enabled", "Visible", "Locked", "PasswordChar", "DataField", "DataSource", "Default", "Cancel" })
            if (props.TryGetValue(k, out var v)) node.Props[k] = v == "-1" ? "True" : v;
        if (props.TryGetValue("Index", out _)) node.Props["array"] = "true";
        module.Controls[name] = node;
    }

    private static int? Px(Dictionary<string, string> props, string key) =>
        props.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var twips) ? (int)Math.Round(twips / TwipsPerPixel) : null;

    /// <summary>A header property's value: quotes and the trailing comment VB6 writes ('True, 'Dropdown List) dropped.</summary>
    private static string PropertyValue(string raw)
    {
        var v = raw.Trim();
        if (v.StartsWith('"'))
        {
            var end = v.IndexOf('"', 1);
            while (end > 0 && end + 1 < v.Length && v[end + 1] == '"') end = v.IndexOf('"', end + 2);
            return end > 0 ? v[1..end].Replace("\"\"", "\"") : v.Trim('"');
        }
        var quote = v.IndexOf('\'');
        return (quote >= 0 ? v[..quote] : v).Trim();
    }

    /// <summary>A line without its comment (' or Rem), keeping apostrophes inside strings.</summary>
    private static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inString = !inString;
            else if (!inString && line[i] == '\'') return line[..i];
        }
        var t = line.TrimStart();
        return t.StartsWith("Rem ", StringComparison.OrdinalIgnoreCase) || t.Equals("Rem", StringComparison.OrdinalIgnoreCase) ? "" : line;
    }

    /// <summary>Splits a logical line at statement colons (<c>a = 1: b = 2</c>), not inside strings or after a label.</summary>
    private static IEnumerable<string> SplitStatements(string line)
    {
        var inString = false;
        var start = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inString = !inString;
            else if (!inString && line[i] == ':' && i + 1 < line.Length && line[i + 1] == ' ' && !Regex.IsMatch(line[..i], @"^\w+$"))
            {
                yield return line[start..i].Trim();
                start = i + 1;
            }
        }
        var rest = line[start..].Trim();
        if (rest.Length > 0) yield return rest;
    }

    [GeneratedRegex(@"^(?:(Public|Private|Friend|Global)\s+)?(?:Static\s+)?(Sub|Function|Property\s+Get|Property\s+Let|Property\s+Set)\s+(\w+)\s*(?:\((.*?)\))?", RegexOptions.IgnoreCase)]
    private static partial Regex ProcStart();

    [GeneratedRegex(@"^End\s+(Sub|Function|Property)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProcEnd();

    [GeneratedRegex(@"^(?:(Public|Private)\s+)?Declare\s+(?:PtrSafe\s+)?(Function|Sub)\s+(\w+)\s+Lib\s+""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex DeclareLine();

    [GeneratedRegex(@"^(Public|Private|Global|Dim|Static)\s+(?:WithEvents\s+)?(\w+)(?:\([^)]*\))?\s+As\s+(?:New\s+)?([\w.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VariableLine();

    private void ReadDeclarations(Module m)
    {
        var globals = 0;
        for (var i = 0; i < m.Code.Count; i++)
        {
            var (line, text) = m.Code[i];
            if (DeclareLine().Match(text) is { Success: true } d)
            {
                var id = $"{m.Key}:M:{m.VbName}.{d.Groups[3].Value}";
                var node = map.Add(new Node { Id = id, Kind = NodeKind.Method, Name = d.Groups[3].Value, Parent = m.Node.Id, File = m.File, Line = line, EndLine = line, Language = "VB6" });
                node.Props["type"] = m.VbName;
                node.Props["project"] = m.Key;
                node.Props["subkind"] = "declare";
                node.Props["access"] = d.Groups[1].Success ? d.Groups[1].Value : "Public";
                node.Props["signature"] = text;
                var lib = d.Groups[4].Value;
                var ext = map.Add(new Node { Id = "external:dll:" + lib.ToLowerInvariant(), Kind = NodeKind.External, Name = lib, Parent = map.Outside(), Props = { ["subkind"] = "native library" } });
                map.Link(id, ext.Id, EdgeKind.Uses, m.File, line, Certainty.Resolved, text);
                continue;
            }
            if (ProcStart().Match(text) is { Success: true } p)
            {
                var end = i + 1;
                while (end < m.Code.Count && !ProcEnd().IsMatch(m.Code[end].Text)) end++;
                var kind = Regex.Replace(p.Groups[2].Value, @"\s+", " ").ToLowerInvariant();
                var name = p.Groups[3].Value;
                var suffix = kind.StartsWith("property ", StringComparison.Ordinal) ? "." + kind[9..] : "";
                var id = $"{m.Key}:M:{m.VbName}.{name}{suffix}";
                var endLine = end < m.Code.Count ? m.Code[end].Line : m.Code[^1].Line;
                var node = map.Add(new Node { Id = id, Kind = NodeKind.Method, Name = name, Parent = m.Node.Id, File = m.File, Line = line, EndLine = endLine, Language = "VB6" });
                var body = m.Code.Skip(i + 1).Take(Math.Max(0, end - i - 1)).ToList();
                node.Props["type"] = m.VbName;
                node.Props["project"] = m.Key;
                node.Props["subkind"] = kind == "sub" ? "method" : kind;
                node.Props["access"] = p.Groups[1].Success ? p.Groups[1].Value : "Public";
                node.Props["signature"] = text;
                node.Props["parameters"] = (p.Groups[4].Value.Trim().Length == 0 ? 0 : p.Groups[4].Value.Split(',').Length).ToString(CultureInfo.InvariantCulture);
                node.Props["lines"] = (endLine - line + 1).ToString(CultureInfo.InvariantCulture);
                node.Props["complexity"] = Complexity(body).ToString(CultureInfo.InvariantCulture);
                if (m.Kind == "module") node.Props["shared"] = "true";
                m.Procs.Add(new Proc(node, name, kind, line, endLine, body));
                i = end;
                continue;
            }
            if (VariableLine().Match(text) is { Success: true } v)
            {
                m.Variables[v.Groups[2].Value] = v.Groups[3].Value;
                if (m.Kind == "module" && v.Groups[1].Value.ToLowerInvariant() is "public" or "global") globals++;
            }
        }
        if (globals > 0) m.Node.Props["globals"] = globals.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Cyclomatic complexity of a VB6 body: one, plus If/ElseIf, each Case (not Case Else), loops, And/Or in conditions.</summary>
    private static int Complexity(List<(int Line, string Text)> body)
    {
        var n = 1;
        foreach (var (_, t) in body)
        {
            var lower = t.ToLowerInvariant();
            var isCondition = lower.StartsWith("if ", StringComparison.Ordinal) || lower.StartsWith("elseif ", StringComparison.Ordinal) || lower.StartsWith("while ", StringComparison.Ordinal)
                || lower.StartsWith("do while", StringComparison.Ordinal) || lower.StartsWith("do until", StringComparison.Ordinal) || lower.StartsWith("loop while", StringComparison.Ordinal) || lower.StartsWith("loop until", StringComparison.Ordinal);
            if (isCondition || lower.StartsWith("for ", StringComparison.Ordinal) || lower.StartsWith("case ", StringComparison.Ordinal) && !lower.StartsWith("case else", StringComparison.Ordinal)) n++;
            if (isCondition) n += Regex.Matches(StripStrings(lower), @"\b(and|or|andalso|orelse)\b").Count;
        }
        return n;
    }

    private static string StripStrings(string text) => Regex.Replace(text, "\"(?:[^\"]|\"\")*\"", "\"\"");

    [GeneratedRegex(@"""(?:[^""]|"""")*""|[A-Za-z_]\w*[$%&!#@]?|\d+(?:\.\d+)?|&|\+|\(|\)|\.|=|,|\S")]
    private static partial Regex Token();

    private void ReadBody(Module m, Proc p)
    {
        var from = p.Node.Id;
        // Handlers bind by name: cmdSave_Click handles cmdSave's Click; Form_Load the form's Load.
        var underscore = p.Name.LastIndexOf('_');
        if (p.Kind == "sub" && underscore > 0)
        {
            var target = p.Name[..underscore];
            var evt = p.Name[(underscore + 1)..];
            if (m.Controls.TryGetValue(target, out var control))
                map.Link(from, control.Id, EdgeKind.Handles, m.File, p.Start, Certainty.Resolved, evt);
            else if (target is "Form" or "UserControl" or "Class" or "MDIForm")
                map.Link(from, m.Node.Id, EdgeKind.Handles, m.File, p.Start, Certainty.Resolved, evt);
        }

        var locals = new Dictionary<string, string>(m.Variables, StringComparer.OrdinalIgnoreCase);
        var storedProcedure = p.Body.Any(b => b.Text.Contains("adCmdStoredProc", StringComparison.OrdinalIgnoreCase));
        foreach (var (line, text) in p.Body)
        {
            var lower = text.ToLowerInvariant();
            if (lower.StartsWith("on error resume next", StringComparison.Ordinal)) p.Node.Props["onErrorResumeNext"] = "true";
            if (Regex.IsMatch(lower, @"^goto\s+\w+") || Regex.IsMatch(lower, @"^(if .* then )?goto\s+\w+")) p.Node.Props["goTo"] = "true";
            if (VariableLine().Match(text) is { Success: true } v) locals[v.Groups[2].Value] = v.Groups[3].Value;

            var tokens = Token().Matches(text).Select(t => t.Value).ToList();
            var masked = Secrets.Mask(text);
            var snippet = masked.Length > 160 ? masked[..157] + "…" : masked;
            ReadStrings(m, p, tokens, line, snippet, storedProcedure);
            for (var i = 0; i < tokens.Count; i++)
            {
                var tok = tokens[i];
                if (!char.IsLetter(tok[0]) && tok[0] != '_') continue;
                var afterDot = i > 0 && tokens[i - 1] == ".";
                var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
                // New X / As New X: creating a class or form of the project.
                if (tok.Equals("New", StringComparison.OrdinalIgnoreCase) && next != null && Find(m.Key, next) is { } created)
                {
                    map.Link(from, created.Node.Id, EdgeKind.Creates, m.File, line, Certainty.ByName, snippet);
                    i++;
                    continue;
                }
                if (afterDot) continue;
                // X.Y: a module's, form's or object's procedure (Show and Load open a form).
                if (next == "." && i + 2 < tokens.Count)
                {
                    var member = tokens[i + 2];
                    var target = Find(m.Key, tok) ?? (locals.TryGetValue(tok, out var typeName) ? Find(m.Key, typeName) : null);
                    if (target != null)
                    {
                        if (target.Kind is "form" or "user control" && member.Equals("Show", StringComparison.OrdinalIgnoreCase))
                            map.Link(from, target.Node.Id, EdgeKind.Creates, m.File, line, Certainty.ByName, snippet);
                        else if (ProcOf(target, member, IsAssigned(tokens, i + 2)) is { } called)
                            map.Link(from, called.Node.Id, EdgeKind.Calls, m.File, line, Certainty.ByName, snippet);
                        i += 2;
                        continue;
                    }
                    if (m.Controls.TryGetValue(tok, out var c1))
                    {
                        map.Link(from, c1.Id, EdgeKind.Uses, m.File, line, Certainty.Resolved, snippet);
                        i += 2;
                        continue;
                    }
                    continue;
                }
                if (tok.Equals("Load", StringComparison.OrdinalIgnoreCase) && i == 0 && next != null && Find(m.Key, next) is { Kind: "form" } loaded)
                {
                    map.Link(from, loaded.Node.Id, EdgeKind.Creates, m.File, line, Certainty.ByName, snippet);
                    i++;
                    continue;
                }
                if (m.Controls.TryGetValue(tok, out var c2))
                {
                    map.Link(from, c2.Id, EdgeKind.Uses, m.File, line, Certainty.Resolved, snippet);
                    continue;
                }
                // A bare call: this module's procedure, or a public one in any module of the project.
                if (tok.Equals(p.Name, StringComparison.OrdinalIgnoreCase) && next == "=") continue; // a function setting its result
                var proc = ProcOf(m, tok, IsAssigned(tokens, i)) ?? _modules.Where(o => o.Key == m.Key && o.Kind == "module" && o != m)
                    .Select(o => ProcOf(o, tok, false)).FirstOrDefault(x => x != null && x.Node.Prop("access")?.Equals("Private", StringComparison.OrdinalIgnoreCase) != true);
                if (proc != null && proc != p && (i == 0 || next == "(" || i > 0 && tokens[i - 1].Equals("Call", StringComparison.OrdinalIgnoreCase) || proc.Kind == "function" || proc.Kind.StartsWith("property", StringComparison.Ordinal)))
                    map.Link(from, proc.Node.Id, EdgeKind.Calls, m.File, line, Certainty.ByName, snippet);
            }
        }
    }

    /// <summary>Whether the member at <paramref name="i"/> is being assigned (<c>c.Amount = 5</c>: Property Let).</summary>
    private static bool IsAssigned(List<string> tokens, int i) => i + 1 < tokens.Count && tokens[i + 1] == "=" && (i == 0 || i >= 2 && tokens[i - 1] == "." && (i == 2 || tokens[i - 3].Equals("Set", StringComparison.OrdinalIgnoreCase)));

    private Module? Find(string key, string name) => _modules.FirstOrDefault(o => o.Key == key && o.VbName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static Proc? ProcOf(Module m, string name, bool assigned)
    {
        var all = m.Procs.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (all.Count <= 1) return all.FirstOrDefault();
        return all.FirstOrDefault(x => assigned ? x.Kind is "property let" or "property set" : x.Kind is "property get" or "function") ?? all[0];
    }

    /// <summary>Strings on a line: SQL (whole concatenations, run-time parts as stand-ins), procedure names, shares, connection strings.</summary>
    private void ReadStrings(Module m, Proc p, List<string> tokens, int line, string snippet, bool storedProcedure)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].StartsWith('"')) continue;
            if (i > 0 && tokens[i - 1] is "&" or "+") continue; // read as part of the concatenation it continues
            // The whole concatenation from here: strings joined by & or +, anything else a stand-in.
            var text = new StringBuilder(Unquote(tokens[i]));
            var built = false;
            var j = i + 1;
            while (j + 1 < tokens.Count && tokens[j] is "&" or "+")
            {
                j++;
                if (tokens[j].StartsWith('"'))
                {
                    text.Append(Unquote(tokens[j]));
                    j++;
                    continue;
                }
                text.Append('0');
                built = true;
                // Skip one operand: a name, its member accesses and any (balanced) arguments.
                var depth = 0;
                while (j < tokens.Count)
                {
                    var t = tokens[j];
                    if (t == "(") depth++;
                    else if (t == ")") { if (depth == 0) break; depth--; }
                    else if (depth == 0 && t is "&" or "+" or "," or "=") break;
                    j++;
                }
            }
            var value = text.ToString();
            if (SqlReader.LooksLikeSql(value))
            {
                p.Node.Props["sql"] = "embedded";
                if (built) p.Node.Props["sqlBuiltByHand"] = "true";
                sql.AnalyzeSql(p.Node.Id, value, m.File, line, built ? Certainty.Guessed : Certainty.Resolved);
                continue;
            }
            if (built) continue;
            if (Regex.IsMatch(value, @"(Data Source|Server|Provider|DSN)\s*=", RegexOptions.IgnoreCase))
            {
                // A connection string written into the code.
                var conn = map.Add(new Node { Id = $"connection:{m.File}#{line}", Kind = NodeKind.Connection, Name = $"in {m.VbName}.{p.Name}", Parent = p.Node.Id, File = m.File, Line = line, EndLine = line, Language = "VB6" });
                foreach (var (k, v) in ConfigReader.Describe(value)) conn.Props[k] = v;
                conn.Props["hardCoded"] = "true";
                if (Secrets.HasPassword(value)) p.Node.Props["hardCodedPassword"] = "true";
                map.Link(p.Node.Id, conn.Id, EdgeKind.Uses, m.File, line, Certainty.Resolved, snippet);
                continue;
            }
            if (config.Outside(value) is { } ext)
            {
                map.Link(p.Node.Id, ext.Id, EdgeKind.Uses, m.File, line, Certainty.Resolved, "hard-coded: " + Secrets.Mask(value));
                p.Node.Props["hardCodedOutside"] = "true";
                continue;
            }
            if (SqlNames.Parse(value) is { } name && !value.Contains(' '))
            {
                var id = SqlNames.ObjectId(name.Schema, name.Name);
                if (map[id] is { Kind: NodeKind.Procedure or NodeKind.Function } known)
                    map.Link(p.Node.Id, known.Id, EdgeKind.Executes, m.File, line, Certainty.Resolved, snippet);
                else if (storedProcedure && tokens.Take(i).Any(t => t.Equals("CommandText", StringComparison.OrdinalIgnoreCase)))
                    map.Link(p.Node.Id, sql.Ensure(name, NodeKind.Procedure).Id, EdgeKind.Executes, m.File, line, Certainty.ByName, snippet);
            }
        }
    }

    private static string Unquote(string literal) => literal.Length >= 2 ? literal[1..^1].Replace("\"\"", "\"") : literal;
}
