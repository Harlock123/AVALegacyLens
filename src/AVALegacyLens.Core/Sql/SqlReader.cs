using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AVALegacyLens.Core.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace AVALegacyLens.Core.Sql;

/// <summary>
/// Reads T-SQL with Microsoft's parser (ScriptDom): table definitions become <see cref="NodeKind.Table"/> nodes with
/// their columns, keys and checks; procedures, views, functions and triggers become nodes whose bodies are read for the
/// tables and columns they read and write and the procedures they run. The same analysis reads SQL found in code
/// (<see cref="AnalyzeSql"/>). Definitions are read first, from every script and the live database, so bodies and code
/// can resolve against them; then <see cref="AnalyzeModules"/> reads the bodies.
/// </summary>
public sealed partial class SqlReader(SystemMap map)
{
    /// <summary>A body to read once every definition is known: whose it is, its syntax, and where its lines start.</summary>
    private sealed record Module(string Id, TSqlFragment Body, string? File, int LineOffset, string? TriggerTable);

    private readonly List<Module> _modules = [];

    /// <summary>The map being added to (for the nested walkers).</summary>
    private SystemMap Map => map;

    /// <summary>The group node for tables only seen used (in code or SQL) and never defined in what was scanned.</summary>
    public const string ReferencedGroup = "database:(referenced)";

