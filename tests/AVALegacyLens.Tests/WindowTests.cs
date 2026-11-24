using Avalonia.Controls;
using Avalonia.Controls.Documents;
using AVALegacyLens.Core.Model;

namespace AVALegacyLens.Tests;

/// <summary>The window, driven headless: scanning, the tree, the inventory, an item's source and links, finding, saving.</summary>
public sealed class WindowTests
{
    internal static MainWindow Scanned()
    {
        var w = new MainWindow();
        w.Show();
        w.SetMap(Samples.ClinicMap);
        HeadlessApp.Settle();
        return w;
    }

    private static string Highlighted(MainWindow w) =>
        string.Concat(w.Source.Inlines!.OfType<Run>().Where(r => r.Background != null).Select(r => r.Text));

    [Fact]
    public void Scanning_a_folder_fills_the_tree_and_the_inventory() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow();
        w.Show();
        var scan = w.ScanAsync(Samples.Clinic);
        for (var i = 0; i < 500 && !scan.IsCompleted; i++) HeadlessApp.Settle(1);
        HeadlessApp.Settle();
        Assert.NotNull(w.Map);
        var top = ((IEnumerable<TreeViewItem>)w.Tree.ItemsSource!).Select(i => ((Node)i.Tag!).Name).ToList();
        Assert.Equal(["ClaimsEntry", "ClinicApp", "ClinicUtil", "Database", "Outside the system"], top);
        Assert.Equal(0, w.Tabs.SelectedIndex);
        var text = string.Join("\n", w.InventoryPanel.GetVisualDescendantsOfType<TextBlock>().Select(t => t.Text ?? string.Concat(t.Inlines?.OfType<Run>().Select(r => r.Text) ?? [])));
        Assert.Contains("lines of code", text);
        Assert.Contains("frmPatient.ValidatePatient", text);
        Assert.Contains("Scanned", w.txtStatusBar.Text);
        w.Close();
    });

    [Fact]
    public void An_item_shows_its_source_and_its_links_both_ways() => HeadlessApp.Run(() =>
    {
        var w = Scanned();
        var map = w.Map!;
        var validate = map.Method("frmPatient", "ValidatePatient");
        w.ShowNode(validate);
        HeadlessApp.Settle();
        Assert.Equal(1, w.Tabs.SelectedIndex);
        Assert.Contains("Private Function ValidatePatient() As Boolean", Highlighted(w));
        Assert.Contains("End Function", Highlighted(w));
        Assert.DoesNotContain("btnSave_Click", Highlighted(w));
        Assert.Contains("ClinicApp/frmPatient.vb:34–53", w.ItemWhere.Text);
        var uses = ((IEnumerable<Relation>)w.UsesList.ItemsSource!).ToList();
        Assert.Equal(["Age", "IsMinor"], uses.Where(r => r.Edge.Kind == EdgeKind.Calls).Select(r => r.Other.Name).Order());
        Assert.Contains(uses, r => r.Edge.Kind == EdgeKind.Uses && r.Other.Name == "txtGuardian");
        var usedBy = ((IEnumerable<Relation>)w.UsedByList.ItemsSource!).Single();
        Assert.Equal("btnSave_Click", usedBy.Other.Name);
        // The tree followed: it's selected, its parents expanded.
        Assert.Equal(validate, ((TreeViewItem)w.Tree.SelectedItem!).Tag);

        // Following a link goes to the other end.
        w.ShowNode(usedBy.Other);
        HeadlessApp.Settle();
        Assert.Equal("btnSave_Click", w.Current!.Name);
        w.Close();
    });

    [Fact]
    public void Finding_by_name_lists_matches_and_opens_one() => HeadlessApp.Run(() =>
    {
        var w = Scanned();
        w.Filter.Text = "calculatecop";
        HeadlessApp.Settle();
        Assert.True(w.FilterResults.IsVisible);
        var found = ((IEnumerable<Node>)w.FilterResults.ItemsSource!).ToList();
        Assert.Equal("CalculateCopay", found[0].Name);
        w.FilterResults.SelectedIndex = 0;
        HeadlessApp.Settle();
        Assert.Equal("CalculateCopay", w.Current!.Name);
        w.Filter.Text = "";
        HeadlessApp.Settle();
        Assert.False(w.FilterResults.IsVisible);
        w.Close();
    });

    [Fact]
    public void The_trace_tab_follows_a_button_forward_and_code_backward() => HeadlessApp.Run(() =>
    {
        var w = Scanned();
        var map = w.Map!;
        w.ShowNode(map[Core.Scanning.WinFormsReader.ControlId(map.Type("frmPatient").Id, "btnSave")]!);
        w.ShowTrace(w.Current, forward: true);
        HeadlessApp.Settle();
        Assert.Equal(2, w.Tabs.SelectedIndex);
        Assert.Equal("btnSave_Click", w.CurrentTrace!.Next[0].Node.Name);
        Assert.StartsWith("What Button btnSave", w.TraceTitle.Text);
        var top = ((IEnumerable<TreeViewItem>)w.TraceTree.ItemsSource!).Single();
        Assert.True(top.IsExpanded);

        w.ShowNode(map.Method("PatientData", "GetConnection"));
        w.ShowTrace(w.Current, forward: false);
        HeadlessApp.Settle();
        Assert.StartsWith("What reaches method PatientData.GetConnection", w.TraceTitle.Text);
        Assert.True(w.TraceBackwardButton.IsChecked);
        Assert.Contains(w.CurrentTrace!.All(), s => s.Node.Name == "btnPost");
        w.Close();
    });

    [Fact]
    public void Tables_columns_and_procedures_show_their_SQL() => HeadlessApp.Run(() =>
    {
        var w = Scanned();
        var map = w.Map!;
        w.ShowNode(map["db:dbo.usp_postcharge"]!);
        HeadlessApp.Settle();
        Assert.Contains("CREATE PROCEDURE dbo.usp_PostCharge", Highlighted(w));
        Assert.Contains("procedure (T-SQL)", w.ItemWhere.Text);
        var uses = ((IEnumerable<Relation>)w.UsesList.ItemsSource!).ToList();
        Assert.Contains(uses, r => r.Edge.Kind == EdgeKind.Writes && r.Other.Name == "Charge");
        var usedBy = ((IEnumerable<Relation>)w.UsedByList.ItemsSource!).ToList();
        Assert.Contains(usedBy, r => r.Edge.Kind == EdgeKind.Executes && r.Other.Name == "PostCharge");

        w.ShowNode(map["db:dbo.patient.lastname"]!);
        HeadlessApp.Settle();
        Assert.Contains("type: varchar(40)", w.ItemProps.Text);
        Assert.Contains("LastName   varchar(40) NOT NULL", Highlighted(w));
        w.Close();
    });

    [Fact]
    public void A_connection_string_that_isnt_one_is_refused() => HeadlessApp.Run(() =>
    {
        var w = Scanned();
        w.AskConnection = () => Task.FromResult<string?>("this is not = a ; connection = string = at all");
        w.AddDatabase().GetAwaiter().GetResult();
        Assert.Empty(w.Databases);
        Assert.StartsWith("That isn't a connection string", w.txtStatusBar.Text);
        w.Close();
    });

    [Fact]
    public void A_scan_saves_as_a_lens_file_and_opens_again() => HeadlessApp.Run(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"window-{Guid.NewGuid():N}.lens");
        try
        {
            var w = Scanned();
            w.PickSaveFile = (_, _) => Task.FromResult<string?>(path);
            w.SaveLens(false).GetAwaiter().GetResult();
            Assert.True(File.Exists(path));
            Assert.Equal(path, w.LensPath);
            w.Close();

            var again = new MainWindow();
            again.Show();
            again.OpenLensNow(path);
            HeadlessApp.Settle();
            Assert.Equal(Samples.ClinicMap.Nodes.Count, again.Map!.Nodes.Count);
            again.ShowNode(again.Map.Method("BillingRules", "CalculateCopay"));
            HeadlessApp.Settle();
            Assert.Contains("Select Case payer", Highlighted(again));
            again.Close();
        }
        finally
        {
            File.Delete(path);
        }
    });
}

internal static class VisualExtensions
{
    public static IEnumerable<T> GetVisualDescendantsOfType<T>(this Avalonia.Visual v) where T : Avalonia.Visual =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(v).OfType<T>();
}
