using AVALegacyLens.Core;
using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Scanning;

namespace AVALegacyLens.Tests;

/// <summary>Traces: forward from a screen or button the way the program runs, backward from code to the screens that reach it.</summary>
public sealed class TraceTests
{
    private static SystemMap Map => Samples.ClinicMap;

    private static List<string> Lines(TraceStep t) => [.. Trace.ToText(t).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd())];

    [Fact]
    public void A_button_traces_to_its_handler_and_everything_it_calls()
    {
        var save = Map[WinFormsReader.ControlId(Map.Type("frmPatient").Id, "btnSave")]!;
        var trace = Trace.Forward(Map, save.Id);
        var handler = Assert.Single(trace.Next);
        Assert.Equal("on Click", handler.Verb);
        Assert.Equal("btnSave_Click", handler.Node.Name);
        Assert.Equal(["ValidatePatient", "SavePatient"], handler.Next.Select(s => s.Node.Name));
        var validate = handler.Next[0];
        Assert.Equal(["IsMinor", "Age"], validate.Next.Select(s => s.Node.Name));
        Assert.All(validate.Next, s => Assert.Equal(Certainty.ByName, s.Edge!.Certainty));
        var text = Lines(trace);
        Assert.Equal("Button btnSave \"Save\"", text[0]);
        Assert.Equal("  on Click method frmPatient.btnSave_Click  (ClinicApp/frmPatient.vb:28)", text[1]);
        Assert.Contains("      calls method AgeCalculator.IsMinor  (ClinicApp/frmPatient.vb:43) [by name]", text);
        // IsMinor calls Age, which ValidatePatient also calls: shown once, then marked.
        Assert.Contains(trace.All(), s => s.Node.Name == "Age" && s.Repeat && s.Stop == "shown above");
    }

    [Fact]
    public void A_screen_traces_through_its_load_and_its_buttons_and_stops_at_other_screens()
    {
        var main = Trace.Forward(Map, Map.Type("frmMain").Id);
        Assert.Equal(["btnPatients", "btnBilling", "btnExit"], main.Next.Select(s => s.Node.Name));
        var opens = main.Next[0].Next[0].Next.Single();
        Assert.Equal("frmPatient", opens.Node.Name);
        Assert.Equal("creates", opens.Verb);
        Assert.Equal("opens another screen", opens.Stop);
        Assert.Empty(opens.Next);

        var patient = Trace.Forward(Map, Map.Type("frmPatient").Id);
        Assert.Equal("on Load", patient.Next[0].Verb);
        Assert.Equal("frmPatient_Load", patient.Next[0].Node.Name);
    }

    [Fact]
    public void Backward_from_shared_code_reaches_every_button_that_runs_it()
    {
        var trace = Trace.Backward(Map, Map.Method("PatientData", "GetConnection").Id);
        var controls = trace.All().Where(s => s.Node.Kind == NodeKind.Control).Select(s => s.Node.Name).Distinct().Order().ToList();
        // The buttons that run it, and through their screens the main menu's buttons that open those.
        Assert.Equal(["btnBilling", "btnLookup", "btnPatients", "btnPost", "btnSave"], controls);
        var reachedScreens = trace.All().Where(s => s.Node.Kind == NodeKind.Form).Select(s => s.Node.Name).Distinct().Order().ToList();
        Assert.Equal(["frmBilling", "frmMain", "frmPatient"], reachedScreens);
        // Dead code reaches nothing: ExportPatients calls GetConnection, but nothing calls it.
        var export = trace.Next.Single(s => s.Node.Name == "ExportPatients");
        Assert.Empty(export.Next);
        // Screens lead back to the buttons that open them.
        var patient = trace.All().First(s => s.Node.Name == "frmPatient" && !s.Repeat);
        Assert.Contains(patient.Next, s => s.Verb == "opened by" && s.Node.Name == "btnPatients_Click");
    }

    [Fact]
    public void A_trace_is_cut_short_rather_than_running_away()
    {
        var trace = Trace.Forward(Map, Map.Type("frmMain").Id, depth: 1);
        Assert.All(trace.Next, s => Assert.Equal("deeper than 1 steps", s.Stop));
        var small = Trace.Backward(Map, Map.Method("PatientData", "GetConnection").Id, maxSteps: 3);
        Assert.Equal(3, small.All().Count());
        Assert.Equal("trace cut at 3 steps", small.Stop);
    }
}
