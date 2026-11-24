using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AVALegacyLens.Mcp;

/// <summary>
/// Saved recipes, served as MCP prompts: short step-by-step plans for what agents are often asked to do with a legacy
/// system, written with the real tool names and arguments. Each step that calls a tool shows the call on a line of its
/// own, "→ tool {arguments}", so an agent can follow it as it is or adapt it (the tests run every recipe through the
/// server, call by call, on the sample, so a recipe that stops working fails the build). The defaults name items in
/// samples/ClinicLegacy.
/// </summary>
[McpServerPromptType]
public static class Recipes
{
    /// <summary>The marker that starts a step's tool call in a recipe's text.</summary>
    public const string CallMarker = "→ ";

    [McpServerPrompt(Name = "explain_screen", Title = "What does this screen do?")]
    [Description("Explains a screen end to end: its fields, what each button does down to the tables it writes and the procedures it runs, the rules it checks on the way, and the screens it opens.")]
    public static string ExplainScreen(
        [Description("The screen (default \"frmPatient\").")] string? screen = null)
    {
        var s = Or(screen, "frmPatient");
        var r = new Recipe($"Explain what the screen {s} does, end to end, for someone who has to rebuild it.");
        r.Step("See the system's size and parts (this scans the working folder if nothing is open).", "get_inventory", new { });
        r.Step("The screen's controls in tab order, each event handler, and what each leads to.", "describe_screen", new { screen = s });
        r.Step("The full trace from the screen, with every file and line.", "trace", new { item = s, direction = "forward" });
        r.Step("The rules checked on this screen and in the code it calls.", "list_rules", new { item = s });
        r.Step("Show the person the screen's main handler code.", "render_view", new { item = s });
        return r.Text("Describe the screen as a user would meet it (its fields and buttons), then what each button does, step by step, naming the methods, procedures and tables with their file:line. List the rules it enforces in the original's words. Say which links are by name or guessed. End with what a rebuild of this screen must keep.");
    }

    [McpServerPrompt(Name = "rules_for_table", Title = "The rules behind a table")]
    [Description("Gathers every rule that guards a table's data: its constraints, the procedures and code that write it, the checks on the way from each screen, and where the same rule is enforced twice.")]
    public static string RulesForTable(
        [Description("The table (default \"Patient\").")] string? table = null)
    {
        var t = Or(table, "Patient");
        var r = new Recipe($"Gather the business rules that guard the table {t}.");
        r.Step("The table: its columns, keys, defaults and CHECK constraints.", "get_node", new { item = t });
        r.Step("What writes it, and from which screens and buttons.", "who_writes", new { item = t });
        r.Step("The rules found in the system (validations, decisions, limits, constraints).", "list_rules", new { });
        r.Step("What reads it, to know who depends on its rules.", "who_reads", new { item = t });
        return r.Text("List the rules that decide what can be written to the table: the constraints first, then each check in the code paths that write it (from who_writes' reached_from and the rules in the writers and the screens they come from), each with its words and file:line. Point out a rule enforced in two places (a screen and a procedure, say) and any path that writes the table without a check the others make.");
    }

    [McpServerPrompt(Name = "impact_of_change", Title = "What would changing this break?")]
    [Description("Works out what depends on an item (a column, a table, a procedure, a method): everything that reads, writes or calls it, up to the screens and buttons that reach it.")]
    public static string ImpactOfChange(
        [Description("The item to change (default \"Patient.Guardian\").")] string? item = null)
    {
        var i = Or(item, "Patient.Guardian");
        var r = new Recipe($"Work out what would be affected by changing or removing {i}.");
        r.Step("The item: what it is and what links to it.", "get_node", new { item = i });
        r.Step("Everything that reaches it, back to the screens and buttons.", "trace", new { item = i, direction = "backward" });
        r.Step("What reads it.", "who_reads", new { item = i });
        r.Step("What writes it.", "who_writes", new { item = i });
        return r.Text("List what would need to change with it: the code and SQL that use it directly (file:line), then the screens and buttons that reach it. Say which links are by name or guessed, since those are where the scan could miss something, and suggest what to test.");
    }

    [McpServerPrompt(Name = "rebuild_estimate", Title = "How big is a rebuild?")]
    [Description("Sizes a rebuild of the system: what's in it, what's dead and can be dropped, the risks that add work, and the estimate with its weights.")]
    public static string RebuildEstimate()
    {
        var r = new Recipe("Size a rebuild of this system.");
        r.Step("What's in the system.", "get_inventory", new { });
        r.Step("The estimate, line by line with its weights.", "get_estimate", new { });
        r.Step("The high risks, which add work.", "list_risks", new { severity = "high" });
        r.Step("The dead code a rebuild can leave out (the surest first).", "list_dead_code", new { confidence = "medium" });
        return r.Text("Give the estimate as a range of person-days, then what drives it (the screens, the most complex code, the high risks), what can be left out (dead code, with its confidence), and what the estimate doesn't include (testing against the old system, data migration, training). Say plainly it's a starting point to argue with, not a quote.");
    }

