using System.Globalization;
using AVALegacyLens.Core.Model;
using Microsoft.CodeAnalysis;
using CSS = Microsoft.CodeAnalysis.CSharp.Syntax;
using VBS = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace AVALegacyLens.Core.Scanning;

/// <summary>
/// Reads WinForms screens from their designer code: each control a form's <c>InitializeComponent</c> creates becomes a
/// <see cref="NodeKind.Control"/> under its form (or the panel or group box holding it), with its type and the
/// properties the designer sets (Text, Location, Size, MaxLength…). Event handlers become <see cref="EdgeKind.Handles"/>
/// links from the method to the control (VB's <c>Handles btnSave.Click</c> and <c>AddHandler</c>, C#'s
/// <c>this.btnSave.Click += …</c>), and code that reads or sets a control gets a <see cref="EdgeKind.Uses"/> link.
/// Control ids are the form's id, '#', and the control's name.
/// </summary>
public sealed class WinFormsReader
{
    /// <summary>Properties worth keeping, in the order they're shown; others set by the designer are ignored.</summary>
    public static readonly string[] KeptProperties =
    [
        "Text", "Location", "Size", "ClientSize", "TabIndex", "MaxLength", "ReadOnly", "Enabled", "Visible", "Checked",
        "Dock", "Anchor", "DropDownStyle", "Format", "Multiline", "PasswordChar", "Font", "DataPropertyName", "HeaderText",
        "DataSource", "DisplayMember", "ValueMember", "Mask", "Minimum", "Maximum", "Value", "Items", "AutoSize",
    ];

    public static string ControlId(string formId, string name) => formId + "#" + name;

    public void Attach(DotNetReader reader)
    {
        reader.TypeRead += OnType;
        reader.MemberRead += OnMember;
    }

    /// <summary>The fields of the form being read: a name that is one is a control, even if a form property has that name too.</summary>
    [ThreadStatic]
    private static HashSet<string>? t_fields;

    private static void OnType(TypeContext type)
    {
        if (type.Node.Kind != NodeKind.Form) return;
        var init = type.Syntax switch
        {
            VBS.TypeBlockSyntax vb => vb.Members.OfType<VBS.MethodBlockSyntax>().FirstOrDefault(m => m.SubOrFunctionStatement.Identifier.ValueText == "InitializeComponent") as SyntaxNode,
            CSS.TypeDeclarationSyntax cs => cs.Members.OfType<CSS.MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "InitializeComponent"),
            _ => null,
        };
        if (init == null) return;
        var map = type.Map;
        var formId = type.Node.Id;
        var fields = FieldLines(type);
        t_fields = new HashSet<string>(fields.Keys, StringComparer.Ordinal);
        var created = new Dictionary<string, (string Type, int Line)>(StringComparer.OrdinalIgnoreCase);
        var props = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var formProps = new Dictionary<string, string>();

        foreach (var statement in init.DescendantNodes())
        {
            // x = New T()  /  this.x = new T();
            if (Assignment(statement) is var (left, right, op) && op == "=")
            {
                var target = Target(left);
                if (target is { Control: { } c, Property: null } && NewType(right) is { } t)
                {
                    created[c] = (t, DotNetReader.Line(statement));
                    continue;
                }
                if (target is { Property: { } p } && KeptProperties.Contains(p))
                {
                    var bag = target.Value.Control == null ? formProps : props.TryGetValue(target.Value.Control, out var b) ? b : props[target.Value.Control] = [];
                    bag[p] = Value(right);
                }
                continue;
            }
            // x.Controls.Add(y) / Controls.AddRange(new Control() {a, b}) / x.Items.AddRange(...)
            if (Invocation(statement) is var (callee, args))
            {
                var target = Target(callee);
                if (target is not { Property: { } member }) continue;
                if (member is "Controls.Add" or "Controls.AddRange")
                {
                    foreach (var a in args.SelectMany(ArrayItems))
                        if (Target(a) is { Control: { } child, Property: null })
                            parents[child] = target.Value.Control ?? "";
                }
                else if (member == "Items.AddRange" && target.Value.Control is { } owner)
                {
                    var items = args.SelectMany(ArrayItems).Select(Value).ToList();
                    if (items.Count > 0) (props.TryGetValue(owner, out var b) ? b : props[owner] = [])["Items"] = string.Join(", ", items);
                }
            }
        }

