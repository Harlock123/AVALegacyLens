using Avalonia.Controls;
using Avalonia.Headless;
using AVALegacyLens.Agents.Chat;
using AVALegacyLens.Core.Scanning;
using AVALegacyLens.Tests.Agents;
using static AVALegacyLens.Tests.Agents.FakeModelServer;

namespace AVALegacyLens.Tests;

/// <summary>
/// The README's pictures, drawn headless: LENS_SHOTS=folder dotnet test --filter Screenshots. The sample is scanned
/// from a copy in the temporary folder, so no one's home folder shows. Off unless LENS_SHOTS is set.
/// </summary>
public sealed class Screenshots
{
    private static void Save(Window w, string path)
    {
        HeadlessApp.Settle(30);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(path);
    }

    /// <summary>A copy of the sample at a plain path (/tmp/lens-shots/ClinicLegacy).</summary>
    private static string Copy()
    {
        var root = Path.Combine(Path.GetTempPath(), "lens-shots");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        foreach (var f in Directory.EnumerateFiles(Samples.Clinic, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(root, "ClinicLegacy", Path.GetRelativePath(Samples.Clinic, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return Path.Combine(root, "ClinicLegacy");
    }

    [Fact]
    public void Main_window() => HeadlessApp.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("LENS_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var w = new MainWindow { Width = 1500, Height = 900 };
        w.AgentSettings.ChatOpen = false;
        w.Show();
        try
        {
            w.SetMap(Scanner.Scan(Copy()));
            var map = w.Map!;
            foreach (var i in ((IEnumerable<TreeViewItem>)w.Tree.ItemsSource!).Take(1)) i.IsExpanded = true;
            Save(w, Path.Combine(dir, "inventory.png"));
            w.ShowNode(map.Method("frmPatient", "ValidatePatient"));
            Save(w, Path.Combine(dir, "item.png"));
            w.ShowNode(map[WinFormsReader.ControlId(map.Type("frmBilling").Id, "btnPost")]!);
            w.ShowTrace(w.Current, forward: true);
            Save(w, Path.Combine(dir, "trace.png"));
            w.ShowNode(map["db:dbo.patient.lastname"]!);
            w.ShowTrace(w.Current, forward: false);
            Save(w, Path.Combine(dir, "trace-backward.png"));
            w.ShowNode(map["db:dbo.usp_savepatient"]!);
            Save(w, Path.Combine(dir, "procedure.png"));
            w.Tabs.SelectedIndex = 3;
            Save(w, Path.Combine(dir, "rules.png"));
            w.Tabs.SelectedIndex = 4;
            Save(w, Path.Combine(dir, "dead-code.png"));
            w.Tabs.SelectedIndex = 5;
            Save(w, Path.Combine(dir, "problems.png"));
            w.ShowNode(map.Method("BillingRules", "PostCharge"));
            w.GraphDeeper.IsChecked = true;
            w.ShowGraph(w.Current);
            Save(w, Path.Combine(dir, "graph.png"));
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public void Ai_chat() => HeadlessApp.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("LENS_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var root = Copy();
        var w = new MainWindow { Width = 1600, Height = 900 };
        w.ChatKeys = (new ApiKeyStore(null, null), new ApiKeyStore(null, null));
        w.AgentSettings.ChatProvider = "OpenAICompatible";
        w.AgentSettings.ChatServerUrl = "http://localhost:1234/v1";
        w.AgentSettings.ChatServerModel = "qwen3-8b";
        w.AgentSettings.WorkingFolder = root;
        w.Show();
        try
        {
            w.SetMap(Scanner.Scan(root));
            if (!w.ChatHolder.IsVisible) w.ToggleChat();
            var server = new FakeModelServer("http://localhost:1234/v1")
                .Reply(Say("I'll look at what writes the column, and from where."), Call("c1", "who_writes", new { item = "Patient.LastName" }))
                .Reply(Call("c2", "describe_screen", new { screen = "frmPatient" }))
                .Reply(Say("Showing you the checks Save makes first."), Call("c3", "show_in_view", new { item = "frmPatient.ValidatePatient", line = 43 }))
                .Reply(Say("Only usp_SavePatient writes Patient.LastName (Database/procs.sql:19 and :25), and only the Patient screen's Save button reaches it: btnSave_Click → ValidatePatient → PatientData.SavePatient → usp_SavePatient.\n\nBefore it saves, the screen checks:\n• Last name is required (frmPatient.vb:35).\n• Birth date can't be in the future (frmPatient.vb:39), checked again in the procedure (procs.sql:7).\n• A guardian is required under 18 (frmPatient.vb:43, procs.sql:12).\n• Medicaid over 64 asks to confirm (frmPatient.vb:47).\n\nThe calls into AgeCalculator are matched by name (VB calling a C# library), so test those two paths."));
            var panel = w.Chat!;
            panel.ServerFactory = (_, key) => server.Client(key);
            panel.Input.Text = "Who writes Patient.LastName, and what does the screen check first?";
            panel.Send();
            AgentTests.Wait(panel.Running.ContinueWith(_ => true));
            Save(w, Path.Combine(dir, "ai-chat.png"));
            panel.SetupOpen = true;
            Save(w, Path.Combine(dir, "ai-chat-setup.png"));
        }
        finally { w.Close(); }
    });
}
