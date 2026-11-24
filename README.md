# AVA Legacy Lens

See what a legacy system really does before you rebuild it. AVA Legacy Lens reads an old system's code and works out
what's in it and what calls what, so a rebuild can start from the facts.

It reads VB.NET and C# projects with Roslyn, and Visual Basic 6 projects (forms, modules, classes) with a reader of its
own, and builds a **system map**: projects, files, screens and their controls,
types and methods, with every call, object creation, inheritance and event handler between them. WinForms screens are
read from their designer code, so each button leads to the code its click runs: **Trace** follows it forward, through
every method it calls, or backward from any piece of code to the screens and buttons that reach it.

It reads the database side too: T-SQL scripts (tables, columns, keys, checks, procedures, views, functions and
triggers, with what each reads, writes and runs), SQL written inside VB.NET and C# strings (marked as a guess when it's
built by concatenation), config files (connection strings with their passwords never kept), and, if you add one, a
live SQL Server database's catalog. **It reads code and schema only, never a table's rows.** So you can ask who writes
`Patient.LastName`, and trace from a button through the code into the procedure, the table and the trigger it fires. Each link says where in the code it
was seen and how sure the scan is of it (resolved by the compiler, or matched by name across projects and languages).
*Work in progress: milestone 6 of 7 (the MCP server and the AI Chat).*

From the map it works out what a rebuild needs to know:

- **Business rules**: checks that show a message and stop, decision tables, limits written into the code, and the
  checks, CHECK constraints and calculations in the database, each in the original's own words where it has them.
  Mark each one confirmed, wrong or noise; marks are kept in the .lens file and carried to a rescan.
- **Dead code**: what nothing reaches from the system's entry points (the startup screen, Main, the framework's
  callbacks), from methods and screens to procedures, tables, settings and connections, each with how sure it is.
- **Risks**: SQL built by joining strings, dynamic SQL, passwords and servers written into the code, errors ignored,
  native Windows calls, ActiveX controls, global state, secrets in config files, very complex code.
- **An estimate**: a rough size of the rebuild in person-days, from counts the scan is sure of, with its weights shown.
- **A report**: the lot as one HTML page or an Excel workbook (File › Export Report…).

## AI agents

An **AI Chat** (F12) and an **MCP server** let Claude, or a model running locally, answer questions about the system
from the map: "what does the patient screen do?", "who writes Patient.LastName?", "what would break if we dropped
usp_PurgeOldVisits?". The model reads the map with 33 tools, cites file and line for what it says, and says when a link
is only matched by name or guessed. It reads code and schema only, with passwords masked; a test calls every tool on the
sample and fails if any password reaches an answer or a file an agent wrote. Six recipes (saved prompts) walk through a
screen end to end, the rules behind a table, the impact of a change, the rebuild estimate, a rules catalogue and the
risky code.

```bash
AVALegacyLens --mcp --root FOLDER [--database CONNECTION]      # the MCP server on stdio, no window
AVALegacyLens --mcp --attach                                   # a bridge to the open window (AI › Allow AI Agents)
AVALegacyLens --mcp --http 8776 --root FOLDER                  # over the network, with a token
claude mcp add avalegacylens -- AVALegacyLens --mcp --attach
```

## Running it

```bash
dotnet run --project src/AVALegacyLens                           # the window
dotnet run --project src/AVALegacyLens -- samples/ClinicLegacy   # scan a folder straight away
dotnet run --project src/AVALegacyLens -- clinic.lens            # open a saved scan
```

`samples/ClinicLegacy` is an invented clinic system (a VB.NET WinForms front desk, a C# helper library, a VB6 claims
screen and its SQL Server scripts) made to exercise every part of the scan.

## License

MIT
