using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AVALegacyLens.Core;
using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Scanning;
using AVALegacyLens.Mcp;

namespace AVALegacyLens;

/// <summary>One line in an item's "uses" or "used by" list: the link and the node at its other end.</summary>
public sealed record Relation(Edge Edge, Node Other, bool Outgoing)
{
    public override string ToString() => $"{Edge.Kind} {Other.Name}";
}

/// <summary>
/// The window: the system's tree (projects, files, screens, types, methods, and later the database) on the left; on
/// the right the inventory, and the selected item with its source and its links both ways. Built in code; its controls
/// are reachable from tests. A scan runs off the UI thread; the map it makes can be saved as a .lens file.
/// </summary>
public sealed partial class MainWindow : Window
{
    private SystemMap? _map;
    private Node? _current;
    private string? _lensPath;
    private bool _scanning;
    private readonly Dictionary<string, TreeViewItem> _items = new(StringComparer.Ordinal);

    public TreeView Tree { get; } = new() { Background = Look.Bar };
    public TextBox Filter { get; } = new() { Watermark = "Find by name…", Margin = new Thickness(8, 4, 8, 6) };
    public ListBox FilterResults { get; } = new() { Background = Look.Bar, IsVisible = false };
    public TabControl Tabs { get; } = new();
    public StackPanel InventoryPanel { get; } = new() { Margin = new Thickness(20, 14), Spacing = 6 };
    public TextBlock ItemTitle { get; } = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, TextWrapping = TextWrapping.Wrap };
    public TextBlock ItemWhere { get; } = new() { Foreground = Look.Heading, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    public SelectableTextBlock ItemProps { get; } = new() { Foreground = Look.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    public SelectableTextBlock Source { get; } = new() { FontFamily = Look.Mono, FontSize = Look.FontSize, Foreground = Look.Code, Margin = new Thickness(6, 8) };
    public ScrollViewer SourceScroll { get; } = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Background = Look.Sheet };
    public ListBox UsesList { get; } = new() { Background = Look.Sheet };
    public ListBox UsedByList { get; } = new() { Background = Look.Sheet };
    public TextBlock UsesTitle { get; } = new() { Foreground = Look.Heading, FontSize = 11, Margin = new Thickness(8, 8, 8, 4) };
    public TextBlock UsedByTitle { get; } = new() { Foreground = Look.Heading, FontSize = 11, Margin = new Thickness(8, 8, 8, 4) };
    public TextBlock txtStatusBar { get; } = new() { Foreground = Look.Heading, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    public Border ChatHolder { get; } = new() { Width = 440, IsVisible = false, BorderBrush = Look.Border, BorderThickness = new Thickness(1, 0, 0, 0) };
    /// <summary>Hidden: where render_view draws for agents (controls need a window to take their look).</summary>
    public Panel AgentStage { get; } = new() { Width = 0, Height = 0, ClipToBounds = true, IsHitTestVisible = false };
    public MenuItem ChatMenuItem { get; } = new() { Header = "AI _Chat", InputGesture = new KeyGesture(Key.F12), ToggleType = MenuItemToggleType.CheckBox };
    public MenuItem AllowAgentsMenuItem { get; } = new() { Header = "_Allow AI Agents (MCP)", ToggleType = MenuItemToggleType.CheckBox };

    /// <summary>Asks for a folder to scan; tests answer it themselves.</summary>
    public Func<Task<string?>> PickFolder { get; set; }

    /// <summary>Asks for a .lens file to open; tests answer it themselves.</summary>
    public Func<Task<string?>> PickLens { get; set; }

    /// <summary>Asks for a SQL Server connection string; tests answer it themselves.</summary>
    public Func<Task<string?>> AskConnection { get; set; }

    /// <summary>Live databases read with each scan (connection strings, kept only while the window is open).</summary>
    public List<string> Databases { get; } = [];

    /// <summary>Asks where to save, given a title and the name to suggest; tests answer it themselves.</summary>
    public Func<string, string, Task<string?>> PickSaveFile { get; set; }

    public SystemMap? Map => _map;
    public Node? Current => _current;
    public string? LensPath => _lensPath;

    public MainWindow() : this(null) { }

    public MainWindow(string? open)
    {
        Title = "AVA Legacy Lens";
        Width = 1440;
        Height = 880;
        MinWidth = 800;
        MinHeight = 500;
        Background = Look.Window;
        PickFolder = AskForFolder;
        PickLens = AskForLens;
        PickSaveFile = AskWhereToSave;
        AskConnection = () => ConnectionDialog.Ask(this);

        var root = new DockPanel();
        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);
        var status = new Border { Child = txtStatusBar, Background = Look.Bar, BorderBrush = Look.Border, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 5) };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        DockPanel.SetDock(ChatHolder, Dock.Right);
        root.Children.Add(ChatHolder);
        DockPanel.SetDock(AgentStage, Dock.Right);
        root.Children.Add(AgentStage);

        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("340,4,*") };
        var left = new DockPanel { Background = Look.Bar };
        var leftTitle = new TextBlock { Text = "SYSTEM", Foreground = Look.Heading, FontSize = 11, Margin = new Thickness(10, 8, 10, 2) };
        DockPanel.SetDock(leftTitle, Dock.Top);
        left.Children.Add(leftTitle);
        DockPanel.SetDock(Filter, Dock.Top);
        left.Children.Add(Filter);
        var treeHolder = new Panel();
        treeHolder.Children.Add(Tree);
        treeHolder.Children.Add(FilterResults);
        left.Children.Add(treeHolder);
        Tree.SelectionChanged += (_, _) =>
        {
            if (Tree.SelectedItem is TreeViewItem { Tag: Node n } && !ReferenceEquals(n, _current)) ShowNode(n, selectInTree: false);
        };
        Filter.TextChanged += (_, _) => FillFilter();
        Filter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Filter.Text = "";
            if (e.Key == Key.Enter && FilterResults.ItemCount > 0) FilterResults.SelectedIndex = 0;
        };
        FilterResults.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Node>((n, _) => n == null ? new TextBlock() : NodeLabel(n, withPlace: true));
        FilterResults.SelectionChanged += (_, _) =>
        {
            if (FilterResults.SelectedItem is Node n) ShowNode(n);
        };
        main.Children.Add(left);
        var split = new GridSplitter { Background = Look.Border, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(split, 1);
        main.Children.Add(split);
        Grid.SetColumn(Tabs, 2);
        main.Children.Add(Tabs);
        BuildFindingViews();
        Tabs.ItemsSource = new[]
        {
            new TabItem { Header = "Inventory", Content = new ScrollViewer { Content = InventoryPanel }, FontSize = 14 },
            new TabItem { Header = "Item", Content = BuildItemPage(), FontSize = 14 },
            new TabItem { Header = "Trace", Content = BuildTracePage(), FontSize = 14 },
            new TabItem { Header = "Rules", Content = RulesView, FontSize = 14 },
            new TabItem { Header = "Dead Code", Content = DeadView, FontSize = 14 },
            new TabItem { Header = "Problems", Content = ProblemsView, FontSize = 14 },
            new TabItem { Header = "Graph", Content = BuildGraphPage(), FontSize = 14 },
        };
        root.Children.Add(main);
        Content = root;

        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var path = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p != null);
            if (path != null) _ = OpenAsync(path);
        });
        DragDrop.SetAllowDrop(this, true);
        ShowInventory();
        Say("Scan a folder of legacy code (File › Scan Folder…, Ctrl+O), open a saved .lens, or drop either here. Try samples/ClinicLegacy.");
        SetUpAgents();
        if (open != null) _ = OpenAsync(open);
    }

    private Menu BuildMenu()
    {
        MenuItem Item(string header, Action click, Key? key = null, KeyModifiers mods = KeyModifiers.Control)
        {
            var m = new MenuItem { Header = header };
            if (key is { } k) m.InputGesture = new KeyGesture(k, mods);
            m.Click += (_, _) => click();
            return m;
        }
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.O, KeyModifiers.Control), Command = new Command(() => _ = ScanPicked()) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, KeyModifiers.Control), Command = new Command(() => _ = SaveLens(false)) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F5), Command = new Command(() => _ = Rescan()) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F, KeyModifiers.Control), Command = new Command(() => Filter.Focus()) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F6), Command = new Command(() => ShowTrace(_current, forward: true)) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F12), Command = new Command(ToggleChat) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F6, KeyModifiers.Shift), Command = new Command(() => ShowTrace(_current, forward: false)) });
        var file = new MenuItem
        {
            Header = "_File",
            ItemsSource = new object[]
            {
                Item("_Scan Folder…", () => _ = ScanPicked(), Key.O),
                Item("_Open Lens…", () => _ = OpenLensPicked(), Key.L),
                Item("Add _Database…", () => _ = AddDatabase()),
                Item("_Rescan", () => _ = Rescan(), Key.F5, KeyModifiers.None),
                new Separator(),
                Item("Sa_ve Lens", () => _ = SaveLens(false), Key.S),
                Item("Save Lens _As…", () => _ = SaveLens(true)),
                Item("_Export Report…", () => _ = ExportReport(), Key.E),
                Item("Export _Parity Plan…", () => _ = ExportParityPlan()),
                Item("Export Screens as A_XAML…", () => _ = ExportAxaml()),
                new Separator(),
                Item("E_xit", Close),
            },
        };
        var view = new MenuItem
        {
            Header = "_View",
            ItemsSource = new object[]
            {
                Item("_Inventory", () => Tabs.SelectedIndex = 0, Key.D1),
                Item("I_tem", () => Tabs.SelectedIndex = 1, Key.D2),
                Item("T_race", () => Tabs.SelectedIndex = 2, Key.D3),
                Item("_Rules", () => Tabs.SelectedIndex = 3, Key.D4),
                Item("_Dead Code", () => Tabs.SelectedIndex = 4, Key.D5),
                Item("_Problems", () => Tabs.SelectedIndex = 5, Key.D6),
                Item("_Graph", () => ShowGraph(_current), Key.D7),
                new Separator(),
                Item("Trace _Forward", () => ShowTrace(_current, forward: true), Key.F6, KeyModifiers.None),
                Item("Trace _Backward", () => ShowTrace(_current, forward: false), Key.F6, KeyModifiers.Shift),
                new Separator(),
                Item("_Find…", () => Filter.Focus(), Key.F),
                Item("_Collapse the Tree", () => { foreach (var i in _items.Values) i.IsExpanded = false; }),
            },
        };
        ChatMenuItem.Click += OnToggleChatClick;
        AllowAgentsMenuItem.Click += OnAllowAgentsClick;
        var ai = new MenuItem
        {
            Header = "_AI",
            ItemsSource = new object[]
            {
                ChatMenuItem,
                new Separator(),
                Item("_Working Folder…", () => OnWorkingFolderClick(null, new Avalonia.Interactivity.RoutedEventArgs())),
                new Separator(),
                AllowAgentsMenuItem,
                Item("C_onnect an Agent…", () => _ = ShowConnect()),
            },
        };
        var help = new MenuItem
        {
            Header = "_Help",
            ItemsSource = new object[] { Item("_About", () => Say($"AVA Legacy Lens {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}: see what a legacy system really does before you rebuild it. MIT licence, Lonnie Watson.")) },
        };
        return new Menu { Background = Look.Bar, ItemsSource = new object[] { file, view, ai, help } };
    }

    private Control BuildItemPage()
    {
        var header = new StackPanel { Margin = new Thickness(16, 12, 16, 8), Spacing = 3 };
        var traceForward = new Button { Content = "Trace forward  F6", FontSize = 12 };
        traceForward.Click += (_, _) => ShowTrace(_current, forward: true);
        var traceBackward = new Button { Content = "Trace backward  ⇧F6", FontSize = 12 };
        traceBackward.Click += (_, _) => ShowTrace(_current, forward: false);
        var graphButton = new Button { Content = "Graph  Ctrl+7", FontSize = 12 };
        graphButton.Click += (_, _) => ShowGraph(_current);
        var traceButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        traceButtons.Children.Add(graphButton);
        traceButtons.Children.Add(traceForward);
        traceButtons.Children.Add(traceBackward);
        var titleRow = new DockPanel();
        DockPanel.SetDock(traceButtons, Dock.Right);
        titleRow.Children.Add(traceButtons);
        titleRow.Children.Add(ItemTitle);
        header.Children.Add(titleRow);
        header.Children.Add(ItemWhere);
        header.Children.Add(ItemProps);
        SourceScroll.Content = Source;

        var relations = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,*") };
        relations.Children.Add(UsesTitle);
        Grid.SetRow(UsesList, 1);
        relations.Children.Add(UsesList);
        Grid.SetRow(UsedByTitle, 2);
        relations.Children.Add(UsedByTitle);
        Grid.SetRow(UsedByList, 3);
        relations.Children.Add(UsedByList);
        foreach (var list in new[] { UsesList, UsedByList })
        {
            list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Relation>((r, _) => r == null ? new TextBlock() : RelationLabel(r));
            list.DoubleTapped += (_, _) =>
            {
                if (list.SelectedItem is Relation r) ShowNode(r.Other);
            };
            list.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && list.SelectedItem is Relation r) ShowNode(r.Other);
            };
            list.SelectionChanged += (_, _) =>
            {
                // A single click shows where the link is in this item's source (or the other end's, for "used by").
                if (list.SelectedItem is Relation { Outgoing: true } r && _current != null && r.Edge.File == _current.File) HighlightSource(r.Edge.Line, r.Edge.Line);
            };
        }

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,380") };
        body.Children.Add(SourceScroll);
        var split = new GridSplitter { Background = Look.Border, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(split, 1);
        body.Children.Add(split);
        var right = new Border { Child = relations, Background = Look.Bar };
        Grid.SetColumn(right, 2);
        body.Children.Add(right);

        var page = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        page.Children.Add(header);
        page.Children.Add(body);
        return page;
    }

    // ----- Opening and scanning -----

    /// <summary>Opens a folder (scanning it) or a .lens file.</summary>
    public Task OpenAsync(string path) =>
        path.EndsWith(LensFile.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? Task.Run(() => OpenLens(path)) : ScanAsync(path);

    /// <summary>Scans a folder (with the databases added, if any) off the UI thread and shows the result.</summary>
    public async Task ScanAsync(string folder)
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            var progress = new Progress<string>(Say);
            Say($"Scanning {folder}…");
            var databases = Databases.ToList();
            var map = await Task.Run(() => Scanner.Scan(folder, new ScanOptions { Progress = progress, Databases = databases }));
            // A rescan keeps people's marks (findings are matched by id).
            if (_map != null && string.Equals(_map.Root, map.Root, StringComparison.Ordinal))
                foreach (var (id, mark) in _map.Marks) map.Marks.TryAdd(id, mark);
            _lensPath = null;
            SetMap(map);
            Say($"Scanned {(map.Root.Length > 0 ? map.Root : "the database")}: {map.Nodes.Count:N0} items, {map.Edges.Count:N0} links" + (map.Notes.Count > 0 ? $", {map.Notes.Count} files couldn't be read" : "") + ".");
        }
        catch (Exception e)
        {
            Say("Couldn't scan: " + e.Message);
        }
        finally
        {
            _scanning = false;
        }
    }

    private void OpenLens(string path)
    {
        try
        {
            var map = LensFile.Load(path);
            Dispatcher.UIThread.Post(() =>
            {
                _lensPath = path;
                SetMap(map);
                Say($"Opened {Path.GetFileName(path)}: a scan of {map.Root} from {map.ScannedAt:g}" + (Directory.Exists(map.Root) ? "." : " (that folder isn't here, so source can't be shown)."));
            });
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Dispatcher.UIThread.Post(() => Say("Couldn't open that .lens file: " + e.Message));
        }
    }

    /// <summary>Opens a .lens file on the UI thread (for tests and agents).</summary>
    public void OpenLensNow(string path)
    {
        var map = LensFile.Load(path);
        _lensPath = path;
        SetMap(map);
    }

    private async Task ScanPicked()
    {
        if (await PickFolder() is { } folder) await ScanAsync(folder);
    }

    private async Task OpenLensPicked()
    {
        if (await PickLens() is { } path) await OpenAsync(path);
    }

    private Task Rescan() => _map != null && (_map.Root.Length == 0 || Directory.Exists(_map.Root)) ? ScanAsync(_map.Root) : Task.CompletedTask;

    /// <summary>Adds a live SQL Server database to the scan (its schema only) and scans again.</summary>
    public async Task AddDatabase()
    {
        if (await AskConnection() is not { Length: > 0 } cs) return;
        try
        {
            _ = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or KeyNotFoundException)
        {
            Say("That isn't a connection string: " + e.Message);
            return;
        }
        Databases.Add(cs);
        await ScanAsync(_map?.Root ?? "");
    }

    public async Task SaveLens(bool ask)
    {
        if (_map == null) return;
        var path = _lensPath;
        if (ask || path == null)
            path = await PickSaveFile("Save the scan", (_map.Root.Length > 0 ? Path.GetFileName(_map.Root) : "database") + LensFile.Extension);
        if (path == null) return;
        try
        {
            LensFile.Save(_map, path);
            _lensPath = path;
            Say("Saved " + path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say("Couldn't save: " + e.Message);
        }
    }

    /// <summary>Shows a map: fills the tree and the inventory, and selects nothing.</summary>
    public void SetMap(SystemMap map)
    {
        _map = map;
        _current = null;
        _items.Clear();
        Title = "AVA Legacy Lens — " + (map.Root.Length > 0 ? Path.GetFileName(map.Root) : "database");
        var top = map.Children(null).OrderBy(n => TopOrder(n)).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Tree.ItemsSource = top.Select(MakeItem).ToList();
        foreach (var item in Tree.ItemsSource.Cast<TreeViewItem>().Take(1)) item.IsExpanded = true;
        ShowInventory();
        ClearItem();
        FillFindings();
        Tabs.SelectedIndex = 0;
        FillFilter();
    }

    private static int TopOrder(Node n) => n.Prop("subkind") switch
    {
        _ when n.Kind != NodeKind.Project => 4,
        "folder" => 1,
        "database" => 2,
        "outside" => 3,
        _ => 0,
    };

    // ----- The tree -----

    private TreeViewItem MakeItem(Node node)
    {
        var item = new TreeViewItem { Header = NodeLabel(node, withPlace: false), Tag = node };
        _items[node.Id] = item;
        if (_map!.Children(node.Id).Count > 0)
        {
            item.ItemsSource = new[] { new TreeViewItem { Header = "…" } };
            var filled = false;
            item.PropertyChanged += (_, e) =>
            {
                if (e.Property != TreeViewItem.IsExpandedProperty || !item.IsExpanded || filled) return;
                filled = true;
                Fill(item, node);
            };
        }
        return item;
    }

    private void Fill(TreeViewItem item, Node node) => item.ItemsSource = _map!.Children(node.Id).Select(MakeItem).ToList();

    /// <summary>Expands the tree down to a node and selects it.</summary>
    private void SelectInTree(Node node)
    {
        if (_map == null) return;
        TreeViewItem? item = null;
        foreach (var n in _map.PathTo(node.Id))
        {
            if (!_items.TryGetValue(n.Id, out item)) return;
            if (n.Id == node.Id) break;
            if (!item.IsExpanded) item.IsExpanded = true;
            if (item.ItemsSource is IEnumerable<TreeViewItem> kids && kids.FirstOrDefault()?.Tag == null) Fill(item, n);
        }
        if (item != null)
        {
            Tree.SelectedItem = item;
            item.BringIntoView();
        }
    }

    internal static Control NodeLabel(Node n, bool withPlace)
    {
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
        text.Inlines!.Add(new Run(Look.Glyph(n.Kind) + "  ") { Foreground = Look.KindBrush(n.Kind) });
        text.Inlines.Add(new Run(n.Name) { Foreground = Look.Text });
        var extra = n.Kind switch
        {
            NodeKind.File => n.Prop("code") is { } c ? $"  {c} lines" : null,
            NodeKind.Method => n.IntProp("complexity") >= 10 ? $"  complexity {n.Prop("complexity")}" : null,
            NodeKind.Form => "  " + (n.Prop("title") is { Length: > 0 } title ? $"\"{title}\"" : n.Prop("subkind") ?? "form"),
            NodeKind.Control => "  " + n.Prop("type") + (n.Prop("Text") is { Length: > 0 } t ? $" \"{t.Replace("&", "", StringComparison.Ordinal)}\"" : ""),
            NodeKind.Column => "  " + n.Prop("type") + (n.Prop("key") is { } k ? $" {k} key" : ""),
            NodeKind.Table when n.Prop("defined") == "false" => "  not defined here",
            NodeKind.Setting => n.Prop("value") is { } v ? $"  = {v}" : null,
            NodeKind.Connection => n.Prop("database") is { } db ? $"  {db} on {n.Prop("server")}" : null,
            _ => null,
        };
        if (withPlace && n.File != null) extra = $"  {n.File}:{n.Line}";
        if (n.Prop("dead") == "true") extra = (extra ?? "") + "  dead";
        if (extra != null) text.Inlines.Add(new Run(extra) { Foreground = Look.Dim, FontSize = 11.5 });
        return text;
    }

    private Control RelationLabel(Relation r)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 };
        text.Inlines!.Add(new Run(Verb(r) + " ") { Foreground = Look.Dim });
        text.Inlines.Add(new Run(Look.Glyph(r.Other.Kind) + " ") { Foreground = Look.KindBrush(r.Other.Kind) });
        var name = r.Other.Kind switch
        {
            NodeKind.Method when r.Other.Prop("type") is { } t => $"{t}.{r.Other.Name}",
            NodeKind.Column when r.Other.Prop("table") is { } table => $"{table}.{r.Other.Name}",
            NodeKind.Control when r.Other.Prop("form") is { } form => $"{form}.{r.Other.Name}",
            _ => r.Other.Name,
        };
        text.Inlines.Add(new Run(name) { Foreground = Look.Text });
        if (r.Edge.Certainty != Certainty.Resolved) text.Inlines.Add(new Run(r.Edge.Certainty == Certainty.ByName ? "  by name" : "  guessed") { Foreground = Look.Warning, FontSize = 11 });
        if (r.Edge.Line > 0) text.Inlines.Add(new Run($"  line {r.Edge.Line}") { Foreground = Look.Dim, FontSize = 11 });
        ToolTip.SetTip(text, r.Edge.Note ?? r.Other.Id);
        return text;
    }

    private static string Verb(Relation r) => (r.Edge.Kind, r.Outgoing) switch
    {
        (EdgeKind.Calls, true) => "calls",
        (EdgeKind.Calls, false) => "called by",
        (EdgeKind.Creates, true) => "creates",
        (EdgeKind.Creates, false) => "created by",
        (EdgeKind.Inherits, true) => "inherits",
        (EdgeKind.Inherits, false) => "inherited by",
        (EdgeKind.Handles, true) => "handles",
        (EdgeKind.Handles, false) => "handled by",
        (EdgeKind.Reads, true) => "reads",
        (EdgeKind.Reads, false) => "read by",
        (EdgeKind.Writes, true) => "writes",
        (EdgeKind.Writes, false) => "written by",
        (EdgeKind.Executes, true) => "runs",
        (EdgeKind.Executes, false) => "run by",
        (EdgeKind.Uses, true) => "uses",
        (EdgeKind.Uses, false) => "used by",
        _ => r.Edge.Kind.ToString().ToLowerInvariant(),
    };

    private void FillFilter()
    {
        var q = Filter.Text?.Trim() ?? "";
        if (_map == null || q.Length < 2)
        {
            FilterResults.IsVisible = false;
            Tree.IsVisible = true;
            return;
        }
        var found = _map.Nodes.Where(n => n.Name.Contains(q, StringComparison.OrdinalIgnoreCase) && n.Kind != NodeKind.Project)
            .OrderBy(n => !n.Name.Equals(q, StringComparison.OrdinalIgnoreCase)).ThenBy(n => !n.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .ThenBy(n => n.Kind).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Take(300).ToList();
        FilterResults.ItemsSource = found;
        FilterResults.IsVisible = true;
        Tree.IsVisible = false;
    }

    // ----- The inventory -----

    private void ShowInventory()
    {
        InventoryPanel.Children.Clear();
        if (_map == null)
        {
            InventoryPanel.Children.Add(Heading("No system yet"));
            InventoryPanel.Children.Add(new TextBlock
            {
                Text = "Scan a folder of legacy code: VB.NET and C# projects (WinForms included). Legacy Lens maps what calls what, " +
                       "so you can see what a system does before rebuilding it. It reads code only, never data.",
                TextWrapping = TextWrapping.Wrap, Foreground = Look.Dim, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left,
            });
            return;
        }
        var inv = Inventory.Of(_map);
        InventoryPanel.Children.Add(Heading(Path.GetFileName(_map.Root)));
        InventoryPanel.Children.Add(new TextBlock { Text = $"{_map.Root}  ·  scanned {_map.ScannedAt:g}", Foreground = Look.Dim, FontSize = 12 });

        var cards = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
        void CardOf(string number, string label, IBrush brush)
        {
            var s = new StackPanel { Spacing = 2 };
            s.Children.Add(new TextBlock { Text = number, FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = brush });
            s.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Look.Heading });
            cards.Children.Add(new Border { Child = s, Background = Look.Card, BorderBrush = Look.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 10, 10), MinWidth = 120 });
        }
        CardOf(inv.TotalCode.ToString("N0"), "lines of code", Look.Accent);
        CardOf(inv.Counts[NodeKind.File].ToString("N0"), "files", Look.Text);
        foreach (var (kind, label) in new[] { (NodeKind.Form, "screens"), (NodeKind.Type, "types"), (NodeKind.Method, "methods"), (NodeKind.Table, "tables"), (NodeKind.Procedure, "procedures") })
            if (inv.Counts[kind] > 0 || kind is NodeKind.Form or NodeKind.Method) CardOf(inv.Counts[kind].ToString("N0"), label, Look.KindBrush(kind));
        CardOf(inv.Links.ToString("N0"), inv.LinksByName > 0 ? $"links ({inv.LinksByName} by name)" : "links", Look.Text);
        CardOf(_map.Findings.Count(f => f.Kind == Core.Findings.FindingKind.Rule).ToString("N0"), "rules found", Look.Good);
        CardOf(_map.Findings.Count(f => f.Kind == Core.Findings.FindingKind.DeadCode).ToString("N0"), "dead items", Look.Dim);
        CardOf(_map.Findings.Count(f => f.Kind == Core.Findings.FindingKind.Risk && f.Severity == Core.Findings.Severity.High).ToString("N0"), "high risks", Look.Error);
        InventoryPanel.Children.Add(cards);

        InventoryPanel.Children.Add(Section("By language"));
        InventoryPanel.Children.Add(Table(["Language", "Files", "Code lines", "Comments", "All lines"],
            inv.Languages.Select(l => new[] { l.Language, l.Files.ToString("N0"), l.Code.ToString("N0"), l.Comments.ToString("N0"), l.Lines.ToString("N0") })));
        InventoryPanel.Children.Add(Section("Projects"));
        InventoryPanel.Children.Add(Table(["Project", "Language", "Files", "Screens", "Types", "Methods", "Code lines"],
            inv.Projects.Select(p => new[] { p.Name, p.Language ?? "folder", p.Files.ToString("N0"), p.Screens.ToString("N0"), p.Types.ToString("N0"), p.Methods.ToString("N0"), p.Code.ToString("N0") })));
        if (inv.MostComplex.Count > 0)
        {
            InventoryPanel.Children.Add(Section("Most complex methods"));
            var list = new StackPanel { Spacing = 2 };
            foreach (var m in inv.MostComplex)
            {
                var b = new Button { Background = Brushes.Transparent, Padding = new Thickness(4, 2), HorizontalAlignment = HorizontalAlignment.Left };
                var t = new TextBlock { FontSize = 13 };
                t.Inlines!.Add(new Run($"{m.Complexity,3}  ") { Foreground = m.Complexity >= 10 ? Look.Warning : Look.Accent, FontFamily = Look.Mono });
                t.Inlines.Add(new Run($"{m.Type}.{m.Name}") { Foreground = Look.Link });
                t.Inlines.Add(new Run($"   {m.Lines} lines · {m.File}:{m.Line}") { Foreground = Look.Dim, FontSize = 11.5 });
                b.Content = t;
                var id = m.Id;
                b.Click += (_, _) => { if (_map[id] is { } n) ShowNode(n); };
                list.Children.Add(b);
            }
            InventoryPanel.Children.Add(list);
        }
        AddEstimate();
        if (_map.Notes.Count > 0)
        {
            InventoryPanel.Children.Add(Section("Couldn't read"));
            foreach (var n in _map.Notes.Take(50))
                InventoryPanel.Children.Add(new TextBlock { Text = $"{n.File}{(n.Line > 0 ? ":" + n.Line : "")}  {n.Message}", Foreground = Look.Warning, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Look.Text };

    private static TextBlock Section(string text) => new() { Text = text.ToUpperInvariant(), FontSize = 11, Foreground = Look.Heading, Margin = new Thickness(0, 14, 0, 4) };

    private static Control Table(string[] headings, IEnumerable<string[]> rows)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        for (var c = 0; c < headings.Length; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var r = 0;
        void Row(string[] cells, bool head)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < cells.Length; c++)
            {
                var t = new TextBlock
                {
                    Text = cells[c], FontSize = head ? 11.5 : 13, Foreground = head ? Look.Heading : (c == 0 ? Look.Text : Look.Code),
                    Margin = new Thickness(0, 2, 28, 2), HorizontalAlignment = c == 0 || head ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                };
                Grid.SetRow(t, r);
                Grid.SetColumn(t, c);
                grid.Children.Add(t);
            }
            r++;
        }
        Row(headings, true);
        foreach (var row in rows) Row(row, false);
        return grid;
    }

    // ----- The selected item -----

    /// <summary>Shows a node on the Item tab: its place, properties, source and links; selects it in the tree.</summary>
    public void ShowNode(Node node, bool selectInTree = true) => ShowNode(node, selectInTree, tab: true);

    /// <summary>Shows a node; <paramref name="tab"/> false leaves the tab as it is (the Graph tab moving on).</summary>
    public void ShowNode(Node node, bool selectInTree, bool tab)
    {
        if (_map == null) return;
        _current = node;
        ItemTitle.Inlines!.Clear();
        ItemTitle.Inlines.Add(new Run(Look.Glyph(node.Kind) + "  ") { Foreground = Look.KindBrush(node.Kind) });
        ItemTitle.Inlines.Add(new Run(node.Kind == NodeKind.Method && node.Prop("type") is { } t ? $"{t}.{node.Name}" : node.Name));
        var kind = node.Prop("subkind") is { } sk && node.Kind != NodeKind.Project ? sk : node.Kind.ToString().ToLowerInvariant();
        ItemWhere.Text = string.Join("  ·  ", new[]
        {
            kind + (node.Language != null ? $" ({node.Language})" : ""),
            node.File != null ? (node.Line > 0 ? $"{node.File}:{node.Line}" + (node.EndLine > node.Line ? $"–{node.EndLine}" : "") : node.File) : null,
            string.Join(" › ", _map.PathTo(node.Id).SkipLast(1).Select(p => p.Name)) is { Length: > 0 } path ? "in " + path : null,
        }.Where(s => s != null));
        ItemProps.Text = string.Join("   ", node.Props
            .Where(p => p.Key is not ("subkind" or "project" or "files" or "key" or "fullName" or "sql" or "schema" or "table" or "form" or "dead") && !(p.Key == "type" && node.Kind == NodeKind.Method))
            .Select(p => $"{p.Key}: {p.Value}"));
        ItemProps.IsVisible = ItemProps.Text.Length > 0;

        var uses = _map.From(node.Id).Where(e => e.Kind != EdgeKind.Contains).Select(e => _map[e.To] is { } o ? new Relation(e, o, true) : null).OfType<Relation>().OrderBy(r => r.Edge.Line).ToList();
        var usedBy = _map.To(node.Id).Where(e => e.Kind != EdgeKind.Contains).Select(e => _map[e.From] is { } o ? new Relation(e, o, false) : null).OfType<Relation>().OrderBy(r => r.Other.Name, StringComparer.OrdinalIgnoreCase).ToList();
        UsesList.ItemsSource = uses;
        UsedByList.ItemsSource = usedBy;
        UsesTitle.Text = $"USES ({uses.Count})";
        UsedByTitle.Text = $"USED BY ({usedBy.Count})";
        ShowSource(node);
        if (tab) Tabs.SelectedIndex = 1;
        if (selectInTree) SelectInTree(node);
        Say(node.Id);
    }

    private void ClearItem()
    {
        ItemTitle.Inlines!.Clear();
        ItemTitle.Inlines.Add(new Run("Nothing selected"));
        ItemWhere.Text = "Pick something in the tree, or find it by name.";
        ItemProps.Text = "";
        UsesList.ItemsSource = null;
        UsedByList.ItemsSource = null;
        UsesTitle.Text = "USES";
        UsedByTitle.Text = "USED BY";
        Source.Inlines!.Clear();
    }

    private string[]? _sourceLines;
    private string? _sourceFile;

    /// <summary>The node's file with line numbers, its own lines highlighted and scrolled to.</summary>
    private void ShowSource(Node node)
    {
        if (node.File == null && node.Prop("sql") is { } moduleText)
        {
            // From a live database: its text is on the node.
            _sourceFile = null;
            _sourceLines = moduleText.Replace("\r\n", "\n").Split('\n');
            HighlightSource(0, 0);
            return;
        }
        if (_map == null || node.File == null || node.Kind == NodeKind.Project)
        {
            _sourceFile = null;
            Source.Inlines!.Clear();
            Source.Inlines.Add(new Run(node.Kind == NodeKind.Project ? "Pick a file, screen or method to see its code." : "No source for this item.") { Foreground = Look.Dim });
            return;
        }
        var path = Path.Combine(_map.Root, node.File);
        if (_sourceFile != node.File)
        {
            try
            {
                _sourceLines = SourceFiles.ReadText(path).Replace("\r\n", "\n").Split('\n');
                _sourceFile = node.File;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _sourceFile = null;
                Source.Inlines!.Clear();
                Source.Inlines.Add(new Run($"{node.File} isn't readable here ({e.Message}).") { Foreground = Look.Dim });
                return;
            }
        }
        var (from, to) = node.Kind == NodeKind.File ? (0, 0) : (node.Line, node.EndLine > 0 ? node.EndLine : node.Line);
        HighlightSource(from, to);
    }

    /// <summary>Redraws the source with lines <paramref name="from"/> to <paramref name="to"/> highlighted, and scrolls to them.</summary>
    public void HighlightSource(int from, int to)
    {
        if (_sourceLines == null) return;
        var lines = _sourceLines;
        var width = lines.Length.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        var inlines = new InlineCollection();
        for (var i = 0; i < lines.Length; i++)
        {
            var n = i + 1;
            var on = n >= from && n <= to;
            inlines.Add(new Run(n.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width) + "  ") { Foreground = on ? Look.Accent : Look.Gutter });
            inlines.Add(new Run(lines[i].TrimEnd('\r') + (i < lines.Length - 1 ? "\n" : "")) { Background = on ? Look.Highlight : null });
        }
        Source.Inlines = inlines;
        if (from > 0)
            Dispatcher.UIThread.Post(() =>
            {
                var lineHeight = Source.Bounds.Height > 0 && lines.Length > 0 ? Source.Bounds.Height / lines.Length : Source.FontSize * 1.3;
                SourceScroll.Offset = new Vector(0, Math.Max(0, (from - 6) * lineHeight));
            }, DispatcherPriority.Background);
    }

    // ----- Asking -----

    private async Task<string?> AskForFolder()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Scan which folder of legacy code?" });
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> AskForLens()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a saved scan",
            FileTypeFilter = [new FilePickerFileType("Legacy Lens scans") { Patterns = ["*.lens"] }],
        });
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> AskWhereToSave(string title, string suggested)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggested });
        return file?.TryGetLocalPath();
    }

    public void Say(string text) => txtStatusBar.Text = text;

    private static readonly string[] TabNames = ["inventory", "item", "trace", "rules", "dead code", "problems", "graph"];

    /// <summary>What the window shows, for agents (get_view, checkpoints).</summary>
    internal ViewState CurrentView => new(
        _map == null ? null : _lensPath ?? _map.Root,
        Tabs.SelectedIndex is 1 or 2 or 6 ? _current?.Id : null,
        Tabs.SelectedIndex == 2 && TraceBackwardButton.IsChecked == true ? "trace backward" : TabNames[Math.Clamp(Tabs.SelectedIndex, 0, TabNames.Length - 1)],
        0);

    /// <summary>Shows what an agent asked for (show_in_view, restore_checkpoint): an item, a trace or a tab.</summary>
    internal void ShowView(ViewState view)
    {
        var node = view.Node != null ? _map?[view.Node] : null;
        switch (view.Tab)
        {
            case "item" when node != null:
                ShowNode(node);
                if (view.Line > 0) HighlightSource(view.Line, view.Line);
                break;
            case "graph" when node != null:
                ShowNode(node);
                ShowGraph(node);
                break;
            case "trace" or "trace backward" when node != null:
                ShowNode(node);
                ShowTrace(node, view.Tab == "trace");
                break;
            default:
                var i = Array.IndexOf(TabNames, view.Tab);
                Tabs.SelectedIndex = i < 0 ? 0 : i;
                if (i >= 3) FillFindings();
                break;
        }
    }

    /// <summary>An ICommand for key bindings.</summary>
    private sealed class Command(Action run) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }
}
