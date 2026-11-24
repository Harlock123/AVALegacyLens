using System.Data.Common;
using AVALegacyLens.Core.Model;
using Microsoft.Data.SqlClient;

namespace AVALegacyLens.Core.Sql;

/// <summary>
/// Reads a live SQL Server database's schema from its catalog views: tables and columns (types, nulls, identity,
/// defaults), primary and foreign keys, check constraints, and the text of every procedure, view, function and trigger,
/// which goes through <see cref="SqlReader"/> as a script would. Only the catalog is queried: <b>no table's rows are
/// ever read</b>. The connection string isn't kept; the map names the database by server and catalog only.
/// </summary>
public static class DatabaseReader
{
    /// <summary>The group node's id for a database (its server and name, never its credentials).</summary>
    public static string GroupId(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        return $"database:{b.DataSource}/{b.InitialCatalog}".ToLowerInvariant();
    }

    public static void Read(SystemMap map, SqlReader sql, string connectionString, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var groupId = GroupId(connectionString);
        var where = $"{builder.DataSource}/{builder.InitialCatalog}";
        var group = map.Add(new Node { Id = groupId, Kind = NodeKind.Project, Name = $"{builder.InitialCatalog} (database on {builder.DataSource})", Props = { ["subkind"] = "database", ["server"] = builder.DataSource, ["database"] = builder.InitialCatalog } });
        progress?.Report($"Reading the schema of {where}…");
        using var cn = new SqlConnection(connectionString);
        cn.Open();

        // Tables, then their columns: given to the SQL reader as CREATE TABLE text, so tables are read one way only.
        var tables = Query(cn, """
            SELECT t.object_id, s.name, t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0 ORDER BY s.name, t.name
            """, r => (Id: r.GetInt32(0), Schema: r.GetString(1), Name: r.GetString(2)));
        var columns = Query(cn, """
            SELECT c.object_id, c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
                   dc.definition, cc.definition
            FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            JOIN sys.tables t ON t.object_id = c.object_id
            ORDER BY c.object_id, c.column_id
            """, r => (Table: r.GetInt32(0), Name: r.GetString(1), Type: TypeName(r.GetString(2), r.GetInt16(3), r.GetByte(4), r.GetByte(5)),
                Nullable: r.GetBoolean(6), Identity: r.GetBoolean(7), Default: r.IsDBNull(8) ? null : r.GetString(8), Computed: r.IsDBNull(9) ? null : r.GetString(9)));
        var keys = Query(cn, """
            SELECT i.object_id, c.name, i.is_primary_key FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.is_primary_key = 1 OR i.is_unique_constraint = 1
            """, r => (Table: r.GetInt32(0), Column: r.GetString(1), Primary: r.GetBoolean(2)));
        var foreignKeys = Query(cn, """
            SELECT fk.parent_object_id, pc.name, rs.name, rt.name, rc.name, fk.name
            FROM sys.foreign_key_columns fkc
            JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            """, r => (Table: r.GetInt32(0), Column: r.GetString(1), RefSchema: r.GetString(2), RefTable: r.GetString(3), RefColumn: r.GetString(4), Name: r.GetString(5)));
        var checks = Query(cn, "SELECT parent_object_id, name, definition FROM sys.check_constraints",
            r => (Table: r.GetInt32(0), Name: r.GetString(1), Definition: r.GetString(2)));

        foreach (var t in tables)
        {
            cancel.ThrowIfCancellationRequested();
            var lines = new List<string>();
            foreach (var c in columns.Where(c => c.Table == t.Id))
            {
                var def = c.Computed != null ? $"[{c.Name}] AS {c.Computed}" : $"[{c.Name}] {c.Type}{(c.Identity ? " IDENTITY" : "")} {(c.Nullable ? "NULL" : "NOT NULL")}{(c.Default != null ? " DEFAULT " + c.Default : "")}";
                lines.Add(def);
            }
            var pk = keys.Where(k => k.Table == t.Id && k.Primary).Select(k => $"[{k.Column}]").ToList();
            if (pk.Count > 0) lines.Add($"PRIMARY KEY ({string.Join(", ", pk)})");
            foreach (var u in keys.Where(k => k.Table == t.Id && !k.Primary)) lines.Add($"UNIQUE ([{u.Column}])");
            foreach (var fk in foreignKeys.Where(f => f.Table == t.Id))
                lines.Add($"CONSTRAINT [{fk.Name}] FOREIGN KEY ([{fk.Column}]) REFERENCES [{fk.RefSchema}].[{fk.RefTable}] ([{fk.RefColumn}])");
            foreach (var ck in checks.Where(c => c.Table == t.Id)) lines.Add($"CONSTRAINT [{ck.Name}] CHECK {ck.Definition}");
            if (lines.Count == 0) continue;
            sql.ReadModuleText($"CREATE TABLE [{t.Schema}].[{t.Name}] (\n  {string.Join(",\n  ", lines)}\n)", groupId, where);
        }

        // Procedures, views, functions and triggers: their own text, read as written.
        var modules = Query(cn, """
            SELECT s.name, o.name, m.definition FROM sys.sql_modules m
            JOIN sys.objects o ON o.object_id = m.object_id JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0 AND m.definition IS NOT NULL
            ORDER BY CASE o.type WHEN 'V' THEN 0 WHEN 'FN' THEN 1 WHEN 'IF' THEN 1 WHEN 'TF' THEN 1 WHEN 'P' THEN 2 ELSE 3 END, s.name, o.name
            """, r => (Schema: r.GetString(0), Name: r.GetString(1), Text: r.GetString(2)));
        foreach (var m in modules)
        {
            cancel.ThrowIfCancellationRequested();
            sql.ReadModuleText(m.Text, groupId, $"{where} {m.Schema}.{m.Name}");
        }
        group.Props["tables"] = tables.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        group.Props["modules"] = modules.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string TypeName(string type, short maxLength, byte precision, byte scale) => type switch
    {
        "varchar" or "char" or "varbinary" or "binary" => $"{type}({(maxLength < 0 ? "max" : maxLength.ToString(System.Globalization.CultureInfo.InvariantCulture))})",
        "nvarchar" or "nchar" => $"{type}({(maxLength < 0 ? "max" : (maxLength / 2).ToString(System.Globalization.CultureInfo.InvariantCulture))})",
        "decimal" or "numeric" => $"{type}({precision},{scale})",
        _ => type,
    };

    private static List<T> Query<T>(SqlConnection cn, string sql, Func<DbDataReader, T> read)
    {
        using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 120 };
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(read(r));
        return list;
    }
}
