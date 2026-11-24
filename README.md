# AVA Legacy Lens

See what a legacy system really does before you rebuild it. AVA Legacy Lens reads an old system's code and works out
what's in it and what calls what, so a rebuild can start from the facts.

It reads VB.NET and C# projects with Roslyn, and builds a **system map**: projects, files, screens and their controls,
types and methods, with every call, object creation, inheritance and event handler between them. WinForms screens are
read from their designer code, so each button leads to the code its click runs: **Trace** follows it forward, through
every method it calls, or backward from any piece of code to the screens and buttons that reach it.

It reads the database side too: T-SQL scripts (tables, columns, keys, checks, procedures, views, functions and
triggers, with what each reads, writes and runs), SQL written inside VB.NET and C# strings (marked as a guess when it's
built by concatenation), config files (connection strings with their passwords never kept), and, if you add one, a
live SQL Server database's catalog. **It reads code and schema only, never a table's rows.** So you can ask who writes
`Patient.LastName`, and trace from a button through the code into the procedure, the table and the trigger it fires. Each link says where in the code it
was seen and how sure the scan is of it (resolved by the compiler, or matched by name across projects and languages).
*Work in progress: milestone 3 of 7 (T-SQL, SQL in code, config files and live databases).*

## Running it

```bash
dotnet run --project src/AVALegacyLens                           # the window
dotnet run --project src/AVALegacyLens -- samples/ClinicLegacy   # scan a folder straight away
dotnet run --project src/AVALegacyLens -- clinic.lens            # open a saved scan
```

`samples/ClinicLegacy` is an invented clinic system (a VB.NET WinForms front desk, a C# helper library and its SQL
Server scripts) made to exercise every part of the scan.

## License

MIT
