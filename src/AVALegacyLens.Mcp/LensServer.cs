using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVALegacyLens.Mcp;

/// <summary>
/// The MCP server: <see cref="LensTools"/>' methods as MCP tools (the SDK's attribute model, read by reflection), with
/// instructions that tell an agent AVA Legacy Lens's conventions. The same server runs over stdio headless, over a
/// pipe for agents attached to the open window, over the network with a token, and in-process for the AI Chat.
/// </summary>
public static class LensServer
{
    public const string Name = "AVALegacyLens";

    /// <summary>What every agent is told on connecting: the conventions the tools' descriptions build on.</summary>
    public const string Instructions = """
        AVA Legacy Lens reads a legacy system's code and database schema (VB.NET, C#, VB6, T-SQL, config files, and live SQL Server catalogs the person added) into a system map, so a rebuild can start from what the system really does. It reads code and schema only, never a table's rows, and passwords are masked in everything it returns. The only files it writes are new ones in the working folder (reports, saved scans, steps files); the only thing it changes is the marks on findings, and only when the person says so.

        The map (it matters):
        - Items are projects, files, screens (WinForms forms and VB6 forms), controls, types, methods, tables, columns, procedures, views, functions, triggers, settings, connections and outside systems (shares, web addresses, native DLLs). Each has an id; tools also take readable names: frmPatient, frmPatient.btnSave, BillingRules.PostCharge, Patient, Patient.LastName, usp_SavePatient.
        - Links say how one item depends on another: calls, creates, inherits, handles (a method handling a control's event), reads and writes (tables and columns), runs (procedures), uses (settings, controls, outside systems). Each link has the file and line it was seen at and the code there.
        - Every link says how sure the scan is: resolved (a compiler bound it), by name (matched by name: across projects or languages, and all VB6), or guessed (from SQL built at run time). Never present a by-name or guessed link as certain; say so.
        - Findings are worked out from the map: business rules (in the original's own words where it has them), dead code (with a confidence: something outside the scan, such as a report or job, may still use medium and low ones), and risks. Each finding has an id; people mark them confirmed, wrong or noise. Use mark_finding only when the person has said what a finding is.
        - The estimate is a rough size in person-days from counts and stated weights, not a quote: present it as a range and say what it leaves out.

        A good way to work: get_inventory (it scans the working folder first if nothing is open; scan rescans or scans a folder in it); list_screens and describe_screen to see what a screen does end to end; trace (forward from a button, backward from a table or method) for how things connect; who_writes and who_reads for a table or column; list_rules, list_dead_code and list_risks for the findings; get_node and get_source to read an item and its code before saying what it does; get_estimate for the size of a rebuild; explain_sql for a query the person pastes; render_view to show the person an item's code or a trace, and show_in_view to select it in their window. export_report writes the lot as .html or .xlsx; save_lens keeps the scan; compare_scans says what changed between two. batch runs several calls at once; save_steps keeps a session's calls to run again on another system.

        Cite places as file:line when you say what code does, and quote the rule text as found. If something isn't in the scan (a table used but not defined, a call matched by name), say so rather than guess.

        The server's prompts are saved recipes (a screen end to end, the rules behind a table, what a change would break, the rebuild estimate, a rules catalogue, the risky code): step-by-step plans with the exact tool calls.
        """;


    /// <summary>The MCP tools, bound to a host.</summary>
    public static IEnumerable<McpServerTool> Tools(IExplorerHost host) => Tools(new LensTools(host));

