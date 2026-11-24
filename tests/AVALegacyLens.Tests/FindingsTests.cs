using AVALegacyLens.Core.Findings;
using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Scanning;
using ClosedXML.Excel;

namespace AVALegacyLens.Tests;

/// <summary>Rules, dead code, risks, the estimate, marks and the reports, on the sample whose answers are known.</summary>
public sealed class FindingsTests
{
    private static SystemMap Map => Samples.ClinicMap;

    private static List<Finding> Of(FindingKind kind) => [.. Map.Findings.Where(f => f.Kind == kind)];

    [Fact]
    public void Every_planted_rule_is_found_and_nothing_else()
    {
        var rules = Of(FindingKind.Rule).Select(f => $"{f.Category}: {f.Title}").Order(StringComparer.Ordinal).ToList();
        string[] expected =
        [
            "calculation: usp_PostCharge works out a value: CASE WHEN @Copay > @Amount THEN @Amount ELSE @Copay END",
            "constraint: Charge: Amount > 0",
            "constraint: Patient: BirthDate <= GETDATE()",
            "decision: CalculateCopay decides by payer: \"MEDICAID\", \"MEDICARE\", \"SELF-PAY\", anything else",
            "decision: IsValid decides by mStatus: \"NEW\", \"HELD\", anything else",
            "guard: IsValid fails when mAmount <= 0 Or mAmount > 99999.99",
            "guard: IsValid fails when mPatientId <= 0",
            "limit: AgeCalculator.AdultAge = 18",
            "limit: BillingRules.SupervisorLimit = 5000D",
            "notice: Claims over $10,000 go to the review queue.",
            "validation: A guardian is required for patients under 18.",
            "validation: A supervisor must approve charges over $5,000.",
            "validation: Birth date cannot be in the future.",
            "validation: Birth date cannot be in the future.",
            "validation: Charges cannot be posted to an inactive patient.",
            "validation: Enter an amount greater than zero.",
            "validation: Last name is required.",
            "validation: Medicaid patients over 64 are usually dual eligible. Save anyway?",
            "validation: No patient found.",
            "validation: Patients under 18 need a guardian.",
            "validation: Procedure codes are five characters.",
            "validation: The amount must be a number.",
            "validation: Type at least two letters of the last name.",
        ];
        Assert.Equal(expected, rules);
        // The same rule in the screen and in the procedure: found in both places, so the rebuild keeps one on purpose.
        Assert.Equal(["ClinicApp/frmPatient.vb:39", "Database/procs.sql:7"], Of(FindingKind.Rule).Where(f => f.Title == "Birth date cannot be in the future.").Select(Report.Place).Order());
        var supervisor = Of(FindingKind.Rule).Single(f => f.Title.StartsWith("A supervisor", StringComparison.Ordinal));
        Assert.Equal("When amount > SupervisorLimit, it stops.", supervisor.Detail);
        Assert.Equal("ClinicApp/BillingRules.vb", supervisor.File);
        Assert.Equal(7, supervisor.Line);
    }

    [Fact]
    public void Every_planted_piece_of_dead_code_is_found_and_nothing_live()
    {
        var dead = Of(FindingKind.DeadCode).Select(f => $"{f.Category} {f.Confidence}: {f.Title}").Order(StringComparer.Ordinal).ToList();
        string[] expected =
        [
            "unreachable Medium: modClaims.GetTickCount: only dead code uses it",
            "unused High: frmMain.ShowSplash: nothing calls it",
            "unused Low: clsClaim.Amount: nothing calls it",
            "unused Low: clsClaim.PatientId: nothing calls it",
            "unused Low: clsClaim.Status: nothing calls it",
            "unused Low: vw_OpenBalances: nothing reads this view",
            "unused Medium: AgeCalculator.AgeBand: nothing calls it",
            "unused Medium: BillingRules.LegacyRound: nothing calls it",
            "unused Medium: ExportFolder: no code reads this setting",
            "unused Medium: FaxQueue: no code reads or writes this table",
            "unused Medium: Npi: nothing uses this static class",
            "unused Medium: PatientData.ExportPatients: nothing calls it",
            "unused Medium: Reporting: no code uses this connection",
            "unused Medium: SmtpPassword: no code reads this setting",
            "unused Medium: SupervisorLimit: no code reads this setting",
            "unused Medium: modClaims.PrintClaimLabel: nothing calls it",
            "unused Medium: usp_PurgeOldVisits: nothing runs it",
        ];
        Assert.Equal(expected, dead);
        Assert.Equal("true", Map.Method("BillingRules", "LegacyRound").Prop("dead"));
        Assert.Null(Map.Method("BillingRules", "PostCharge").Prop("dead"));
        // The trigger is live: its table is written by live code.
        Assert.Null(Map["db:dbo.trg_charge_audit"]!.Prop("dead"));
    }

