using Avalonia.Threading;
using AVALegacyLens.Core.Model;
using AVALegacyLens.Mcp;

namespace AVALegacyLens.Agents;

/// <summary>The window as an agent sees it: what it shows, the scan open in it, and showing something in it.</summary>
internal sealed class WindowSession(MainWindow window) : IExplorerSession
{
    public ViewState View => window.CurrentView;

    public SystemMap? Map
    {
        get => window.Map;
        set
        {
            if (value != null && !ReferenceEquals(value, window.Map)) window.SetMap(value);
        }
    }

    public IReadOnlyList<string> Databases => window.Databases;

    public void Show(ViewState view) => window.ShowView(view);
}

/// <summary>
/// AI agents in the open window: the session and host the chat and outside agents share, the door on this computer
/// (a pipe, for <c>AVALegacyLens --mcp --attach</c>) and the network door (MCP over HTTP with a token).
/// </summary>
internal sealed class LiveAgents(MainWindow window) : IExplorerDesk, IUiThread, IAsyncDisposable
{
    private readonly WindowSession _session = new(window);
    private LiveListener? _listener;
    private NetworkListener? _network;

    public ViewPainter Painter { get; } = new(window.AgentStage);

    public bool Running => _listener != null;

    public TimeSpan BusyWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How many outside agents are connected (pipe and network together), when it changes.</summary>
    public event Action<int>? ConnectionsChanged;

    public IExplorerSession Session => _session;

    public string? Busy => window.OwnedWindows.Count > 0 || ChatLook.ModalsOpen > 0 ? "a dialog box is open in AVA Legacy Lens" : null;

    public void Report(string text) => window.Say(text);

    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);

    /// <summary>The host outside agents work through, in <paramref name="root"/>.</summary>
    public ExplorerHost Host(string root) => new(this, this, Painter, root) { BusyWait = BusyWait, Live = true };

    /// <summary>The host the AI Chat runs its in-process MCP server on; its status lines name the chat's model.</summary>
    public ExplorerHost ChatHost(Func<string> root, Func<string> who) => new(this, new ChatDesk(this, who), Painter, root) { BusyWait = BusyWait, Live = true };

    private sealed class ChatDesk(LiveAgents agents, Func<string> who) : IExplorerDesk
    {
        public IExplorerSession Session => agents.Session;

        public string? Busy => agents.Busy;

        public void Report(string text) =>
            agents.Report(text.StartsWith("AI agent: ", StringComparison.Ordinal) ? who() + ": " + text["AI agent: ".Length..] : text);
    }

    /// <summary>Opens the door on a pipe; returns why it couldn't (another AVA Legacy Lens has it), or null.</summary>
    public string? Start(string pipeName, Func<string> root)
    {
        if (_listener != null) return null;
        var listener = new LiveListener(pipeName, asked => Host(Directory.Exists(asked) ? asked : root()));
        listener.ConnectionsChanged += n => Dispatcher.UIThread.Post(() => ConnectionsChanged?.Invoke(n));
        try
        {
            listener.Start();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = listener.DisposeAsync();
            return e.Message;
        }
        _listener = listener;
        return null;
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        _listener = null;
        if (listener == null) return;
        listener.Stop();
        await listener.DisposeAsync();
    }

    public bool NetworkRunning => _network != null;

    public NetworkSettings? NetworkSettings { get; private set; }

    public string? NetworkRoot { get; private set; }

    public IReadOnlyList<string> NetworkUrls => _network?.Urls ?? [];

    public async Task<string?> StartNetworkAsync(NetworkSettings settings, string root)
    {
        if (_network != null) return null;
        if (!Directory.Exists(root)) return $"the working folder {root} doesn't exist.";
        var listener = new NetworkListener(settings, LensServer.Name, o => LensServer.Configure(o, Host(root)));
        listener.ConnectionsChanged += n => Dispatcher.UIThread.Post(() => ConnectionsChanged?.Invoke(n));
        try
        {
            await listener.StartAsync();
        }
        catch (IOException e)
        {
            return e.Message;
        }
        _network = listener;
        NetworkSettings = settings;
        NetworkRoot = root;
        return null;
    }

    public async Task StopNetworkAsync()
    {
        var listener = _network;
        _network = null;
        NetworkSettings = null;
        NetworkRoot = null;
        if (listener != null) await listener.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await StopNetworkAsync();
    }
}
