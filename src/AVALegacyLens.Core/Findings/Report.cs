using System.Globalization;
using System.Net;
using System.Text;
using AVALegacyLens.Core.Model;
using ClosedXML.Excel;

namespace AVALegacyLens.Core.Findings;

/// <summary>
/// The scan as a report people keep: an HTML page (one file, no outside links) or an Excel workbook, with the
/// inventory, the estimate, the rules (with people's marks), dead code, risks, the screens and who reads and writes
/// each table. Nothing in it comes from data: only code, schema and what was worked out from them.
/// </summary>
public static class Report
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static void Save(SystemMap map, string path)
    {
        if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) SaveExcel(map, path);
        else File.WriteAllText(path, Html(map), Encoding.UTF8);
    }

    public static string Place(Finding f) => f.File == null ? "" : f.Line > 0 ? $"{f.File}:{f.Line}" : f.File;

    public static string MarkText(SystemMap map, Finding f) => map.MarkOf(f) is { } m ? m.State.ToString().ToLowerInvariant() + (m.Note is { Length: > 0 } n ? ": " + n : "") : "";

    /// <summary>The screens, each control with what handles it.</summary>
    public static IEnumerable<(Node Form, Node Control, string Handlers)> Screens(SystemMap map) =>
        map.OfKind(NodeKind.Form).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).SelectMany(f => Trace.Controls(map, f.Id).Select(c => (f, c,
            string.Join(", ", map.To(c.Id).Where(e => e.Kind == EdgeKind.Handles).Select(e => $"{e.Note} → {map[e.From]?.Name}")))));

    /// <summary>Tables and columns with their writers and readers.</summary>
    public static IEnumerable<(Node Table, Node? Column, string Writers, string Readers)> Tables(SystemMap map)
    {
        string Names(IEnumerable<(Node Node, Edge Edge)> list) => string.Join(", ", list.Select(x => (x.Node.Kind == NodeKind.Method ? $"{x.Node.Prop("type")}.{x.Node.Name}" : x.Node.Name) + (x.Edge.Certainty == Certainty.Guessed ? " (guessed)" : "")));
        foreach (var t in map.OfKind(NodeKind.Table).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            yield return (t, null, Names(Usage.Writers(map, t.Id)), Names(Usage.Readers(map, t.Id)));
            foreach (var c in map.Children(t.Id).Where(c => c.Kind == NodeKind.Column))
                yield return (t, c, Names(Usage.Writers(map, c.Id)), Names(Usage.Readers(map, c.Id)));
        }
    }

    public static string Html(SystemMap map)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var inv = Inventory.Of(map);
        var est = Estimate.Of(map);
        var name = map.Root.Length > 0 ? Path.GetFileName(map.Root) : "database";
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{E(name)}} — Legacy Lens report</title>
            <style>
            :root { --bg:#fff; --fg:#1d2128; --dim:#5d6573; --line:#d9dde3; --head:#f3f5f8; --accent:#a8650d; --high:#b42318; --med:#b54708; --low:#475467; }
            @media (prefers-color-scheme: dark) { :root { --bg:#14161b; --fg:#dde1e8; --dim:#8c93a1; --line:#2d323d; --head:#1b1e25; --accent:#f2b866; --high:#ff8a80; --med:#f2b66d; --low:#a3acb9; } }
            body { background:var(--bg); color:var(--fg); font:14px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; margin:0; padding:24px 16px 64px; }
            main { max-width:1180px; margin:0 auto; }
            h1 { font-size:26px; margin:0 0 4px; } h2 { font-size:18px; margin:36px 0 8px; border-bottom:1px solid var(--line); padding-bottom:4px; }
            .dim { color:var(--dim); } .cards { display:flex; flex-wrap:wrap; gap:10px; margin:16px 0; }
            .card { border:1px solid var(--line); border-radius:8px; padding:10px 14px; min-width:110px; } .card b { display:block; font-size:22px; color:var(--accent); }
            table { border-collapse:collapse; width:100%; font-size:13px; } th, td { text-align:left; vertical-align:top; padding:5px 8px; border-bottom:1px solid var(--line); }
            th { background:var(--head); font-weight:600; } td.n { text-align:right; font-variant-numeric:tabular-nums; } code { font:12px ui-monospace, Menlo, Consolas, monospace; word-break:break-word; }
            .High { color:var(--high); font-weight:600; } .Medium { color:var(--med); } .Low, .Info { color:var(--low); }
            .wrap { overflow-x:auto; }
            </style></head><body><main>
            """);
        sb.Append(Ci, $"<h1>{E(name)}</h1><p class=\"dim\">{E(map.Root)} · scanned {map.ScannedAt:yyyy-MM-dd HH:mm} · AVA Legacy Lens</p>");
        sb.Append("<div class=\"cards\">");
        void Card(string v, string label) => sb.Append($"<div class=\"card\"><b>{E(v)}</b>{E(label)}</div>");
        Card(inv.TotalCode.ToString("N0", Ci), "lines of code");
        Card(inv.Counts[NodeKind.Form].ToString(Ci), "screens");
        Card(inv.Counts[NodeKind.Method].ToString(Ci), "methods");
        Card(inv.Counts[NodeKind.Table].ToString(Ci), "tables");
        Card(map.Findings.Count(f => f.Kind == FindingKind.Rule).ToString(Ci), "rules found");
        Card(map.Findings.Count(f => f.Kind == FindingKind.DeadCode).ToString(Ci), "dead items");
        Card(map.Findings.Count(f => f.Kind == FindingKind.Risk && f.Severity == Severity.High).ToString(Ci), "high risks");
        Card($"{est.Low:0}–{est.High:0}", "person-days to rebuild");
        sb.Append("</div>");

        sb.Append("<h2>Languages and projects</h2><div class=\"wrap\"><table><tr><th>Language</th><th>Files</th><th>Code lines</th><th>Comments</th></tr>");
        foreach (var l in inv.Languages) sb.Append(Ci, $"<tr><td>{E(l.Language)}</td><td class=n>{l.Files}</td><td class=n>{l.Code:N0}</td><td class=n>{l.Comments:N0}</td></tr>");
        sb.Append("</table></div><div class=\"wrap\" style=\"margin-top:12px\"><table><tr><th>Project</th><th>Language</th><th>Files</th><th>Screens</th><th>Types</th><th>Methods</th><th>Code lines</th></tr>");
        foreach (var p in inv.Projects) sb.Append(Ci, $"<tr><td>{E(p.Name)}</td><td>{E(p.Language ?? "folder")}</td><td class=n>{p.Files}</td><td class=n>{p.Screens}</td><td class=n>{p.Types}</td><td class=n>{p.Methods}</td><td class=n>{p.Code:N0}</td></tr>");
        sb.Append("</table></div>");

        sb.Append(Ci, $"<h2>Estimate</h2><p>About <b>{est.Days:0} person-days</b> to rebuild the live part (a range of {est.Low:0} to {est.High:0}); dead code left out. A rough size from counts the scan is sure of, not a quote.</p>");
        sb.Append("<div class=\"wrap\"><table><tr><th>Area</th><th>What</th><th>Count</th><th>Days each</th><th>Days</th></tr>");
        foreach (var l in est.Lines) sb.Append(Ci, $"<tr><td>{E(l.Area)}</td><td>{E(l.What)}</td><td class=n>{l.Count}</td><td class=n>{l.DaysEach:0.##}</td><td class=n>{l.Days:0.##}</td></tr>");
        sb.Append("</table></div>");

        void Findings(string title, FindingKind kind, string intro)
        {
            var list = map.Findings.Where(f => f.Kind == kind).OrderByDescending(f => f.Severity).ThenBy(f => f.Category, StringComparer.Ordinal).ThenBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line).ToList();
            sb.Append(Ci, $"<h2>{E(title)} ({list.Count})</h2><p class=\"dim\">{E(intro)}</p><div class=\"wrap\"><table><tr><th>{(kind == FindingKind.Risk ? "Severity" : "Kind")}</th><th>Finding</th><th>Where</th><th>Confidence</th><th>Mark</th></tr>");
            foreach (var f in list)
            {
                var lead = kind == FindingKind.Risk ? $"<span class=\"{f.Severity}\">{f.Severity}</span>" : E(f.Category);
                sb.Append(Ci, $"<tr><td>{lead}</td><td>{E(f.Title)}<div class=\"dim\">{E(f.Detail)}</div>{(f.Code is { Length: > 0 } c ? $"<code>{E(c)}</code>" : "")}</td><td><code>{E(Place(f))}</code></td><td class=\"{f.Confidence}\">{f.Confidence}</td><td>{E(MarkText(map, f))}</td></tr>");
            }
            sb.Append("</table></div>");
        }
        Findings("Business rules", FindingKind.Rule, "Checks, decisions and limits found in the code, in the original's own words where it has them. Confirm each with the business before rebuilding it.");
        Findings("Dead code", FindingKind.DeadCode, "What nothing live reaches. Check the medium and low confidence ones (reports, jobs or other systems outside the scan may use them) before dropping them.");
        Findings("Risks", FindingKind.Risk, "What makes the rebuild harder or the system riskier, worst first.");

        sb.Append("<h2>Screens</h2><div class=\"wrap\"><table><tr><th>Screen</th><th>Control</th><th>Type</th><th>Text</th><th>Handled by</th></tr>");
        foreach (var (form, control, handlers) in Screens(map))
            sb.Append($"<tr><td>{E(form.Name)}</td><td>{E(control.Name)}</td><td>{E(control.Prop("type"))}</td><td>{E(control.Prop("Text")?.Replace("&", "", StringComparison.Ordinal))}</td><td>{E(handlers)}</td></tr>");
        sb.Append("</table></div>");

        sb.Append("<h2>Tables: who writes and reads them</h2><div class=\"wrap\"><table><tr><th>Table</th><th>Column</th><th>Type</th><th>Written by</th><th>Read by</th></tr>");
        foreach (var (table, column, writers, readers) in Tables(map))
            sb.Append($"<tr><td>{(column == null ? "<b>" + E(table.Name) + "</b>" : "")}</td><td>{E(column?.Name)}</td><td>{E(column?.Prop("type"))}</td><td>{E(writers)}</td><td>{E(readers)}</td></tr>");
        sb.Append("</table></div>");
        sb.Append("<p class=\"dim\" style=\"margin-top:32px\">Made by AVA Legacy Lens from code and schema only: no table's rows were read, and passwords are masked.</p></main></body></html>\n");
        return sb.ToString();
    }

    public static void SaveExcel(SystemMap map, string path)
    {
        using var book = new XLWorkbook();
        var inv = Inventory.Of(map);
        var est = Estimate.Of(map);

        var summary = book.Worksheets.Add("Summary");
        var r = 1;
        summary.Cell(r++, 1).SetValue(map.Root.Length > 0 ? Path.GetFileName(map.Root) : "database").Style.Font.SetBold().Font.SetFontSize(16);
        summary.Cell(r++, 1).SetValue($"Scanned {map.ScannedAt:yyyy-MM-dd HH:mm} by AVA Legacy Lens");
        r++;
        foreach (var (label, value) in new (string, double)[]
                 {
                     ("Lines of code", inv.TotalCode), ("Files", inv.Counts[NodeKind.File]), ("Screens", inv.Counts[NodeKind.Form]),
                     ("Types", inv.Counts[NodeKind.Type]), ("Methods", inv.Counts[NodeKind.Method]), ("Tables", inv.Counts[NodeKind.Table]),
                     ("Procedures", inv.Counts[NodeKind.Procedure]), ("Rules found", map.Findings.Count(f => f.Kind == FindingKind.Rule)),
                     ("Dead items", map.Findings.Count(f => f.Kind == FindingKind.DeadCode)), ("Risks", map.Findings.Count(f => f.Kind == FindingKind.Risk)),
                     ("Estimate (person-days)", Math.Round(est.Days, 1)), ("Estimate, low", est.Low), ("Estimate, high", est.High),
                 })
        {
            summary.Cell(r, 1).SetValue(label);
            summary.Cell(r++, 2).SetValue(value);
        }
        summary.Columns().AdjustToContents();

        void Sheet(string title, string[] headings, IEnumerable<object?[]> rows)
        {
            var ws = book.Worksheets.Add(title);
            for (var c = 0; c < headings.Length; c++) ws.Cell(1, c + 1).SetValue(headings[c]);
            var i = 2;
            foreach (var row in rows)
            {
                for (var c = 0; c < row.Length; c++)
                {
                    var cell = ws.Cell(i, c + 1);
                    switch (row[c])
                    {
                        case int n: cell.SetValue(n); break;
                        case double d: cell.SetValue(d); break;
                        default: cell.SetValue(row[c]?.ToString() ?? ""); break;
                    }
                }
                i++;
            }
            var range = ws.Range(1, 1, Math.Max(1, i - 1), headings.Length);
            if (i > 2) range.CreateTable();
            else ws.Row(1).Style.Font.SetBold();
            ws.Columns().AdjustToContents(1, 400);
            foreach (var col in ws.ColumnsUsed()) if (col.Width > 70) col.Width = 70;
            ws.SheetView.FreezeRows(1);
        }

        Sheet("Estimate", ["Area", "What", "Count", "Days each", "Days"], est.Lines.Select(l => new object?[] { l.Area, l.What, l.Count, l.DaysEach, l.Days }));
        object?[] FindingRow(Finding f) => [f.Kind == FindingKind.Risk ? f.Severity.ToString() : f.Category, f.Title, f.Detail, f.Code, f.File, f.Line, f.Confidence.ToString(), MarkText(map, f), f.NodeId];
        string[] findingHeadings = ["Kind", "Finding", "Detail", "Code", "File", "Line", "Confidence", "Mark", "Item"];
        Sheet("Rules", findingHeadings, map.Findings.Where(f => f.Kind == FindingKind.Rule).OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line).Select(FindingRow));
        Sheet("Dead code", findingHeadings, map.Findings.Where(f => f.Kind == FindingKind.DeadCode).OrderByDescending(f => f.Confidence).ThenBy(f => f.File, StringComparer.Ordinal).Select(FindingRow));
        Sheet("Risks", findingHeadings, map.Findings.Where(f => f.Kind == FindingKind.Risk).OrderByDescending(f => f.Severity).ThenBy(f => f.File, StringComparer.Ordinal).Select(FindingRow));
        Sheet("Screens", ["Screen", "Control", "Type", "Text", "Location", "Size", "Handled by"],
            Screens(map).Select(s => new object?[] { s.Form.Name, s.Control.Name, s.Control.Prop("type"), s.Control.Prop("Text")?.Replace("&", "", StringComparison.Ordinal), s.Control.Prop("Location"), s.Control.Prop("Size"), s.Handlers }));
        Sheet("Tables", ["Table", "Column", "Type", "Key", "Written by", "Read by"],
            Tables(map).Select(t => new object?[] { t.Table.Name, t.Column?.Name ?? "", t.Column?.Prop("type"), t.Column?.Prop("key"), t.Writers, t.Readers }));
        Sheet("Methods", ["Type", "Method", "Kind", "Language", "File", "Line", "Lines", "Complexity", "Callers", "Dead"],
            map.OfKind(NodeKind.Method).OrderBy(m => m.File, StringComparer.Ordinal).ThenBy(m => m.Line)
                .Select(m => new object?[] { m.Prop("type"), m.Name, m.Prop("subkind"), m.Language, m.File, m.Line, m.IntProp("lines"), m.IntProp("complexity"), map.To(m.Id).Count(e => e.Kind is EdgeKind.Calls or EdgeKind.Handles), m.Prop("dead") == "true" ? "yes" : "" }));
        book.SaveAs(path);
    }
}
