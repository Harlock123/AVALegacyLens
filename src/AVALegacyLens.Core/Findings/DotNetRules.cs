using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Scanning;
using Microsoft.CodeAnalysis;
using CS = Microsoft.CodeAnalysis.CSharp;
using CSS = Microsoft.CodeAnalysis.CSharp.Syntax;
using VB = Microsoft.CodeAnalysis.VisualBasic;
using VBS = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace AVALegacyLens.Core.Findings;

/// <summary>
/// Finds candidate business rules in VB.NET and C#: a check that shows a message and stops (or throws) is a
/// <c>validation</c> (its message is the rule in the original's own words); one that only shows a message is a
/// <c>notice</c>; a Select Case or switch over literal values is a <c>decision</c> table; a named constant is a
/// <c>limit</c>. Only the outermost qualifying If counts, so a confirmation inside a rule isn't a second rule.
/// </summary>
public sealed class DotNetRules
{
    public void Attach(DotNetReader reader)
    {
        reader.MemberRead += m => Read(m.Map, m.Node, m.Syntax, m.File);
        reader.TypeRead += t => Constants(t.Map, t.Node, t.Syntax, t.File);
    }

    private static void Read(SystemMap map, Node owner, SyntaxNode body, string file)
    {
        if (owner.Name is "InitializeComponent" or "Dispose") return;
        var taken = new List<SyntaxNode>();
        foreach (var n in body.DescendantNodes())
        {
            if (taken.Any(t => t.Span.Contains(n.Span))) continue;
            if (IfParts(n) is var (condition, block) && Judge(block) is var (message, stops) && message != null)
            {
                taken.Add(n);
                map.Find(new Finding
                {
                    Kind = FindingKind.Rule,
                    Category = stops ? "validation" : "notice",
                    NodeId = owner.Id,
                    Title = message,
                    Detail = $"When {Flat(condition)}" + (stops ? ", it stops." : ", it tells the user and carries on."),
                    Code = DotNetReader.Snippet(IfLine(n)),
                    File = file,
                    Line = DotNetReader.Line(n),
                    Severity = Severity.Info,
                    Confidence = stops ? Severity.High : Severity.Medium,
                });
                continue;
            }
            if (Decision(n) is var (subject, cases) && cases.Count >= 2)
            {
                taken.Add(n);
                map.Find(new Finding
                {
                    Kind = FindingKind.Rule,
                    Category = "decision",
                    NodeId = owner.Id,
                    Title = $"{owner.Name} decides by {Flat(subject)}: {string.Join(", ", cases.Select(c => c.Label))}",
                    Detail = string.Join("; ", cases.Select(c => $"{c.Label} → {c.Outcome}")),
                    Code = DotNetReader.Snippet(SelectLine(n)),
                    File = file,
                    Line = DotNetReader.Line(n),
                    Confidence = Severity.Medium,
                });
            }
        }
    }

    /// <summary>A named constant with a literal value: a limit or code the business set.</summary>
    private static void Constants(SystemMap map, Node type, SyntaxNode decl, string file)
    {
        foreach (var f in decl.ChildNodes())
        {
            IEnumerable<(string Name, SyntaxNode Value)> consts = f switch
            {
                VBS.FieldDeclarationSyntax vf when vf.Modifiers.Any(m => m.IsKind(VB.SyntaxKind.ConstKeyword)) =>
                    vf.Declarators.Where(d => d.Initializer != null).SelectMany(d => d.Names.Select(n => (n.Identifier.ValueText, (SyntaxNode)d.Initializer!.Value))),
                CSS.FieldDeclarationSyntax cf when cf.Modifiers.Any(m => m.IsKind(CS.SyntaxKind.ConstKeyword)) =>
                    cf.Declaration.Variables.Where(v => v.Initializer != null).Select(v => (v.Identifier.ValueText, (SyntaxNode)v.Initializer!.Value)),
                _ => [],
            };
            foreach (var (name, value) in consts)
            {
                if (value is not (VBS.LiteralExpressionSyntax or CSS.LiteralExpressionSyntax)) continue;
                map.Find(new Finding
                {
                    Kind = FindingKind.Rule,
                    Category = "limit",
                    NodeId = type.Id,
                    Title = $"{type.Name}.{name} = {value}",
                    Detail = "A value fixed in the code: check it's still the business's number, and where else it's written.",
                    Code = DotNetReader.Snippet(f),
                    File = file,
                    Line = DotNetReader.Line(f),
                    Confidence = Severity.Low,
                });
            }
        }
    }

    /// <summary>An If (block, single-line or ElseIf) or C# if: its condition and the statements it runs.</summary>
    private static (SyntaxNode Condition, SyntaxNode Block)? IfParts(SyntaxNode n) => n switch
    {
        VBS.MultiLineIfBlockSyntax b => (b.IfStatement.Condition, b),
        VBS.SingleLineIfStatementSyntax s => (s.Condition, s),
        CSS.IfStatementSyntax i => (i.Condition, i.Statement),
        _ => null,
    };

