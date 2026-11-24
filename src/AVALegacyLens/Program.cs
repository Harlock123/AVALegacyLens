using Avalonia;
using AVALegacyLens.Mcp;

namespace AVALegacyLens;

internal static class Program
{
    /// <summary>
    /// Starts the window, on a folder to scan or a .lens file to open if one is given. With <c>--mcp</c> it opens no
    /// window: it serves the Model Context Protocol for AI agents instead (see <see cref="McpCommandLine"/>).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (McpCommandLine.Wanted(args)) return Agents.AgentMain.Run(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
