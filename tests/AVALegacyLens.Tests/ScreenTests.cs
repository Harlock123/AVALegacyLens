using AVALegacyLens.Core;
using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Scanning;

namespace AVALegacyLens.Tests;

/// <summary>WinForms screens from their designer code: controls, their properties and containers, and event handlers.</summary>
public sealed class ScreenTests
{
    private static SystemMap Map => Samples.ClinicMap;

    private static Node Control(SystemMap map, string form, string name) =>
        map[WinFormsReader.ControlId(map.Type(form).Id, name)] ?? throw new KeyNotFoundException($"No control {form}.{name}.");

    [Fact]
    public void Each_control_the_designer_creates_is_a_node_with_its_properties()
    {
        var patient = Map.Type("frmPatient");
        var controls = Trace.Controls(Map, patient.Id).Select(c => c.Name).ToList();
        Assert.Equal(13, controls.Count);
        Assert.Equal("txtLastName", controls[0]);
        Assert.Equal("13", patient.Prop("controls"));
        Assert.Equal("Patient", patient.Prop("title"));
        Assert.Equal("440, 366", patient.Prop("ClientSize"));

        var last = Control(Map, "frmPatient", "txtLastName");
        Assert.Equal("TextBox", last.Prop("type"));
        Assert.Equal("40", last.Prop("MaxLength"));
        Assert.Equal("112, 16", last.Prop("Location"));
        Assert.Equal("ClinicApp/frmPatient.Designer.vb", last.File);
        Assert.Equal(146, last.Line);
        Assert.Equal("&Save", Control(Map, "frmPatient", "btnSave").Prop("Text"));
        Assert.Equal("True", Control(Map, "frmPatient", "chkActive").Prop("Checked"));
        Assert.Equal("DropDownList", Control(Map, "frmPatient", "cboPayer").Prop("DropDownStyle"));
        Assert.Equal("Microsoft Sans Serif, 14, Bold", Control(Map, "frmMain", "lblTitle").Prop("Font"));
    }

    [Fact]
    public void Handles_clauses_link_handlers_to_their_controls_and_form()
    {
        var save = Map.Method("frmPatient", "btnSave_Click");
        var handles = Assert.Single(Map.From(save.Id), e => e.Kind == EdgeKind.Handles);
        Assert.Equal(Control(Map, "frmPatient", "btnSave").Id, handles.To);
        Assert.Equal("Click", handles.Note);
        var load = Assert.Single(Map.From(Map.Method("frmPatient", "frmPatient_Load").Id), e => e.Kind == EdgeKind.Handles);
        Assert.Equal(Map.Type("frmPatient").Id, load.To);
        Assert.Equal("Load", load.Note);
    }

    [Fact]
    public void Code_that_reads_or_sets_a_control_uses_it()
    {
        var used = Map.From(Map.Method("frmPatient", "ValidatePatient").Id).Where(e => e.Kind == EdgeKind.Uses).Select(e => Map[e.To]!.Name).Distinct().Order().ToList();
        Assert.Equal(["cboPayer", "dtpBirthDate", "txtGuardian", "txtLastName"], used);
        // The designer's own setup isn't a use.
        Assert.DoesNotContain(Map.From(Map.Method("frmPatient", "InitializeComponent").Id), e => e.Kind == EdgeKind.Uses);
    }

    [Fact]
    public void Csharp_designers_containers_and_AddHandler_are_read()
    {
        var root = Directory.CreateTempSubdirectory("lens-forms-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Cs"));
            File.WriteAllText(Path.Combine(root, "Cs", "Cs.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "Cs", "OrderForm.cs"), """
                namespace Shop
                {
                    public partial class OrderForm : System.Windows.Forms.Form
                    {
                        private void okButton_Click(object sender, System.EventArgs e) { Submit(okButton.Text); }
                        private void Submit(string s) { }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root, "Cs", "OrderForm.Designer.cs"), """
                namespace Shop
                {
                    partial class OrderForm
                    {
                        private void InitializeComponent()
                        {
                            this.panel1 = new System.Windows.Forms.Panel();
                            this.okButton = new System.Windows.Forms.Button();
                            this.okButton.Text = "OK";
                            this.okButton.Click += new System.EventHandler(this.okButton_Click);
                            this.panel1.Controls.Add(this.okButton);
                            this.Controls.AddRange(new System.Windows.Forms.Control[] { this.panel1 });
                            this.Text = "Order";
                        }
                        private System.Windows.Forms.Panel panel1;
                        private System.Windows.Forms.Button okButton;
                    }
                }
                """);
            Directory.CreateDirectory(Path.Combine(root, "Vb"));
            File.WriteAllText(Path.Combine(root, "Vb", "Vb.vbproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "Vb", "Search.vb"), """
                Public Class Search
                    Inherits System.Windows.Forms.Form
                    Private Sub InitializeComponent()
                        Me.btnGo = New System.Windows.Forms.Button()
                        Me.Controls.Add(Me.btnGo)
                    End Sub
                    Friend btnGo As System.Windows.Forms.Button
                    Public Sub New()
                        InitializeComponent()
                        AddHandler btnGo.Click, AddressOf Go
                    End Sub
                    Private Sub Go(sender As Object, e As EventArgs)
                    End Sub
                End Class
                """);
            var map = Scanner.Scan(root);
            var form = map.Type("OrderForm");
            Assert.Equal(NodeKind.Form, form.Kind);
            Assert.Equal("Order", form.Prop("title"));
            var ok = Control(map, "OrderForm", "okButton");
            Assert.Equal(Control(map, "OrderForm", "panel1").Id, ok.Parent);
            Assert.Equal("Button", ok.Prop("type"));
            Assert.Equal("OK", ok.Prop("Text"));
            Assert.Contains(map.To(ok.Id), e => e.Kind == EdgeKind.Handles && e.From == map.Method("OrderForm", "okButton_Click").Id && e.Note == "Click");
            Assert.Contains(map.To(ok.Id), e => e.Kind == EdgeKind.Uses);
            var go = Control(map, "Search", "btnGo");
            Assert.Contains(map.To(go.Id), e => e.Kind == EdgeKind.Handles && map[e.From]!.Name == "Go");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
