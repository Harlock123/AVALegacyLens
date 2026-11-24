using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AVALegacyLens;

/// <summary>Asks for a SQL Server connection string: the schema is read from its catalog, never its rows.</summary>
public sealed class ConnectionDialog : Window
{
    public TextBox Box { get; } = new()
    {
        Watermark = "Server=myserver;Database=Clinic;Integrated Security=True;TrustServerCertificate=True",
        AcceptsReturn = false, MinWidth = 560, FontFamily = Look.Mono, FontSize = 12,
    };

    public ConnectionDialog()
    {
        Title = "Add a Database";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Window;
        var ok = new Button { Content = "Read Its Schema", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => Close(Box.Text?.Trim());
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "SQL Server connection string", Foreground = Look.Text, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(Box);
        panel.Children.Add(new TextBlock
        {
            Text = "Legacy Lens reads the database's catalog (tables, columns, keys, and the text of procedures, views, functions and triggers). It never reads a table's rows, and the connection string isn't saved.",
            TextWrapping = TextWrapping.Wrap, Foreground = Look.Dim, FontSize = 12, MaxWidth = 600,
        });
        panel.Children.Add(buttons);
        Content = panel;
        Opened += (_, _) => Box.Focus();
    }

    public static Task<string?> Ask(Window owner) => new ConnectionDialog().ShowDialog<string?>(owner);
}
