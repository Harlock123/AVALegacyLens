using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AVALegacyLens.Agents;
using AVALegacyLens.Agents.Chat;
using AVALegacyLens.Mcp;

namespace AVALegacyLens;

/// <summary>
/// The window's AI side: the AI menu, the AI Chat docked on the right (F12), the door for agents outside the program
/// (AI › Allow AI Agents: <c>AVALegacyLens --mcp --attach</c>) and the network door (AI › Connect an Agent). Agents work on
/// the scan the window shows; they read code and schema, never data, and passwords are masked in what they get.
/// </summary>
public partial class MainWindow
{
    private LazyToolBridge? _chatTools;
    private string? _networkToken;
    private ToolsHolder? _toolsHolder;

    internal AgentSettings AgentSettings { get; private set; } = new();

    internal LiveAgents Agents { get; private set; } = null!;

    internal ChatPanel? Chat { get; private set; }

    /// <summary>The pipe outside agents connect on (tests give one of their own).</summary>
    public string AgentPipe { get; set; } = LiveProtocol.PipeName;

    /// <summary>Tests' own API key stores (never the person's real keys).</summary>
    internal (ApiKeyStore Keys, ApiKeyStore ServerKeys)? ChatKeys { get; set; }

    /// <summary>The folder the chat and attached agents work in: the one chosen, else the scanned folder, else Documents.</summary>
    internal string AgentRoot =>
        AgentSettings.WorkingFolder is { Length: > 0 } chosen && Directory.Exists(chosen) ? chosen
        : _map is { Root.Length: > 0 } m && Directory.Exists(m.Root) ? m.Root
        : DocumentsFolder;

