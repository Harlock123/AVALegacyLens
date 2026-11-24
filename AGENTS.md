# AGENTS.md

Notes for anyone, human or AI agent, changing this code. `README.md` covers what the program does; this file covers
how the code is put together and the rules that keep it working.

## What this is

A legacy system analyzer in C# on .NET 10 and Avalonia 11.3: it scans a folder of old code (VB.NET, C#, VB6, T-SQL,
config files) and optionally a live SQL Server's schema into a system map of nodes and links, and shows it. It is the first step of a re-implementation:
find out what the old system does, then rebuild it (and prove parity with AVAParityCheck).

## Commands

```bash
dotnet build AVALegacyLens.slnx
dotnet test AVALegacyLens.slnx
dotnet run --project src/AVALegacyLens [folder | file.lens]
LENS_DUMP=/tmp/map.txt dotnet test --filter Dump      # every node and link of the sample, as text
LENS_SHOTS=/tmp/shots dotnet test --filter Screenshots # the README's pictures, headless
LENS_TEST_MSSQL="Server=localhost,11435;User Id=sa;Password=…;TrustServerCertificate=True" dotnet test   # live SQL Server test
```

`dotnet test | head` stalls: write the output to a file and grep it.

## Layout

- `Directory.Build.props`: net10.0, nullable on, for everything. `src/AVALegacyLens.Core` also has warnings as errors.
- `src/AVALegacyLens.Core/`: scanning, with no Avalonia code.
  - `Model/SystemMap.cs`: `Node` (id, kind, name, parent, file and lines, props), `Edge` (from, to, kind, certainty,
    where it was seen), `SystemMap` (lookups both ways, children, paths). `Model/LensFile.cs`: the .lens file (JSON).
  - `Scanning/SourceFiles.cs` (finding projects and files; reading project files; reading text that may be
    Windows-1252), `LineCounter.cs`, `DotNetReader.cs` (Roslyn, VB.NET and C#; raises `TypeRead` and `MemberRead` for
    the readers that add to it), `WinFormsReader.cs` (controls from `InitializeComponent`, handlers from `Handles`,
    `AddHandler` and `+=`, control uses), `Scanner.cs` (puts it together).
  - `Sql/`: `SqlNames` (ids `db:schema.name`, lower case; display names), `SqlReader` (ScriptDom: definitions from
    scripts split at GO, then `AnalyzeModules` reads bodies; `AnalyzeSql` reads SQL from code; a rough regex reading,
    marked Guessed, for dynamic SQL), `EmbeddedSqlReader` (SQL strings in VB/C#, concatenation → Guessed and
    `sqlBuiltByHand`; procedure names given to a *Command), `DatabaseReader` (a live SQL Server's catalog: never rows).
  - `Vb6/Vb6Reader.cs`: VB6 projects (.vbp), forms' controls from their headers (twips → pixels, containers, control
    arrays), procedures read line by line (logical lines, comments stripped), handlers by name (`cmdSave_Click`), calls
    and `New` matched by name across the project (always `ByName`), SQL and connection strings in strings, risks
    (`On Error Resume Next`, `GoTo`, hard-coded passwords and shares).
  - `Secrets.cs`: `Mask` runs over every snippet and note the map keeps.
  - `Config/ConfigReader.cs`: connection strings (password never kept), settings (secrets masked), outside systems
    (shares, web addresses) and code that uses them.
  - `Inventory.cs`: counts, lines by language, project sizes, the most complex methods. `Usage.cs`: writers and readers.
  - `Findings/`: `Finding` (rule, dead code or risk; its `Id` is kind|category|node|line so marks survive a rescan),
    `DotNetRules` (validations, notices, decisions, limits in VB.NET/C#), rules in `SqlReader` (RAISERROR/THROW in IF,
    CASE, CHECK) and `Vb6Reader` (MsgBox/Exit, guards, Select Case), `Analysis` (reachability from entry points →
    dead code; risks from the props readers set), `Estimate` (person-days from stated weights, dead code left out),
    `Report` (HTML page and Excel workbook).
  - `Trace.cs`: forward (screen → controls → handlers → calls…) and backward (code → callers → handlers → controls →
    screens → whatever opens them), breadth first, cut at a depth and a step count, repeats marked.
- `src/AVALegacyLens/`: the window, built in code with no XAML (`MainWindow.cs`, `MainWindow.Trace.cs`,
  `MainWindow.Findings.cs` with `FindingsView`, `ConnectionDialog.cs`, `Look.cs`).
- `tests/AVALegacyLens.Tests/`: xUnit v3 on Avalonia.Headless. `Samples.cs` scans the sample once for every test.
- `samples/ClinicLegacy/`: the invented sample system.

## Rules that keep it working

- **Core stays free of Avalonia.** Everything worked out from code belongs in Core, where tests check it without a window.
- **The scan is deterministic and done by code.** The same folder gives the same map. A model may explain the map; it
  never makes it up.
- **Ids are stable.** Code nodes are `<project key>:<documentation id>` (`ClinicApp:M:ClinicApp.BillingRules.PostCharge(…)`);
  files `file:<relative path>`; projects `project:<relative path>`; controls `<form id>#<name>`. Marks kept in a .lens file depend on them.
- **Every link says how sure it is.** `Resolved` when the compiler bound it, `ByName` when matched by name, `Guessed`
  for anything worked out from strings. Never present a guess as a fact.
- **Findings are bounded by tests.** `FindingsTests` lists every rule, dead item and risk the sample has: a change
  that finds more (a false hit) or fewer fails it. Add to the sample and the list together.
- **Readers set facts, Analysis judges.** Readers put facts on nodes (`sqlBuiltByHand`, `onErrorResumeNext`,
  `hardCodedPassword`, `override`…); `Analysis.Run` turns them into findings after every scan.
- **Every link joins two nodes**: `SystemMap.RemoveDangling` runs at the end of a scan.
- **Paths in the map are relative to the scanned folder**, with forward slashes; lines count from 1.
- **Schema, never data.** `DatabaseReader` queries catalog views only; connection strings are never stored (the map
  names a database by server and catalog). Config passwords and secret settings are masked as they're read; a test
  checks no sample password reaches a .lens file.
- **SQL scans in order**: every definition (scripts, then databases) before any body or code is read, so references
  resolve; a stand-in made for a name seen before its definition is replaced, keeping its links.
- **VB6 files are Windows-1252 with CRLF** (`.gitattributes` keeps them byte for byte); read them through
  `SourceFiles.ReadText`.
- **No real code or data in the repository.** The sample is invented; its config's passwords are made up for the tests.

## Making a change

- Run `dotnet test` after any change.
- Pushes to `main` publish builds to the rolling `latest` pre-release; a `v*` tag makes a proper release.