        foreach (var (name, (controlType, line)) in created)
        {
            var parent = parents.TryGetValue(name, out var p) && p.Length > 0 && created.ContainsKey(p) ? ControlId(formId, p) : formId;
            var node = map.Add(new Node
            {
                Id = ControlId(formId, name),
                Kind = NodeKind.Control,
                Name = name,
                Parent = parent,
                File = type.File,
                Line = fields.TryGetValue(name, out var fieldLine) ? fieldLine : line,
                Language = type.Node.Language,
            });
            node.EndLine = node.Line;
            node.Props["type"] = controlType;
            node.Props["form"] = type.Node.Name;
            if (!parents.ContainsKey(name)) node.Props["subkind"] = "component";
            if (props.TryGetValue(name, out var bag))
                foreach (var k in KeptProperties)
                    if (bag.TryGetValue(k, out var v)) node.Props[k] = v;
        }
        foreach (var k in KeptProperties)
            if (formProps.TryGetValue(k, out var v)) type.Node.Props[k == "Text" ? "title" : k] = v;
        type.Node.Props["controls"] = created.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The line each field is declared on, by name (a control's own line is its declaration's).</summary>
    private static Dictionary<string, int> FieldLines(TypeContext type)
    {
        var lines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        switch (type.Syntax)
        {
            case VBS.TypeBlockSyntax vb:
                foreach (var f in vb.Members.OfType<VBS.FieldDeclarationSyntax>())
                    foreach (var d in f.Declarators)
                        foreach (var n in d.Names) lines.TryAdd(n.Identifier.ValueText, DotNetReader.Line(f));
                break;
            case CSS.TypeDeclarationSyntax cs:
                foreach (var f in cs.Members.OfType<CSS.FieldDeclarationSyntax>())
                    foreach (var v in f.Declaration.Variables) lines.TryAdd(v.Identifier.ValueText, DotNetReader.Line(f));
                break;
        }
        return lines;
    }

    /// <summary>What an expression refers to on the form: a control (<c>Me.btnSave</c>, <c>btnSave</c>), a property of
    /// one (<c>Me.btnSave.Text</c> → Text), or of the form (<c>Me.Text</c>: Control is null).</summary>
    private static (string? Control, string? Property)? Target(SyntaxNode expr)
    {
        var parts = new List<string>();
        for (var e = expr; ;)
        {
            switch (e)
            {
                case VBS.MemberAccessExpressionSyntax vm when vm.Expression != null:
                    parts.Insert(0, vm.Name.Identifier.ValueText);
                    e = vm.Expression;
                    continue;
                case CSS.MemberAccessExpressionSyntax cm:
                    parts.Insert(0, cm.Name.Identifier.ValueText);
                    e = cm.Expression;
                    continue;
                case VBS.MeExpressionSyntax or VBS.MyBaseExpressionSyntax or VBS.MyClassExpressionSyntax or CSS.ThisExpressionSyntax or CSS.BaseExpressionSyntax:
                    // Me.x… : the first part names a control, unless it's a property of the form itself.
                    if (parts.Count == 0) return null;
                    if (parts.Count == 1) return IsFormProperty(parts[0]) ? (null, parts[0]) : (parts[0], null);
                    return IsFormProperty(parts[0]) ? (null, string.Join('.', parts)) : (parts[0], string.Join('.', parts.Skip(1)));
                case VBS.IdentifierNameSyntax vi:
                    parts.Insert(0, vi.Identifier.ValueText);
                    return parts.Count == 1 ? (parts[0], null) : IsFormProperty(parts[0]) ? (null, string.Join('.', parts)) : (parts[0], string.Join('.', parts.Skip(1)));
                case CSS.IdentifierNameSyntax ci:
                    parts.Insert(0, ci.Identifier.ValueText);
                    return parts.Count == 1 ? (parts[0], null) : IsFormProperty(parts[0]) ? (null, string.Join('.', parts)) : (parts[0], string.Join('.', parts.Skip(1)));
                default:
                    return null;
            }
        }
    }

