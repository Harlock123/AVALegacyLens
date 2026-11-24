using System.Text.Json.Serialization;

namespace AVALegacyLens.Core.Findings;

[JsonConverter(typeof(JsonStringEnumConverter<FindingKind>))]
public enum FindingKind
{
    /// <summary>A candidate business rule: a check, a decision, a limit.</summary>
    Rule,
    /// <summary>Code or schema nothing reaches.</summary>
    DeadCode,
    /// <summary>Something that makes the rebuild harder or the system riskier.</summary>
    Risk,
}

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    Info,
    Low,
    Medium,
    High,
}

/// <summary>How a person judged a finding. Kept in the .lens file, by the finding's id, across rescans.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MarkState>))]
public enum MarkState
{
    Confirmed,
    Wrong,
    Noise,
}

/// <summary>A person's mark on a finding, and why.</summary>
public sealed record Mark(MarkState State, string? Note, DateTimeOffset At);

/// <summary>
/// Something the scan found worth a person's attention, at a place in the code: a rule to keep, dead code to drop, a
/// risk to plan for. <see cref="Id"/> is made from what and where, so a person's mark on it survives a rescan of the
/// same code. <see cref="Confidence"/> says how sure the finding is (dead code behind reflection is less sure).
/// </summary>
public sealed class Finding
{
    public required FindingKind Kind { get; init; }
    /// <summary>A rule's kind (validation, decision, limit, constraint, calculation), a risk's (sql-injection…), or why code is dead.</summary>
    public required string Category { get; init; }
    public required string NodeId { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    /// <summary>The code it's about, as written (masked).</summary>
    public string? Code { get; init; }
    public string? File { get; init; }
    public int Line { get; init; }
    public Severity Severity { get; init; } = Severity.Info;
    public Severity Confidence { get; init; } = Severity.High;

    [JsonIgnore]
    public string Id => $"{Kind}|{Category}|{NodeId}|{Line}";

    public override string ToString() => $"{Kind} {Title}";
}