    [GeneratedRegex(@"^\s*GO\s*(\d+)?\s*(--.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex GoLine();

    /// <summary>Reads a .sql script: split into batches at each GO, each parsed on its own so one bad batch costs only itself.</summary>
    public void ReadScript(string file, string text, string parentId)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var batch = new StringBuilder();
        var start = 0;
        for (var i = 0; i <= lines.Length; i++)
        {
            if (i == lines.Length || GoLine().IsMatch(lines[i]))
            {
                if (batch.ToString().Trim().Length > 0) ReadBatch(file, batch.ToString(), start, parentId, null);
                batch.Clear();
                start = i + 1;
                continue;
            }
            batch.Append(lines[i]).Append('\n');
        }
    }

    /// <summary>Reads one module's text from a live database (no file: its text is kept on the node to be shown).</summary>
    public void ReadModuleText(string text, string parentId, string where)
    {
        var before = map.Nodes.Count;
        ReadBatch(null, text, 0, parentId, where);
        if (map.Nodes.Count == before) return;
        // The node it made is the one whose sql isn't set yet.
        foreach (var n in map.Children(parentId).Where(n => n.Kind is NodeKind.Procedure or NodeKind.View or NodeKind.Function or NodeKind.Trigger && n.Prop("sql") == null))
            n.Props["sql"] = text.Length > 200_000 ? text[..200_000] : text;
    }

    private void ReadBatch(string? file, string text, int lineOffset, string parentId, string? where)
    {
        var fragment = SqlNames.Parser().Parse(new StringReader(text), out var errors);
        if (errors.Count > 0)
        {
            var e = errors[0];
            map.Notes.Add(new ScanNote(file ?? where ?? "?", e.Line + lineOffset, $"T-SQL that doesn't parse: {e.Message}"));
            return;
        }
        if (fragment is not TSqlScript script) return;
        foreach (var statement in script.Batches.SelectMany(b => b.Statements))
            ReadStatement(statement, file, lineOffset, parentId, script);
    }

    private void ReadStatement(TSqlStatement statement, string? file, int lineOffset, string parentId, TSqlScript script)
    {
        switch (statement)
        {
            case CreateTableStatement table:
                ReadTable(table, file, lineOffset, parentId);
                break;
            case ProcedureStatementBody proc when SqlNames.Of(proc.ProcedureReference?.Name) is { } name:
                {
                    var node = Define(name, NodeKind.Procedure, "procedure", statement, file, lineOffset, parentId);
                    node.Props["parameters"] = string.Join(", ", proc.Parameters.Select(p => $"{p.VariableName.Value} {TypeText(p.DataType)}"));
                    node.Props["complexity"] = Complexity(proc).ToString(CultureInfo.InvariantCulture);
                    _modules.Add(new Module(node.Id, proc.StatementList, file, lineOffset, null));
                    break;
                }
            case ViewStatementBody view when SqlNames.Of(view.SchemaObjectName) is { } name:
                {
                    var node = Define(name, NodeKind.View, "view", statement, file, lineOffset, parentId);
                    _modules.Add(new Module(node.Id, view.SelectStatement, file, lineOffset, null));
                    break;
                }
            case FunctionStatementBody fn when SqlNames.Of(fn.Name) is { } name:
                {
                    var node = Define(name, NodeKind.Function, fn.ReturnType is SelectFunctionReturnType or TableValuedFunctionReturnType ? "table function" : "scalar function", statement, file, lineOffset, parentId);
                    node.Props["parameters"] = string.Join(", ", fn.Parameters.Select(p => $"{p.VariableName.Value} {TypeText(p.DataType)}"));
                    node.Props["complexity"] = Complexity(fn).ToString(CultureInfo.InvariantCulture);
                    _modules.Add(new Module(node.Id, (TSqlFragment?)fn.StatementList ?? fn.ReturnType, file, lineOffset, null));
                    break;
                }
            case TriggerStatementBody trigger when SqlNames.Of(trigger.Name) is { } name:
                {
                    var node = Define(name, NodeKind.Trigger, "trigger", statement, file, lineOffset, parentId);
                    string? tableId = null;
                    if (SqlNames.Of(trigger.TriggerObject?.Name) is { } on)
                    {
                        tableId = Ensure(on, NodeKind.Table).Id;
                        var when = $"{trigger.TriggerType.ToString().ToUpperInvariant()} {string.Join(", ", trigger.TriggerActions.Select(a => a.TriggerActionType.ToString().ToUpperInvariant()))}";
                        node.Props["fires"] = when;
                        map.Link(node.Id, tableId, EdgeKind.Handles, file, Line(statement, lineOffset), Certainty.Resolved, when);
                    }
                    _modules.Add(new Module(node.Id, trigger.StatementList, file, lineOffset, tableId));
                    break;
                }
            case AlterTableAddTableElementStatement alter when SqlNames.Of(alter.SchemaObjectName) is { } name:
                {
                    var table = Ensure(name, NodeKind.Table);
                    foreach (var c in alter.Definition?.TableConstraints ?? [])
                        ReadConstraint(table, null, c, file, lineOffset);
                    break;
                }
            case InsertStatement or UpdateStatement or DeleteStatement or MergeStatement or ExecuteStatement or SelectStatement when file != null:
                // Top-level DML in a script (seed data, a migration): the script's own reads and writes.
                _modules.Add(new Module("file:" + file, statement, file, lineOffset, null));
                break;
        }
    }

    private void ReadTable(CreateTableStatement table, string? file, int lineOffset, string parentId)
    {
        if (SqlNames.Of(table.SchemaObjectName) is not { } name || name.Name.StartsWith('#')) return;
        var node = Define(name, NodeKind.Table, "table", table, file, lineOffset, parentId);
        foreach (var col in table.Definition?.ColumnDefinitions ?? [])
        {
            var column = Column(node, col.ColumnIdentifier.Value, file, Line(col, lineOffset));
            column.Props["type"] = col.ComputedColumnExpression != null ? "computed" : TypeText(col.DataType);
            column.Props["nullable"] = "true";
            if (col.IdentityOptions != null) column.Props["identity"] = "true";
            if (col.DefaultConstraint?.Expression is { } d) column.Props["default"] = Text(d);
            if (col.ComputedColumnExpression is { } computed) column.Props["computed"] = Text(computed);
            foreach (var c in col.Constraints)
            {
                if (c is NullableConstraintDefinition n) column.Props["nullable"] = n.Nullable ? "true" : "false";
                else ReadConstraint(node, column, c, file, lineOffset);
            }
        }
        foreach (var c in table.Definition?.TableConstraints ?? [])
            ReadConstraint(node, null, c, file, lineOffset);
    }

    private void ReadConstraint(Node table, Node? column, ConstraintDefinition c, string? file, int lineOffset)
    {
        var line = Line(c, lineOffset);
        switch (c)
        {
            case UniqueConstraintDefinition u:
                {
                    var cols = u.Columns.Count > 0 ? u.Columns.Select(x => Last(x.Column)).ToList() : column != null ? [column.Name] : [];
                    foreach (var name in cols)
                    {
                        var col = Column(table, name, null, 0);
                        col.Props["key"] = u.IsPrimaryKey ? "primary" : "unique";
                        col.Props["nullable"] = u.IsPrimaryKey ? "false" : col.Prop("nullable") ?? "true";
                    }
                    break;
                }
            case ForeignKeyConstraintDefinition fk when SqlNames.Of(fk.ReferenceTableName) is { } refName:
                {
                    var target = Ensure(refName, NodeKind.Table);
                    var from = fk.Columns.Count > 0 ? fk.Columns.Select(x => x.Value).ToList() : column != null ? [column.Name] : [];
                    var to = fk.ReferencedTableColumns.Select(x => x.Value).ToList();
                    var note = $"foreign key {string.Join(", ", from)} → {target.Name}" + (to.Count > 0 ? $"({string.Join(", ", to)})" : "");
                    map.Link(table.Id, target.Id, EdgeKind.Uses, file, line, Certainty.Resolved, note);
                    for (var i = 0; i < from.Count; i++)
                    {
                        var col = Column(table, from[i], null, 0);
                        col.Props["references"] = target.Name + (i < to.Count ? "." + to[i] : "");
                        if (i < to.Count) map.Link(col.Id, Column(target, to[i], null, 0).Id, EdgeKind.Uses, file, line, Certainty.Resolved, "foreign key");
                    }
                    break;
                }
            case CheckConstraintDefinition check:
                {
                    var name = check.ConstraintIdentifier?.Value ?? (column != null ? $"check on {column.Name}" : "check");
                    table.Props["check " + name] = Text(check.CheckCondition);
                    if (column != null) column.Props["check"] = Text(check.CheckCondition);
                    break;
                }
            case DefaultConstraintDefinition d when column != null:
                column.Props["default"] = Text(d.Expression);
                break;
        }
    }

    /// <summary>A defined object: a node under its script (or database), replacing a stand-in made when it was only seen used.</summary>
    private Node Define((string Schema, string Name) name, NodeKind kind, string subkind, TSqlFragment fragment, string? file, int lineOffset, string parentId)
    {
        var id = SqlNames.ObjectId(name.Schema, name.Name);
        var existing = map[id];
        if (existing != null && existing.Prop("defined") != "false")
        {
            // Defined twice (a script and the live database, or an ALTER script): the first one stays.
            var also = file ?? "database";
            existing.Props["alsoDefinedIn"] = existing.Prop("alsoDefinedIn") is { } a ? a + "|" + also : also;
            return existing;
        }
        var node = new Node
        {
            Id = id,
            Kind = kind,
            Name = SqlNames.Display(name.Schema, name.Name),
            Parent = parentId,
            File = file,
            Line = file == null ? 0 : Line(fragment, lineOffset),
            EndLine = file == null ? 0 : fragment.ScriptTokenStream?[fragment.LastTokenIndex].Line + lineOffset ?? 0,
            Language = "T-SQL",
        };
        node.Props["schema"] = name.Schema;
        node.Props["subkind"] = subkind;
        if (file != null && node.EndLine >= node.Line) node.Props["lines"] = (node.EndLine - node.Line + 1).ToString(CultureInfo.InvariantCulture);
        if (existing != null)
        {
            // It was a stand-in: take its place, keeping its links (and its columns, which name it as their parent).
            map.Replace(node);
            return node;
        }
        return map.Add(node);
    }

    /// <summary>An object by name: the defined one, or a stand-in under "referenced" until (if ever) its definition is read.</summary>
    public Node Ensure((string Schema, string Name) name, NodeKind kind)
    {
        var id = SqlNames.ObjectId(name.Schema, name.Name);
        if (map[id] is { } found) return found;
        if (!map.Contains(ReferencedGroup))
            map.Add(new Node { Id = ReferencedGroup, Kind = NodeKind.Project, Name = "Used but not defined here", Props = { ["subkind"] = "database" } });
        var node = map.Add(new Node { Id = id, Kind = kind, Name = SqlNames.Display(name.Schema, name.Name), Parent = ReferencedGroup, Language = "T-SQL" });
        node.Props["schema"] = name.Schema;
        node.Props["defined"] = "false";
        node.Props["subkind"] = kind.ToString().ToLowerInvariant();
        return node;
    }

    private Node Column(Node table, string name, string? file, int line)
    {
        var id = SqlNames.ColumnId(table.Id, name);
        if (map[id] is { } found)
        {
            if (file != null && found.File == null)
            {
                found.File = file;
                found.Line = found.EndLine = line;
            }
            return found;
        }
        var node = map.Add(new Node { Id = id, Kind = NodeKind.Column, Name = name, Parent = table.Id, File = file, Line = line, EndLine = line, Language = "T-SQL" });
        node.Props["table"] = table.Name;
        if (table.Prop("defined") == "false") node.Props["defined"] = "false";
        return node;
    }

    /// <summary>The columns a defined table has, by name (for working out which table an unqualified column is in).</summary>
    private bool HasColumn(string tableId, string column) => map.Contains(SqlNames.ColumnId(tableId, column)) && map[tableId]?.Prop("defined") != "false";

    /// <summary>Reads every body found while reading definitions. Call once all definitions are in.</summary>
    public void AnalyzeModules()
    {
        foreach (var m in _modules)
            Analyze(m.Id, m.Body, m.File, m.LineOffset, Certainty.Resolved, m.TriggerTable, null);
        _modules.Clear();
    }

    /// <summary>
    /// Reads SQL found somewhere (a string in code): its reads, writes and procedure calls become links from
    /// <paramref name="from"/>, all at <paramref name="line"/>. Returns false if it doesn't parse, after trying a
    /// rougher reading of its table names (marked <see cref="Certainty.Guessed"/>).
    /// </summary>
    public bool AnalyzeSql(string from, string sql, string? file, int line, Certainty certainty)
    {
        var fragment = SqlNames.Parser().Parse(new StringReader(sql), out var errors);
        if (errors.Count > 0 || fragment == null)
        {
            Rough(from, sql, file, line);
            return false;
        }
        Analyze(from, fragment, file, 0, certainty, null, line);
        return true;
    }

    [GeneratedRegex(@"\b(?<verb>DELETE\s+FROM|DELETE|UPDATE|INSERT\s+INTO|INSERT|MERGE\s+INTO|MERGE|TRUNCATE\s+TABLE|FROM|JOIN|INTO|EXEC|EXECUTE)\s+(?<name>(\[[^\]]+\]|[A-Za-z_#@][\w#@$]*)(\s*\.\s*(\[[^\]]+\]|[A-Za-z_][\w$]*)){0,3})", RegexOptions.IgnoreCase)]
    private static partial Regex RoughName();

    /// <summary>For SQL that won't parse (pieces of a string built at run time): table names after FROM, JOIN, INTO, UPDATE…</summary>
    private void Rough(string from, string sql, string? file, int line)
    {
        foreach (Match m in RoughName().Matches(sql))
        {
            if (SqlNames.Parse(Regex.Replace(m.Groups["name"].Value, @"\s+", "")) is not { } name || name.Name.StartsWith('#') || name.Name.StartsWith('@')) continue;
            var verb = Regex.Replace(m.Groups["verb"].Value.ToUpperInvariant(), @"\s+", " ");
            if (verb is "EXEC" or "EXECUTE")
            {
                map.Link(from, Ensure(name, NodeKind.Procedure).Id, EdgeKind.Executes, file, line, Certainty.Guessed, Snip(sql));
                continue;
            }
            if (Keyword(name.Name)) continue;
            var kind = verb is "FROM" or "JOIN" ? EdgeKind.Reads : EdgeKind.Writes;
            map.Link(from, Ensure(name, NodeKind.Table).Id, kind, file, line, Certainty.Guessed, Snip(sql));
        }
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase) { "SELECT", "WHERE", "SET", "VALUES", "TOP", "DISTINCT", "OUTPUT", "WITH", "AS", "ON", "dbo" };

    private static bool Keyword(string name) => Keywords.Contains(name);

    /// <summary>The reads, writes and calls in a body, as links from <paramref name="from"/>.</summary>
    private void Analyze(string from, TSqlFragment body, string? file, int lineOffset, Certainty certainty, string? triggerTable, int? fixedLine)
    {
        var walker = new BodyWalker(this, from, file, lineOffset, certainty, triggerTable, fixedLine);
        body.Accept(walker);
        walker.Finish();
        if (walker.Dynamic && map[from] is { } owner) owner.Props["dynamicSql"] = "true";
    }

    /// <summary>One statement's tables (with aliases), columns, and targets: the unit columns are resolved within.</summary>
    private sealed class Scope
    {
        public List<NamedTableReference> Tables { get; } = [];
        public List<ColumnReferenceExpression> Columns { get; } = [];
        public List<(TableReference Target, IList<ColumnReferenceExpression> Columns, string Verb, TSqlFragment At)> Writes { get; } = [];
        public List<SchemaObjectName> Into { get; } = [];
        public HashSet<string> Ctes { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ScopeCollector(Scope scope) : TSqlFragmentVisitor
    {
        public override void ExplicitVisit(NamedTableReference node)
        {
            scope.Tables.Add(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            scope.Columns.Add(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InsertSpecification node)
        {
            scope.Writes.Add((node.Target, node.Columns, "INSERT", node));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateSpecification node)
        {
            scope.Writes.Add((node.Target, node.SetClauses.OfType<AssignmentSetClause>().Where(s => s.Column != null).Select(s => s.Column).ToList(), "UPDATE", node));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteSpecification node)
        {
            scope.Writes.Add((node.Target, [], "DELETE", node));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(MergeSpecification node)
        {
            scope.Writes.Add((node.Target, [], "MERGE", node));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            if (node.Into != null) scope.Into.Add(node.Into);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CommonTableExpression node)
        {
            scope.Ctes.Add(node.ExpressionName.Value);
            base.ExplicitVisit(node);
        }

        // A procedure call inside the statement is the walker's (it's seen at the statement level).
    }

    /// <summary>
    /// Walks a body: each DML statement (and each query outside one, as in IF EXISTS) is a scope read on its own;
    /// EXEC is a procedure call, or dynamic SQL when it runs a string.
    /// </summary>
    private sealed class BodyWalker(SqlReader reader, string from, string? file, int lineOffset, Certainty certainty, string? triggerTable, int? fixedLine) : TSqlFragmentVisitor
    {
        public bool Dynamic { get; private set; }
        private readonly List<string> _literals = [];
        private int? _dynamicLine;

        private int LineOf(TSqlFragment f) => fixedLine is { } l ? l + Math.Max(0, f.StartLine - 1) : f.StartLine + lineOffset;

        public override void ExplicitVisit(SelectStatement node) => ReadScope(node);
        public override void ExplicitVisit(InsertStatement node) => ReadScope(node);
        public override void ExplicitVisit(UpdateStatement node) => ReadScope(node);
        public override void ExplicitVisit(DeleteStatement node) => ReadScope(node);
        public override void ExplicitVisit(MergeStatement node) => ReadScope(node);
        public override void ExplicitVisit(QuerySpecification node) => ReadScope(node);

        public override void ExplicitVisit(ExecuteSpecification node)
        {
            switch (node.ExecutableEntity)
            {
                case ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name: { } name } when SqlNames.Of(name) is { } proc:
                    if (proc.Name.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase))
                    {
                        Dynamic = true;
                        _dynamicLine ??= LineOf(node);
                    }
                    else reader.Map.Link(from, reader.Ensure(proc, NodeKind.Procedure).Id, EdgeKind.Executes, file, LineOf(node), certainty, Snippet(node));
                    break;
                case ExecutableStringList:
                    Dynamic = true;
                    _dynamicLine ??= LineOf(node);
                    break;
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(StringLiteral node)
        {
            _literals.Add(node.Value);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            // A user function called in an expression: dbo.fn_Age(@d). Built-in functions have no schema.
            if (node.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: { Count: > 0 } ids })
            {
                var name = (ids[^1].Value, node.FunctionName.Value);
                var id = SqlNames.ObjectId(name.Item1, name.Item2);
                if (reader.Map.Contains(id) || name.Item1.Equals(SqlNames.DefaultSchema, StringComparison.OrdinalIgnoreCase))
                    reader.Map.Link(from, reader.Ensure(name, NodeKind.Function).Id, EdgeKind.Calls, file, LineOf(node), certainty, Snippet(node));
            }
            base.ExplicitVisit(node);
        }

        /// <summary>Dynamic SQL: what its string pieces name, guessed. Called once the whole body is walked.</summary>
        public void Finish()
        {
            if (!Dynamic) return;
            foreach (var text in _literals.Where(t => SqlStart().IsMatch(t)))
                reader.Rough(from, text, file, _dynamicLine ?? fixedLine ?? 0);
        }

        private void ReadScope(TSqlFragment statement)
        {
            var scope = new Scope();
            statement.Accept(new ScopeCollector(scope));
            reader.ReadScope(scope, from, file, certainty, triggerTable, LineOf(statement), Snippet(statement));
            // Look inside for what the scope collector leaves: EXEC, user functions, string literals.
            statement.Accept(new InnerWalker(this));
        }

        /// <summary>Visits a statement already read as a scope for its calls only, not its tables again.</summary>
        private sealed class InnerWalker(BodyWalker outer) : TSqlFragmentVisitor
        {
            public override void ExplicitVisit(ExecuteSpecification node) => outer.ExplicitVisit(node);
            public override void ExplicitVisit(FunctionCall node) => outer.ExplicitVisit(node);
            public override void ExplicitVisit(StringLiteral node) => outer.ExplicitVisit(node);
        }
    }

    [GeneratedRegex(@"^\s*(SELECT|INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE|WITH|TRUNCATE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SqlStart();

    /// <summary>Whether a string looks like SQL (it starts with a statement's keyword).</summary>
    public static bool LooksLikeSql(string text) => SqlStart().IsMatch(text);

    private void ReadScope(Scope scope, string from, string? file, Certainty certainty, string? triggerTable, int line, string note)
    {
        // Tables by alias and name, leaving out temporary tables, CTEs and a trigger's inserted/deleted rows.
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tables = new List<(NamedTableReference Ref, string Id)>();
        foreach (var t in scope.Tables)
        {
            if (SqlNames.Of(t.SchemaObject) is not { } name || name.Name.StartsWith('#') || name.Name.StartsWith('@')) continue;
            if (t.SchemaObject.SchemaIdentifier == null && scope.Ctes.Contains(name.Name)) continue;
            string id;
            if (t.SchemaObject.SchemaIdentifier == null && name.Name is var n && (n.Equals("inserted", StringComparison.OrdinalIgnoreCase) || n.Equals("deleted", StringComparison.OrdinalIgnoreCase)))
            {
                if (triggerTable == null) continue;
                id = triggerTable;
                if (t.Alias != null) aliases[t.Alias.Value] = id;
                aliases[n] = id;
                continue;
            }
            // An unqualified name that is an alias of another table in this statement (UPDATE p ... FROM Patient p).
            if (t.SchemaObject.SchemaIdentifier == null && scope.Tables.Any(o => o != t && o.Alias?.Value is { } a && a.Equals(name.Name, StringComparison.OrdinalIgnoreCase))) continue;
            id = Ensure(name, NodeKind.Table).Id;
            tables.Add((t, id));
            aliases[name.Name] = id;
            if (t.Alias != null) aliases[t.Alias.Value] = id;
        }

        // Writes: the target (through its alias), and the columns named.
        var targets = new HashSet<NamedTableReference>();
        var written = new HashSet<ColumnReferenceExpression>();
        foreach (var (target, columns, verb, at) in scope.Writes)
        {
            if (target is not NamedTableReference named || SqlNames.Of(named.SchemaObject) is not { } tn) continue;
            targets.Add(named);
            string? id = named.SchemaObject.SchemaIdentifier == null && aliases.TryGetValue(tn.Name, out var aliased) ? aliased : null;
            if (id == null)
            {
                if (tn.Name.StartsWith('#') || tn.Name.StartsWith('@') || named.SchemaObject.SchemaIdentifier == null && scope.Ctes.Contains(tn.Name)) continue;
                id = Ensure(tn, NodeKind.Table).Id;
            }
            map.Link(from, id, EdgeKind.Writes, file, line, certainty, verb + ": " + note);
            foreach (var c in columns)
            {
                written.Add(c);
                if (map[id] is { } table) map.Link(from, Column(table, Last(c), null, 0).Id, EdgeKind.Writes, file, line, certainty, verb + ": " + note);
            }
        }
        foreach (var into in scope.Into)
            if (SqlNames.Of(into) is { } name && !name.Name.StartsWith('#'))
                map.Link(from, Ensure(name, NodeKind.Table).Id, EdgeKind.Writes, file, line, certainty, "SELECT INTO: " + note);

        // Reads: every other table, and the columns that resolve to one.
        foreach (var (r, id) in tables)
            if (!targets.Contains(r)) map.Link(from, id, EdgeKind.Reads, file, line, certainty, note);
        var inScope = tables.Select(t => t.Id).Distinct().ToList();
        foreach (var c in scope.Columns)
        {
            if (written.Contains(c) || c.ColumnType != ColumnType.Regular || c.MultiPartIdentifier is not { Identifiers.Count: > 0 } mpi) continue;
            var column = mpi.Identifiers[^1].Value;
            if (column.StartsWith('@')) continue;
            string? tableId = null;
            if (mpi.Identifiers.Count >= 2)
                aliases.TryGetValue(mpi.Identifiers[^2].Value, out tableId);
            else if (inScope.Count == 1)
                tableId = inScope[0];
            else
            {
                var having = inScope.Where(t => HasColumn(t, column)).ToList();
                if (having.Count == 1) tableId = having[0];
            }
            if (tableId == null || map[tableId] is not { } table) continue;
            // A column the table is known not to have isn't one of its columns (a variable, an alias of an expression).
            if (table.Prop("defined") != "false" && !map.Contains(SqlNames.ColumnId(tableId, column))) continue;
            map.Link(from, Column(table, column, null, 0).Id, EdgeKind.Reads, file, line, certainty, note);
        }
    }

    // ----- Small helpers -----

    private static int Line(TSqlFragment f, int lineOffset) => f.StartLine + lineOffset;

    private static string Last(ColumnReferenceExpression c) => c.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value ?? "?";

    /// <summary>A fragment's text as written.</summary>
    public static string Text(TSqlFragment f)
    {
        if (f.ScriptTokenStream == null || f.FirstTokenIndex < 0) return "";
        var sb = new StringBuilder();
        for (var i = f.FirstTokenIndex; i <= f.LastTokenIndex; i++) sb.Append(f.ScriptTokenStream[i].Text);
        return sb.ToString().Trim();
    }

    /// <summary>A statement on one line, cut short.</summary>
    public static string Snippet(TSqlFragment f) => Snip(Text(f));

    private static string Snip(string text)
    {
        var one = Regex.Replace(text, @"\s+", " ").Trim();
        return one.Length > 160 ? one[..157] + "…" : one;
    }

    private static string TypeText(DataTypeReference? type) => type == null ? "?" : Text(type);

    /// <summary>Cyclomatic complexity of a body: one, plus IF, WHILE, each CASE branch, and CATCH.</summary>
    private static int Complexity(TSqlFragment body)
    {
        var counter = new ComplexityCounter();
        body.Accept(counter);
        return counter.Count;
    }

    private sealed class ComplexityCounter : TSqlFragmentVisitor
    {
        public int Count { get; private set; } = 1;
        public override void Visit(IfStatement node) { Count++; base.Visit(node); }
        public override void Visit(WhileStatement node) { Count++; base.Visit(node); }
        public override void Visit(SearchedWhenClause node) { Count++; base.Visit(node); }
        public override void Visit(SimpleWhenClause node) { Count++; base.Visit(node); }
        public override void Visit(TryCatchStatement node) { Count++; base.Visit(node); }
        public override void Visit(BooleanBinaryExpression node) { Count++; base.Visit(node); }
    }
}
