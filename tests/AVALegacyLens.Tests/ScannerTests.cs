using System.Text;
using AVALegacyLens.Core;
using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Scanning;

namespace AVALegacyLens.Tests;

/// <summary>The scan of VB.NET and C# code: projects, files, screens, types, methods and the links between them.</summary>
public sealed class ScannerTests
{
    private static SystemMap Map => Samples.ClinicMap;

    [Fact]
    public void Projects_and_the_files_they_compile_are_found()
    {
        var projects = Map.OfKind(NodeKind.Project).Where(p => p.Prop("subkind") != "folder").ToDictionary(p => p.Name);
        Assert.Equal(["ClinicApp", "ClinicUtil"], projects.Keys.Order());
        Assert.Equal("VB.NET", projects["ClinicApp"].Language);
        Assert.Equal("C#", projects["ClinicUtil"].Language);
        Assert.Equal("WinExe", projects["ClinicApp"].Prop("outputType"));
        var app = Map.Children(projects["ClinicApp"].Id).Select(f => f.Name).ToList();
        Assert.Contains("frmPatient.Designer.vb", app);
        Assert.Contains("app.config", app);
        Assert.Contains(Map.From(projects["ClinicApp"].Id), e => e.Kind == EdgeKind.Uses && e.To == projects["ClinicUtil"].Id);
        // SQL scripts no project lists sit under their folder.
        var schema = Map["file:Database/schema.sql"]!;
        Assert.Equal("folder:Database", schema.Parent);
        Assert.Equal("T-SQL", schema.Language);
    }

    [Fact]
    public void Files_have_their_lines_counted()
    {
        var f = Map["file:ClinicApp/BillingRules.vb"]!;
        Assert.Equal(53, f.IntProp("lines"));
        Assert.Equal(2, f.IntProp("comments"));
        Assert.Equal(6, f.IntProp("blank"));
        Assert.Equal(45, f.IntProp("code"));
        Assert.Equal(new LineCount(5, 1, 3), LineCounter.Count("/* a\n b */\nint x;\n\n// c\n", "C#"));
        Assert.Equal(new LineCount(3, 0, 2), LineCounter.Count("' one\nREM two\nDim x\n", "VB.NET"));
    }

    [Fact]
    public void Forms_are_screens_listed_under_the_file_people_edit()
    {
        var forms = Map.OfKind(NodeKind.Form).Select(f => f.Name).Order().ToList();
        Assert.Equal(["frmBilling", "frmMain", "frmPatient"], forms);
        var patient = Map.Type("frmPatient");
        Assert.Equal("ClinicApp/frmPatient.vb", patient.File);
        Assert.Equal("file:ClinicApp/frmPatient.vb", patient.Parent);
        Assert.Equal("ClinicApp/frmPatient.Designer.vb|ClinicApp/frmPatient.vb", patient.Prop("files"));
        Assert.Equal("form", patient.Prop("subkind"));
        Assert.Equal(NodeKind.Type, Map.Type("BillingRules").Kind);
        Assert.Equal("module", Map.Type("PatientData").Prop("subkind"));
        Assert.Equal("static class", Map.Type("AgeCalculator").Prop("subkind"));
    }

    [Fact]
    public void Methods_have_their_place_size_and_complexity()
    {
        var validate = Map.Method("frmPatient", "ValidatePatient");
        Assert.Equal("ClinicApp/frmPatient.vb", validate.File);
        Assert.Equal(34, validate.Line);
        Assert.Equal(53, validate.EndLine);
        Assert.Equal(8, validate.IntProp("complexity"));
        Assert.Equal("function", validate.Prop("subkind"));
        Assert.Equal("Private", validate.Prop("access"));
        Assert.Equal("ClinicApp/frmPatient.Designer.vb", Map.Method("frmPatient", "InitializeComponent").File);
        Assert.Equal(5, Map.Method("BillingRules", "CalculateCopay").IntProp("complexity"));
        Assert.Equal(8, Map.Method("Npi", "IsValid").IntProp("complexity"));
        Assert.Equal("true", Map.Method("PatientData", "GetConnection").Prop("shared"));
    }

