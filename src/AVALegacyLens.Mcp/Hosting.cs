using System.Diagnostics;
using AVALegacyLens.Core.Model;

namespace AVALegacyLens.Mcp;

/// <summary>
/// What the window shows: the scan open in it (its folder or .lens file), the item selected (a node id), the tab on
/// show and the line highlighted. Agents read the same map the person sees; this is only where the person is looking.
/// </summary>
public sealed record ViewState(string? Scan, string? Node, string Tab, int Line)
{
    public static readonly ViewState Empty = new(null, null, "inventory", 0);
}

/// <summary>What a picture of the view shows: a title, a line under it, and lines of code or of a trace, some highlighted.</summary>
public sealed record ViewPicture(string Title, string Subtitle, IReadOnlyList<string> Lines, int FirstLine, int HighlightFrom, int HighlightTo, bool Numbered, IReadOnlyList<string> Notes);

/// <summary>What the server needs from the program that it can't do itself: drawing the view, which needs Avalonia.</summary>
public interface IViewPainter
{
    /// <summary>The view drawn as a PNG, at <paramref name="scale"/>.</summary>
    byte[] Picture(ViewPicture view, double scale);
}

/// <summary>The window (or the headless server's stand-in for one) as an agent works with it. Used on the host's thread only.</summary>
public interface IExplorerSession
{
    /// <summary>What the window shows now.</summary>
    ViewState View { get; }

    /// <summary>The system map on show (null until something is scanned or opened). Agents read the same map the person sees.</summary>
    SystemMap? Map { get; set; }

    /// <summary>
    /// The live databases the person added to the scan (File › Add Database…, or --database). Agents can scan with them
    /// but never see them: a connection string isn't the agent's to read.
    /// </summary>
    IReadOnlyList<string> Databases { get; }

    /// <summary>Selects in the window (headless: just remembers it).</summary>
    void Show(ViewState view);
}

/// <summary>
/// Where the MCP tools' work runs. The handlers (<see cref="LensTools"/>) touch the window only through
/// <see cref="RunAsync{T}"/>, so the same handlers serve the headless server and the open window.
/// </summary>
public interface IExplorerHost
{
    /// <summary>The working folder: scans are of it (or a folder in it), and new files are written only inside it.</summary>
    string Root { get; }

    IViewPainter Painter { get; }

    /// <summary>Working in the open window, with the person watching.</summary>
    bool Live { get; }

    /// <summary>Runs <paramref name="work"/> on the thread that owns the window, one call at a time.</summary>
    Task<T> RunAsync<T>(string action, Func<IExplorerSession, Task<T>> work, CancellationToken cancel = default);
}

public static class ExplorerHostExtensions
{
    public static Task<T> RunAsync<T>(this IExplorerHost host, string action, Func<IExplorerSession, T> work, CancellationToken cancel = default) =>
        host.RunAsync(action, s => Task.FromResult(work(s)), cancel);
}

/// <summary>Runs work on a UI thread (the program's Avalonia dispatcher, or a test's).</summary>
public interface IUiThread
{
    Task<T> InvokeAsync<T>(Func<Task<T>> work);
}

/// <summary>The program as the host sees it: the session, whether it can take a call now, and the status bar.</summary>
public interface IExplorerDesk
{
    IExplorerSession Session { get; }

    /// <summary>Why the program can't take a call just now ("a dialog box is open"), or null. The call waits.</summary>
    string? Busy { get; }

    /// <summary>Tells the person what an agent just did.</summary>
    void Report(string text);
}

/// <summary>
/// The host: calls run against the desk's session on its UI thread, one at a time. While the person is busy (a dialog
/// open) a call waits up to <see cref="BusyWait"/>.
/// </summary>
public sealed class ExplorerHost(IUiThread ui, IExplorerDesk desk, IViewPainter painter, Func<string> root) : IExplorerHost
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ExplorerHost(IUiThread ui, IExplorerDesk desk, IViewPainter painter, string root)
        : this(ui, desk, painter, () => root) { }

    public string Root => Path.GetFullPath(root());

    public IViewPainter Painter { get; } = painter;

    public bool Live { get; init; }

    public TimeSpan BusyWait { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan BusyPoll { get; init; } = TimeSpan.FromMilliseconds(200);

    public async Task<T> RunAsync<T>(string action, Func<IExplorerSession, Task<T>> work, CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var (ran, result, busy) = await ui.InvokeAsync(async () =>
                {
                    if (desk.Busy is { } why) return (false, default(T), why);
                    try
                    {
                        var r = await work(desk.Session);
                        desk.Report($"AI agent: {action}.");
                        return (true, r, (string?)null);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        desk.Report($"AI agent: {action} failed: {e.Message}");
                        throw;
                    }
                }).ConfigureAwait(false);
                if (ran) return result!;
                if (clock.Elapsed >= BusyWait)
                    throw new ExplorerBusyException($"AVA Legacy Lens is busy: {busy}. The call waited {BusyWait.TotalSeconds:0} seconds; ask the person to finish, then try again.");
                await Task.Delay(BusyPoll, cancel).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class ExplorerBusyException(string message) : Exception(message);

/// <summary>A session with no window round it, as <c>AVALegacyLens --mcp</c> serves it.</summary>
public sealed class HeadlessSession(IReadOnlyList<string>? databases = null) : IExplorerSession
{
    public ViewState View { get; private set; } = ViewState.Empty;

    public SystemMap? Map { get; set; }

    public IReadOnlyList<string> Databases { get; } = databases ?? [];

    public void Show(ViewState view) => View = view;
}

public sealed class HeadlessDesk(TextWriter? log = null, IReadOnlyList<string>? databases = null) : IExplorerDesk
{
    public IExplorerSession Session { get; } = new HeadlessSession(databases);

    public string? Busy => null;

    public void Report(string text) => log?.WriteLine(text);
}
