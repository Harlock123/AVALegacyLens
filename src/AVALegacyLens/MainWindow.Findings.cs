using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using AVALegacyLens.Core.Findings;
using AVALegacyLens.Core.Model;

namespace AVALegacyLens;

/// <summary>A list of findings of one kind (rules, dead code or risks), filtered by mark, each with mark buttons.</summary>
public sealed class FindingsView : DockPanel
{
    private readonly MainWindow _window;
    private readonly FindingKind _kind;

    public ListBox List { get; } = new() { Background = Look.Sheet };
    public ComboBox Filter { get; } = new() { ItemsSource = new[] { "All", "Not marked yet", "Confirmed", "Wrong", "Noise" }, SelectedIndex = 0, MinWidth = 160 };
    public TextBlock Count { get; } = new() { Foreground = Look.Heading, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
    public TextBlock Intro { get; } = new() { Foreground = Look.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 0, 14, 8) };

    public FindingsView(MainWindow window, FindingKind kind, string intro)
    {
        _window = window;
        _kind = kind;
        Intro.Text = intro;
        var bar = new DockPanel { Margin = new Thickness(14, 10, 14, 6) };
        DockPanel.SetDock(Filter, Dock.Right);
        bar.Children.Add(Filter);
        bar.Children.Add(Count);
        DockPanel.SetDock(bar, Dock.Top);
        Children.Add(bar);
        DockPanel.SetDock(Intro, Dock.Top);
        Children.Add(Intro);
        Children.Add(List);
        Filter.SelectionChanged += (_, _) => Fill();
        List.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Finding>((f, _) => f == null ? new TextBlock() : Row(f));
        List.DoubleTapped += (_, _) =>
        {
            if (List.SelectedItem is Finding f) _window.ShowFinding(f);
        };
    }

    /// <summary>The findings on show, after the filter.</summary>
    public IReadOnlyList<Finding> Shown => (List.ItemsSource as IReadOnlyList<Finding>) ?? [];