    private static string DocumentsFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } d && Directory.Exists(d) ? d
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private void SetUpAgents()
    {
        if (Agents != null) return;
        AgentSettings = AgentSettings.Load();
        Agents = new LiveAgents(this);
        Agents.ConnectionsChanged += n => txtStatusBar.Text = n == 0 ? "No AI agents are connected now." : $"{n} AI agent{(n == 1 ? " is" : "s are")} connected.";
        AllowAgentsMenuItem.IsChecked = AgentSettings.AllowAgents;
        Opened += async (_, _) =>
        {
            if (AgentSettings.AllowAgents) Say(SetAllowAgents(true));
            if (AgentSettings.AllowNetworkAgents && await SyncNetworkAsync() is { Length: > 0 } line) Say(line);
            if (AgentSettings.ChatOpen && !ChatHolder.IsVisible) ToggleChat();
        };
        Closed += async (_, _) =>
        {
            AgentSettings.Save();
            if (_chatTools is not null) await _chatTools.DisposeAsync();
            await Agents.DisposeAsync();
        };
    }

    // ---- the AI menu ----

    private void OnToggleChatClick(object? sender, RoutedEventArgs e) => ToggleChat();

    private void OnAllowAgentsClick(object? sender, RoutedEventArgs e) => Say(SetAllowAgents(!AgentSettings.AllowAgents));

    private async void OnWorkingFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "The folder AI agents read and write in", AllowMultiple = false });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        AgentSettings.WorkingFolder = path;
        AgentSettings.Save();
        await RestartChatToolsAsync();
        Say($"AI agents now read and write in {path}.");
    }

    /// <summary>The chat's tools start again in the new working folder (its conversation stays).</summary>
    private async Task RestartChatToolsAsync()
    {
        if (_toolsHolder is null || Chat is null) return;
        var old = _toolsHolder.Bridge;
        _toolsHolder.Bridge = NewChatTools(Chat);
        _chatTools = _toolsHolder.Bridge;
        await old.DisposeAsync();
    }

    /// <summary>Lets agents outside the program in, or shuts them out, and remembers it.</summary>
    public string SetAllowAgents(bool allow)
    {
        if (AgentSettings.AllowAgents != allow)
        {
            AgentSettings.AllowAgents = allow;
            AgentSettings.Save();
        }
        AllowAgentsMenuItem.IsChecked = allow;
        if (!allow)
        {
            _ = Agents.StopAsync();
            return "AI agents are shut out.";
        }
        return Agents.Start(AgentPipe, () => AgentRoot) is { } problem
            ? $"AI agents can't connect: {problem}"
            : "AI agents may connect (AVALegacyLens --mcp --attach). They work on the scan open here and can show you items in this window.";
    }

    public string NetworkToken => _networkToken ??= TryToken(t => t.Get());

    private static string TryToken(Func<NetworkToken, string> get)
    {
        try
        {
            return get(global::AVALegacyLens.Mcp.NetworkToken.In(AgentSettings.Folder));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return NetworkListener.NewToken();
        }
    }

    public async Task<string> SyncNetworkAsync()
    {
        var s = AgentSettings;
        var want = s.AllowNetworkAgents
            ? new NetworkSettings(s.NetworkAgentsAddress, s.NetworkAgentsPort, NetworkToken, Blank(s.NetworkAgentsCertificate), Blank(s.NetworkAgentsKey))
            : null;
        var root = Blank(s.NetworkAgentsFolder) ?? DocumentsFolder;
        if (Agents.NetworkRunning && (want == null || want != Agents.NetworkSettings || root != Agents.NetworkRoot))
        {
            await Agents.StopNetworkAsync();
            if (want == null) return "AI agents on other computers are shut out.";
        }
        if (want == null || Agents.NetworkRunning) return "";
        if (want.Problem() is { } bad) return $"AI agents on other computers can't connect: {bad}";
        return await Agents.StartNetworkAsync(want, root) is { } problem
            ? $"AI agents on other computers can't connect: {problem}"
            : $"AI agents on other computers may connect (MCP): {Agents.NetworkUrls.FirstOrDefault()}, with the access token (AI › Connect an Agent).";
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static TextBlock Words(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>How to connect an agent: on this computer through the pipe, or from another over the network.</summary>
    public async Task ShowConnect()
    {
        var exe = Environment.ProcessPath ?? "AVALegacyLens";
        var local = new SelectableTextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = $"On this computer: turn on AI › Allow AI Agents (it's {(AgentSettings.AllowAgents ? "on" : "off")} now), then add the server to the agent. For Claude Code:\n\n" +
                   $"    claude mcp add avalegacylens -- \"{exe}\" --mcp --attach\n\n" +
                   $"The agent then works on the scan open in this window (code and schema only, passwords masked), writes only new files in {AgentRoot} (AI › Working Folder changes it), and can select items here to show you. Without --attach the server runs with no window and scans its own folder (add --root FOLDER).",
        };
        var allow = new CheckBox { Content = "Let AI agents on other computers connect (MCP over HTTP)", IsChecked = AgentSettings.AllowNetworkAgents };
        var port = new TextBox { Text = AgentSettings.NetworkAgentsPort.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 80 };
        var folder = new TextBox { Text = AgentSettings.NetworkAgentsFolder, Watermark = DocumentsFolder, MinWidth = 300 };
        var token = new TextBox { Text = new string('•', 20), IsReadOnly = true, Width = 300 };
        var show = new Button { Content = "Show" };
        show.Click += (_, _) => token.Text = NetworkToken;
        var copy = new Button { Content = "Copy" };
        copy.Click += async (_, _) => { if (Clipboard is { } c) await c.SetTextAsync(NetworkToken); };
        var renew = new Button { Content = "New token" };
        renew.Click += (_, _) => { _networkToken = TryToken(t => t.Renew()); token.Text = NetworkToken; };
        var where = new SelectableTextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        void ShowWhere() => where.Text = Agents.NetworkRunning
            ? "Listening at " + string.Join(", ", Agents.NetworkUrls) + ". Agents send \"Authorization: Bearer <token>\"."
            : "Not listening.";
        ShowWhere();
        var close = new Button { Content = "Close", MinWidth = 90 };
        var apply = new Button { Content = "Apply", MinWidth = 90 };
        var dialog = new Window { Title = "Connect an Agent", Width = 640, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        apply.Click += async (_, _) =>
        {
            if (!int.TryParse(port.Text, out var p) || p is < 1 or > 65535) { where.Text = "The port is a number from 1 to 65535."; return; }
            AgentSettings.AllowNetworkAgents = allow.IsChecked == true;
            AgentSettings.NetworkAgentsPort = p;
            AgentSettings.NetworkAgentsFolder = folder.Text?.Trim() ?? "";
            AgentSettings.Save();
            var line = await SyncNetworkAsync();
            ShowWhere();
            if (line.Length > 0) { Say(line); if (line.Contains("can't")) where.Text = line; }
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 12,
            Children =
            {
                local,
                new Separator(),
                allow,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Words("Port"), port, Words("Working folder"), folder } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Words("Access token"), token, show, copy, renew } },
                where,
                new TextBlock
                {
                    Opacity = 0.7, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Text = "Plain HTTP sends the token and the work unencrypted: use it on a network you trust, or run AVALegacyLens --mcp --http with --cert and --key for HTTPS. Anyone with the token can scan and read the working folder's code and write new files there.",
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { apply, close } },
            },
        };
        await ChatLook.ShowModal(dialog, this);
    }

    // ---- the AI Chat ----

    /// <summary>Shows or hides the AI Chat (F12), making it the first time.</summary>
    public void ToggleChat()
    {
        if (Chat is null) MakeChat();
        ChatHolder.IsVisible = !ChatHolder.IsVisible;
        ChatMenuItem.IsChecked = ChatHolder.IsVisible;
        if (AgentSettings.ChatOpen != ChatHolder.IsVisible)
        {
            AgentSettings.ChatOpen = ChatHolder.IsVisible;
            AgentSettings.Save();
        }
        if (ChatHolder.IsVisible) Chat!.Input.Focus();
    }

    private LazyToolBridge NewChatTools(ChatPanel panel) =>
        new(async cancel => await McpToolBridge.StartAsync(Agents.ChatHost(() => AgentRoot, () => panel.Session.Provider == ChatProvider.Anthropic ? "Claude" : "AI Chat"), cancel));

    private void MakeChat()
    {
        var folder = AgentSettings.Folder;
        var keys = ChatKeys?.Keys ?? ApiKeyStore.ForApp(folder);
        var serverKeys = ChatKeys?.ServerKeys ?? ApiKeyStore.ForServer(folder);
        var holder = new ToolsHolder();
        ChatPanel? panel = null;
        holder.Bridge = new LazyToolBridge(async cancel => await McpToolBridge.StartAsync(Agents.ChatHost(() => AgentRoot, () => panel?.Session.Provider == ChatProvider.Anthropic ? "Claude" : "AI Chat"), cancel));
        _chatTools = holder.Bridge;
        panel = new ChatPanel(holder, keys, Say, AgentSettings, serverKeys) { SaveSettings = AgentSettings.Save };
        panel.CloseRequested += ToggleChat;
        if (ChatKeys is null)
        {
            panel.History = new ChatHistory(Path.Combine(folder, "Chats"));
            panel.Ledger = new UsageLedger(Path.Combine(folder, "chat-usage.json"));
            panel.RestoreLast();
        }
        Chat = panel;
        _toolsHolder = holder;
        ChatHolder.Child = panel;
    }

    /// <summary>The chat's tools, which can be swapped for a new bridge when the working folder changes.</summary>
    private sealed class ToolsHolder : IToolBridge
    {
        private LazyToolBridge _bridge = null!;
        private Func<string, CancellationToken, Task<bool>>? _ask;

        public LazyToolBridge Bridge
        {
            get => _bridge;
            set
            {
                _bridge = value;
                _bridge.Ask = _ask;
            }
        }

        public Task<IReadOnlyList<ModelContextProtocol.Protocol.Tool>> ListToolsAsync(CancellationToken cancel = default) => Bridge.ListToolsAsync(cancel);

        public Task<ToolOutcome> CallAsync(string name, IReadOnlyDictionary<string, System.Text.Json.JsonElement> input, CancellationToken cancel = default) => Bridge.CallAsync(name, input, cancel);

        public Task<ToolOutcome> CallAsync(string name, IReadOnlyDictionary<string, System.Text.Json.JsonElement> input, IProgress<(double Done, string? Message)>? progress, CancellationToken cancel = default) => Bridge.CallAsync(name, input, progress, cancel);

        public Func<string, CancellationToken, Task<bool>>? Ask
        {
            get => _ask;
            set
            {
                _ask = value;
                _bridge.Ask = value;
            }
        }

        public string? CheckpointFolder { get => Bridge.CheckpointFolder; set => Bridge.CheckpointFolder = value; }
    }
}