    private static readonly HashSet<string> FormProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Controls", "Text", "ClientSize", "Size", "Name", "Font", "AutoScaleDimensions", "AutoScaleMode", "StartPosition",
        "FormBorderStyle", "MaximizeBox", "MinimizeBox", "Icon", "Menu", "MainMenuStrip", "AcceptButton", "CancelButton",
        "BackColor", "ForeColor", "Location", "WindowState", "KeyPreview", "ShowInTaskbar", "IsMdiContainer", "components",
        "SuspendLayout", "ResumeLayout", "PerformLayout",
    };

    private static bool IsFormProperty(string name) => FormProperties.Contains(name) && t_fields?.Contains(name) != true;

    private static (SyntaxNode Left, SyntaxNode Right, string Op)? Assignment(SyntaxNode s) => s switch
    {
        VBS.AssignmentStatementSyntax a => (a.Left, a.Right, a.OperatorToken.Text),
        CSS.AssignmentExpressionSyntax a => (a.Left, a.Right, a.OperatorToken.Text),
        _ => null,
    };

    private static (SyntaxNode Callee, IReadOnlyList<SyntaxNode> Args)? Invocation(SyntaxNode s) => s switch
    {
        VBS.InvocationExpressionSyntax { Expression: { } e } i => (e, i.ArgumentList?.Arguments.Select(a => (SyntaxNode)a.GetExpression()).ToList() ?? []),
        CSS.InvocationExpressionSyntax i => (i.Expression, i.ArgumentList.Arguments.Select(a => (SyntaxNode)a.Expression).ToList()),
        _ => null,
    };

    /// <summary>The items of an array argument (<c>New Control() {a, b}</c>), or the argument itself.</summary>
    private static IEnumerable<SyntaxNode> ArrayItems(SyntaxNode arg) => arg switch
    {
        VBS.ArrayCreationExpressionSyntax a => a.Initializer.Initializers,
        VBS.CollectionInitializerSyntax c => c.Initializers,
        CSS.ArrayCreationExpressionSyntax { Initializer: { } i } => i.Expressions,
        CSS.ImplicitArrayCreationExpressionSyntax i => i.Initializer.Expressions,
        _ => [arg],
    };

    private static string? NewType(SyntaxNode expr) => expr switch
    {
        VBS.ObjectCreationExpressionSyntax o => ShortName(o.Type.ToString()),
        CSS.ObjectCreationExpressionSyntax o => ShortName(o.Type.ToString()),
        _ => null,
    };

    private static string ShortName(string qualified)
    {
        var dot = qualified.LastIndexOf('.');
        return dot < 0 ? qualified : qualified[(dot + 1)..];
    }

    /// <summary>A designer value as people read it: a string's text, a point's numbers, an enum's member.</summary>
    public static string Value(SyntaxNode expr)
    {
        switch (expr)
        {
            case VBS.LiteralExpressionSyntax l:
                return l.Token.ValueText;
            case CSS.LiteralExpressionSyntax l:
                return l.Token.ValueText;
            case VBS.ObjectCreationExpressionSyntax { ArgumentList: { } a }:
                return string.Join(", ", a.Arguments.Select(x => Value(x.GetExpression())));
            case CSS.ObjectCreationExpressionSyntax { ArgumentList: { } a }:
                return string.Join(", ", a.Arguments.Select(x => Value(x.Expression)));
            case VBS.MemberAccessExpressionSyntax m:
                return m.Name.Identifier.ValueText;
            case CSS.MemberAccessExpressionSyntax m:
                return m.Name.Identifier.ValueText;
            case VBS.UnaryExpressionSyntax or CSS.PrefixUnaryExpressionSyntax:
                return expr.ToString();
            default:
                var text = expr.ToString();
                return text.Length > 80 ? text[..77] + "…" : text;
        }
    }

    /// <summary>Handlers (Handles, AddHandler, +=) and the controls a member reads or sets.</summary>
    private static void OnMember(MemberContext m)
    {
        var map = m.Map;
        var type = m.Symbol.ContainingType;
        var formId = DotNetReader.IdOf(m.Key, type);
        var from = m.Node.Id;
        var isInit = m.Symbol.Name is "InitializeComponent" or "Dispose";

        // VB: Sub btnSave_Click(...) Handles btnSave.Click, MyBase.Load
        if (m.Syntax is VBS.MethodBlockSyntax { SubOrFunctionStatement.HandlesClause: { } handles })
        {
            foreach (var item in handles.Events)
            {
                var eventName = item.EventMember.Identifier.ValueText;
                var target = item.EventContainer switch
                {
                    VBS.WithEventsEventContainerSyntax w => ControlId(formId, w.Identifier.ValueText),
                    VBS.WithEventsPropertyEventContainerSyntax wp => ControlId(formId, wp.WithEventsContainer.Identifier.ValueText),
                    _ => formId,
                };
                map.Link(from, target, EdgeKind.Handles, m.File, DotNetReader.Line(item), Certainty.Resolved, eventName);
            }
        }

        foreach (var node in m.Syntax.DescendantNodes())
        {
            // VB: AddHandler btnSave.Click, AddressOf SaveIt
            if (node is VBS.AddRemoveHandlerStatementSyntax { AddHandlerOrRemoveHandlerKeyword.Text: var kw } ah && kw.Equals("AddHandler", StringComparison.OrdinalIgnoreCase))
            {
                if (Target(ah.EventExpression) is { Property: { } ev } t && HandlerMethod(m, ah.DelegateExpression) is { } handler)
                    map.Link(handler, t.Control == null ? formId : ControlId(formId, t.Control), EdgeKind.Handles, m.File, DotNetReader.Line(ah), Certainty.Resolved, ev);
                continue;
            }
            // C#: this.btnSave.Click += new System.EventHandler(this.btnSave_Click);
            if (node is CSS.AssignmentExpressionSyntax { OperatorToken.Text: "+=" } add && Target(add.Left) is { Property: { } evt } ct && !evt.Contains('.'))
            {
                var right = add.Right is CSS.ObjectCreationExpressionSyntax { ArgumentList.Arguments: [var only] } ? only.Expression : add.Right;
                if (HandlerMethod(m, right) is { } handler)
                    map.Link(handler, ct.Control == null ? formId : ControlId(formId, ct.Control), EdgeKind.Handles, m.File, DotNetReader.Line(add), Certainty.Resolved, evt);
                continue;
            }
            // Code that reads or sets a control (not the designer's own setup).
            if (!isInit && node is VBS.IdentifierNameSyntax or CSS.IdentifierNameSyntax)
            {
                var name = node is VBS.IdentifierNameSyntax v ? v.Identifier.ValueText : ((CSS.IdentifierNameSyntax)node).Identifier.ValueText;
                if (type.GetMembers(name).FirstOrDefault() is IFieldSymbol or IPropertySymbol { IsWithEvents: true } && IsOwnMemberAccess(node))
                    map.Link(from, ControlId(formId, name), EdgeKind.Uses, m.File, DotNetReader.Line(node), Certainty.Resolved, DotNetReader.Snippet(Statement(node)));
            }
        }
    }

    /// <summary><c>x</c> or <c>Me.x</c> / <c>this.x</c>, not <c>other.x</c>.</summary>
    private static bool IsOwnMemberAccess(SyntaxNode name) => name.Parent switch
    {
        VBS.MemberAccessExpressionSyntax vm when vm.Name == name => vm.Expression is VBS.MeExpressionSyntax or VBS.MyClassExpressionSyntax,
        CSS.MemberAccessExpressionSyntax cm when cm.Name == name => cm.Expression is CSS.ThisExpressionSyntax,
        _ => true,
    };

    private static SyntaxNode Statement(SyntaxNode node)
    {
        for (var n = node; n != null; n = n.Parent)
            if (n is VBS.StatementSyntax or CSS.StatementSyntax) return n;
        return node;
    }

    /// <summary>The method a handler expression names (<c>AddressOf Save</c>, <c>this.Save</c>, <c>Save</c>), as a node id.</summary>
    private static string? HandlerMethod(MemberContext m, SyntaxNode expr)
    {
        if (expr is VBS.UnaryExpressionSyntax u) expr = u.Operand;
        var info = m.Model.GetSymbolInfo(expr);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (symbol is IMethodSymbol method && method.Locations.Any(l => l.IsInSource)) return DotNetReader.IdOf(m.Key, method);
        // Unresolved (its delegate type is WinForms'): the method of that name on this type.
        var name = expr switch
        {
            VBS.IdentifierNameSyntax i => i.Identifier.ValueText,
            VBS.MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            CSS.IdentifierNameSyntax i => i.Identifier.ValueText,
            CSS.MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            _ => null,
        };
        var found = name == null ? null : m.Symbol.ContainingType.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault();
        return found == null ? null : DotNetReader.IdOf(m.Key, found);
    }
}
