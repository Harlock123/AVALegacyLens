using Avalonia.Controls;
using Avalonia.Headless;
using AVALegacyLens.Core.Scanning;

namespace AVALegacyLens.Tests;

/// <summary>The README's pictures, drawn headless: LENS_SHOTS=folder dotnet test --filter Screenshots.</summary>
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

    [Fact]
    public void Main_window() => HeadlessApp.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("LENS_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var w = new MainWindow { Width = 1500, Height = 900 };
        w.Show();
        try
        {
            w.SetMap(Samples.ClinicMap);
            var map = w.Map!;
            foreach (var i in ((IEnumerable<TreeViewItem>)w.Tree.ItemsSource!).Take(1)) i.IsExpanded = true;
            Save(w, Path.Combine(dir, "inventory.png"));
            w.ShowNode(map.Method("frmPatient", "ValidatePatient"));
            Save(w, Path.Combine(dir, "item.png"));
            w.ShowNode(map[WinFormsReader.ControlId(map.Type("frmPatient").Id, "btnSave")]!);
            w.ShowTrace(w.Current, forward: true);
            Save(w, Path.Combine(dir, "trace.png"));
            w.ShowNode(map[WinFormsReader.ControlId(map.Type("frmBilling").Id, "btnPost")]!);
            w.ShowTrace(w.Current, forward: true);
            Save(w, Path.Combine(dir, "trace-sql.png"));
            w.ShowNode(map["db:dbo.usp_savepatient"]!);
            Save(w, Path.Combine(dir, "procedure.png"));
        }
        finally
        {
            w.Close();
        }
    });
}
