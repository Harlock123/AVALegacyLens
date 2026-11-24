using System.Globalization;
using System.Net;
using System.Text;
using AVALegacyLens.Core.Findings;
using AVALegacyLens.Core.Model;

namespace AVALegacyLens.Core.Handoff;

/// <summary>
/// A first draft of an old screen as an Avalonia .axaml window, for AVAUIDesigner to open and carry on with: the same
/// controls at the same places and sizes (on a Canvas, as WinForms and VB6 placed them), each control mapped to its
/// Avalonia counterpart (a TextBox keeps its MaxLength, a ComboBox its items, a frame or panel becomes a bordered
/// canvas holding its controls), and comments saying which old handler each event ran and the rules it checked. Events
/// aren't wired: the drafts carry no code, only the layout and what to rebuild behind it.
/// </summary>
public static class AxamlDraft
{
    public const string Extension = ".axaml";

    public static string Make(SystemMap map, string formId)
    {
        var form = map[formId] ?? throw new KeyNotFoundException($"There's no screen with the id {formId}.");
        if (form.Kind != NodeKind.Form) throw new ArgumentException($"{form.Name} isn't a screen.");
        var (w, h) = Pair(form.Prop("ClientSize")) ?? (640, 480);
        var sb = new StringBuilder();
        var isControl = form.Prop("subkind") == "user control";
        var root = isControl ? "UserControl" : "Window";
        sb.AppendLine(CultureInfo.InvariantCulture, $"<!-- Drafted by AVA Legacy Lens from {form.Name} ({form.Language} {form.Prop("subkind") ?? "form"}), {form.File}.");
        sb.AppendLine("     Places and sizes are the original's, in pixels; events are listed where they were handled, to rebuild behind them. -->");
        sb.Append(CultureInfo.InvariantCulture, $"<{root} xmlns=\"https://github.com/avaloniaui\"\n        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n        x:Class=\"Rebuilt.Views.{Name(form.Name)}\"\n");
        if (!isControl) sb.Append(CultureInfo.InvariantCulture, $"        Title=\"{A(form.Prop("title") ?? form.Name)}\"\n");
        sb.Append(CultureInfo.InvariantCulture, $"        Width=\"{w}\" Height=\"{h}\">\n");
        foreach (var e in map.To(form.Id).Where(e => e.Kind == EdgeKind.Handles))
            sb.AppendLine(CultureInfo.InvariantCulture, $"  <!-- {e.Note}: {Handler(map, e)} -->");
        sb.AppendLine("  <Canvas>");
        foreach (var c in Children(map, form.Id)) Write(map, sb, c, 2);
        sb.AppendLine("  </Canvas>");
        sb.AppendLine(CultureInfo.InvariantCulture, $"</{root}>");
        return sb.ToString();
    }

    /// <summary>The direct children of a screen or container, in tab order then place.</summary>
    private static IEnumerable<Node> Children(SystemMap map, string id) =>
        map.Children(id).Where(c => c.Kind == NodeKind.Control && c.Prop("subkind") != "component")
            .OrderBy(c => int.TryParse(c.Prop("TabIndex"), out var t) ? t : int.MaxValue).ThenBy(c => c.Line);

