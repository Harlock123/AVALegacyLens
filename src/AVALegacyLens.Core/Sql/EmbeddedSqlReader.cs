using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Scanning;
using Microsoft.CodeAnalysis;
using CSS = Microsoft.CodeAnalysis.CSharp.Syntax;
using VB = Microsoft.CodeAnalysis.VisualBasic;
using VBS = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace AVALegacyLens.Core.Sql;

/// <summary>
/// Finds SQL in VB.NET and C# code: a string that starts like a statement (SELECT, INSERT, UPDATE, DELETE, EXEC…) is
/// read by <see cref="SqlReader"/>, and its tables, columns and procedures become links from the method it's in. A
/// string built by concatenation or interpolation is read with stand-ins for the parts only known at run time, its
/// links marked <see cref="Certainty.Guessed"/> and the method marked as building SQL by hand. A string that is just a
/// procedure's name (<c>New SqlCommand("usp_GetVisits", cn)</c>) runs that procedure.
/// </summary>
public sealed class EmbeddedSqlReader(SqlReader sql)
{
    public void Attach(DotNetReader reader)
    {
        reader.MemberRead += m => Read(m.Map, m.Node, m.Syntax, m.File);
        reader.TypeRead += t =>
        {
            // Constants and fields holding SQL belong to the type.
            foreach (var field in t.Syntax.ChildNodes().Where(n => n is VBS.FieldDeclarationSyntax or CSS.FieldDeclarationSyntax))
                Read(t.Map, t.Node, field, t.File);
        };
    }

    private void Read(SystemMap map, Node owner, SyntaxNode syntax, string file)
    {
        var done = new HashSet<SyntaxNode>();
        var mentionsStoredProcedure = syntax.ToString().Contains("StoredProcedure", StringComparison.Ordinal);
        foreach (var literal in syntax.DescendantNodes().Where(IsStringLiteral))
        {
            var top = Top(literal);
            if (!done.Add(top)) continue;
            var (text, built) = Flatten(top);
            var line = DotNetReader.Line(literal);
            if (SqlReader.LooksLikeSql(text))
            {
                owner.Props["sql"] = "embedded";
                if (built) owner.Props["sqlBuiltByHand"] = "true";
                sql.AnalyzeSql(owner.Id, text, file, line, built ? Certainty.Guessed : Certainty.Resolved);
                continue;
            }
            if (built || SqlNames.Parse(text) is not { } name || text.Contains(' ')) continue;
            var id = SqlNames.ObjectId(name.Schema, name.Name);
            if (map[id] is { Kind: NodeKind.Procedure or NodeKind.Function } known)
                map.Link(owner.Id, known.Id, EdgeKind.Executes, file, line, Certainty.Resolved, DotNetReader.Snippet(Statement(literal)));
            else if (mentionsStoredProcedure && IsCommandText(literal))
                map.Link(owner.Id, sql.Ensure(name, NodeKind.Procedure).Id, EdgeKind.Executes, file, line, Certainty.ByName, DotNetReader.Snippet(Statement(literal)));
        }
    }

    private static bool IsStringLiteral(SyntaxNode n) =>
        n is VBS.LiteralExpressionSyntax { Token.RawKind: (int)VB.SyntaxKind.StringLiteralToken }
        || n is CSS.LiteralExpressionSyntax { Token.RawKind: (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralToken }
        || n is VBS.InterpolatedStringExpressionSyntax or CSS.InterpolatedStringExpressionSyntax;

    /// <summary>The whole concatenation a literal is part of (<c>"SELECT … '" &amp; name &amp; "%'"</c>).</summary>
    private static SyntaxNode Top(SyntaxNode literal)
    {
        var n = literal;
        while (n.Parent is VBS.BinaryExpressionSyntax vb && (vb.IsKind(VB.SyntaxKind.ConcatenateExpression) || vb.IsKind(VB.SyntaxKind.AddExpression))
               || n.Parent is CSS.BinaryExpressionSyntax cs && cs.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.AddExpression)
               || n.Parent is VBS.ParenthesizedExpressionSyntax or CSS.ParenthesizedExpressionSyntax)
            n = n.Parent;
        return n;
    }

    /// <summary>The text of a string expression, with "0" for every part only known at run time, and whether there were any.</summary>
    private static (string Text, bool Built) Flatten(SyntaxNode expr)
    {
        var sb = new System.Text.StringBuilder();
        var built = false;
        void Walk(SyntaxNode e)
        {
            switch (e)
            {
                case VBS.LiteralExpressionSyntax l when l.Token.IsKind(VB.SyntaxKind.StringLiteralToken):
                    sb.Append(l.Token.ValueText);
                    break;
                case CSS.LiteralExpressionSyntax l when l.Token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralToken):
                    sb.Append(l.Token.ValueText);
                    break;
                case VBS.BinaryExpressionSyntax b:
                    Walk(b.Left);
                    Walk(b.Right);
                    break;
                case CSS.BinaryExpressionSyntax b:
                    Walk(b.Left);
                    Walk(b.Right);
                    break;
                case VBS.ParenthesizedExpressionSyntax p:
                    Walk(p.Expression);
                    break;
                case CSS.ParenthesizedExpressionSyntax p:
                    Walk(p.Expression);
                    break;
                case VBS.InterpolatedStringExpressionSyntax vi:
                    foreach (var c in vi.Contents)
                        if (c is VBS.InterpolatedStringTextSyntax t) sb.Append(t.TextToken.ValueText);
                        else
                        {
                            sb.Append('0');
                            built = true;
                        }
                    break;
                case CSS.InterpolatedStringExpressionSyntax ci:
                    foreach (var c in ci.Contents)
                        if (c is CSS.InterpolatedStringTextSyntax t) sb.Append(t.TextToken.ValueText);
                        else
                        {
                            sb.Append('0');
                            built = true;
                        }
                    break;
                default:
                    sb.Append('0');
                    built = true;
                    break;
            }
        }
        Walk(expr);
        return (sb.ToString(), built);
    }

    /// <summary>A command's text: the first argument of <c>New SqlCommand(…)</c> (any *Command), or a value given to CommandText.</summary>
    private static bool IsCommandText(SyntaxNode literal)
    {
        var arg = literal.Parent;
        if (arg is VBS.SimpleArgumentSyntax { Parent: VBS.ArgumentListSyntax { Parent: VBS.ObjectCreationExpressionSyntax vo } va } && va.Arguments.First() == arg)
            return vo.Type.ToString().EndsWith("Command", StringComparison.Ordinal);
        if (arg is CSS.ArgumentSyntax { Parent: CSS.ArgumentListSyntax { Parent: CSS.ObjectCreationExpressionSyntax co } ca } && ca.Arguments.First() == arg)
            return co.Type.ToString().EndsWith("Command", StringComparison.Ordinal);
        return literal.Parent switch
        {
            VBS.AssignmentStatementSyntax { Left: VBS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CommandText" } } => true,
            CSS.AssignmentExpressionSyntax { Left: CSS.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CommandText" } } => true,
            _ => false,
        };
    }

    private static SyntaxNode Statement(SyntaxNode node)
    {
        for (var n = node; n != null; n = n.Parent)
            if (n is VBS.StatementSyntax or CSS.StatementSyntax) return n;
        return node;
    }
}