    public void Fill()
    {
        var map = _window.Map;
        if (map == null)
        {
            List.ItemsSource = Array.Empty<Finding>();
            Count.Text = "";
            return;
        }
        var all = map.Findings.Where(f => f.Kind == _kind).ToList();
        var shown = all.Where(f => (Filter.SelectedIndex, map.MarkOf(f)?.State) switch
            {
                (1, null) => true,
                (1, _) => false,
                (2, MarkState.Confirmed) or (3, MarkState.Wrong) or (4, MarkState.Noise) => true,
                (2 or 3 or 4, _) => false,
                _ => true,
            })
            .OrderByDescending(f => _kind == FindingKind.Risk ? f.Severity : f.Confidence)
            .ThenBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line).ToList();
        List.ItemsSource = shown;
        var marked = all.Count(f => map.MarkOf(f) != null);
        Count.Text = $"{all.Count} found · {marked} marked" + (shown.Count != all.Count ? $" · {shown.Count} shown" : "");
    }

    private Control Row(Finding f)
    {
        var map = _window.Map!;
        var mark = map.MarkOf(f);
        var head = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5 };
        var badge = _kind == FindingKind.Risk ? f.Severity.ToString() : f.Category;
        var badgeBrush = f.Severity switch { Severity.High => Look.Error, Severity.Medium => Look.Warning, _ => Look.Accent };
        if (_kind != FindingKind.Risk) badgeBrush = Look.Accent;
        head.Inlines!.Add(new Run(badge.ToUpperInvariant() + "  ") { Foreground = badgeBrush, FontSize = 11, FontWeight = FontWeight.SemiBold });
        head.Inlines.Add(new Run(f.Title) { Foreground = mark?.State is MarkState.Wrong or MarkState.Noise ? Look.Dim : Look.Text });
        var lines = new StackPanel { Spacing = 2 };
        lines.Children.Add(head);
        if (f.Detail is { Length: > 0 } d) lines.Children.Add(new TextBlock { Text = d, Foreground = Look.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var place = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        if (f.Code is { Length: > 0 } code) place.Inlines!.Add(new Run(code + "   ") { FontFamily = Look.Mono, Foreground = Look.Code });
        place.Inlines!.Add(new Run(Report.Place(f)) { Foreground = Look.Link });
        place.Inlines.Add(new Run($"   {f.Confidence.ToString().ToLowerInvariant()} confidence") { Foreground = Look.Gutter });
        lines.Children.Add(place);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        foreach (var (state, label) in new[] { (MarkState.Confirmed, "✓"), (MarkState.Wrong, "✗"), (MarkState.Noise, "–") })
        {
            var on = mark?.State == state;
            var b = new Button { Content = label, Width = 30, Padding = new Thickness(0, 2), HorizontalContentAlignment = HorizontalAlignment.Center, FontSize = 13, Background = on ? (state == MarkState.Confirmed ? Look.Good : Look.Warning) : null, Foreground = on ? Brushes.Black : Look.Text };
            ToolTip.SetTip(b, state switch { MarkState.Confirmed => "Confirmed: it's right", MarkState.Wrong => "Wrong: it isn't so", _ => "Noise: true but not worth keeping" });
            b.Click += (_, _) => _window.MarkFinding(f, on ? null : state);
            buttons.Children.Add(b);
        }
        var row = new DockPanel { Margin = new Thickness(2, 4) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(lines);
        return row;
    }
}

/// <summary>The Rules, Dead Code and Problems tabs, marking findings, and the report.</summary>
public sealed partial class MainWindow
{
    public FindingsView RulesView { get; private set; } = null!;
    public FindingsView DeadView { get; private set; } = null!;
    public FindingsView ProblemsView { get; private set; } = null!;

    private void BuildFindingViews()
    {
        RulesView = new FindingsView(this, FindingKind.Rule,
            "Checks, decisions and limits found in the code, in the original's own words where it has them. Mark each: ✓ it's a real rule, ✗ it isn't, – true but not worth keeping. Marks are kept in the .lens file and carried to a rescan.");
        DeadView = new FindingsView(this, FindingKind.DeadCode,
            "What nothing live reaches from the system's entry points. Unused: nothing refers to it; unreachable: only dead code does. Reports, jobs or other systems outside the scan may still use the medium and low confidence ones.");
        ProblemsView = new FindingsView(this, FindingKind.Risk, "What makes the system riskier or the rebuild harder, worst first.");
    }

    private void FillFindings()
    {
        RulesView.Fill();
        DeadView.Fill();
        ProblemsView.Fill();
        if (Tabs.ItemsSource is TabItem[] tabs && _map != null)
        {
            tabs[3].Header = $"Rules ({_map.Findings.Count(f => f.Kind == FindingKind.Rule)})";
            tabs[4].Header = $"Dead Code ({_map.Findings.Count(f => f.Kind == FindingKind.DeadCode)})";
            tabs[5].Header = $"Problems ({_map.Findings.Count(f => f.Kind == FindingKind.Risk)})";
        }
    }

    /// <summary>Marks a finding (or clears its mark); kept in the map, saved with the .lens file.</summary>
    public void MarkFinding(Finding f, MarkState? state, string? note = null)
    {
        if (_map == null) return;
        if (state is { } s) _map.Marks[f.Id] = new Mark(s, note, DateTimeOffset.Now);
        else _map.Marks.Remove(f.Id);
        FillFindings();
        ShowInventory();
        Say(state is not { } marked ? "Mark cleared." : $"Marked {marked.ToString().ToLowerInvariant()}: {f.Title}" + (_lensPath == null ? "  (save the lens to keep it)" : ""));
    }

    /// <summary>Shows a finding's item with its line highlighted.</summary>
    public void ShowFinding(Finding f)
    {
        if (_map?[f.NodeId] is not { } node) return;
        ShowNode(node);
        if (f.Line > 0 && f.File == node.File) HighlightSource(f.Line, f.Line);
    }

    /// <summary>Saves the report: HTML or Excel by the name chosen.</summary>
    public async Task ExportReport()
    {
        if (_map == null) return;
        var path = await PickSaveFile("Export the report (.html or .xlsx)", (_map.Root.Length > 0 ? Path.GetFileName(_map.Root) : "database") + "-report.html");
        if (path == null) return;
        try
        {
            Report.Save(_map, path);
            Say("Saved the report: " + path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say("Couldn't save the report: " + e.Message);
        }
    }

    /// <summary>The estimate, for the Inventory tab.</summary>
    private void AddEstimate()
    {
        if (_map == null) return;
        var est = Estimate.Of(_map);
        InventoryPanel.Children.Add(Section("Estimate to rebuild"));
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
        line.Inlines!.Add(new Run($"About {est.Days:0} person-days") { Foreground = Look.Accent, FontWeight = FontWeight.SemiBold });
        line.Inlines.Add(new Run($" for the live part (a range of {est.Low:0} to {est.High:0}); dead code left out. A rough size from counts the scan is sure of, not a quote.") { Foreground = Look.Text });
        InventoryPanel.Children.Add(line);
        InventoryPanel.Children.Add(Table(["Area", "What", "Count", "Days each", "Days"],
            est.Lines.Select(l => new[] { l.Area, l.What, l.Count.ToString("N0"), l.DaysEach.ToString("0.##"), l.Days.ToString("0.##") })));
    }
}