    private static void Write(SystemMap map, StringBuilder sb, Node c, int depth)
    {
        var pad = new string(' ', depth * 2);
        foreach (var e in map.To(c.Id).Where(e => e.Kind == EdgeKind.Handles))
            sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<!-- {c.Name} {e.Note}: {Handler(map, e)} -->");
        var (x, y) = Pair(c.Prop("Location")) ?? (0, 0);
        var size = Pair(c.Prop("Size"));
        // The original's sizes win over Fluent's minimums (a legacy text box is often 21 pixels high).
        var place = $"x:Name=\"{Name(c.Name)}\" Canvas.Left=\"{x}\" Canvas.Top=\"{y}\"" + (size is var (sw, shh) ? $" Width=\"{sw}\" Height=\"{shh}\" MinHeight=\"0\"" : "");
        var text = c.Prop("Text");
        var type = c.Prop("type") ?? "";
        var kids = Children(map, c.Id).ToList();
        switch (type)
        {
            case "TextBox" or "MaskedTextBox" or "RichTextBox":
                sb.Append(CultureInfo.InvariantCulture, $"{pad}<{(type == "MaskedTextBox" ? "MaskedTextBox" : "TextBox")} {place} Padding=\"4,1\" VerticalContentAlignment=\"Center\"");
                if (c.Prop("MaxLength") is { } max && max != "0") sb.Append(CultureInfo.InvariantCulture, $" MaxLength=\"{max}\"");
                if (c.Prop("Multiline") == "True" || type == "RichTextBox") sb.Append(" AcceptsReturn=\"True\" TextWrapping=\"Wrap\"");
                if (c.Prop("ReadOnly") == "True" || c.Prop("Locked") == "True") sb.Append(" IsReadOnly=\"True\"");
                if (c.Prop("PasswordChar") is { Length: > 0 } pc) sb.Append(CultureInfo.InvariantCulture, $" PasswordChar=\"{A(pc.Trim('\''))}\"");
                if (c.Prop("Mask") is { } mask && type == "MaskedTextBox") sb.Append(CultureInfo.InvariantCulture, $" Mask=\"{A(mask)}\"");
                if (text is { Length: > 0 }) sb.Append(CultureInfo.InvariantCulture, $" Text=\"{A(text)}\"");
                sb.AppendLine(" />");
                break;
            case "Label" or "LinkLabel":
                if (c.Prop("array") == "true" && c.Prop("Locations") is { } locations && text?.Contains(" | ", StringComparison.Ordinal) == true)
                {
                    // A VB6 control array: each member where it was, named name_0, name_1….
                    var texts = text.Split(" | ");
                    var places = locations.Split(" | ").Select(p => Pair(p)).ToList();
                    var indexes = (c.Prop("Index") ?? "").Split(", ");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<!-- {c.Name} was a control array: one control each here -->");
                    for (var i = 0; i < texts.Length && i < places.Count; i++)
                    {
                        var (ax, ay) = places[i] ?? (x, y);
                        var idx = i < indexes.Length ? indexes[i] : i.ToString(CultureInfo.InvariantCulture);
                        var sized = size is var (aw, ah) ? $" Width=\"{aw}\" Height=\"{ah}\"" : "";
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<TextBlock x:Name=\"{Name(c.Name)}_{idx}\" Canvas.Left=\"{ax}\" Canvas.Top=\"{ay}\"{sized} Text=\"{A(Plain(texts[i]))}\" />");
                    }
                    break;
                }
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<TextBlock {place} Text=\"{A(Plain(text))}\" />");
                break;
            case "Button" or "CommandButton":
                sb.Append(CultureInfo.InvariantCulture, $"{pad}<Button {place} Padding=\"6,0\" VerticalContentAlignment=\"Center\" HorizontalContentAlignment=\"Center\" Content=\"{A(Access(text))}\"");
                if (c.Prop("Default") == "True") sb.Append(" IsDefault=\"True\"");
                if (c.Prop("Cancel") == "True") sb.Append(" IsCancel=\"True\"");
                sb.AppendLine(" />");
                break;
            case "CheckBox":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<CheckBox {place} Content=\"{A(Access(text))}\"{(c.Prop("Checked") == "True" ? " IsChecked=\"True\"" : "")} />");
                break;
            case "RadioButton" or "OptionButton":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<RadioButton {place} Content=\"{A(Access(text))}\"{(c.Prop("Checked") == "True" ? " IsChecked=\"True\"" : "")} />");
                break;
            case "ComboBox" or "ListBox":
                if (type == "ComboBox") place += " Padding=\"4,0\"";
                var items = (c.Prop("Items") ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries);
                if (items.Length == 0) sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<{type} {place} />");
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<{type} {place}>");
                    foreach (var item in items) sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}  <{type}Item Content=\"{A(item)}\" />");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}</{type}>");
                }
                break;
            case "DateTimePicker" or "DTPicker" or "MonthCalendar":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<DatePicker {place} />");
                break;
            case "DataGridView" or "DataGrid" or "MSFlexGrid" or "MSHFlexGrid":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<DataGrid {place}{(c.Prop("ReadOnly") == "True" ? " IsReadOnly=\"True\"" : "")} />");
                break;
            case "NumericUpDown":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<NumericUpDown {place} />");
                break;
            case "ProgressBar":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<ProgressBar {place} />");
                break;
            case "TrackBar" or "Slider":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<Slider {place} />");
                break;
            case "PictureBox" or "Image":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<Image {place} />");
                break;
            case "TabControl" or "SSTab" or "TabStrip":
                // Each tab page a TabItem, its controls on a canvas of their own.
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<TabControl {place}>");
                foreach (var page in kids)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}  <TabItem x:Name=\"{Name(page.Name)}\" Header=\"{A(Plain(page.Prop("Text") ?? page.Name))}\">");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}    <Canvas>");
                    foreach (var k in Children(map, page.Id)) Write(map, sb, k, depth + 3);
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}    </Canvas>");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}  </TabItem>");
                }
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}</TabControl>");
                return;
            case "GroupBox" or "Frame" or "Panel" or "PictureBox2" or "TabPage" or "SplitContainer" or "FlowLayoutPanel" or "TableLayoutPanel":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<Border {place} BorderBrush=\"Gray\" BorderThickness=\"1\" CornerRadius=\"3\">");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}  <Canvas>");
                if (text is { Length: > 0 } && type is "GroupBox" or "Frame") sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}    <TextBlock Canvas.Left=\"6\" Canvas.Top=\"-2\" Text=\"{A(Plain(text))}\" FontWeight=\"SemiBold\" />");
                foreach (var k in kids) Write(map, sb, k, depth + 2);
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}  </Canvas>");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}</Border>");
                return;
            default:
                // Something with no counterpart (a third-party or ActiveX control): a placeholder saying what it was.
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}<Border {place} BorderBrush=\"Orange\" BorderThickness=\"1\" ToolTip.Tip=\"Was a {A(type)}: choose its replacement\">");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}  <TextBlock Text=\"{A(type)} {A(c.Name)}\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\" />");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{pad}</Border>");
                break;
        }
        foreach (var k in kids) Write(map, sb, k, depth);
    }

    private static string Handler(SystemMap map, Edge e)
    {
        var h = map[e.From];
        if (h == null) return e.From;
        var rules = map.Findings.Count(f => f.Kind == FindingKind.Rule && f.NodeId == h.Id);
        return $"{h.Prop("type")}.{h.Name} ({h.File}:{h.Line})" + (rules > 0 ? $", {rules} rule{(rules == 1 ? "" : "s")} checked" : "");
    }

    private static (int, int)? Pair(string? text)
    {
        var parts = (text ?? "").Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? (a, b) : null;
    }

    /// <summary>A WinForms/VB6 access key (&amp;Save) as Avalonia's (_Save).</summary>
    private static string Access(string? text) => (text ?? "").Replace("&&", "\u0001", StringComparison.Ordinal).Replace("_", "__", StringComparison.Ordinal).Replace('&', '_').Replace("\u0001", "&", StringComparison.Ordinal);

    private static string Plain(string? text) => (text ?? "").Replace("&&", "\u0001", StringComparison.Ordinal).Replace("&", "", StringComparison.Ordinal).Replace("\u0001", "&", StringComparison.Ordinal);

    private static string A(string text) => WebUtility.HtmlEncode(text);

    /// <summary>A name usable as x:Name and a class name.</summary>
    private static string Name(string name)
    {
        var s = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
        return s.Length > 0 && char.IsDigit(s[0]) ? "_" + s : s;
    }
}
