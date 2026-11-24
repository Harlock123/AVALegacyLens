using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AVALegacyLens.Core.Handoff;
using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Scanning;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AVALegacyLens.Tests;

/// <summary>The hand-offs: a parity plan for AVAParityCheck, and .axaml drafts of the old screens for AVAUIDesigner.</summary>
public sealed class HandoffTests
{
    private static SystemMap Map => Samples.ClinicMap;

    private static readonly XNamespace Av = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void The_parity_plan_checks_every_table_the_live_code_writes_and_the_procedures_that_return_rows()
    {
        var plan = ParityPlan.Make(Map);
        Assert.Equal(ParityPlan.Format, (string?)plan["format"]);
        var checks = plan["checks"]!.AsArray().Select(c => c!.AsObject()).ToList();
        Assert.Equal(["table AuditLog", "table Charge", "table Claim", "table Patient", "procedure usp_GetVisits"], checks.Select(c => (string)c["name"]!));

        var patient = checks.Single(c => (string?)c["name"] == "table Patient");
        Assert.Equal("sql", (string?)patient["old"]!["kind"]);
        Assert.Equal("old", (string?)patient["old"]!["connection"]);
        Assert.Equal("new", (string?)patient["new"]!["connection"]);
        Assert.Equal("SELECT [PatientId], [LastName], [FirstName], [BirthDate], [PayerCode], [Guardian], [Active] FROM [dbo].[Patient] ORDER BY [PatientId]", (string?)patient["old"]!["sql"]);
        Assert.Equal(["PatientId"], patient["keys"]!.AsArray().Select(k => (string)k!));
        var rules = patient["rules"]!.AsArray().ToDictionary(r => (string)r!["column"]!, r => r!.AsObject());
        Assert.True((bool)rules["BirthDate"]["date"]!);
        Assert.True((bool)rules["LastName"]["trim"]!);
        Assert.True((bool)rules["Guardian"]["null_equals_empty"]!);
        var note = (string)patient["note"]!;
        Assert.Contains("written by usp_SavePatient", note);
        Assert.Contains("frmPatient.btnSave", note);
        Assert.Contains("identity", note);
        Assert.Contains("business rules guard it", note);

        var charge = checks.Single(c => (string?)c["name"] == "table Charge");
        Assert.Equal(["PostedOn"], charge["ignored"]!.AsArray().Select(k => (string)k!));
        Assert.Equal(0.01, (double)charge["rules"]!.AsArray().Single(r => (string?)r!["column"] == "Amount")!["abs_tolerance"]!);
        Assert.Contains("guessed", (string)checks.Single(c => (string?)c["name"] == "table Claim")["note"]!);
        var visits = checks.Single(c => (string?)c["name"] == "procedure usp_GetVisits");
        Assert.Equal("EXEC [dbo].[usp_GetVisits] @PatientId = NULL", (string?)visits["old"]!["sql"]);
        // Dead tables and procedures aren't checked; no connection string is written.
        Assert.DoesNotContain(checks, c => ((string)c["name"]!).Contains("FaxQueue") || ((string)c["name"]!).Contains("Purge"));
        Assert.DoesNotContain("connection_string", plan.ToJsonString());
    }

