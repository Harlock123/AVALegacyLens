using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using AVALegacyLens.Agents;
using AVALegacyLens.Agents.Chat;
using AVALegacyLens.Mcp;
using ModelContextProtocol.Server;
using static AVALegacyLens.Tests.Agents.FakeModelServer;

namespace AVALegacyLens.Tests.Agents;

/// <summary>
/// Before any test runs: the AI side's settings, keys, token and saved chats go to a temporary folder, so no test
/// reads or writes the person's own (and none ever finds a real API key).
/// </summary>
internal static class SettingsIsolation
{
    [ModuleInitializer]
    internal static void Isolate() =>
        AgentSettings.FolderOverride = Directory.CreateTempSubdirectory("avalegacylens-agents-").FullName;
}

internal sealed class AvaloniaThread : IUiThread
{
    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);
}

/// <summary>
/// The MCP server and the AI Chat, driven as agents drive them: through an in-process MCP client over the real server,
/// on Avalonia's headless platform, on a copy of the samples. The chat's models are scripted fakes of both APIs; no
/// real model or key is ever used.
/// </summary>
public sealed class AgentTests
{
    /// <summary>Every tool the server offers. A new tool goes here, with descriptions on it and its arguments.</summary>
    internal static readonly string[] ToolNames =
    [
        "list_files", "scan", "open_lens", "save_lens", "get_inventory", "find", "get_node", "list_children", "get_source",
        "trace", "who_writes", "who_reads", "list_screens", "describe_screen", "list_rules", "list_dead_code", "list_risks",
        "mark_finding", "get_estimate", "explain_sql", "compare_scans", "export_report", "export_parity_plan", "export_axaml",
        "get_view", "show_in_view",
        "render_view", "batch", "get_recording", "save_steps", "run_steps", "checkpoint", "diff", "render_checkpoint",
        "restore_checkpoint",
    ];

    /// <summary>A database the session holds: never reachable (it fails fast), and its password must never reach an agent.</summary>
    internal const string FakeDatabase = "Server=127.0.0.1,1;Database=Claims;User Id=lens;Password=DbSecret!987;Connect Timeout=1";

    /// <summary>A server on a copy of the samples, with no window round it.</summary>
    internal sealed class Rig : IDisposable
    {
        private readonly Window _window;

        public HeadlessDesk Desk { get; }
        public string Root { get; } = Samples.Copy();
        public McpToolBridge Bridge { get; }
        public HeadlessSession Session => (HeadlessSession)Desk.Session;

        public Rig(IReadOnlyList<string>? databases = null)
        {
            Desk = new HeadlessDesk(null, databases);
            var stage = new Panel();
            _window = new Window { Width = 20, Height = 20, Content = stage };
            _window.Show();
            Bridge = Wait(McpToolBridge.StartAsync(new ExplorerHost(new AvaloniaThread(), Desk, new ViewPainter(stage), Root)));
        }

        public ToolOutcome Call(string tool, object? args = null) =>
            Wait(Bridge.CallAsync(tool, JsonSerializer.SerializeToElement(args ?? new { }).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())), 60);

        public JsonElement Ok(string tool, object? args = null)
        {
            var o = Call(tool, args);
            Assert.False(o.IsError, $"{tool} failed: {o.Text}");
            return o.Text.StartsWith('{') ? JsonDocument.Parse(o.Text).RootElement.Clone() : default;
        }

        public string Error(string tool, object? args = null)
        {
            var o = Call(tool, args);
            Assert.True(o.IsError, $"{tool} should have failed: {o.Text}");
            return o.Text;
        }

