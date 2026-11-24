using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AVALegacyLens.Core;
using AVALegacyLens.Core.Model;

namespace AVALegacyLens;

/// <summary>
/// Draws a <see cref="Neighbourhood"/>: columns of items, what drives the centre on the left and what it drives on the
/// right, joined by curves coloured by kind (dashed when only matched by name, dotted when guessed). Clicking an item
/// raises <see cref="Picked"/>; a double-click <see cref="Opened"/>.
/// </summary>
public sealed class GraphView : Control
{
    public const double BoxWidth = 200, BoxHeight = 30, ColumnGap = 70, RowGap = 12, Margin = 24;

    private Neighbourhood? _graph;
    private readonly Dictionary<Placed, Rect> _boxes = [];

    public event Action<Node>? Picked;
    public event Action<Node>? Opened;

    public Neighbourhood? Graph
    {
        get => _graph;
        set
        {
            _graph = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private (int Min, int Max, int Rows) Extent()
    {
        if (_graph == null) return (0, 0, 1);
        var cols = _graph.Items.Select(i => i.Column).ToList();
        var rows = _graph.Items.GroupBy(i => i.Column).Max(g => g.Count() + (_graph.More.ContainsKey(g.Key) ? 1 : 0));
        return (cols.Min(), cols.Max(), rows);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (min, max, rows) = Extent();
        return new Size(Margin * 2 + (max - min + 1) * BoxWidth + (max - min) * ColumnGap, Margin * 2 + 24 + rows * (BoxHeight + RowGap));
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Look.Sheet, new Rect(Bounds.Size));
        _boxes.Clear();
        if (_graph == null) return;
        var (min, _, rows) = Extent();
        var typeface = new Typeface(FontFamily.Default);
        double X(int column) => Margin + (column - min) * (BoxWidth + ColumnGap);
        foreach (var col in _graph.Items.GroupBy(i => i.Column))
        {
            var count = col.Count();
            var top = Margin + 24 + (rows - count) * (BoxHeight + RowGap) / 2.0;
            foreach (var p in col) _boxes[p] = new Rect(X(p.Column), top + p.Row * (BoxHeight + RowGap), BoxWidth, BoxHeight);
            if (_graph.More.TryGetValue(col.Key, out var extra))
                Text(context, $"and {extra} more…", typeface, 11, Look.Dim, new Point(X(col.Key) + 8, top + count * (BoxHeight + RowGap) + 6));
        }
        // Column headings.
        foreach (var c in _graph.Items.Select(i => i.Column).Distinct())
            Text(context, c switch { < 0 => c == -1 ? "DRIVEN BY" : "AND BY", 0 => "", _ => c == 1 ? "DRIVES" : "AND THEN" }, typeface, 10.5, Look.Heading, new Point(X(c) + 4, Margin));

        foreach (var l in _graph.Links)
        {
            var a = _boxes[l.From];
            var b = _boxes[l.To];
            var start = new Point(a.Right, a.Center.Y);
            var end = new Point(b.Left, b.Center.Y);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(start, false);
                var dx = (end.X - start.X) / 2;
                g.CubicBezierTo(new Point(start.X + dx, start.Y), new Point(end.X - dx, end.Y), end);
            }
            var pen = new Pen(EdgeBrush(l.Kind), l.Kind == EdgeKind.Writes ? 2 : 1.3, l.Certainty switch
            {
                Certainty.ByName => new DashStyle([4, 3], 0),
                Certainty.Guessed => new DashStyle([1, 3], 0),
                _ => null,
            });
            context.DrawGeometry(null, pen, geometry);
            context.DrawEllipse(EdgeBrush(l.Kind), null, end, 2.5, 2.5);
        }

        foreach (var (p, r) in _boxes)
        {
            var centre = p == _graph.Centre;
            var kind = Look.KindBrush(p.Node.Kind);
            context.DrawRectangle(centre ? Look.Highlight : Look.Card, new Pen(centre ? Look.Accent : Look.Border, centre ? 2 : 1), r, 5, 5);
            context.FillRectangle(kind, new Rect(r.X, r.Y + 4, 3, r.Height - 8));
            var label = p.Node.Kind switch
            {
                NodeKind.Method => $"{p.Node.Prop("type")}.{p.Node.Name}",
                NodeKind.Control => $"{p.Node.Prop("form")}.{p.Node.Name}",
                _ => p.Node.Name,
            };
            var text = new FormattedText($"{Look.Glyph(p.Node.Kind)}  {label}", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, p.Node.Prop("dead") == "true" ? Look.Dim : Look.Text)
            {
                MaxTextWidth = r.Width - 16,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            context.DrawText(text, new Point(r.X + 10, r.Y + (r.Height - text.Height) / 2));
        }
    }

    private static void Text(DrawingContext context, string s, Typeface typeface, double size, IBrush brush, Point at) =>
        context.DrawText(new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush), at);

    public static IBrush EdgeBrush(EdgeKind kind) => kind switch
    {
        EdgeKind.Writes => Look.Error,
        EdgeKind.Reads => Look.KindBrush(NodeKind.Table),
        EdgeKind.Executes => Look.KindBrush(NodeKind.Procedure),
        EdgeKind.Handles or EdgeKind.Contains => Look.Accent,
        EdgeKind.Creates => Look.Link,
        EdgeKind.Uses => Look.KindBrush(NodeKind.Setting),
        _ => Look.KindBrush(NodeKind.Method),
    };

    /// <summary>The item at a point, if any.</summary>
    public Node? At(Point p) => _boxes.FirstOrDefault(b => b.Value.Contains(p)).Key?.Node;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (At(e.GetPosition(this)) is not { } node) return;
        if (e.ClickCount >= 2) Opened?.Invoke(node);
        else Picked?.Invoke(node);
    }
}
