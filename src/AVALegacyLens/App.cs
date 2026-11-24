using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AVALegacyLens;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // AVALegacyLens [folder | file.lens]: scans the folder, or opens the saved scan.
            var args = desktop.Args ?? [];
            desktop.MainWindow = new MainWindow(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