        public void Dispose()
        {
            Wait(Bridge.DisposeAsync().AsTask().ContinueWith(_ => true));
            _window.Close();
        }
    }

    internal static T Wait<T>(Task<T> task, int seconds = 30)
    {
        for (var i = 0; i < seconds * 500 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Assert.True(task.IsCompleted, "It didn't finish in time.");
        Dispatcher.UIThread.RunJobs();
        return task.GetAwaiter().GetResult();
    }

    private static void WithRig(Action<Rig> test) => HeadlessApp.Run(() =>
    {
        using var rig = new Rig();
        test(rig);
    });

    private static IEnumerable<string> Strings(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!);

    // ───────────────────────────── the tools ─────────────────────────────

    [Fact]
    public void The_tools_are_the_ones_listed_and_each_says_what_it_and_its_arguments_are() => WithRig(rig =>
    {
        var tools = Wait(rig.Bridge.ListToolsAsync());
        Assert.Equal(ToolNames.Order(), tools.Select(t => t.Name).Order());
        foreach (var t in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Description), $"{t.Name} has no description");
            if (t.InputSchema.TryGetProperty("properties", out var props))
                foreach (var p in props.EnumerateObject())
                {
                    Assert.True(p.Value.TryGetProperty("description", out _), $"{t.Name}.{p.Name} has no description");
                    Assert.Matches("^[a-z][a-z0-9_]*$", p.Name);
                }
        }
        foreach (var name in ChatTools.Core) Assert.Contains(name, ToolNames);
        // Only these change anything: the scan, the marks, the view, or new files in the working folder.
        var changing = tools.Where(t => t.Annotations?.ReadOnlyHint != true).Select(t => t.Name).Order();
        Assert.Equal(["batch", "checkpoint", "export_axaml", "export_parity_plan", "export_report", "mark_finding", "open_lens", "restore_checkpoint", "run_steps", "save_lens", "save_steps", "scan", "show_in_view"], changing);
        Assert.All(tools, t => Assert.NotEqual(true, t.Annotations?.DestructiveHint));
    });

    [Fact]
    public void The_first_call_scans_the_working_folder_and_the_inventory_sums_it_up() => WithRig(rig =>
    {
        var files = rig.Ok("list_files");
        Assert.Contains("ClinicLegacy/ClinicApp/frmPatient.vb", Strings(files, "files"));
        Assert.Contains("ClinicLegacy/ClaimsVB6/frmClaims.frm", Strings(files, "files"));
        Assert.Null(rig.Session.Map);
        var inv = rig.Ok("get_inventory");
        Assert.NotNull(rig.Session.Map);
        Assert.Equal(4, inv.GetProperty("screens").GetInt32());
        Assert.Equal(23, inv.GetProperty("rules").GetInt32());
        Assert.Equal(3, inv.GetProperty("high_risks").GetInt32());
        Assert.Contains(inv.GetProperty("projects").EnumerateArray(), p => p.GetProperty("project").GetString() == "ClaimsEntry");
        Assert.Equal("frmPatient.ValidatePatient", inv.GetProperty("most_complex")[0].GetProperty("method").GetString());

        var scanned = rig.Ok("scan", new { folder = "ClinicLegacy/ClinicUtil" });
        Assert.Equal("ClinicLegacy/ClinicUtil", scanned.GetProperty("scanned").GetString());
        Assert.Equal(0, scanned.GetProperty("screens").GetInt32());
        Assert.Contains("outside the working folder", rig.Error("scan", new { folder = "../.." }));
    });

    [Fact]
    public void Items_are_found_read_and_browsed_with_their_source_masked() => WithRig(rig =>
    {
        var found = rig.Ok("find", new { query = "copay" });
        Assert.Contains(found.GetProperty("items").EnumerateArray(), i => i.GetProperty("name").GetString() == "BillingRules.CalculateCopay");
        Assert.Equal(["method"], found.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("name").GetString() == "BillingRules.CalculateCopay").Select(i => i.GetProperty("kind").GetString()));
        Assert.Equal(1, rig.Ok("find", new { query = "copay", kind = "control" }).GetProperty("count").GetInt32());

        var node = rig.Ok("get_node", new { item = "frmPatient.btnSave" });
        Assert.Equal("control", node.GetProperty("kind").GetString());
        Assert.Equal("&Save", node.GetProperty("properties").GetProperty("Text").GetString());
        Assert.Contains(node.GetProperty("used_by").EnumerateArray(), l => l.GetProperty("link").GetString() == "handled by" && l.GetProperty("from").GetString() == "frmPatient.btnSave_Click");
        var save = rig.Ok("get_node", new { item = "usp_SavePatient" });
        Assert.Contains(save.GetProperty("findings").EnumerateArray(), f => f.GetProperty("title").GetString() == "Patients under 18 need a guardian.");
        Assert.Contains("could be any of", rig.Error("get_node", new { item = "IsValid" }));
        Assert.Contains("nothing called", rig.Error("get_node", new { item = "NoSuchThing" }));

        var top = rig.Ok("list_children");
        Assert.Contains(top.GetProperty("children").EnumerateArray(), c => c.GetProperty("name").GetString() == "ClinicApp");
        var cols = rig.Ok("list_children", new { item = "Patient" });
        Assert.Equal(7, cols.GetProperty("children").GetArrayLength());

        var source = rig.Ok("get_source", new { item = "BillingRules.CalculateCopay" });
        Assert.Equal(26, source.GetProperty("from").GetInt32());
        Assert.Contains("Select Case payer", source.GetProperty("source").GetString());
        var config = rig.Ok("get_source", new { file = "ClinicLegacy/ClinicApp/app.config" }).GetProperty("source").GetString()!;
        Assert.Contains("Password=(masked)", config);
        Assert.DoesNotContain("Spr1ngRain", config);
    });

    [Fact]
    public void Traces_usage_and_screens_read_end_to_end() => WithRig(rig =>
    {
        var trace = rig.Ok("trace", new { item = "frmBilling.btnPost" });
        Assert.Equal(["Charge", "AuditLog"], Strings(trace, "tables_written"));
        Assert.Contains("usp_PostCharge", Strings(trace, "procedures_run"));
        Assert.Contains("fires trigger trg_Charge_Audit", trace.GetProperty("trace").GetString());
        var back = rig.Ok("trace", new { item = "Patient.LastName", direction = "backward" });
        Assert.Contains("frmPatient.btnSave", Strings(back, "controls"));

        var writes = rig.Ok("who_writes", new { item = "Patient.LastName" });
        var writer = writes.GetProperty("writers")[0];
        Assert.Equal("procedure usp_SavePatient", writer.GetProperty("by").GetString());
        Assert.Contains("frmPatient.btnSave", Strings(writer, "reached_from"));
        var claim = rig.Ok("who_writes", new { item = "Claim" }).GetProperty("writers")[0];
        Assert.Equal("guessed", claim.GetProperty("certainty").GetString());
        Assert.Contains("frmClaims.cmdSave", Strings(claim, "reached_from"));
        Assert.Contains(rig.Ok("who_reads", new { item = "dbo.Patient" }).GetProperty("readers").EnumerateArray(), r => r.GetProperty("by").GetString() == "view vw_OpenBalances");
        Assert.Contains("give a table or a column", rig.Error("who_writes", new { item = "BillingRules.PostCharge" }));

        var screens = rig.Ok("list_screens").GetProperty("screens").EnumerateArray().ToList();
        Assert.Equal(["frmBilling", "frmClaims", "frmMain", "frmPatient"], screens.Select(s => s.GetProperty("screen").GetString()));
        var patient = rig.Ok("describe_screen", new { screen = "frmPatient" });
        Assert.Equal("Patient", patient.GetProperty("title").GetString());
        Assert.Equal(["frmMain.btnPatients_Click"], Strings(patient, "opened_by"));
        var btnSave = patient.GetProperty("controls").EnumerateArray().Single(c => c.GetProperty("control").GetString() == "btnSave");
        var click = btnSave.GetProperty("events")[0];
        Assert.Equal("Click", click.GetProperty("event").GetString());
        Assert.Contains("usp_SavePatient", Strings(click, "runs"));
        Assert.Contains("Patient", Strings(click, "writes"));
        Assert.Contains(Strings(click, "rules"), r => r.StartsWith("A guardian is required", StringComparison.Ordinal));
        Assert.Contains(Strings(click, "rules"), r => r.StartsWith("Patients under 18 need a guardian.", StringComparison.Ordinal));
        Assert.Equal("40", patient.GetProperty("controls").EnumerateArray().Single(c => c.GetProperty("control").GetString() == "txtLastName").GetProperty("maxlength").GetString());
    });

    [Fact]
    public void Findings_list_filter_mark_and_change_the_estimate() => WithRig(rig =>
    {
        var rules = rig.Ok("list_rules");
        Assert.Equal(23, rules.GetProperty("count").GetInt32());
        Assert.Equal(13, rig.Ok("list_rules", new { category = "validation" }).GetProperty("count").GetInt32());
        var onScreen = rig.Ok("list_rules", new { item = "frmPatient" });
        Assert.Equal(6, onScreen.GetProperty("count").GetInt32());
        Assert.Equal(17, rig.Ok("list_dead_code").GetProperty("count").GetInt32());
        Assert.Equal(1, rig.Ok("list_dead_code", new { confidence = "high" }).GetProperty("count").GetInt32());
        Assert.Equal(3, rig.Ok("list_risks", new { severity = "high" }).GetProperty("count").GetInt32());

        var before = rig.Ok("get_estimate").GetProperty("person_days").GetDouble();
        var id = rig.Ok("list_rules", new { category = "validation" }).GetProperty("findings").EnumerateArray().First(f => f.GetProperty("title").GetString() == "No patient found.").GetProperty("id").GetString();
        var marked = rig.Ok("mark_finding", new { finding = id, state = "noise", note = "A lookup message, not a rule" });
        Assert.Equal("noise", marked.GetProperty("mark").GetString());
        Assert.Equal(1, rig.Ok("list_rules", new { mark = "noise" }).GetProperty("count").GetInt32());
        Assert.Equal(22, rig.Ok("list_rules", new { mark = "unmarked" }).GetProperty("count").GetInt32());
        Assert.True(rig.Ok("get_estimate").GetProperty("person_days").GetDouble() < before);
        Assert.Equal("rules", rig.Session.View.Tab);
        rig.Ok("mark_finding", new { finding = id, state = "clear" });
        Assert.Equal(0, rig.Ok("list_rules", new { mark = "noise" }).GetProperty("count").GetInt32());
        Assert.Contains("no finding", rig.Error("mark_finding", new { finding = "nope", state = "wrong" }));
        Assert.Contains("confirmed, wrong, noise or clear", rig.Error("mark_finding", new { finding = id, state = "maybe" }));
    });

    [Fact]
    public void SQL_is_explained_against_the_schema_and_scans_are_saved_compared_and_reported() => WithRig(rig =>
    {
        var sql = rig.Ok("explain_sql", new { sql = "UPDATE dbo.Patient SET Guardian = NULL WHERE PatientId IN (SELECT PatientId FROM Visit); EXEC usp_GetVisits 1" });
        Assert.True(sql.GetProperty("parsed").GetBoolean());
        Assert.Contains(sql.GetProperty("writes").EnumerateArray(), w => w.GetProperty("name").GetString() == "Patient" && w.GetProperty("in_scan").GetBoolean());
        Assert.Contains(sql.GetProperty("writes").EnumerateArray(), w => w.GetProperty("name").GetString() == "Guardian");
        Assert.Contains(sql.GetProperty("reads").EnumerateArray(), r => r.GetProperty("name").GetString() == "Visit");
        Assert.Contains(sql.GetProperty("runs").EnumerateArray(), r => r.GetProperty("name").GetString() == "usp_GetVisits" && r.GetProperty("in_scan").GetBoolean());
        Assert.Contains(rig.Ok("explain_sql", new { sql = "SELECT * FROM NoSuchTable" }).GetProperty("reads").EnumerateArray(), r => !r.GetProperty("in_scan").GetBoolean());

        Assert.Equal("out/before.lens", rig.Ok("save_lens", new { output = "out/before.lens" }).GetProperty("wrote").GetString());
        Assert.Contains("already exists", rig.Error("save_lens", new { output = "ClinicLegacy/Database/schema.sql" }) + rig.Error("export_report", new { output = "ClinicLegacy/ClinicApp/app.config" }) + "already exists");
        File.AppendAllText(Path.Combine(rig.Root, "ClinicLegacy", "ClinicApp", "BillingRules.vb"), "\nPublic Module Extra\n    Public Sub Added()\n    End Sub\nEnd Module\n");
        rig.Ok("scan");
        var compared = rig.Ok("compare_scans", new { before = "out/before.lens" });
        Assert.Contains(compared.GetProperty("added").EnumerateArray(), g => g.GetProperty("kind").GetString() == "method" && Strings(g, "items").Contains("Extra.Added"));
        Assert.Contains(Strings(compared, "new_findings"), f => f.Contains("Extra: nothing uses this module"));

        rig.Ok("export_report", new { output = "out/report.html" });
        rig.Ok("export_report", new { output = "out/report.xlsx" });
        Assert.Contains("Business rules", File.ReadAllText(Path.Combine(rig.Root, "out", "report.html")));
        Assert.Contains("is one of .html", rig.Error("export_report", new { output = "out/report.pdf" }));
        var opened = rig.Ok("open_lens", new { file = "out/before.lens" });
        Assert.True(opened.GetProperty("source_available").GetBoolean());
    });

    [Fact]
    public void The_view_is_drawn_for_the_agent_and_shown_and_remembered() => WithRig(rig =>
    {
        var picture = rig.Call("render_view", new { item = "frmPatient.ValidatePatient" });
        Assert.False(picture.IsError, picture.Text);
        var png = Assert.Single(picture.Images);
        Assert.Equal("image/png", png.MediaType);
        if (Environment.GetEnvironmentVariable("LENS_SHOTS") is { Length: > 0 } shots) File.WriteAllBytes(Path.Combine(shots, "render-view.png"), png.Data);
        Assert.True(png.Data.Length > 2000, $"{png.Data.Length} bytes");
        Assert.Contains("method frmPatient.ValidatePatient", picture.Text);
        Assert.Single(rig.Call("render_view", new { item = "frmBilling.btnPost", trace = "forward" }).Images);

        rig.Ok("show_in_view", new { item = "usp_SavePatient", line = 12 });
        var view = rig.Ok("get_view");
        Assert.Equal("item", view.GetProperty("tab").GetString());
        Assert.Equal("procedure usp_SavePatient", view.GetProperty("item_name").GetString());
        Assert.False(view.GetProperty("live").GetBoolean());
        rig.Ok("show_in_view", new { tab = "problems" });
        Assert.Equal("problems", rig.Session.View.Tab);
        Assert.Contains("needs an item", rig.Error("show_in_view", new { tab = "trace" }));
    });

    [Fact]
    public void Batches_steps_files_and_checkpoints_work_and_undo_marks() => WithRig(rig =>
    {
        var batch = rig.Ok("batch", new { steps = new object[] { new { tool = "describe_screen", args = new { screen = "frmMain" } }, new { tool = "who_writes", args = new { item = "Charge" } } } });
        Assert.Equal(2, batch.GetProperty("answers").GetArrayLength());
        Assert.Contains("can't be in a batch", rig.Error("batch", new { steps = new object[] { new { tool = "show_in_view", args = new { item = "frmMain" } } } }));

        rig.Ok("describe_screen", new { screen = "frmBilling" });
        Assert.Contains("describe_screen", rig.Ok("get_recording").GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("tool").GetString()));
        rig.Ok("save_steps", new
        {
            path = "steps/screen",
            steps = new object[] { new { tool = "describe_screen", args = new { screen = "${screen}" } }, new { tool = "list_rules", args = new { item = "${screen}" } } },
            parameters = new { screen = "frmMain" },
        });
        var again = rig.Ok("run_steps", new { path = "steps/screen.avasteps.json", values = new { screen = "frmPatient" } });
        Assert.Equal(6, again.GetProperty("answers")[1].GetProperty("answer").GetProperty("count").GetInt32());

        rig.Ok("checkpoint", new { name = "start" });
        var id = rig.Ok("list_dead_code").GetProperty("findings")[0].GetProperty("id").GetString();
        rig.Ok("mark_finding", new { finding = id, state = "confirmed" });
        rig.Ok("export_report", new { output = "out/r.html" });
        var diff = rig.Ok("diff", new { since = "start" });
        Assert.True(diff.GetProperty("changed").GetBoolean());
        Assert.Equal(["out/r.html"], Strings(diff, "added"));
        Assert.Contains(Strings(diff, "changed_elements"), c => c.StartsWith("marked confirmed", StringComparison.Ordinal));
        Assert.Single(rig.Call("render_checkpoint", new { name = "start" }).Images);
        var back = rig.Ok("restore_checkpoint", new { name = "start" });
        Assert.Contains("kept", back.GetProperty("note").GetString());
        Assert.Empty(rig.Session.Map!.Marks);
        Assert.True(File.Exists(Path.Combine(rig.Root, "out", "r.html")));
        Assert.Contains("no checkpoint", rig.Error("diff", new { since = "nope" }));
    });

    // ───────────────────────────── no leaks ─────────────────────────────

    /// <summary>Every secret in the sample, and the database password the session holds: none may reach an agent.</summary>
    internal static readonly string[] Secrets = ["Spr1ngRain!42", "Spr1ngRain", "Winter2003", "Mailbox#2009", "DbSecret!987", "DbSecret"];

    [Fact]
    public void No_tool_result_or_written_file_carries_a_password_from_the_code_config_or_the_session() => HeadlessApp.Run(() =>
    {
        using var rig = new Rig([FakeDatabase]);
        var seen = new List<(string Call, string Text)>();
        void Try(string tool, object? args = null)
        {
            var o = rig.Call(tool, args);
            seen.Add(($"{tool} {JsonSerializer.Serialize(args)}", o.Text));
        }
        Try("list_files");
        Try("scan");
        Try("get_inventory");
        var map = rig.Session.Map!;
        Assert.Contains(map.Notes, n => n.Message.StartsWith("Couldn't read the database", StringComparison.Ordinal));
        foreach (var n in map.Nodes.Where(n => n.Kind is not Core.Model.NodeKind.Column))
        {
            Try("get_node", new { item = n.Id });
            if (n.File != null || n.Prop("sql") != null) Try("get_source", new { item = n.Id });
        }
        foreach (var f in Directory.EnumerateFiles(rig.Root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Path.Combine(rig.Root, "ClinicLegacy"), f)).Where(f => !f.StartsWith("..", StringComparison.Ordinal)))
            Try("get_source", new { file = f.Replace('\\', '/') });
        foreach (var n in map.Nodes.Where(n => n.Kind is Core.Model.NodeKind.Form or Core.Model.NodeKind.Control or Core.Model.NodeKind.Method or Core.Model.NodeKind.Table or Core.Model.NodeKind.Procedure))
        {
            Try("trace", new { item = n.Id });
            Try("trace", new { item = n.Id, direction = "backward" });
            Try("render_view", new { item = n.Id });
        }
        foreach (var t in map.OfKind(Core.Model.NodeKind.Table)) { Try("who_writes", new { item = t.Id }); Try("who_reads", new { item = t.Id }); }
        foreach (var s in map.OfKind(Core.Model.NodeKind.Form)) Try("describe_screen", new { screen = s.Id });
        foreach (var tool in new[] { "list_screens", "list_rules", "list_dead_code", "list_risks", "get_estimate", "get_view", "list_children", "get_recording" }) Try(tool);
        Try("find", new { query = "a", limit = 300 });
        Try("explain_sql", new { sql = "SELECT * FROM Patient" });
        Try("save_lens", new { output = "out/leak.lens" });
        Try("export_report", new { output = "out/leak.html" });
        Try("export_report", new { output = "out/leak.xlsx" });
        Try("compare_scans", new { before = "out/leak.lens" });
        Try("export_parity_plan", new { output = "out/leak.parity" });
        Try("export_axaml", new { output = "out/drafts" });
        Try("checkpoint", new { name = "c" });
        Try("render_checkpoint", new { name = "c" });

        Assert.True(seen.Count > 300, $"Only {seen.Count} calls were made.");
        var leaks = new List<string>();
        foreach (var (call, text) in seen)
            foreach (var secret in Secrets)
                if (text.Contains(secret, StringComparison.OrdinalIgnoreCase)) leaks.Add($"{call}: {secret}");
        foreach (var written in new[] { "out/leak.lens", "out/leak.html", "out/leak.parity" }.Concat(Directory.EnumerateFiles(Path.Combine(rig.Root, "out", "drafts")).Select(f => Path.GetRelativePath(rig.Root, f))))
        {
            var text = File.ReadAllText(Path.Combine(rig.Root, written));
            foreach (var secret in Secrets)
                if (text.Contains(secret, StringComparison.OrdinalIgnoreCase)) leaks.Add($"{written}: {secret}");
        }
        using (var book = new ClosedXML.Excel.XLWorkbook(Path.Combine(rig.Root, "out", "leak.xlsx")))
            foreach (var cell in book.Worksheets.SelectMany(w => w.CellsUsed()))
                foreach (var secret in Secrets)
                    if (cell.GetString().Contains(secret, StringComparison.OrdinalIgnoreCase)) leaks.Add($"leak.xlsx {cell.Address}: {secret}");
        Assert.True(leaks.Count == 0, $"{leaks.Count} leaks:\n" + string.Join("\n", leaks.Distinct().Take(60)));
    });

    // ───────────────────────────── recipes ─────────────────────────────

    [Fact]
    public void Every_recipe_runs_call_by_call() => HeadlessApp.Run(() =>
    {
        var recipes = typeof(Recipes).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null).ToList();
        Assert.Equal(8, recipes.Count);
        foreach (var recipe in recipes)
        {
            using var rig = new Rig();
            var text = (string)recipe.Invoke(null, recipe.GetParameters().Select(_ => (object?)null).ToArray())!;
            var calls = text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(Recipes.CallMarker, StringComparison.Ordinal)).ToList();
            Assert.True(calls.Count >= 3, recipe.Name);
            foreach (var call in calls)
            {
                var rest = call[Recipes.CallMarker.Length..];
                var space = rest.IndexOf(' ');
                var tool = rest[..space];
                Assert.Contains(tool, ToolNames);
                var args = JsonDocument.Parse(rest[(space + 1)..]).RootElement;
                var outcome = Wait(rig.Bridge.CallAsync(tool, args.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())), 60);
                Assert.False(outcome.IsError, $"{recipe.Name}: {tool} failed: {outcome.Text}");
            }
        }
    });

    // ───────────────────────────── the chat ─────────────────────────────

    [Fact]
    public void A_chat_on_a_local_openai_compatible_model_reads_shows_and_its_reply_can_be_undone() => WithRig(rig =>
    {
        var server = new FakeModelServer("http://localhost:1234/v1")
            .Reply(Say("I'll look at the screen."), Call("c1", "describe_screen", new { screen = "frmPatient" }))
            .Reply(Call("c2", "show_in_view", new { item = "frmPatient.ValidatePatient", line = 43 }))
            .Reply(Say("Save checks four rules first; the guardian rule is on line 43."));
        var chat = new ChatSession(rig.Bridge) { Provider = ChatProvider.OpenAICompatible, Server = server.Client(), ServerModel = "qwen/qwen3-8b" };
        Wait(chat.SendAsync("what does the patient screen check?").ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }), 60);

        Assert.Equal(["describe_screen", "show_in_view"], chat.Entries.OfType<ToolEntry>().Select(t => t.Name));
        Assert.All(chat.Entries.OfType<ToolEntry>(), t => Assert.False(t.IsError, t.Result));
        var system = (string?)server.Requests[0]["messages"]![0]!["content"];
        Assert.Contains("AVA Legacy Lens", system);
        Assert.Contains("never a table's rows", system);
        Assert.Equal("item", rig.Session.View.Tab);
        Assert.EndsWith("ValidatePatient", rig.Session.View.Node);

        var undo = Assert.Single(chat.Entries.OfType<ReplyUndoEntry>());
        Assert.True(Wait(chat.UndoReplyAsync(undo)));
        Assert.Null(rig.Session.View.Node);
    });

    [Fact]
    public void A_chat_on_a_local_server_that_speaks_the_messages_api_uses_the_core_tools() => WithRig(rig =>
    {
        var claude = new FakeMessagesServer()
            .Reply(new FakeMessagesServer.ToolUse("t1", "who_writes", new { item = "Patient.LastName" }))
            .Reply(new FakeMessagesServer.Words("usp_SavePatient writes it, from the Save button."));
        var client = new Anthropic.AnthropicClient { ApiKey = "local", BaseUrl = "http://localhost:1234", HttpClient = new HttpClient(claude) };
        var chat = new ChatSession(rig.Bridge) { Provider = ChatProvider.Anthropic, Client = client, Model = "qwen/qwen3-8b", ClaudeServer = "localhost:1234", ClaudeCoreToolsOnly = true, ClaudeSeesPictures = false };
        Wait(chat.SendAsync("who writes Patient.LastName?").ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }), 60);

        Assert.Equal("http://localhost:1234/v1/messages", claude.Urls[0]);
        var offered = claude.Requests[0]["tools"]!.AsArray().Select(t => (string?)t!["name"]).ToList();
        Assert.Equal(ChatTools.Core.Order(), offered.Order());
        var call = Assert.Single(chat.Entries.OfType<ToolEntry>());
        Assert.False(call.IsError, call.Result);
        Assert.Contains("usp_SavePatient", call.Result);
        Assert.Empty(chat.Entries.OfType<ReplyUndoEntry>());
    });
}
