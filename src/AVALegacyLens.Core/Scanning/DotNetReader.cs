using System.Collections.Immutable;
using AVALegacyLens.Core.Model;
using Microsoft.CodeAnalysis;
using CS = Microsoft.CodeAnalysis.CSharp;
using CSS = Microsoft.CodeAnalysis.CSharp.Syntax;
using VB = Microsoft.CodeAnalysis.VisualBasic;
using VBS = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace AVALegacyLens.Core.Scanning;

/// <summary>
/// Reads VB.NET and C# projects with Roslyn: each project is compiled (against the .NET runtime's own assemblies, so
/// framework calls resolve where they still exist and the rest is simply left unresolved), and its types and members
/// become nodes, with calls, object creations and inheritance as edges. What the compiler can't resolve inside a
/// project, or across projects and languages, is kept as a pending call and matched by name once every project is
/// read (<see cref="ResolvePending"/>), marked <see cref="Certainty.ByName"/>.
/// </summary>
public sealed class DotNetReader(SystemMap map, string root)
{
    /// <summary>A call or creation the compiler couldn't resolve: matched by name after all projects are read.</summary>
    private sealed record Pending(string From, string? Receiver, string Name, bool Create, string File, int Line, string Note, string Project, int Arguments = -1);

    private readonly List<Pending> _pending = [];

    /// <summary>Called for each member body after it is read (the WinForms, SQL and config readers hook in here).</summary>
    public event Action<MemberContext>? MemberRead;

    /// <summary>Called for each type after it is read.</summary>
    public event Action<TypeContext>? TypeRead;

    private static ImmutableArray<MetadataReference>? s_references;

    /// <summary>The running runtime's assemblies, once: enough to resolve System, System.Data, Microsoft.VisualBasic…</summary>
    private static ImmutableArray<MetadataReference> References()
    {
        if (s_references is { } done) return done;
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var list = tpa
            .Where(p =>
            {
                var n = Path.GetFileName(p);
                return n.StartsWith("System.", StringComparison.Ordinal) || n.StartsWith("Microsoft.VisualBasic", StringComparison.Ordinal)
                    || n is "netstandard.dll" or "mscorlib.dll" or "System.dll" or "Microsoft.Win32.Primitives.dll";
            })
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();
        s_references = list;
        return list;
    }

    /// <summary>The default project-level imports of a VB WinForms project, used when the project file lists none.</summary>
    private static readonly string[] DefaultVbImports =
        ["Microsoft.VisualBasic", "System", "System.Collections", "System.Collections.Generic", "System.Data", "System.Diagnostics", "System.Drawing", "System.Linq", "System.Windows.Forms", "System.Xml"];

    public void Read(ProjectInfo project, string projectId, string key, CancellationToken cancel)
    {
        var trees = new List<SyntaxTree>();
        foreach (var path in project.Sources)
        {
            cancel.ThrowIfCancellationRequested();
            string text;
            try
            {
                text = SourceFiles.ReadText(path);
            }
            catch (IOException e)
            {
                map.Notes.Add(new ScanNote(SourceFiles.Relative(root, path), 0, "Couldn't read: " + e.Message));
                continue;
            }
            trees.Add(project.Language == "VB.NET"
                ? VB.VisualBasicSyntaxTree.ParseText(text, path: path)
                : CS.CSharpSyntaxTree.ParseText(text, new CS.CSharpParseOptions(CS.LanguageVersion.Latest), path: path));
        }
        if (trees.Count == 0) return;

        Compilation compilation;
        if (project.Language == "VB.NET")
        {
            var imports = (project.Imports.Count > 0 ? project.Imports : [.. DefaultVbImports])
                .Select(i => VB.GlobalImport.Parse(i)).ToList();
            var options = new VB.VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                rootNamespace: project.RootNamespace ?? "", globalImports: imports,
                optionStrict: VB.OptionStrict.Off, optionInfer: true, optionExplicit: true, optionCompareText: false);
            compilation = VB.VisualBasicCompilation.Create(key, trees, References(), options);
        }
        else
        {
            compilation = CS.CSharpCompilation.Create(key, trees, References(),
                new CS.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        }

        foreach (var tree in trees)
        {
            cancel.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var file = SourceFiles.Relative(root, tree.FilePath);
            var fileId = "file:" + file;
            foreach (var decl in TypeDeclarations(tree.GetRoot(cancel)))
            {
                if (DeclaredType(model, decl, cancel) is not { } type) continue;
                var typeNode = TypeNode(type, decl, key, file, fileId, project);
                var context = new TypeContext(map, key, project, type, typeNode, decl, model, file);
                foreach (var field in FieldDeclarations(decl))
                    ReadBody(typeNode.Id, field, model, key, file, cancel);
                foreach (var member in MemberDeclarations(decl))
                {
                    var symbol = DeclaredMember(model, member, cancel);
                    if (symbol == null) continue;
                    var memberNode = MemberNode(symbol, member, key, typeNode, file, project.Language);
                    var body = new MemberContext(map, key, project, symbol, memberNode, member, model, file);
                    ReadBody(memberNode.Id, member, model, key, file, cancel);
                    MemberRead?.Invoke(body);
                }
                TypeRead?.Invoke(context);
            }
        }
    }

