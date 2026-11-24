using Avalonia;
using Avalonia.Controls;
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
        // Denser trees and lists than Fluent's defaults: a legacy system has thousands of items.
        Styles.Add(new Style(x => x.OfType<TreeViewItem>()) { Setters = { new Setter(TreeViewItem.MinHeightProperty, 26.0) } });
        Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(10, 4)), new Setter(ListBoxItem.MinHeightProperty, 26.0) } });
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
