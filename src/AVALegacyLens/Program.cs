using Avalonia;

namespace AVALegacyLens;

internal static class Program
{
    /// <summary>Starts the window, on a folder to scan or a .lens file to open if one is given.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