    [Fact]
    public void Each_screen_drafts_as_well_formed_axaml_with_its_controls_where_they_were()
    {
        foreach (var form in Map.OfKind(NodeKind.Form))
        {
            var doc = XDocument.Parse(AxamlDraft.Make(Map, form.Id));
            var names = doc.Descendants().Select(e => (string?)e.Attribute(X + "Name")).Where(n => n != null).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.Equal($"Rebuilt.Views.{form.Name}", (string?)doc.Root!.Attribute(X + "Class"));
        }
        var patient = XDocument.Parse(AxamlDraft.Make(Map, Map.Type("frmPatient").Id));
        Assert.Equal("Patient", (string?)patient.Root!.Attribute("Title"));
        Assert.Equal("440", (string?)patient.Root.Attribute("Width"));
        var last = patient.Descendants(Av + "TextBox").Single(e => (string?)e.Attribute(X + "Name") == "txtLastName");
        Assert.Equal(("112", "16", "40"), ((string?)last.Attribute("Canvas.Left"), (string?)last.Attribute("Canvas.Top"), (string?)last.Attribute("MaxLength")));
        Assert.Equal("_Save", (string?)patient.Descendants(Av + "Button").Single(e => (string?)e.Attribute(X + "Name") == "btnSave").Attribute("Content"));
        Assert.Single(patient.Descendants(Av + "DatePicker"));
        Assert.Single(patient.Descendants(Av + "DataGrid"));
        Assert.Contains(patient.Nodes().Concat(patient.Descendants().SelectMany(d => d.Nodes())).OfType<XComment>(), c => c.Value.Contains("btnSave Click: frmPatient.btnSave_Click (ClinicApp/frmPatient.vb:28)"));

        // VB6: the frame holds its label, the control array is one control each, the ActiveX grid a grid.
        var claims = XDocument.Parse(AxamlDraft.Make(Map, "ClaimsEntry:T:frmClaims"));
        var frame = claims.Descendants(Av + "Border").Single(e => (string?)e.Attribute(X + "Name") == "fraTotals");
        Assert.Contains(frame.Descendants(Av + "TextBlock"), t => (string?)t.Attribute(X + "Name") == "lblTotal");
        Assert.Equal(["Patient ID", "Amount"], claims.Descendants(Av + "TextBlock").Where(t => ((string?)t.Attribute(X + "Name"))?.StartsWith("lblPatient_") == true).Select(t => (string)t.Attribute("Text")!));
        Assert.Single(claims.Descendants(Av + "DataGrid"));
        Assert.Equal("True", (string?)claims.Descendants(Av + "Button").Single(e => (string?)e.Attribute(X + "Name") == "cmdSave").Attribute("IsDefault"));
    }

    [Fact]
    public void Tab_controls_and_unknown_controls_draft_as_tabs_and_placeholders()
    {
        var root = Directory.CreateTempSubdirectory("lens-tabs-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Main.cs"), """
                public partial class Main : System.Windows.Forms.Form
                {
                    private void InitializeComponent()
                    {
                        this.tabs = new System.Windows.Forms.TabControl();
                        this.pageA = new System.Windows.Forms.TabPage();
                        this.pageB = new System.Windows.Forms.TabPage();
                        this.name = new System.Windows.Forms.TextBox();
                        this.chart = new Vendor.Charts.FancyChart();
                        this.tabs.Controls.Add(this.pageA);
                        this.tabs.Controls.Add(this.pageB);
                        this.tabs.Location = new System.Drawing.Point(5, 5);
                        this.tabs.Size = new System.Drawing.Size(300, 200);
                        this.pageA.Text = "First";
                        this.pageB.Text = "Second && more";
                        this.pageA.Controls.Add(this.name);
                        this.pageB.Controls.Add(this.chart);
                        this.Controls.Add(this.tabs);
                        this.ClientSize = new System.Drawing.Size(320, 220);
                    }
                    private System.Windows.Forms.TabControl tabs;
                    private System.Windows.Forms.TabPage pageA, pageB;
                    private System.Windows.Forms.TextBox name;
                    private Vendor.Charts.FancyChart chart;
                }
                """);
            var map = Scanner.Scan(root);
            var doc = XDocument.Parse(AxamlDraft.Make(map, map.Type("Main").Id));
            var items = doc.Descendants(Av + "TabItem").ToList();
            Assert.Equal(["First", "Second & more"], items.Select(i => (string)i.Attribute("Header")!));
            Assert.Single(items[0].Descendants(Av + "TextBox"));
            var placeholder = items[1].Descendants(Av + "Border").Single();
            Assert.Contains("FancyChart", (string?)placeholder.Attribute("ToolTip.Tip"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>A client on another AVA app's MCP server, headless on stdio (its path from an environment variable).</summary>
    private static McpClient Connect(string exe, string root) =>
        Await(McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions { Command = exe, Arguments = ["--mcp", "--root", root], Name = Path.GetFileName(exe) })));