    /// <summary>Type declarations, nested ones included (each read once, by its own declaration).</summary>
    private static IEnumerable<SyntaxNode> TypeDeclarations(SyntaxNode root) =>
        root.DescendantNodes().Where(n => n is VBS.TypeBlockSyntax or VBS.EnumBlockSyntax or CSS.BaseTypeDeclarationSyntax);

    /// <summary>The members directly in a type declaration (not those of nested types).</summary>
    private static IEnumerable<SyntaxNode> MemberDeclarations(SyntaxNode type) => type switch
    {
        VBS.TypeBlockSyntax vb => vb.Members.Where(m => m is VBS.MethodBlockBaseSyntax or VBS.PropertyBlockSyntax or VBS.EventBlockSyntax
            or VBS.DeclareStatementSyntax or VBS.PropertyStatementSyntax or VBS.MethodStatementSyntax),
        CSS.TypeDeclarationSyntax cs => cs.Members.Where(m => m is CSS.BaseMethodDeclarationSyntax or CSS.BasePropertyDeclarationSyntax),
        _ => [],
    };

    /// <summary>Field declarations with initializers (<c>Private _rules As New BillingRules()</c>): read as the type's own uses.</summary>
    private static IEnumerable<SyntaxNode> FieldDeclarations(SyntaxNode type) => type switch
    {
        VBS.TypeBlockSyntax vb => vb.Members.OfType<VBS.FieldDeclarationSyntax>(),
        CSS.TypeDeclarationSyntax cs => cs.Members.OfType<CSS.FieldDeclarationSyntax>(),
        _ => [],
    };

    private static ISymbol? DeclaredMember(SemanticModel model, SyntaxNode member, CancellationToken cancel) => member switch
    {
        VBS.MethodBlockBaseSyntax m => VB.VisualBasicExtensions.GetDeclaredSymbol(model, m, cancel),
        VBS.PropertyBlockSyntax p => VB.VisualBasicExtensions.GetDeclaredSymbol(model, p.PropertyStatement, cancel),
        VBS.EventBlockSyntax e => VB.VisualBasicExtensions.GetDeclaredSymbol(model, e.EventStatement, cancel),
        VBS.PropertyStatementSyntax p => VB.VisualBasicExtensions.GetDeclaredSymbol(model, p, cancel),
        VBS.DeclareStatementSyntax d => VB.VisualBasicExtensions.GetDeclaredSymbol(model, d, cancel),
        VBS.MethodStatementSyntax m => VB.VisualBasicExtensions.GetDeclaredSymbol(model, m, cancel),
        _ => model.GetDeclaredSymbol(member, cancel),
    };

