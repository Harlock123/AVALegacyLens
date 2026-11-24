using Avalonia.Media;
using AVALegacyLens.Core.Model;

namespace AVALegacyLens;

/// <summary>The colours and fonts the window is drawn in: dark, as the other AVA tools, with an amber accent for old code.</summary>
internal static class Look
{
    public static readonly IBrush Window = Brush("#14161B");
    public static readonly IBrush Bar = Brush("#1B1E25");
    public static readonly IBrush Card = Brush("#1F232B");
    public static readonly IBrush Border = Brush("#2D323D");
    public static readonly IBrush Heading = Brush("#8C93A1");
    public static readonly IBrush Text = Brush("#DDE1E8");
    public static readonly IBrush Dim = Brush("#7D8594");
    public static readonly IBrush Accent = Brush("#F2B866");
    public static readonly IBrush Link = Brush("#7FC8F2");
    public static readonly IBrush Code = Brush("#C9D1DC");
    public static readonly IBrush Gutter = Brush("#5B6372");
    public static readonly IBrush Highlight = Brush("#3B3420");
    public static readonly IBrush Error = Brush("#FF7A7A");
    public static readonly IBrush Warning = Brush("#F2B66D");
    public static readonly IBrush Good = Brush("#8FD19E");
    public static readonly IBrush Sheet = Brush("#101217");

    /// <summary>A fixed-width face, with fallbacks on every platform.</summary>
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, Liberation Mono, Courier New, monospace");

    public const double FontSize = 13;

    /// <summary>A short mark for each kind of node, shown before its name in the tree and lists.</summary>
    public static string Glyph(NodeKind kind) => kind switch
    {
        NodeKind.Project => "▣",
        NodeKind.File => "▤",
        NodeKind.Form => "▭",
        NodeKind.Control => "▪",
        NodeKind.Type => "◆",
        NodeKind.Method => "ƒ",
        NodeKind.Procedure => "⚙",
        NodeKind.View => "◫",
        NodeKind.Function => "ƒx",
        NodeKind.Trigger => "⚡",
        NodeKind.Table => "▦",
        NodeKind.Column => "│",
        NodeKind.Setting => "≡",
        NodeKind.Connection => "⇄",
        NodeKind.External => "⇗",
        _ => "•",
    };

    public static IBrush KindBrush(NodeKind kind) => kind switch
    {
        NodeKind.Form or NodeKind.Control => Brush("#F2B866"),
        NodeKind.Type => Brush("#7FC8F2"),
        NodeKind.Method => Brush("#C9A7F2"),
        NodeKind.Procedure or NodeKind.Function or NodeKind.Trigger or NodeKind.View => Brush("#8FD19E"),
        NodeKind.Table or NodeKind.Column => Brush("#7FD1C7"),
        NodeKind.Setting or NodeKind.Connection or NodeKind.External => Brush("#F29A8B"),
        _ => Heading,
    };

    private static SolidColorBrush Brush(string colour) => new(Color.Parse(colour));
}
