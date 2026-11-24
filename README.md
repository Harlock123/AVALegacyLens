# AVA Legacy Lens

See what a legacy system really does before you rebuild it. AVA Legacy Lens reads an old system's code and works out
what's in it and what calls what, so a rebuild can start from the facts.

It reads VB.NET and C# projects with Roslyn, and builds a **system map**: projects, files, screens and their controls,
types and methods, with every call, object creation, inheritance and event handler between them. WinForms screens are
read from their designer code, so each button leads to the code its click runs: **Trace** follows it forward, through
every method it calls, or backward from any piece of code to the screens and buttons that reach it. Each link says where in the code it
was seen and how sure the scan is of it (resolved by the compiler, or matched by name across projects and languages).
It reads code only, never data.

*Work in progress: milestone 2 of 7 (screens, controls, handlers and traces).*

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