    /// <summary>The MCP tools of one set of handlers (one session's: its checkpoints and its recording).</summary>
    public static IEnumerable<McpServerTool> Tools(LensTools target)
    {
        foreach (var method in typeof(LensTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (method.GetCustomAttribute<McpServerToolAttribute>() != null)
                yield return McpServerTool.Create(method, target);
    }

    /// <summary>The saved recipes (<see cref="Recipes"/>) as MCP prompts.</summary>
    public static IEnumerable<McpServerPrompt> Prompts() =>
        typeof(Recipes).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null)
            .Select(m => McpServerPrompt.Create(m, target: null));

    /// <summary>The tools' MCP definitions (names, descriptions, input schemas), without a host to run them.</summary>
    public static IList<Tool> Definitions() => [.. _definitions.Value];

    private static readonly Lazy<Tool[]> _definitions = new(() => Tools(new NoHost()).Select(t => t.ProtocolTool).ToArray());

    public static Implementation Info => new()
    {
        Name = Name,
        Title = "AVA Legacy Lens",
        Version = typeof(LensServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0",
    };

    public static McpServerOptions Options(IExplorerHost host) => Configure(new McpServerOptions(), host);

    /// <summary>Fills a new session's options (ones the network door's HTTP handler made) as <see cref="Options"/> makes them.</summary>
    public static McpServerOptions Configure(McpServerOptions options, IExplorerHost host)
    {
        options.ServerInfo = Info;
        options.ServerInstructions = Instructions;
        options.ToolCollection = [];
        var target = new LensTools(host);
        options.Filters.Request.CallToolFilters.Add(next => async (request, cancel) =>
        {
            var tool = request.Params is { } p && options.ToolCollection.TryGetPrimitive(p.Name, out var t) ? t.ProtocolTool : null;
            if (tool != null && Arguments.Check(tool, request.Params!.Arguments) is { } problem) return Arguments.Error(problem);
            try
            {
                var result = await next(request, cancel).ConfigureAwait(false);
                // The session's calls, as steps save_steps can keep: every call that worked.
                if (tool != null && result.IsError != true)
                    target.Record(tool.Name, request.Params!.Arguments);
                return result;
            }
            catch (Exception e) when (e is not McpException and not OperationCanceledException)
            {
                // The SDK couldn't bind the arguments to the handler (a value of the wrong kind): say which, not just that it failed.
                return Arguments.Error(tool != null ? $"{tool.Name}: {e.Message} Check the arguments against the tool's input schema." : e.Message);
            }
        });
        foreach (var tool in Tools(target)) options.ToolCollection.Add(tool);
        options.PromptCollection = [];
        foreach (var prompt in Prompts()) options.PromptCollection.Add(prompt);
        return options;
    }

    /// <summary>Serves one MCP session over a transport until the client goes away or <paramref name="cancel"/> fires.</summary>
    public static async Task RunAsync(ITransport transport, IExplorerHost host, CancellationToken cancel = default)
    {
        await using var server = McpServer.Create(transport, Options(host));
        await server.RunAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Checks a call's arguments against its tool's input schema before the SDK binds them.</summary>
    internal static class Arguments
    {
        public static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

        /// <summary>What's wrong with the arguments (a name the tool doesn't take, a value of the wrong kind), or null.</summary>
        public static string? Check(Tool tool, IDictionary<string, JsonElement>? args)
        {
            if (!tool.InputSchema.TryGetProperty("properties", out var props)) return null;
            // A required argument that's missing: say which, and what it is (the SDK's own words are about dictionaries).
            if (tool.InputSchema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                var missing = required.EnumerateArray().Select(r => r.GetString() ?? "").Where(r => r.Length > 0 && args?.ContainsKey(r) != true).ToList();
                if (missing.Count > 0)
                    return $"{tool.Name} needs {string.Join(" and ", missing)}: " + string.Join(" ", missing.Select(m =>
                        props.TryGetProperty(m, out var p) && p.TryGetProperty("description", out var d) ? $"{m} is {d.GetString()?.TrimEnd('.')}." : $"{m}."));
            }
            if (args == null) return null;
            foreach (var (name, value) in args)
            {
                if (!props.TryGetProperty(name, out var schema))
                {
                    var names = props.EnumerateObject().Select(x => x.Name).ToList();
                    return $"{tool.Name} has no argument \"{name}\". It takes: {(names.Count == 0 ? "none" : string.Join(", ", names))}.";
                }
                if (Kinds(schema) is { Count: > 0 } kinds && !kinds.Any(k => Fits(k, value)))
                    return $"{tool.Name}: {name} must be {string.Join(" or ", kinds.Where(k => k != "null").Select(Describe))}, not {Describe(value)}.";
            }
            return null;
        }

        private static List<string> Kinds(JsonElement schema) =>
            schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) ? []
            : type.ValueKind == JsonValueKind.String ? [type.GetString()!]
            : type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];

        private static bool Fits(string kind, JsonElement v) => kind switch
        {
            "string" => v.ValueKind == JsonValueKind.String,
            "number" => v.ValueKind == JsonValueKind.Number,
            "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
            "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => v.ValueKind == JsonValueKind.Array,
            "object" => v.ValueKind == JsonValueKind.Object,
            "null" => v.ValueKind == JsonValueKind.Null,
            _ => true,
        };

        private static string Describe(string kind) => kind switch
        {
            "string" => "text", "number" => "a number", "integer" => "a whole number", "boolean" => "true or false",
            "array" => "a list", "object" => "an object", _ => kind,
        };

        private static string Describe(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => $"the text \"{v.GetString()}\"",
            JsonValueKind.Number => $"the number {v.GetRawText()}",
            JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            JsonValueKind.Array => "a list",
            JsonValueKind.Object => "an object",
            _ => "null",
        };
    }

    /// <summary>A host for reading the tools' definitions only.</summary>
    private sealed class NoHost : IExplorerHost
    {
        public string Root => Path.GetTempPath();
        public IViewPainter Painter => throw new InvalidOperationException("Not connected.");
        public bool Live => false;
        public Task<T> RunAsync<T>(string action, Func<IExplorerSession, Task<T>> work, CancellationToken cancel = default) =>
            throw new InvalidOperationException("Not connected.");
    }
}