    [Fact]
    public void Calls_resolved_by_the_compiler_are_links()
    {
        Assert.Equal(["frmPatient.ValidatePatient", "PatientData.SavePatient"], Map.CalleeNames(Map.Method("frmPatient", "btnSave_Click")).Order());
        Assert.Equal(["BillingRules.CalculateCopay", "BillingRules.PayerOf", "PatientData.GetConnection"], Map.CalleeNames(Map.Method("BillingRules", "PostCharge")).Order());
        var call = Map.From(Map.Method("frmPatient", "btnSave_Click").Id).Single(e => Map[e.To]!.Name == "SavePatient");
        Assert.Equal(Certainty.Resolved, call.Certainty);
        Assert.Equal(30, call.Line);
        Assert.StartsWith("_patientId = PatientData.SavePatient(", call.Note);
        // Called through a field whose arguments' types are unknown (WinForms isn't referenced): still the one method.
        var post = Map.From(Map.Method("frmBilling", "btnPost_Click").Id).Single(e => e.Kind == EdgeKind.Calls);
        Assert.Equal(Certainty.Resolved, post.Certainty);
        Assert.Equal(Map.Method("BillingRules", "PostCharge").Id, post.To);
    }

    [Fact]
    public void Calls_across_projects_and_languages_are_matched_by_name()
    {
        var calls = Map.From(Map.Method("frmPatient", "ValidatePatient").Id).Where(e => e.Kind == EdgeKind.Calls).ToList();
        Assert.All(calls, c => Assert.Equal(Certainty.ByName, c.Certainty));
        Assert.Equal(["AgeCalculator.Age", "AgeCalculator.IsMinor"], calls.Select(c => $"AgeCalculator.{Map[c.To]!.Name}").Order());
        // Of Age's two overloads, the call passes one argument: only the one-parameter Age.
        var age = calls.Single(c => Map[c.To]!.Name == "Age");
        Assert.Equal("1", Map[age.To]!.Prop("parameters"));
        // Framework calls the runtime doesn't have (MessageBox.Show) aren't linked to anything.
        Assert.DoesNotContain(Map.Edges, e => Map[e.To]?.Name == "Show");
    }

    [Fact]
    public void Object_creations_and_field_initializers_are_links()
    {
        Assert.Equal(["frmPatient"], Map.From(Map.Method("frmMain", "btnPatients_Click").Id).Where(e => e.Kind == EdgeKind.Creates).Select(e => Map[e.To]!.Name));
        Assert.Contains(Map.From(Map.Type("frmBilling").Id), e => e.Kind == EdgeKind.Creates && e.To == Map.Type("BillingRules").Id);
    }

    [Fact]
    public void Every_link_joins_two_nodes_and_ids_are_stable()
    {
        Assert.All(Map.Edges, e =>
        {
            Assert.True(Map.Contains(e.From), e.From);
            Assert.True(Map.Contains(e.To), e.To);
        });
        Assert.Empty(Map.Notes);
        var again = Scanner.Scan(Samples.Clinic);
        Assert.Equal(Map.Nodes.Select(n => n.Id).Order(StringComparer.Ordinal), again.Nodes.Select(n => n.Id).Order(StringComparer.Ordinal));
        Assert.Equal(Map.Edges.Count, again.Edges.Count);
        Assert.StartsWith("ClinicApp:M:ClinicApp.BillingRules.PostCharge(", Map.Method("BillingRules", "PostCharge").Id);
    }

