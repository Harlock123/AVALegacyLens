using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace AVALegacyLens.Core.Sql;

/// <summary>
/// Names of database objects as the map keeps them: ids are <c>db:schema.name</c> in lower case (T-SQL names don't
/// care about case by default), display names drop the default <c>dbo</c> schema. Columns are <c>db:schema.table.column</c>.
/// </summary>
public static class SqlNames
{
    public const string DefaultSchema = "dbo";

    public static string ObjectId(string schema, string name) => $"db:{schema}.{name}".ToLowerInvariant();

    public static string ColumnId(string tableId, string column) => $"{tableId}.{column}".ToLowerInvariant();

    public static string Display(string schema, string name) => string.Equals(schema, DefaultSchema, StringComparison.OrdinalIgnoreCase) ? name : $"{schema}.{name}";

    /// <summary>Schema and name of a ScriptDom name (<c>[dbo].[Patient]</c>, <c>Patient</c>, <c>Clinic.dbo.Patient</c>).</summary>
    public static (string Schema, string Name)? Of(SchemaObjectName? name)
    {
        if (name?.BaseIdentifier?.Value is not { Length: > 0 } baseName) return null;
        return (name.SchemaIdentifier?.Value is { Length: > 0 } s ? s : DefaultSchema, baseName);
    }

    /// <summary>Parses a plain name (<c>usp_GetVisits</c>, <c>dbo.usp_GetVisits</c>, <c>[dbo].[x]</c>) as a string in code.</summary>
    public static (string Schema, string Name)? Parse(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Length > 256) return null;
        var parts = new List<string>();
        var i = 0;
        while (i < t.Length)
        {
            string part;
            if (t[i] == '[')
            {
                var end = t.IndexOf(']', i + 1);
                if (end < 0) return null;
                part = t[(i + 1)..end];
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < t.Length && (char.IsLetterOrDigit(t[i]) || t[i] is '_' or '#' or '@' or '$')) i++;
                if (i == start) return null;
                part = t[start..i];
            }
            parts.Add(part);
            if (i == t.Length) break;
            if (t[i] != '.') return null;
            i++;
            if (i == t.Length) return null;
        }
        if (parts.Count is 0 or > 4 || !char.IsLetter(parts[^1][0]) && parts[^1][0] != '_') return null;
        return (parts.Count >= 2 ? parts[^2] : DefaultSchema, parts[^1]);
    }

    /// <summary>A ScriptDom parser for the newest T-SQL it knows, with quoted identifiers on.</summary>
    public static TSqlParser Parser() => new TSql170Parser(initialQuotedIdentifiers: true);
}