    [McpServerPrompt(Name = "rules_catalogue", Title = "A catalogue of the business rules")]
    [Description("Drafts the rules catalogue a rebuild works from: every rule found, grouped, in the original's words, with where it is, and a report file to keep.")]
    public static string RulesCatalogue(
        [Description("The report to write (default \"rules-catalogue.xlsx\").")] string? output = null)
    {
        var o = Or(output, "rules-catalogue.xlsx");
        var r = new Recipe("Draft the catalogue of business rules for the rebuild.");
        r.Step("Every rule found, with its marks.", "list_rules", new { });
        r.Step("The rules nobody has judged yet.", "list_rules", new { mark = "unmarked" });
        r.Step("Write the report (a Rules sheet among the rest).", "export_report", new { output = o });
        return r.Text("Write the catalogue grouped by what the rules are about (patients, billing, claims…), each rule in the original's words with its file:line and kind. Put first the ones enforced in more than one place. List the unmarked ones the business should confirm, and say where the report was written.");
    }

    [McpServerPrompt(Name = "find_risky_code", Title = "Where is the risky code?")]
    [Description("Finds the code that's riskiest to keep or hardest to rebuild, reads the worst of it, and says what to do about each.")]
    public static string FindRiskyCode(
        [Description("A method to read closely (default \"PatientData.FindPatients\").")] string? item = null)
    {
        var i = Or(item, "PatientData.FindPatients");
        var r = new Recipe("Find the riskiest code in the system and say what to do about it.");
        r.Step("Every risk, worst first.", "list_risks", new { });
        r.Step("Read the worst one's code.", "get_source", new { item = i });
        r.Step("What it touches.", "get_node", new { item = i });
        r.Step("Show the person the code.", "render_view", new { item = i });
        return r.Text("List the high risks first (SQL built by joining strings, passwords in code), each with its file:line, why it matters and the fix; then the medium ones grouped. For the one read closely, quote the lines that are the problem.");
    }

    [McpServerPrompt(Name = "plan_parity_checks", Title = "Plan the parity checks")]
    [Description("Plans how to prove the rebuild matches the old system: which tables and procedures to compare, keyed how, with what tolerances, as an AVAParityCheck suite to open and fill in.")]
    public static string PlanParityChecks(
        [Description("The suite to write (default \"parity/plan.parity\").")] string? output = null)
    {
        var o = Or(output, "parity/plan.parity");
        var r = new Recipe("Plan the checks that prove the rebuild does what the old system did.");
        r.Step("What the system writes and runs.", "get_inventory", new { });
        r.Step("Write the plan as an AVAParityCheck suite.", "export_parity_plan", new { output = o });
        r.Step("The rules each check should test with rows that break them.", "list_rules", new { });
        r.Step("Which tables are only written by guessed SQL (built at run time): check those by hand.", "list_risks", new { severity = "high" });
        return r.Text("Say where the suite is and list its checks, each with what it compares and why. Then say what it can't cover on its own: test data that exercises each rule (name them), the old and new connections to set up in AVAParityCheck, and the outputs that aren't tables (reports, exported files, screens) to compare another way.");
    }

    [McpServerPrompt(Name = "port_form_to_avalonia", Title = "Port this screen to Avalonia")]
    [Description("Drafts an old screen as an Avalonia window for AVAUIDesigner, and lists what to rebuild behind it: each event's handler, the rules it checks and the data it touches.")]
    public static string PortFormToAvalonia(
        [Description("The screen (default \"frmPatient\").")] string? screen = null,
        [Description("The draft to write (default \"drafts/<screen>.axaml\").")] string? output = null)
    {
        var s = Or(screen, "frmPatient");
        var o = Or(output, $"drafts/{s}.axaml");
        var r = new Recipe($"Draft {s} as an Avalonia window and say what to rebuild behind it.");
        r.Step("The screen end to end.", "describe_screen", new { screen = s });
        r.Step("Draft its layout as .axaml.", "export_axaml", new { screen = s, output = o });
        r.Step("The rules its handlers check, to rebuild as validation.", "list_rules", new { item = s });
        r.Step("Show the person the screen's main handler.", "render_view", new { item = s });
        return r.Text("Say where the draft is (open it in AVAUIDesigner). Then, control by control, what each event must do in the rebuild: the old handler (file:line), the rules it checks in their words, the procedures it runs and the tables it writes. Point out controls with no Avalonia counterpart (drawn as placeholders) and logic that sat in the screen that belongs in a service.");
    }

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private sealed class Recipe(string intro)
    {
        private readonly StringBuilder _text = new(intro + "\n\nFollow these steps in order. A line starting with \"" + CallMarker.Trim() + "\" is one tool call, with its arguments as JSON. If a step fails, read the error, fix the cause and carry on. Item names in these calls are the sample's: use the system's own (find looks them up).\n");
        private int _number;

        public void Step(string? says, string tool, object args)
        {
            _number++;
            _text.Append('\n').Append(_number).Append('.').Append(says != null ? " " : "");
            if (says != null) _text.Append(says);
            _text.Append("\n   ");
            _text.Append(CallMarker).Append(tool).Append(' ').Append(JsonSerializer.Serialize(args, Json.Options));
        }

        public string Text(string closing) => _text.Append("\n\n").Append(closing).ToString();
    }
}