    [Fact]
    public void Loose_files_old_encodings_and_sdk_projects_are_read()
    {
        var root = Directory.CreateTempSubdirectory("lens-loose-").FullName;
        try
        {
            // A VB file no project lists, in Windows-1252 (é as 0xE9): still read, its text kept.
            File.WriteAllBytes(Path.Combine(root, "Old.vb"), Encoding.Latin1.GetBytes("Public Module Old\n    ' Café\n    Public Sub Run()\n        Helper()\n    End Sub\n    Sub Helper()\n    End Sub\nEnd Module\n"));
            // An SDK-style project, with a nested project whose files it must not take.
            Directory.CreateDirectory(Path.Combine(root, "Lib", "Inner"));
            File.WriteAllText(Path.Combine(root, "Lib", "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "Lib", "A.cs"), "namespace Lib; public class A { public void Go() => new B(); }");
            File.WriteAllText(Path.Combine(root, "Lib", "Inner", "Inner.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "Lib", "Inner", "B.cs"), "namespace Lib; public class B { }");
            var map = Scanner.Scan(root);
            Assert.Equal(["Inner", "Lib", "Loose VB.NET files"], map.OfKind(NodeKind.Project).Select(p => p.Name).Order());
            Assert.Equal("project:Lib/Lib.csproj", map["file:Lib/A.cs"]!.Parent);
            Assert.Equal("project:Lib/Inner/Inner.csproj", map["file:Lib/Inner/B.cs"]!.Parent);
            Assert.Equal(["Module1.Helper"], map.CalleeNames(map.Method("Old", "Run")).Select(n => n.Replace("Old.", "Module1.")));
            Assert.Contains("Café", SourceFiles.ReadText(Path.Combine(root, "Old.vb")));
            // A creates B across projects: matched by name.
            var create = Assert.Single(map.Edges, e => e.Kind == EdgeKind.Creates);
            Assert.Equal(Certainty.ByName, create.Certainty);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_lens_file_keeps_the_whole_map()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clinic-{Guid.NewGuid():N}.lens");
        try
        {
            LensFile.Save(Map, path);
            var back = LensFile.Load(path);
            Assert.Equal(Map.Root, back.Root);
            Assert.Equal(Map.Nodes.Count, back.Nodes.Count);
            Assert.Equal(Map.Edges.Count, back.Edges.Count);
            var m = back[Map.Method("frmPatient", "ValidatePatient").Id]!;
            Assert.Equal("8", m.Prop("complexity"));
            Assert.Equal(NodeKind.Method, m.Kind);
            Assert.Equal(Map.Inventory().ToText(), back.Inventory().ToText());
            Assert.Contains("\"kind\": \"Form\"", File.ReadAllText(path));

            File.WriteAllText(path, "{\"version\": 99}");
            Assert.Throws<InvalidDataException>(() => LensFile.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_inventory_sums_the_system_up()
    {
        var inv = Map.Inventory();
        Assert.Equal(3, inv.Counts[NodeKind.Form]);
        Assert.Equal(Map.OfKind(NodeKind.Method).Count(), inv.Counts[NodeKind.Method]);
        Assert.Equal(["VB.NET", "T-SQL", "C#", "Config"], inv.Languages.Select(l => l.Language));
        Assert.Equal(Map.OfKind(NodeKind.File).Sum(f => f.IntProp("code")), inv.TotalCode);
        var app = inv.Projects.Single(p => p.Name == "ClinicApp");
        Assert.Equal(3, app.Screens);
        Assert.Equal(9, app.Files);
        Assert.Equal("ValidatePatient", inv.MostComplex[0].Name);
        Assert.Contains("ClinicApp (VB.NET): 9 files, 3 screens", inv.ToText());
    }

    /// <summary>X12 Explorer's own VB.NET original, when it's on this machine (it isn't in CI): it scans cleanly.</summary>
    [Fact]
    public void A_real_legacy_project_scans_cleanly()
    {
        var folder = Environment.GetEnvironmentVariable("LENS_REAL_LEGACY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RiderProjects", "X12_820Explorer", "legacy");
        if (!Directory.Exists(folder)) return;
        var map = Scanner.Scan(folder);
        Assert.Empty(map.Notes);
        Assert.NotEmpty(map.OfKind(NodeKind.Form));
        Assert.True(map.OfKind(NodeKind.Method).Count() > 10);
    }
}

internal static class InventoryExtensions
{
    public static Inventory Inventory(this SystemMap map) => Core.Inventory.Of(map);
}
