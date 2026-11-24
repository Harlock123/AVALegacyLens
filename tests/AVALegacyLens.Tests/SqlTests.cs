using AVALegacyLens.Core;
using AVALegacyLens.Core.Model;
using AVALegacyLens.Core.Scanning;
using AVALegacyLens.Core.Sql;
using Microsoft.Data.SqlClient;

namespace AVALegacyLens.Tests;

/// <summary>T-SQL from scripts, SQL inside code, config files, and a live SQL Server's catalog.</summary>
public sealed class SqlTests
{
    private static SystemMap Map => Samples.ClinicMap;

    private static Node Db(SystemMap map, string name) => map[SqlNames.ObjectId("dbo", name)] ?? throw new KeyNotFoundException(name);

    private static Node Col(SystemMap map, string table, string column) => map[SqlNames.ColumnId(SqlNames.ObjectId("dbo", table), column)] ?? throw new KeyNotFoundException($"{table}.{column}");

    private static string[] WritersOf(SystemMap map, string id) => [.. Usage.Writers(map, id).Select(w => w.Node.Name).Order(StringComparer.OrdinalIgnoreCase)];

    [Fact]
    public void Tables_and_columns_come_with_types_keys_defaults_and_checks()
    {
        var patient = Db(Map, "Patient");
        Assert.Equal(NodeKind.Table, patient.Kind);
        Assert.Equal("file:Database/schema.sql", patient.Parent);
        Assert.Equal(8, patient.Line);
        Assert.Equal("BirthDate <= GETDATE()", patient.Prop("check CK_Patient_BirthDate"));
        Assert.Equal(["Active", "BirthDate", "FirstName", "Guardian", "LastName", "PatientId", "PayerCode"], Map.Children(patient.Id).Select(c => c.Name).Order());
        var id = Col(Map, "Patient", "PatientId");
        Assert.Equal("int", id.Prop("type"));
        Assert.Equal("primary", id.Prop("key"));
        Assert.Equal("true", id.Prop("identity"));
        Assert.Equal("false", id.Prop("nullable"));
        Assert.Equal("varchar(40)", Col(Map, "Patient", "LastName").Prop("type"));
        Assert.Equal("true", Col(Map, "Patient", "Guardian").Prop("nullable"));
        Assert.Equal("1", Col(Map, "Patient", "Active").Prop("default"));
        Assert.Equal("Payer.PayerCode", Col(Map, "Patient", "PayerCode").Prop("references"));
        Assert.Contains(Map.From(patient.Id), e => e.Kind == EdgeKind.Uses && e.To == Db(Map, "Payer").Id && e.Note!.StartsWith("foreign key PayerCode"));
        Assert.Equal("Amount > 0", Db(Map, "Charge").Prop("check CK_Charge_Amount"));
    }

    [Fact]
    public void Procedures_views_and_triggers_are_read()
    {
        var save = Db(Map, "usp_SavePatient");
        Assert.Equal(NodeKind.Procedure, save.Kind);
        Assert.Equal((1, 32), (save.Line, save.EndLine));
        Assert.StartsWith("@PatientId int, @LastName varchar(40)", save.Prop("parameters"));
        Assert.Equal(5, save.IntProp("complexity"));
        Assert.Equal(NodeKind.View, Db(Map, "vw_OpenBalances").Kind);
        var trigger = Db(Map, "trg_Charge_Audit");
        Assert.Equal("AFTER INSERT", trigger.Prop("fires"));
        Assert.Contains(Map.From(trigger.Id), e => e.Kind == EdgeKind.Handles && e.To == Db(Map, "Charge").Id);
        Assert.Equal(["Charge", "Patient"], Usage.Readers(Map, Db(Map, "Charge").Id).Concat(Usage.Readers(Map, Db(Map, "Patient").Id)).Where(r => r.Node.Name == "vw_OpenBalances").Select(r => r.Edge.To == Db(Map, "Charge").Id ? "Charge" : "Patient").Order());
    }

