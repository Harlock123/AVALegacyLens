# AVA Legacy Lens

See what a legacy system really does before you rebuild it. AVA Legacy Lens reads an old system's code and works out
what's in it and what calls what, so a rebuild can start from the facts.

It reads VB.NET and C# projects with Roslyn (WinForms included), and builds a **system map**: projects, files, screens,
types and methods, with every call, object creation and inheritance between them. Each link says where in the code it
was seen and how sure the scan is of it (resolved by the compiler, or matched by name across projects and languages).
It reads code only, never data.

*Work in progress: milestone 1 of 7 (the scan, the map, .lens files and the inventory).*

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