    private static INamedTypeSymbol? DeclaredType(SemanticModel model, SyntaxNode decl, CancellationToken cancel) => decl switch
    {
        VBS.TypeBlockSyntax t => VB.VisualBasicExtensions.GetDeclaredSymbol(model, t, cancel),
        VBS.EnumBlockSyntax e => VB.VisualBasicExtensions.GetDeclaredSymbol(model, e.EnumStatement, cancel),
        _ => model.GetDeclaredSymbol(decl, cancel) as INamedTypeSymbol,
    };

    /// <summary>A symbol's id in the map: the project's key and the symbol's documentation id.</summary>
    public static string IdOf(string key, ISymbol symbol)
    {
        var s = symbol is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : symbol.OriginalDefinition;
        return key + ":" + (s.GetDocumentationCommentId() ?? s.ToDisplayString());
    }

    private Node TypeNode(INamedTypeSymbol type, SyntaxNode decl, string key, string file, string fileId, ProjectInfo project)
    {
        var id = IdOf(key, type);
        var line = Line(decl);
        var designer = IsDesignerFile(file);
        var isForm = IsScreen(type);
        var node = map[id];
        if (node == null)
        {
            node = map.Add(new Node
            {
                Id = id,
                Kind = isForm ? NodeKind.Form : NodeKind.Type,
                Name = type.Name,
                Parent = type.ContainingType != null ? IdOf(key, type.ContainingType) : fileId,
                File = file,
                Line = line,
                EndLine = EndLine(decl),
                Language = project.Language,
            });
            node.Props["fullName"] = type.ToDisplayString();
            node.Props["subkind"] = isForm ? (Inherits(type, "UserControl") ? "user control" : "form") : SubKind(type);
            node.Props["access"] = type.DeclaredAccessibility.ToString();
            node.Props["project"] = key;
            if (type.BaseType is { SpecialType: not SpecialType.System_Object } b && b.Name != "Object" && type.TypeKind == TypeKind.Class)
                node.Props["base"] = b.ToDisplayString();
        }
        else if (!designer && IsDesignerFile(node.File ?? ""))
        {
            // A partial type: list it under the file people edit, not the designer's.
            node.File = file;
            node.Line = line;
            node.EndLine = EndLine(decl);
            if (type.ContainingType == null) node.Parent = fileId;
        }
        var files = node.Prop("files");
        if (files == null || !files.Split('|').Contains(file)) node.Props["files"] = files == null ? file : files + "|" + file;

        if (type.BaseType is { } baseType && baseType.Locations.Any(l => l.IsInSource))
            map.Link(id, IdOf(key, baseType), EdgeKind.Inherits, file, line);
        else if (type.BaseType is { TypeKind: TypeKind.Error } unresolvedBase)
            _pending.Add(new Pending(id, null, unresolvedBase.Name, true, file, line, "Inherits " + unresolvedBase.Name, key));
        return node;
    }