    /// <summary>Every column of the sample and what writes it, worked out by hand from the scripts.</summary>
    public static TheoryData<string, string, string[]> ColumnWriters => new()
    {
        { "Payer", "PayerCode", [] }, { "Payer", "Name", [] }, { "Payer", "CopayPercent", [] },
        { "Patient", "PatientId", [] },
        { "Patient", "LastName", ["usp_SavePatient"] }, { "Patient", "FirstName", ["usp_SavePatient"] },
        { "Patient", "BirthDate", ["usp_SavePatient"] }, { "Patient", "PayerCode", ["usp_SavePatient"] },
        { "Patient", "Guardian", ["usp_SavePatient"] }, { "Patient", "Active", ["usp_SavePatient"] },
        { "Visit", "VisitId", [] }, { "Visit", "PatientId", [] }, { "Visit", "VisitDate", [] }, { "Visit", "Clinician", [] }, { "Visit", "Reason", [] },
        { "Charge", "ChargeId", [] }, { "Charge", "PostedOn", [] },
        { "Charge", "PatientId", ["usp_PostCharge"] }, { "Charge", "ProcedureCode", ["usp_PostCharge"] },
        { "Charge", "Amount", ["usp_PostCharge"] }, { "Charge", "Copay", ["usp_PostCharge"] },
        { "AuditLog", "AuditId", [] }, { "AuditLog", "ChangedOn", [] },
        { "AuditLog", "TableName", ["trg_Charge_Audit", "usp_SavePatient"] }, { "AuditLog", "KeyValue", ["trg_Charge_Audit", "usp_SavePatient"] },
        { "AuditLog", "Action", ["trg_Charge_Audit", "usp_SavePatient"] },
        { "FaxQueue", "FaxId", [] }, { "FaxQueue", "Number", [] }, { "FaxQueue", "Document", [] },
        // Written only by the VB6 screen's SQL, built by concatenation.
        { "Claim", "ClaimId", [] }, { "Claim", "EnteredOn", [] },
        { "Claim", "PatientId", ["SaveClaim"] }, { "Claim", "Amount", ["SaveClaim"] }, { "Claim", "Status", ["SaveClaim"] },
    };

    [Theory]
    [MemberData(nameof(ColumnWriters))]
    public void Who_writes_each_column(string table, string column, string[] writers) =>
        Assert.Equal(writers, WritersOf(Map, Col(Map, table, column).Id));

    [Fact]
    public void Who_writes_each_table()
    {
        Assert.Equal(["usp_SavePatient"], WritersOf(Map, Db(Map, "Patient").Id));
        Assert.Equal(["usp_PostCharge"], WritersOf(Map, Db(Map, "Charge").Id));
        Assert.Equal(["trg_Charge_Audit", "usp_SavePatient"], WritersOf(Map, Db(Map, "AuditLog").Id));
        Assert.Empty(WritersOf(Map, Db(Map, "Payer").Id));
        Assert.Empty(WritersOf(Map, Db(Map, "FaxQueue").Id));
        // Only dynamic SQL deletes visits: a guess, from the string it builds.
        var purge = Assert.Single(Usage.Writers(Map, Db(Map, "Visit").Id));
        Assert.Equal("usp_PurgeOldVisits", purge.Node.Name);
        Assert.Equal(Certainty.Guessed, purge.Edge.Certainty);
        Assert.Equal(79, purge.Edge.Line);
        Assert.Equal("true", purge.Node.Prop("dynamicSql"));
    }