    [Fact]
    public void A_library_nothing_scanned_uses_is_all_reachable()
    {
        var root = Directory.CreateTempSubdirectory("lens-lib-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "Api.cs"), "public class Api { public int Add(int a, int b) => a + b; private void Unused() { } }");
            var map = Scanner.Scan(root);
            Assert.Equal(["Api.Unused: nothing calls it"], map.Findings.Where(f => f.Kind == FindingKind.DeadCode).Select(f => f.Title));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Risks_are_found_where_they_are()
    {
        var risks = Of(FindingKind.Risk).Select(f => $"{f.Severity} {f.Category} {f.NodeId}").Order(StringComparer.Ordinal).ToList();
        Assert.Contains("High sql-built-by-hand ClinicApp:M:ClinicApp.PatientData.FindPatients(System.String)", risks);
        Assert.Contains("High sql-built-by-hand ClaimsEntry:M:modClaims.SaveClaim", risks);
        Assert.Contains("High hard-coded-password ClaimsEntry:M:modClaims.OpenDb", risks);
        Assert.Contains("Medium dynamic-sql db:dbo.usp_purgeoldvisits", risks);
        Assert.Contains("Medium on-error-resume-next ClaimsEntry:M:modClaims.SaveClaim", risks);
        Assert.Contains("Medium native-call ClaimsEntry:M:modClaims.GetTickCount", risks);
        Assert.Contains("Medium activex project:ClaimsVB6/ClaimsEntry.vbp", risks);
        Assert.Contains("Medium hard-coded-location ClinicApp:M:ClinicApp.PatientData.ExportPatients", risks);
        Assert.Contains("Medium secret-in-config connection:ClinicApp/app.config#Clinic", risks);
        Assert.Contains("Medium secret-in-config setting:ClinicApp/app.config#SmtpPassword", risks);
        Assert.Contains("Low select-star ClinicApp:M:ClinicApp.PatientData.ExportPatients", risks);
        Assert.Contains("Low global-state ClaimsEntry:T:modClaims", risks);
        Assert.Equal(12, risks.Count);
        var find = Of(FindingKind.Risk).Single(f => f.Category == "sql-built-by-hand" && f.NodeId.Contains("FindPatients"));
        Assert.Equal(13, find.Line);
        Assert.Contains("LIKE '0%'", find.Code);
    }

    [Fact]
    public void The_estimate_counts_only_live_code_and_shows_its_weights()
    {
        var est = Estimate.Of(Map);
        var screens = est.Lines.Where(l => l.Area == "Screens").Sum(l => l.Count);
        Assert.Equal(4, screens);
        var methods = est.Lines.Where(l => l.What.StartsWith("method", StringComparison.Ordinal)).Sum(l => l.Count);
        var live = Map.OfKind(NodeKind.Method).Count(m => m.Prop("dead") != "true" && m.Name != "InitializeComponent" && m.Prop("subkind") != "declare" && Map[m.Parent!]?.Prop("dead") != "true");
        Assert.Equal(live, methods);
        Assert.Equal(est.Lines.Sum(l => l.Days), est.Days, 3);
        Assert.True(est.Low < est.Days && est.Days < est.High);
        Assert.Contains("person-days", est.ToText());
        Assert.Equal(3, est.Lines.Single(l => l.What == "high risk").Count);
    }

    [Fact]
    public void Marks_are_kept_in_the_lens_file_and_change_the_estimate()
    {
        var map = Scanner.Scan(Samples.Clinic);
        var rule = map.Findings.First(f => f.Kind == FindingKind.Rule);
        var before = Estimate.Of(map).Days;
        map.Marks[rule.Id] = new Mark(MarkState.Wrong, "Only a UI hint", DateTimeOffset.Now);
        Assert.True(Estimate.Of(map).Days < before);
        var path = Path.Combine(Path.GetTempPath(), $"marks-{Guid.NewGuid():N}.lens");
        try
        {
            LensFile.Save(map, path);
            var back = LensFile.Load(path);
            Assert.Equal(MarkState.Wrong, back.MarkOf(back.Findings.Single(f => f.Id == rule.Id))!.State);
            Assert.Equal("Only a UI hint", back.Marks[rule.Id].Note);
            Assert.Equal(map.Findings.Count, back.Findings.Count);
            // A rescan finds the same finding by the same id.
            Assert.Contains(Scanner.Scan(Samples.Clinic).Findings, f => f.Id == rule.Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_HTML_report_has_every_section_and_no_secrets()
    {
        var html = Report.Html(Map);
        foreach (var heading in new[] { "Languages and projects", "Estimate", "Business rules (23)", "Dead code (17)", "Risks (12)", "Screens", "Tables: who writes and reads them" })
            Assert.Contains(heading, html);
        Assert.Contains("A guardian is required for patients under 18.", html);
        Assert.Contains("usp_SavePatient", html);
        Assert.DoesNotContain("Spr1ngRain", html);
        Assert.DoesNotContain("Winter2003", html);
        Assert.DoesNotContain("Mailbox#2009", html);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void The_Excel_report_has_a_sheet_for_each_part()
    {
        var path = Path.Combine(Path.GetTempPath(), $"report-{Guid.NewGuid():N}.xlsx");
        try
        {
            Report.Save(Map, path);
            using var book = new XLWorkbook(path);
            Assert.Equal(["Summary", "Estimate", "Rules", "Dead code", "Risks", "Screens", "Tables", "Methods"], book.Worksheets.Select(w => w.Name));
            Assert.Equal(23, book.Worksheet("Rules").RowsUsed().Count() - 1);
            Assert.Equal(17, book.Worksheet("Dead code").RowsUsed().Count() - 1);
            var tables = book.Worksheet("Tables");
            var lastName = tables.RowsUsed().Single(r => r.Cell(1).GetString() == "Patient" && r.Cell(2).GetString() == "LastName");
            Assert.Equal("usp_SavePatient", lastName.Cell(5).GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
