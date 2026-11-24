using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AVALegacyLens.Core;
using AVALegacyLens.Core.Model;

namespace AVALegacyLens;

/// <summary>The Trace tab: from the selected item, forward the way the program runs, or backward to what reaches it.</summary>
public sealed partial class MainWindow
{
    public TreeView TraceTree { get; } = new() { Background = Look.Sheet };
    public TextBlock TraceTitle { get; } = new() { Foreground = Look.Text, FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    public ToggleButton TraceForwardButton { get; } = new() { Content = "Forward", IsChecked = true };
    public ToggleButton TraceBackwardButton { get; } = new() { Content = "Backward" };

    /// <summary>The trace on show, for tests and for copying.</summary>
    public TraceStep? CurrentTrace { get; private set; }

    private bool _traceForward = true;

    private Control BuildTracePage()
    {
        TraceForwardButton.Click += (_, _) => ShowTrace(_current, forward: true);
        TraceBackwardButton.Click += (_, _) => ShowTrace(_current, forward: false);
        var copy = new Button { Content = "Copy as Text" };
        copy.Click += async (_, _) =>
        {
            if (CurrentTrace != null && Clipboard != null) await Clipboard.SetTextAsync(Trace.ToText(CurrentTrace));
        };
        var bar = new DockPanel { Margin = new Thickness(14, 10, 14, 8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(TraceForwardButton);
        buttons.Children.Add(TraceBackwardButton);
        buttons.Children.Add(copy);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(TraceTitle);
        TraceTree.DoubleTapped += (_, _) =>
        {
            if (TraceTree.SelectedItem is TreeViewItem { Tag: TraceStep s }) ShowNode(s.Node);
        };
        var page = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        page.Children.Add(bar);
        page.Children.Add(TraceTree);
        return page;
    }

    /// <summary>Traces from a node and shows the Trace tab.</summary>
    public void ShowTrace(Node? node, bool forward)
    {
        _traceForward = forward;
        TraceForwardButton.IsChecked = forward;
        TraceBackwardButton.IsChecked = !forward;
        if (_map == null || node == null)
        {
            CurrentTrace = null;
            TraceTree.ItemsSource = null;
            TraceTitle.Text = "Pick a screen, control, method or table, then trace it.";
            return;
        }
        var trace = forward ? Trace.Forward(_map, node.Id) : Trace.Backward(_map, node.Id);
        CurrentTrace = trace;
        var steps = trace.All().Count() - 1;
        TraceTitle.Text = (forward ? "What " : "What reaches ") + Trace.Describe(node) + (forward ? " leads to" : "") + $"  ·  {steps} step{(steps == 1 ? "" : "s")}";
        TraceTree.ItemsSource = new[] { TraceItem(trace, 0) };
        Tabs.SelectedIndex = 2;
    }

    private TreeViewItem TraceItem(TraceStep step, int depth)
    {
        var text = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        if (step.Verb.Length > 0) text.Inlines!.Add(new Run(step.Verb + "  ") { Foreground = Look.Dim });
        text.Inlines!.Add(new Run(Look.Glyph(step.Node.Kind) + "  ") { Foreground = Look.KindBrush(step.Node.Kind) });
        text.Inlines.Add(new Run(Trace.Describe(step.Node)) { Foreground = step.Repeat ? Look.Dim : Look.Text });
        if (step.Edge is { Certainty: not Certainty.Resolved } e)
            text.Inlines.Add(new Run(e.Certainty == Certainty.ByName ? "  by name" : "  guessed") { Foreground = Look.Warning, FontSize = 11 });
        if (step.Edge is { Line: > 0 } at) text.Inlines.Add(new Run($"  {at.File}:{at.Line}") { Foreground = Look.Dim, FontSize = 11 });
        if (step.Stop != null) text.Inlines.Add(new Run($"  — {step.Stop}") { Foreground = Look.Gutter, FontSize = 11, FontStyle = FontStyle.Italic });
        if (step.Edge?.Note is { } note) ToolTip.SetTip(text, note);
        return new TreeViewItem
        {
            Header = text,
            Tag = step,
            IsExpanded = depth < 6,
            ItemsSource = step.Next.Select(n => TraceItem(n, depth + 1)).ToList(),
        };
    }
}