    [Fact]
    public void SQL_in_code_is_read_and_procedures_named_in_code_are_run()
    {
        var payerOf = Map.Method("BillingRules", "PayerOf");
        var reads = Map.From(payerOf.Id).Where(e => e.Kind == EdgeKind.Reads).ToList();
        Assert.All(reads, e => Assert.Equal(Certainty.Resolved, e.Certainty));
        Assert.Contains(reads, e => e.To == Col(Map, "Patient", "PayerCode").Id);
        Assert.Equal(42, reads[0].Line);

        // Built by concatenation: read with a stand-in for the name, marked as a guess and as SQL built by hand.
        var find = Map.Method("PatientData", "FindPatients");
        Assert.Equal("true", find.Prop("sqlBuiltByHand"));
        Assert.Contains(Map.From(find.Id), e => e.Kind == EdgeKind.Reads && e.To == Col(Map, "Patient", "LastName").Id && e.Certainty == Certainty.Guessed);

        Assert.Contains(Map.From(Map.Method("PatientData", "LoadVisits").Id), e => e.Kind == EdgeKind.Executes && e.To == Db(Map, "usp_GetVisits").Id && e.Line == 25);
        Assert.Contains(Map.From(Map.Method("BillingRules", "PostCharge").Id), e => e.Kind == EdgeKind.Executes && e.To == Db(Map, "usp_PostCharge").Id);
        Assert.Contains(Map.From(Map.Method("PatientData", "ExportPatients").Id), e => e.Kind == EdgeKind.Reads && e.To == Db(Map, "Patient").Id);
    }

