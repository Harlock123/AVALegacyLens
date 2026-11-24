using AVALegacyLens.Core;
using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Scanning;
using AVALegacyLens.Core.Sql;

namespace AVALegacyLens.Tests;

/// <summary>VB6: projects, forms and their controls, modules, classes, handlers by name, calls, SQL in strings, risks.</summary>
public sealed class Vb6Tests
{
    private static SystemMap Map => Samples.ClinicMap;

    private static Node Proc(string module, string name) => Map.OfKind(NodeKind.Method).Single(m => m.Language == "VB6" && m.Prop("type") == module && m.Name == name && !m.Id.EndsWith(".get", StringComparison.Ordinal) && !m.Id.EndsWith(".let", StringComparison.Ordinal));

    private static Node Control(string name) => Map[WinFormsReader.ControlId("ClaimsEntry:T:frmClaims", name)]!;

    [Fact]
    public void The_project_lists_its_files_references_and_startup()
    {
        var project = Map["project:ClaimsVB6/ClaimsEntry.vbp"]!;
        Assert.Equal("ClaimsEntry", project.Name);
        Assert.Equal("VB6", project.Language);
        Assert.Equal("frmClaims", project.Prop("startup"));
        Assert.Contains("Microsoft ActiveX Data Objects 2.8 Library", project.Prop("references"));
        Assert.Equal("MSFLXGRD.OCX", project.Prop("controls (OCX)"));
        Assert.Equal(["ClaimsEntry.vbp", "clsClaim.cls", "frmClaims.frm", "modClaims.bas"], Map.Children(project.Id).Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.Equal(NodeKind.Type, Map["ClaimsEntry:T:modClaims"]!.Kind);
        Assert.Equal("2", Map["ClaimsEntry:T:modClaims"]!.Prop("globals"));
        Assert.Equal("class", Map["ClaimsEntry:T:clsClaim"]!.Prop("subkind"));
    }

    [Fact]
    public void A_form_has_its_controls_in_pixels_with_containers_and_arrays()
    {
        var form = Map["ClaimsEntry:T:frmClaims"]!;
        Assert.Equal(NodeKind.Form, form.Kind);
        Assert.Equal("Claims Entry", form.Prop("title"));
        Assert.Equal("432, 288", form.Prop("ClientSize"));
        Assert.Equal(9, Trace.Controls(Map, form.Id).Count());
        var save = Control("cmdSave");
        Assert.Equal("CommandButton", save.Prop("type"));
        Assert.Equal("&Save Claim", save.Prop("Text"));
        Assert.Equal("312, 16", save.Prop("Location"));
        Assert.Equal("True", save.Prop("Default"));
        Assert.Equal("9", Control("txtPatientId").Prop("MaxLength"));
        Assert.Equal(Control("fraTotals").Id, Control("lblTotal").Parent);
        Assert.Equal("MSFlexGrid", Control("grdRecent").Prop("type"));
        var array = Control("lblPatient");
        Assert.Equal("true", array.Prop("array"));
        Assert.Equal("0, 1", array.Prop("Index"));
        Assert.Equal("Patient ID | Amount", array.Prop("Text"));
    }

    [Fact]
    public void Procedures_have_their_kind_size_and_complexity()
    {
        var save = Proc("frmClaims", "cmdSave_Click");
        Assert.Equal((114, 132), (save.Line, save.EndLine));
        Assert.Equal("Private", save.Prop("access"));
        Assert.Equal(4, save.IntProp("complexity"));
        var valid = Proc("clsClaim", "IsValid");
        Assert.Equal("function", valid.Prop("subkind"));
        Assert.Equal(5, valid.IntProp("complexity"));
        Assert.Equal("property let", Map["ClaimsEntry:M:clsClaim.Amount.let"]!.Prop("subkind"));
        Assert.Equal("property get", Map["ClaimsEntry:M:clsClaim.Amount.get"]!.Prop("subkind"));
        var declare = Proc("modClaims", "GetTickCount");
        Assert.Equal("declare", declare.Prop("subkind"));
        Assert.Contains(Map.From(declare.Id), e => e.To == "external:dll:kernel32");
    }

    [Fact]
    public void Handlers_bind_by_name_and_calls_match_across_the_project()
    {
        var handles = Assert.Single(Map.From(Proc("frmClaims", "cmdSave_Click").Id), e => e.Kind == EdgeKind.Handles);
        Assert.Equal(Control("cmdSave").Id, handles.To);
        Assert.Equal("Click", handles.Note);
        Assert.Contains(Map.From(Proc("frmClaims", "Form_Load").Id), e => e.Kind == EdgeKind.Handles && e.To == "ClaimsEntry:T:frmClaims" && e.Note == "Load");

        var calls = Map.From(Proc("frmClaims", "cmdSave_Click").Id).Where(e => e.Kind == EdgeKind.Calls).Select(e => e.To.Replace("ClaimsEntry:M:", "")).ToList();
        Assert.Equal(["clsClaim.Amount.let", "clsClaim.IsValid", "clsClaim.PatientId.let", "clsClaim.Status.let", "frmClaims.ShowTotal", "modClaims.SaveClaim"], calls.Order(StringComparer.Ordinal));
        Assert.All(Map.From(Proc("frmClaims", "cmdSave_Click").Id).Where(e => e.Kind == EdgeKind.Calls), e => Assert.Equal(Certainty.ByName, e.Certainty));
        Assert.Contains(Map.From(Proc("frmClaims", "cmdSave_Click").Id), e => e.Kind == EdgeKind.Creates && e.To == "ClaimsEntry:T:clsClaim");
        Assert.Equal(["cboStatus", "txtAmount", "txtPatientId"], Map.From(Proc("frmClaims", "cmdSave_Click").Id).Where(e => e.Kind == EdgeKind.Uses).Select(e => Map[e.To]!.Name).Distinct().Order());
        // Nothing calls PrintClaimLabel.
        Assert.Empty(Map.To(Proc("modClaims", "PrintClaimLabel").Id));
    }

    [Fact]
    public void SQL_in_strings_and_hard_coded_connections_are_read_without_their_passwords()
    {
        var save = Proc("modClaims", "SaveClaim");
        Assert.Equal("true", save.Prop("sqlBuiltByHand"));
        Assert.Equal("true", save.Prop("onErrorResumeNext"));
        Assert.Contains(Map.From(save.Id), e => e.Kind == EdgeKind.Writes && e.To == SqlNames.ColumnId("db:dbo.claim", "Status") && e.Certainty == Certainty.Guessed);
        Assert.Contains(Map.From(save.Id), e => e.Kind == EdgeKind.Executes && e.To == "db:dbo.usp_postcharge");
        Assert.Contains(Map.From(Proc("modClaims", "CountToday").Id), e => e.Kind == EdgeKind.Reads && e.To == "db:dbo.claim" && e.Certainty == Certainty.Resolved);

        var open = Proc("modClaims", "OpenDb");
        Assert.Equal("true", open.Prop("hardCodedPassword"));
        var conn = Map.Children(open.Id).Single(n => n.Kind == NodeKind.Connection);
        Assert.Equal("clinicdb01", conn.Prop("server"));
        Assert.Equal("(masked)", conn.Prop("password"));
        var path = Path.Combine(Path.GetTempPath(), $"vb6-{Guid.NewGuid():N}.lens");
        try
        {
            LensFile.Save(Map, path);
            Assert.DoesNotContain("Winter2003", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_VB6_button_traces_into_SQL()
    {
        var text = Trace.ToText(Trace.Forward(Map, Control("cmdSave").Id));
        Assert.Contains("on Click method frmClaims.cmdSave_Click", text);
        Assert.Contains("calls method modClaims.SaveClaim", text);
        Assert.Contains("writes table Claim", text);
        Assert.Contains("runs procedure usp_PostCharge", text);
        Assert.Contains("fires trigger trg_Charge_Audit", text);
    }

    [Fact]
    public void Secrets_are_masked_in_any_text_kept()
    {
        Assert.Equal("Server=x;Password=(masked);", Secrets.Mask("Server=x;Password=hunter2;"));
        Assert.Equal("cn.Open \"uid=sa;pwd=(masked)\"", Secrets.Mask("cn.Open \"uid=sa;pwd=abc\""));
        Assert.True(Secrets.HasPassword("User ID=a;Password=b"));
        Assert.False(Secrets.HasPassword("Integrated Security=SSPI"));
        Assert.Equal("<add key=\"SmtpPassword\" value=\"(masked)\" />", Secrets.Mask("<add key=\"SmtpPassword\" value=\"Mailbox#2009\" />"));
        Assert.Equal("<add key=\"ExportFolder\" value=\"\\\\fs\\x\" />", Secrets.Mask("<add key=\"ExportFolder\" value=\"\\\\fs\\x\" />"));
    }
}