    private static SyntaxNode IfLine(SyntaxNode n) => n switch
    {
        VBS.MultiLineIfBlockSyntax b => b.IfStatement,
        CSS.IfStatementSyntax i => i.Condition,
        _ => n,
    };

    private static SyntaxNode SelectLine(SyntaxNode n) => n switch
    {
        VBS.SelectBlockSyntax s => s.SelectStatement,
        CSS.SwitchStatementSyntax s => s.Expression,
        _ => n,
    };

    /// <summary>What a block does to the user: its message (a thrown exception's first), and whether it stops.</summary>
    private static (string? Message, bool Stops) Judge(SyntaxNode block)
    {
        // For a multi-line If, only its own statements (not an Else's).
        IEnumerable<SyntaxNode> nodes = block is VBS.MultiLineIfBlockSyntax mb ? mb.Statements.SelectMany(s => s.DescendantNodesAndSelf()) : block.DescendantNodesAndSelf();
        var list = nodes.ToList();
        string? thrown = null, shown = null;
        var stops = false;
        foreach (var n in list)
        {
            switch (n)
            {
                case VBS.ThrowStatementSyntax t:
                    stops = true;
                    thrown ??= FirstString(t);
                    break;
                case CSS.ThrowStatementSyntax or CSS.ThrowExpressionSyntax:
                    stops = true;
                    thrown ??= FirstString(n);
                    break;
                case VBS.ExitStatementSyntax:
                case VBS.ReturnStatementSyntax r when r.Expression is null or VBS.LiteralExpressionSyntax { Token.ValueText: "False" or "Nothing" }:
                case CSS.ReturnStatementSyntax cr when cr.Expression is null or CSS.LiteralExpressionSyntax { Token.ValueText: "false" or "null" }:
                    stops = true;
                    break;
                case VBS.InvocationExpressionSyntax vi when IsMessage(vi.Expression.ToString()):
                    shown ??= FirstString(vi.ArgumentList);
                    break;
                case CSS.InvocationExpressionSyntax ci when IsMessage(ci.Expression.ToString()):
                    shown ??= FirstString(ci.ArgumentList);
                    break;
            }
        }
        return (thrown ?? shown, stops);
    }

    private static bool IsMessage(string callee) =>
        callee.EndsWith("MessageBox.Show", StringComparison.Ordinal) || callee is "MsgBox" or "Interaction.MsgBox"
        || callee.EndsWith(".SetError", StringComparison.Ordinal);

    private static string? FirstString(SyntaxNode? n) => n?.DescendantNodesAndSelf().Select(d => d switch
    {
        VBS.LiteralExpressionSyntax l when l.Token.Value is string s => s,
        CSS.LiteralExpressionSyntax l when l.Token.Value is string s => s,
        _ => null,
    }).FirstOrDefault(s => s is { Length: > 3 });

    /// <summary>A Select Case / switch over literal values: the subject and each case's outcome.</summary>
    private static (SyntaxNode Subject, List<(string Label, string Outcome)> Cases)? Decision(SyntaxNode n)
    {
        switch (n)
        {
            case VBS.SelectBlockSyntax s:
                {
                    var cases = new List<(string, string)>();
                    foreach (var c in s.CaseBlocks)
                    {
                        var labels = c.CaseStatement.Cases.Select(x => x is VBS.ElseCaseClauseSyntax ? "anything else" : x.ToString()).ToList();
                        if (c.CaseStatement.Cases.Any(x => x is VBS.SimpleCaseClauseSyntax { Value: not VBS.LiteralExpressionSyntax }) ) return null;
                        cases.Add((string.Join(" or ", labels), Outcome(c.Statements.FirstOrDefault())));
                    }
                    return (s.SelectStatement.Expression, cases);
                }
            case CSS.SwitchStatementSyntax sw:
                {
                    var cases = new List<(string, string)>();
                    foreach (var section in sw.Sections)
                    {
                        var labels = section.Labels.Select(l => l is CSS.DefaultSwitchLabelSyntax ? "anything else" : l is CSS.CaseSwitchLabelSyntax cl ? cl.Value.ToString() : l.ToString()).ToList();
                        if (section.Labels.OfType<CSS.CaseSwitchLabelSyntax>().Any(l => l.Value is not CSS.LiteralExpressionSyntax)) return null;
                        cases.Add((string.Join(" or ", labels), Outcome(section.Statements.FirstOrDefault())));
                    }
                    return (sw.Expression, cases);
                }
            default:
                return null;
        }
    }

    private static string Outcome(SyntaxNode? statement) => statement == null ? "nothing" : DotNetReader.Snippet(statement);

    private static string Flat(SyntaxNode n) => DotNetReader.Snippet(n);
}