    /// <summary>Waits for another program's answer (these tests don't run on the UI thread).</summary>
    private static T Await<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(90)).GetAwaiter().GetResult();

    private static T Await<T>(ValueTask<T> task) => Await(task.AsTask());

    private static string Text(CallToolResult r) => string.Join(" ", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    /// <summary>
    /// Every draft (the sample's screens, and a real legacy system's when LENS_REAL_LEGACY or X12 Explorer's legacy
    /// folder is here) opens in AVAUIDesigner and draws: LENS_UIDESIGNER is the path to its program. Skipped without it.
    /// </summary>
    [Fact]
    public void The_drafts_open_and_draw_in_AVAUIDesigner()
    {
        if (Environment.GetEnvironmentVariable("LENS_UIDESIGNER") is not { Length: > 0 } designer) return;
        var root = Directory.CreateTempSubdirectory("lens-drafts-").FullName;
        try
        {
            var maps = new List<SystemMap> { Map };
            var real = Environment.GetEnvironmentVariable("LENS_REAL_LEGACY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RiderProjects", "X12_820Explorer", "legacy");
            if (Directory.Exists(real)) maps.Add(Scanner.Scan(real));
            var files = new List<string>();
            foreach (var (map, i) in maps.Select((m, i) => (m, i)))
                foreach (var f in map.OfKind(NodeKind.Form))
                {
                    var name = $"{i}-{f.Prop("project")}-{f.Name}.axaml";
                    File.WriteAllText(Path.Combine(root, name), AxamlDraft.Make(map, f.Id));
                    files.Add(name);
                }
            var client = Connect(designer, root);
            try
            {
                foreach (var file in files)
                {
                    var opened = Await(client.CallToolAsync("open_document", new Dictionary<string, object?> { ["path"] = file, ["discard_changes"] = true }).AsTask());
                    Assert.False(opened.IsError == true, $"{file}: {Text(opened)}");
                    Assert.Contains("\"warnings\":null", Text(opened));
                    var drawn = Await(client.CallToolAsync("render_view", new Dictionary<string, object?>()).AsTask());
                    var image = Assert.Single(drawn.Content.OfType<ImageContentBlock>());
                    if (Environment.GetEnvironmentVariable("LENS_SHOTS") is { Length: > 0 } shots)
                        File.WriteAllBytes(Path.Combine(shots, "draft-" + Path.GetFileNameWithoutExtension(file) + ".png"), image.DecodedData.ToArray());
                }
            }
            finally
            {
                client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(30));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>The parity plan opens in AVAParityCheck as a suite of its checks: LENS_PARITYCHECK is the path to its program. Skipped without it.</summary>
    [Fact]
    public void The_parity_plan_opens_in_AVAParityCheck()
    {
        if (Environment.GetEnvironmentVariable("LENS_PARITYCHECK") is not { Length: > 0 } parity) return;
        var root = Directory.CreateTempSubdirectory("lens-parity-").FullName;
        try
        {
            ParityPlan.Save(Map, Path.Combine(root, "plan.parity"));
            var client = Connect(parity, root);
            try
            {
                var opened = Await(client.CallToolAsync("open_suite", new Dictionary<string, object?> { ["path"] = "plan.parity", ["discard_changes"] = true }).AsTask());
                Assert.False(opened.IsError == true, Text(opened));
                var suite = Await(client.CallToolAsync("get_suite", new Dictionary<string, object?>()).AsTask());
                var text = Text(suite);
                foreach (var name in new[] { "table Patient", "table Charge", "table Claim", "table AuditLog", "procedure usp_GetVisits" }) Assert.Contains(name, text);
            }
            finally
            {
                client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(30));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
