using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AVALegacyLens.Core;
using AVALegacyLens.Core.Model;

namespace AVALegacyLens;

/// <summary>The Graph tab: the selected item's neighbourhood, one or two steps out; click an item to move there.</summary>
public sealed partial class MainWindow
{
    public GraphView Graph { get; } = new();
    public TextBlock GraphTitle { get; } = new() { Foreground = Look.Text, FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    public ToggleButton GraphDeeper { get; } = new() { Content = "Two steps out" };

    private Control BuildGraphPage()
    {
        GraphDeeper.Click += (_, _) => ShowGraph(_current);
        Graph.Picked += n =>
        {
            ShowNode(n, selectInTree: true, tab: false);
            ShowGraph(n);
        };
        Graph.Opened += n => ShowNode(n);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(14, 0, 14, 8) };
        foreach (var (label, kind) in new[] { ("calls", EdgeKind.Calls), ("handles", EdgeKind.Handles), ("runs", EdgeKind.Executes), ("reads", EdgeKind.Reads), ("writes", EdgeKind.Writes), ("uses", EdgeKind.Uses) })
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            item.Children.Add(new Border { Width = 16, Height = 3, Background = GraphView.EdgeBrush(kind), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = label, Foreground = Look.Dim, FontSize = 11.5 });
            legend.Children.Add(item);
        }
        legend.Children.Add(new TextBlock { Text = "- - by name    · · · guessed", Foreground = Look.Dim, FontSize = 11.5 });
        var bar = new DockPanel { Margin = new Thickness(14, 10, 14, 6) };
        DockPanel.SetDock(GraphDeeper, Dock.Right);
        bar.Children.Add(GraphDeeper);
        bar.Children.Add(GraphTitle);
        var page = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        page.Children.Add(bar);
        DockPanel.SetDock(legend, Dock.Top);
        page.Children.Add(legend);
        // The whole picture in view: a wide or tall neighbourhood is drawn smaller, never cut off.
        page.Children.Add(new Border { Background = Look.Sheet, Child = new Viewbox { Child = Graph, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top } });
        return page;
    }

    /// <summary>Draws an item's neighbourhood on the Graph tab.</summary>
    public void ShowGraph(Node? node)
    {
        if (_map == null || node == null)
        {
            Graph.Graph = null;
            GraphTitle.Text = "Pick an item to see what drives it and what it drives.";
            return;
        }
        var depth = GraphDeeper.IsChecked == true ? 2 : 1;
        Graph.Graph = Neighbourhood.Of(_map, node.Id, depth);
        GraphTitle.Text = $"Around {Trace.Describe(node)}  ·  {Graph.Graph.Items.Count - 1} items {(depth == 1 ? "one step" : "two steps")} out";
        Tabs.SelectedIndex = 6;
    }
}