    public static bool IsDesignerFile(string file) => file.EndsWith(".Designer.vb", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>A WinForms form or user control: it inherits Form or UserControl, by name (WinForms itself isn't referenced).</summary>
    public static bool IsScreen(INamedTypeSymbol type) => type.TypeKind == TypeKind.Class && (Inherits(type, "Form") || Inherits(type, "UserControl"));

    private static bool Inherits(INamedTypeSymbol type, string name)
    {
        for (var b = type.BaseType; b != null; b = b.BaseType)
            if (b.Name == name || b.ToDisplayString().EndsWith("." + name, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string SubKind(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Module => "module",
        TypeKind.Struct => "structure",
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => type.IsStatic ? "static class" : "class",
    };

    private Node MemberNode(ISymbol symbol, SyntaxNode decl, string key, Node type, string file, string language)
    {
        var id = IdOf(key, symbol);
        var existing = map[id];
        if (existing != null) return existing;
        var name = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } ? (language == "VB.NET" ? "New" : symbol.ContainingType.Name) : symbol.Name;
        var node = map.Add(new Node
        {
            Id = id,
            Kind = NodeKind.Method,
            Name = name,
            Parent = type.Id,
            File = file,
            Line = Line(decl),
            EndLine = EndLine(decl),
            Language = language,
        });
        node.Props["type"] = symbol.ContainingType.Name;
        node.Props["project"] = key;
        node.Props["signature"] = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        node.Props["access"] = symbol.DeclaredAccessibility.ToString();
        if (symbol is IMethodSymbol ms) node.Props["parameters"] = ms.Parameters.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        node.Props["subkind"] = symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "constructor",
            IMethodSymbol { MethodKind: MethodKind.DeclareMethod } => "declare",
            IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => "operator",
            IMethodSymbol { IsAbstract: true } => "abstract",
            IMethodSymbol => decl is VBS.MethodBlockSyntax { SubOrFunctionStatement.SubOrFunctionKeyword.Text: var k } && k.Equals("Function", StringComparison.OrdinalIgnoreCase) ? "function" : "method",
            IPropertySymbol => "property",
            IEventSymbol => "event",
            _ => "member",
        };
        if (symbol.IsStatic || symbol.ContainingType.TypeKind == TypeKind.Module) node.Props["shared"] = "true";
        var lines = node.EndLine - node.Line + 1;
        node.Props["lines"] = lines.ToString(System.Globalization.CultureInfo.InvariantCulture);
        node.Props["complexity"] = Complexity(decl).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (decl is VBS.DeclareStatementSyntax declare)
        {
            var lib = declare.LibraryName.Token.ValueText;
            var ext = map.Add(new Node { Id = "external:dll:" + lib.ToLowerInvariant(), Kind = NodeKind.External, Name = lib, Parent = map.Outside(), Props = { ["subkind"] = "native library" } });
            map.Link(id, ext.Id, EdgeKind.Uses, file, node.Line, note: declare.ToString().Trim());
        }
        return node;
    }

    /// <summary>Cyclomatic complexity: one, plus one for each branch, loop, case, catch and short-circuit operator.</summary>
    public static int Complexity(SyntaxNode body)
    {
        var n = 1;
        foreach (var d in body.DescendantNodes())
        {
            if (d.Language == LanguageNames.VisualBasic)
            {
                switch ((VB.SyntaxKind)d.RawKind)
                {
                    case VB.SyntaxKind.IfStatement or VB.SyntaxKind.SingleLineIfStatement or VB.SyntaxKind.ElseIfStatement
                        or VB.SyntaxKind.CaseStatement or VB.SyntaxKind.ForStatement or VB.SyntaxKind.ForEachStatement
                        or VB.SyntaxKind.WhileStatement or VB.SyntaxKind.DoWhileStatement or VB.SyntaxKind.DoUntilStatement
                        or VB.SyntaxKind.LoopWhileStatement or VB.SyntaxKind.LoopUntilStatement or VB.SyntaxKind.CatchStatement
                        or VB.SyntaxKind.AndAlsoExpression or VB.SyntaxKind.OrElseExpression
                        or VB.SyntaxKind.TernaryConditionalExpression or VB.SyntaxKind.BinaryConditionalExpression:
                        n++;
                        break;
                }
            }
            else
            {
                switch ((CS.SyntaxKind)d.RawKind)
                {
                    case CS.SyntaxKind.IfStatement or CS.SyntaxKind.CaseSwitchLabel or CS.SyntaxKind.CasePatternSwitchLabel
                        or CS.SyntaxKind.SwitchExpressionArm or CS.SyntaxKind.ForStatement or CS.SyntaxKind.ForEachStatement
                        or CS.SyntaxKind.WhileStatement or CS.SyntaxKind.DoStatement or CS.SyntaxKind.CatchClause
                        or CS.SyntaxKind.LogicalAndExpression or CS.SyntaxKind.LogicalOrExpression
                        or CS.SyntaxKind.ConditionalExpression or CS.SyntaxKind.CoalesceExpression:
                        n++;
                        break;
                }
            }
        }
        return n;
    }

    /// <summary>Calls, property uses and object creations in a member's body.</summary>
    private void ReadBody(string from, SyntaxNode syntax, SemanticModel model, string key, string file, CancellationToken cancel)
    {
        foreach (var name in syntax.DescendantNodes().Where(n => n is VBS.SimpleNameSyntax or CSS.SimpleNameSyntax))
        {
            cancel.ThrowIfCancellationRequested();
            if (IsDeclarationName(name)) continue;
            var info = model.GetSymbolInfo(name, cancel);
            var symbol = info.Symbol ?? (info.CandidateSymbols.Length > 0 ? info.CandidateSymbols[0] : null);
            // One candidate means only the arguments' types were unknown (a WinForms control's .Text, say): it's that one.
            var certainty = info.Symbol != null || info.CandidateSymbols.Length == 1 ? Certainty.Resolved : Certainty.ByName;
            var line = Line(name);
            var creation = CreationOf(name);
            if (symbol is INamedTypeSymbol type)
            {
                if (creation == null) continue;
                if (type.Locations.Any(l => l.IsInSource))
                    map.Link(from, IdOf(key, type), EdgeKind.Creates, file, line, certainty, Snippet(creation));
                else if (type.TypeKind == TypeKind.Error)
                    _pending.Add(new Pending(from, null, type.Name, true, file, line, Snippet(creation), key));
                continue;
            }
            // A VB WithEvents field (a form's control) is a property to the compiler: controls are the designer reader's.
            if (symbol is IPropertySymbol { IsWithEvents: true }) continue;
            if (symbol is IMethodSymbol or IPropertySymbol)
            {
                if (!symbol.Locations.Any(l => l.IsInSource)) continue;
                var target = IdOf(key, symbol);
                if (target == from) continue;
                map.Link(from, target, EdgeKind.Calls, file, line, certainty, Snippet(StatementOf(name)));
                continue;
            }
            if (symbol != null) continue;
            if (creation != null)
            {
                _pending.Add(new Pending(from, null, NameText(name), true, file, line, Snippet(creation), key));
                continue;
            }
            if (IsInvokedName(name, out var receiver, out var arguments))
                _pending.Add(new Pending(from, receiver, NameText(name), false, file, line, Snippet(StatementOf(name)), key, arguments));
        }
    }

    private static string NameText(SyntaxNode name) => name switch
    {
        VBS.SimpleNameSyntax v => v.Identifier.ValueText,
        CSS.SimpleNameSyntax c => c.Identifier.ValueText,
        _ => name.ToString(),
    };

    private static bool IsDeclarationName(SyntaxNode name) => name.Parent is VBS.HandlesClauseItemSyntax or VBS.EventContainerSyntax or VBS.KeywordEventContainerSyntax
        or VBS.WithEventsEventContainerSyntax or VBS.WithEventsPropertyEventContainerSyntax;

    /// <summary>The object-creation expression a name is the type of, if it is one.</summary>
    private static SyntaxNode? CreationOf(SyntaxNode name)
    {
        var n = name;
        while (n.Parent is VBS.QualifiedNameSyntax or CSS.QualifiedNameSyntax or CSS.AliasQualifiedNameSyntax) n = n.Parent;
        return n.Parent is VBS.ObjectCreationExpressionSyntax or CSS.ObjectCreationExpressionSyntax ? n.Parent : null;
    }

    /// <summary>
    /// Whether a name is what's called (<c>Foo()</c>, <c>X.Foo()</c>, VB's <c>Foo</c> on its own), and the simple
    /// name before the dot if there is one (<c>AgeCalculator</c> in <c>AgeCalculator.IsMinor(d)</c>).
    /// </summary>
    private static bool IsInvokedName(SyntaxNode name, out string? receiver, out int arguments)
    {
        receiver = null;
        arguments = -1;
        SyntaxNode expr = name;
        switch (name.Parent)
        {
            case VBS.MemberAccessExpressionSyntax vm when vm.Name == name:
                receiver = vm.Expression is VBS.IdentifierNameSyntax vid ? vid.Identifier.ValueText : vm.Expression is VBS.MemberAccessExpressionSyntax vmm ? vmm.Name.Identifier.ValueText : null;
                expr = vm;
                break;
            case CSS.MemberAccessExpressionSyntax cm when cm.Name == name:
                receiver = cm.Expression is CSS.IdentifierNameSyntax cid ? cid.Identifier.ValueText : cm.Expression is CSS.MemberAccessExpressionSyntax cmm ? cmm.Name.Identifier.ValueText : null;
                expr = cm;
                break;
        }
        switch (expr.Parent)
        {
            case VBS.InvocationExpressionSyntax vi when vi.Expression == expr:
                arguments = vi.ArgumentList?.Arguments.Count ?? 0;
                return true;
            case CSS.InvocationExpressionSyntax ci when ci.Expression == expr:
                arguments = ci.ArgumentList.Arguments.Count;
                return true;
            case VBS.CallStatementSyntax:
                return true;
            default:
                return false;
        }
    }

    private static SyntaxNode StatementOf(SyntaxNode node)
    {
        for (var n = node; n != null; n = n.Parent)
            if (n is VBS.StatementSyntax or CSS.StatementSyntax or CSS.ArrowExpressionClauseSyntax or CSS.EqualsValueClauseSyntax) return n;
        return node;
    }

    /// <summary>A node's text on one line, cut to a readable length.</summary>
    public static string Snippet(SyntaxNode node)
    {
        var text = string.Join(' ', node.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        return text.Length > 160 ? text[..157] + "…" : text;
    }

    public static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    public static int EndLine(SyntaxNode node) => node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

    /// <summary>
    /// Matches what the compiler couldn't resolve, by name, once every project is in the map. A call with a receiver
    /// (<c>AgeCalculator.IsMinor</c>) matches methods of that name on a type of that name; a bare call matches methods
    /// of a VB module (whose members need no qualifier) in another project; a creation matches a type of that name.
    /// Anything else (a framework call the runtime doesn't have, like <c>MessageBox.Show</c>) is left alone.
    /// </summary>
    public void ResolvePending()
    {
        var methods = map.OfKind(NodeKind.Method).ToLookup(n => n.Name, StringComparer.OrdinalIgnoreCase);
        var types = map.Nodes.Where(n => n.Kind is NodeKind.Type or NodeKind.Form).ToLookup(n => n.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var p in _pending)
        {
            if (p.Create)
            {
                var kind = p.Note.StartsWith("Inherits ", StringComparison.Ordinal) ? EdgeKind.Inherits : EdgeKind.Creates;
                foreach (var t in types[p.Name].Where(t => t.Id != p.From).Take(1))
                    map.Link(p.From, t.Id, kind, p.File, p.Line, Certainty.ByName, p.Note);
                continue;
            }
            var candidates = methods[p.Name].Where(m => m.Id != p.From && m.Prop("subkind") != "constructor").ToList();
            if (p.Receiver != null)
                candidates = [.. candidates.Where(m => string.Equals(m.Prop("type"), p.Receiver, StringComparison.OrdinalIgnoreCase))];
            else
                candidates = [.. candidates.Where(m => m.Prop("project") != p.Project && map[m.Parent ?? ""]?.Prop("subkind") == "module")];
            // Overloads: keep the ones that take as many arguments as the call passes, if any do.
            if (candidates.Count > 1 && p.Arguments >= 0 && candidates.Any(m => m.IntProp("parameters") == p.Arguments))
                candidates = [.. candidates.Where(m => m.IntProp("parameters") == p.Arguments)];
            foreach (var m in candidates)
                map.Link(p.From, m.Id, EdgeKind.Calls, p.File, p.Line, Certainty.ByName, p.Note);
        }
        _pending.Clear();
    }
}

/// <summary>A type as the reader saw it, for the readers that add to it (WinForms designer, for one).</summary>
public sealed record TypeContext(SystemMap Map, string Key, ProjectInfo Project, INamedTypeSymbol Symbol, Node Node, SyntaxNode Syntax, SemanticModel Model, string File);

/// <summary>A member's body as the reader saw it, for the readers that add to it (embedded SQL, config use, events).</summary>
public sealed record MemberContext(SystemMap Map, string Key, ProjectInfo Project, ISymbol Symbol, Node Node, SyntaxNode Syntax, SemanticModel Model, string File);