    [Fact]
    public void Config_files_give_connections_and_settings_with_secrets_masked()
    {
        var clinic = Map["connection:ClinicApp/app.config#Clinic"]!;
        Assert.Equal("clinicdb01", clinic.Prop("server"));
        Assert.Equal("Clinic", clinic.Prop("database"));
        Assert.Equal("clinic_app", clinic.Prop("user"));
        Assert.Equal("(masked)", clinic.Prop("password"));
        Assert.Equal("Windows (integrated)", Map["connection:ClinicApp/app.config#Reporting"]!.Prop("signIn"));
        Assert.Equal("5000", Map["setting:ClinicApp/app.config#SupervisorLimit"]!.Prop("value"));
        Assert.Equal("(masked)", Map["setting:ClinicApp/app.config#SmtpPassword"]!.Prop("value"));
        Assert.Contains(Map.From(Map.Method("PatientData", "GetConnection").Id), e => e.Kind == EdgeKind.Uses && e.To == clinic.Id);

        // The file share, from the setting and from the code that writes to it.
        var share = Map[@"external:\\fileserver01\exports"]!;
        Assert.Equal("file share", share.Prop("subkind"));
        Assert.Equal(SystemMap.OutsideGroup, share.Parent);
        Assert.Equal(["ExportFolder", "ExportPatients"], Map.To(share.Id).Select(e => Map[e.From]!.Name).Order());
        Assert.Equal("true", Map.Method("PatientData", "ExportPatients").Prop("hardCodedOutside"));

        // No password from the config is anywhere in the map or its .lens file.
        var path = Path.Combine(Path.GetTempPath(), $"secrets-{Guid.NewGuid():N}.lens");
        try
        {
            LensFile.Save(Map, path);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("Spr1ngRain", text);
            Assert.DoesNotContain("Mailbox#2009", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Writes_fire_triggers_and_traces_cross_from_code_into_SQL()
    {
        var post = Map[WinFormsReader.ControlId(Map.Type("frmBilling").Id, "btnPost")]!;
        var text = Trace.ToText(Trace.Forward(Map, post.Id));
        Assert.Contains("runs procedure usp_PostCharge", text);
        Assert.Contains("writes table Charge", text);
        Assert.Contains("fires trigger trg_Charge_Audit", text);
        Assert.Contains("writes table AuditLog", text);

        // Backward from a column to the button that saves it.
        var back = Trace.Backward(Map, Col(Map, "Patient", "LastName").Id);
        Assert.Contains(back.All(), s => s.Node.Name == "btnSave" && s.Node.Kind == NodeKind.Control);
    }

    [Fact]
    public void A_batch_that_doesnt_parse_is_noted_and_the_rest_still_read()
    {
        var map = new SystemMap();
        var sql = new SqlReader(map);
        sql.ReadScript("bad.sql", "CREATE TABLE dbo.A (Id int)\nGO\nCREATE PROCEDURE dbo.B AS SELEC Id FRM dbo.A\nGO\nCREATE VIEW dbo.C AS SELECT Id FROM dbo.A\nGO\n", "file:bad.sql");
        sql.AnalyzeModules();
        var note = Assert.Single(map.Notes);
        Assert.Equal(3, note.Line);
        Assert.NotNull(map["db:dbo.a"]);
        Assert.NotNull(map["db:dbo.c"]);
        Assert.Null(map["db:dbo.b"]);
        Assert.Contains(map.From("db:dbo.c"), e => e.Kind == EdgeKind.Reads && e.To == "db:dbo.a" && e.Line == 5);
    }

    [Fact]
    public void Names_and_SQL_starts_are_recognised()
    {
        Assert.Equal(("dbo", "usp_GetVisits"), SqlNames.Parse("usp_GetVisits"));
        Assert.Equal(("sales", "Order Lines"), SqlNames.Parse("[sales].[Order Lines]"));
        Assert.Null(SqlNames.Parse("Saved."));
        Assert.Null(SqlNames.Parse("Type at least two letters"));
        Assert.True(SqlReader.LooksLikeSql("  select * from x"));
        Assert.False(SqlReader.LooksLikeSql("Selected patient"));
    }

    /// <summary>
    /// A live SQL Server (LENS_TEST_MSSQL, a connection string to a server where a test database may be made): the
    /// sample's scripts are run into a new database, and reading its catalog gives the same tables, columns and writers
    /// as reading the scripts. Skipped when the variable isn't set.
    /// </summary>
    [Fact]
    public void A_live_database_reads_like_its_scripts()
    {
        if (Environment.GetEnvironmentVariable("LENS_TEST_MSSQL") is not { Length: > 0 } server) return;
        var name = "LensTest" + Guid.NewGuid().ToString("N")[..8];
        using (var cn = new SqlConnection(server))
        {
            cn.Open();
            new SqlCommand($"CREATE DATABASE [{name}]", cn).ExecuteNonQuery();
        }
        var cs = new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString;
        var empty = Directory.CreateTempSubdirectory("lens-db-").FullName;
        try
        {
            using (var cn = new SqlConnection(cs))
            {
                cn.Open();
                foreach (var script in new[] { "schema.sql", "procs.sql" })
                    foreach (var batch in System.Text.RegularExpressions.Regex.Split(File.ReadAllText(Path.Combine(Samples.Clinic, "Database", script)), @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline))
                        if (batch.Trim().Length > 0) new SqlCommand(batch, cn).ExecuteNonQuery();
            }
            var map = Scanner.Scan(empty, new ScanOptions { Databases = [cs] });
            Assert.Empty(map.Notes);
            var group = map[DatabaseReader.GroupId(cs)]!;
            Assert.Equal("database", group.Prop("subkind"));
            Assert.Equal("7", group.Prop("tables"));
            Assert.Equal(group.Id, Db(map, "Patient").Parent);
            Assert.Equal("varchar(40)", Col(map, "Patient", "LastName").Prop("type"));
            Assert.Equal("primary", Col(map, "Patient", "PatientId").Prop("key"));
            Assert.Equal("Payer.PayerCode", Col(map, "Patient", "PayerCode").Prop("references"));
            Assert.Contains("usp_SavePatient", Db(map, "usp_SavePatient").Prop("sql"));
            foreach (var row in ColumnWriters)
            {
                var (table, column, writers) = row.Data;
                if (table == "Claim") continue; // written from code, which a database alone doesn't have
                Assert.Equal(writers, WritersOf(map, Col(map, table, column).Id));
            }
            Assert.Equal(["trg_Charge_Audit", "usp_SavePatient"], WritersOf(map, Db(map, "AuditLog").Id));
            // The connection string isn't kept: no password in the map.
            var password = new SqlConnectionStringBuilder(server).Password;
            var path = Path.Combine(empty, "db.lens");
            LensFile.Save(map, path);
            if (password.Length > 0) Assert.DoesNotContain(password, File.ReadAllText(path));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            using (var cn = new SqlConnection(server))
            {
                cn.Open();
                new SqlCommand($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]", cn).ExecuteNonQuery();
            }
            Directory.Delete(empty, true);
        }
    }
}
