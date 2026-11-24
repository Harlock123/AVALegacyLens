using AVALegacyLens.Core;

namespace AVALegacyLens.Tests;

public class Dump
{
    [Fact]
    public void Print()
    {
        var map = Samples.ClinicMap;
        var o = Environment.GetEnvironmentVariable("LENS_DUMP");
        if (o == null) return;
        using var w = new StreamWriter(o);
        w.WriteLine(Inventory.Of(map).ToText());
        foreach (var n in map.Nodes.OrderBy(n => n.Id)) w.WriteLine($"{n.Kind} {n.Id} parent={n.Parent} {n.File}:{n.Line}-{n.EndLine} {string.Join(';', n.Props.Select(p => p.Key + '=' + p.Value))}");
        foreach (var e in map.Edges) w.WriteLine($"EDGE {e.Kind} {e.Certainty} {e.From} -> {e.To} @{e.File}:{e.Line} {e.Note}");
        foreach (var n in map.Notes) w.WriteLine("NOTE " + n);
    }
}
