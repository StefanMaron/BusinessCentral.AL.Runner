using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using Microsoft.Dynamics.Nav.CodeAnalysis.Emit;
using Microsoft.Dynamics.Nav.CodeAnalysis.Packaging;
using Microsoft.Dynamics.Nav.CodeAnalysis.SymbolReference;
using Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using Microsoft.Dynamics.Nav.Runtime;
using AlRunner;

// ---------------------------------------------------------------------------
// AlRunner: AL source code in -> execution out, no BC server needed
// Supports single files, multiple files, and project directories.
// ---------------------------------------------------------------------------

if (args.Length == 0 || args.Any(a => a is "-h" or "--help"))
{
    Console.Error.WriteLine("AL Runner — run BC AL unit tests in milliseconds");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage: al-runner [options] <src-dir> [test-dir]");
    Console.Error.WriteLine("       al-runner [options] <file.al> [file2.al ...]");
    Console.Error.WriteLine("       al-runner [options] <package.app> [package2.app ...]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Options:");
    Console.Error.WriteLine("  --coverage            Show statement-level coverage report and write cobertura.xml");
    Console.Error.WriteLine("  --packages <dir>      Add symbol references from .app files in directory");
    Console.Error.WriteLine("                        (auto-detected: .alpackages in/near source dirs when omitted)");
    Console.Error.WriteLine("  --dep-dlls <dir>      Load pre-compiled dependency DLLs for runtime execution");
    Console.Error.WriteLine("  --compile-dep <app> <out-dir> [--packages <dir>]");
    Console.Error.WriteLine("                        Compile a .app dependency to a rewritten DLL on disk");
    Console.Error.WriteLine("  --extract-deps <src-dir> <out-dir> <app1> [<app2> ...]");
    Console.Error.WriteLine("                        Extract the minimal reachable dependency slice from .app");
    Console.Error.WriteLine("                        artifacts for the extension source in <src-dir> and write");
    Console.Error.WriteLine("                        extracted AL files to <out-dir>");
    Console.Error.WriteLine("  --stubs <dir>         Override dependency objects with stub AL files");
    Console.Error.WriteLine("  --init-events         Fire BC lifecycle integration events once at runner startup");
    Console.Error.WriteLine("                        (OnCompanyInitialize from CU 2/27, OnInstallAppPerCompany from CU 2)");
    Console.Error.WriteLine("                        The resulting DB state is the baseline every test starts from;");
    Console.Error.WriteLine("                        test isolation restores from the baseline on each reset.");
    Console.Error.WriteLine("  --test-isolation <mode>  codeunit (default) — reset tables between test codeunits,");
    Console.Error.WriteLine("                           sharing state within a codeunit (BC default behaviour);");
    Console.Error.WriteLine("                           method — reset tables before every test method (legacy).");
    Console.Error.WriteLine("  --test-timeout <sec>  Per-test timeout in seconds (default: 5, 0 = disable)");
    Console.Error.WriteLine("  --dump-csharp         Print generated C# (before rewriting) and exit");
    Console.Error.WriteLine("  --dump-rewritten      Print rewritten C# (after rewriting) and exit");
    Console.Error.WriteLine("  -e '<al code>'        Run inline AL code");
    Console.Error.WriteLine("  -v, --verbose         Show detailed transpilation and compilation output");
    Console.Error.WriteLine("  --output-json         Output results as machine-readable JSON (status: pass/fail/error)");
    Console.Error.WriteLine("  --output-junit <path> Write JUnit XML test report to <path> (for CI test result tabs)");
    Console.Error.WriteLine("  --capture-values      Capture variable values after each test for inline display");
    Console.Error.WriteLine("  --iteration-tracking  Track per-iteration data for loops (requires --output-json)");
    Console.Error.WriteLine("  --run <procedure>     Run only the specified procedure by name");
    Console.Error.WriteLine("  --run-codeunit <name> Run the OnRun trigger of a named codeunit explicitly");
    Console.Error.WriteLine("                        (codeunits with TableNo set are skipped)");
    Console.Error.WriteLine("  --server              Start in server mode (JSON-RPC over stdin/stdout)");
    Console.Error.WriteLine("  --dap [port]          Start DAP debugger server (default port 4711)");
    Console.Error.WriteLine("                        Connect VS Code or any DAP client to set breakpoints");
    Console.Error.WriteLine("                        and inspect variables during AL test execution.");
    Console.Error.WriteLine("  --generate-stubs <packages-dir> <output-dir> [<src-dir> ...]");
    Console.Error.WriteLine("                        Scaffold empty AL stub files from .app symbol packages.");
    Console.Error.WriteLine("                        <packages-dir>  required  directory of .app files to read");
    Console.Error.WriteLine("                        <output-dir>    required  where to write the .al stub files");
    Console.Error.WriteLine("                        <src-dir> ...   optional  when given, only codeunits actually");
    Console.Error.WriteLine("                                                  referenced in those dirs are emitted");
    Console.Error.WriteLine("  --guide               Print test-writing guide for AI coding agents");
    Console.Error.WriteLine("  --no-telemetry        Disable crash reporting prompt on unexpected errors");
    Console.Error.WriteLine("  --strict              Fail on runner limitations (exit 1 instead of 2)");
    Console.Error.WriteLine("  --company-name <name> Default value returned by CompanyName() (empty string otherwise)");
    Console.Error.WriteLine("  --user-id <value>     Set the value returned by UserId() (default: TESTUSER)");
    Console.Error.WriteLine("  -h, --help            Show this help");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Examples:");
    Console.Error.WriteLine("  al-runner ./src ./test                        Run tests");
    Console.Error.WriteLine("  al-runner --coverage ./src ./test             Run tests with coverage");
    Console.Error.WriteLine("  al-runner --packages .alpackages ./src        Run with dependencies");
    Console.Error.WriteLine("  al-runner --output-json ./src ./test          Get JSON results for tooling");
    Console.Error.WriteLine("  al-runner --output-junit results.xml ./src ./test  Write JUnit XML report");
    Console.Error.WriteLine("  al-runner --run TestMyThing ./src ./test      Run a single test procedure");
    Console.Error.WriteLine("  al-runner --server                            Start JSON-RPC daemon");
    Console.Error.WriteLine("  al-runner --generate-stubs .alpackages ./stubs ./src ./test");
    Console.Error.WriteLine("                                                Scaffold stubs for referenced codeunits");
    Console.Error.WriteLine("  al-runner --generate-stubs .alpackages ./stubs");
    Console.Error.WriteLine("                                                Scaffold stubs for all codeunits in packages");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Test codeunits (Subtype = Test) are auto-detected.");
    Console.Error.WriteLine("BC Service Tier DLLs are auto-downloaded on first run.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Exit codes:");
    Console.Error.WriteLine("  0  All tests passed");
    Console.Error.WriteLine("  1  Test assertion failures (real bugs in code) or usage error");
    Console.Error.WriteLine("  2  Runner limitations only (not with --strict; use in CI to catch regressions)");
    Console.Error.WriteLine("  3  AL compilation error (the AL source itself does not compile)");
    Console.Error.WriteLine();
    Console.Error.WriteLine("For AI agents: run `al-runner --guide` for a complete test-writing reference.");
    return args.Length == 0 ? 1 : 0;
}

// Parse arguments into PipelineOptions
var options = new AlRunner.PipelineOptions();
bool noTelemetry = false;

int argIdx = 0;
while (argIdx < args.Length)
{
    switch (args[argIdx])
    {
        case "--dump-csharp":
            options.DumpCSharp = true;
            argIdx++;
            break;
        case "--dump-rewritten":
            options.DumpRewritten = true;
            argIdx++;
            break;
        case "--guide":
            PrintGuide();
            return 0;
        case "--generate-stubs":
        {
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --generate-stubs requires <packages-dir> <output-dir>"); return 1; }
            var pkgDir = Path.GetFullPath(args[argIdx]);
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --generate-stubs requires <packages-dir> <output-dir>"); return 1; }
            var outDir = Path.GetFullPath(args[argIdx]);
            argIdx++;
            // Remaining args are optional source directories
            var srcDirs = new List<string>();
            while (argIdx < args.Length && !args[argIdx].StartsWith("-"))
            {
                srcDirs.Add(Path.GetFullPath(args[argIdx]));
                argIdx++;
            }
            try
            {
                // Build a minimal BC compilation against the packages directory so that
                // GetApplicationObjectTypeSymbolsAcrossModules can enumerate platform/system
                // codeunits that have no SymbolReference.json in their .app file.
                Compilation? genCompilation = null;
                {
                    var savedErr = Console.Error;
                    Console.SetError(TextWriter.Null);
                    try
                    {
                        AlTranspiler.TranspileMulti(
                            new List<string> { "codeunit 99999999 \"AlRunnerStubProbe\" { }" },
                            packagePaths: new List<string> { pkgDir });
                        genCompilation = AlTranspiler.LastCompilation;
                    }
                    catch { /* symbol table may still be usable if TranspileMulti threw */ }
                    finally { Console.SetError(savedErr); }
                }

                var genResult = AlRunner.StubGenerator.Generate(
                    new[] { pkgDir }, outDir,
                    srcDirs.Count > 0 ? srcDirs : null,
                    genCompilation);
                if (genResult.SourceFileCount > 0)
                    Console.Error.WriteLine($"Scanned {srcDirs.Count} source director{(srcDirs.Count == 1 ? "y" : "ies")} ({genResult.SourceFileCount} .al files)");
                Console.Error.WriteLine($"Generated {genResult.Generated} stub files in {outDir}"
                    + (genResult.SourceFileCount > 0 ? $"  (filtered from {genResult.TotalAvailable} available codeunits)" : ""));
                if (genResult.GeneratedFromSymbolTable > 0)
                    Console.Error.WriteLine($"  {genResult.GeneratedFromSymbolTable} stubs from symbol table (platform/system codeunits without SymbolReference.json)");
                if (genResult.SkippedExisting.Count > 0)
                    Console.Error.WriteLine($"  Skipped {genResult.SkippedExisting.Count} (already exist): {string.Join(", ", genResult.SkippedExisting)}");
                if (genResult.SkippedNotReferenced > 0)
                    Console.Error.WriteLine($"  Skipped {genResult.SkippedNotReferenced} (not referenced in source)");
                if (genResult.SkippedNativeMock > 0)
                    Console.Error.WriteLine($"  Skipped {genResult.SkippedNativeMock} (natively mocked)");
                if (genResult.SkippedNonCodeunit > 0)
                    Console.Error.WriteLine($"  Skipped {genResult.SkippedNonCodeunit} (non-codeunit objects)");
            }
            catch (DirectoryNotFoundException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
            return 0;
        }
        case "--server":
        {
            var server = new AlRunner.AlRunnerServer();
            await server.RunAsync(Console.In, Console.Out);
            return 0;
        }
        case "--dap":
        {
            argIdx++;
            int dapPort = 4711;
            if (argIdx < args.Length && int.TryParse(args[argIdx], out var parsedPort))
            {
                dapPort = parsedPort;
                argIdx++;
            }
            // Collect remaining source paths into pipeline options
            var dapPipelineOptions = new PipelineOptions();
            while (argIdx < args.Length)
            {
                dapPipelineOptions.InputPaths.Add(Path.GetFullPath(args[argIdx]));
                argIdx++;
            }
            Console.Error.WriteLine($"al-runner DAP server listening on 127.0.0.1:{dapPort}");
            Console.Error.WriteLine("Connect your DAP client (e.g. VS Code with vscode-al) to debug.");
            var dapServer = new AlRunner.DapServer(dapPort);
            dapServer.PipelineOptions = dapPipelineOptions;
            // Wire BreakpointHit → stopped event is handled inside DapServer
            await dapServer.RunAsync();
            return 0;
        }
        case "--coverage":
            options.ShowCoverage = true;
            argIdx++;
            break;
        case "--output-json":
            options.OutputJson = true;
            argIdx++;
            break;
        case "--output-junit":
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --output-junit requires a file path argument"); return 1; }
            options.OutputJunitPath = args[argIdx];
            argIdx++;
            break;
        case "--capture-values":
            options.CaptureValues = true;
            argIdx++;
            break;
        case "--iteration-tracking":
            options.IterationTracking = true;
            argIdx++;
            break;
        case "--run":
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --run requires a procedure name"); return 1; }
            options.RunProcedure = args[argIdx];
            argIdx++;
            break;
        case "--run-codeunit":
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --run-codeunit requires a codeunit name"); return 1; }
            options.RunCodeunit = args[argIdx];
            argIdx++;
            break;
        case "--verbose":
        case "-v":
            options.Verbose = true;
            argIdx++;
            break;
        case "--no-telemetry":
            noTelemetry = true;
            argIdx++;
            break;
        case "--strict":
            options.Strict = true;
            argIdx++;
            break;
        case "--test-timeout":
            argIdx++;
            if (argIdx >= args.Length || !int.TryParse(args[argIdx], out var timeoutSec))
            { Console.Error.WriteLine("Error: --test-timeout requires a number (seconds)"); return 1; }
            options.TestTimeoutSeconds = timeoutSec;
            argIdx++;
            break;
        case "--company-name":
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --company-name requires a value"); return 1; }
            AlRunner.Runtime.MockSession.DefaultCompanyName = args[argIdx] ?? string.Empty;
            argIdx++;
            break;
        case "--user-id":
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --user-id requires a value argument"); return 1; }
            options.UserId = args[argIdx];
            argIdx++;
            break;
        case "--init-events":
            options.InitEvents = true;
            argIdx++;
            break;
        case "--test-isolation":
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --test-isolation requires a value (codeunit|method)"); return 1; }
            options.TestIsolation = args[argIdx].ToLowerInvariant() switch
            {
                "codeunit" => AlRunner.TestIsolation.Codeunit,
                "method" => AlRunner.TestIsolation.Method,
                _ => throw new ArgumentException($"Unknown --test-isolation value '{args[argIdx]}'. Use 'codeunit' or 'method'.")
            };
            argIdx++;
            break;
        case "--dep-dlls":
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --dep-dlls requires a directory argument"); return 1; }
            options.DepDllPaths.Add(Path.GetFullPath(args[argIdx]));
            argIdx++;
            break;
        case "--extract-deps":
        {
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --extract-deps requires <src-dir> <out-dir> <app1> [<app2> ...]"); return 1; }
            var edSrcDir = Path.GetFullPath(args[argIdx]);
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --extract-deps requires <src-dir> <out-dir> <app1> [<app2> ...]"); return 1; }
            var edOutDir = Path.GetFullPath(args[argIdx]);
            argIdx++;
            var edApps = new List<string>();
            while (argIdx < args.Length && !args[argIdx].StartsWith("-"))
            {
                edApps.Add(Path.GetFullPath(args[argIdx]));
                argIdx++;
            }
            if (edApps.Count == 0)
            {
                Console.Error.WriteLine("Error: --extract-deps requires at least one <app> path");
                return 1;
            }
            return AlRunner.DepExtractor.ExtractDeps(edSrcDir, edApps, edOutDir);
        }
        case "--compile-dep":
        {
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --compile-dep requires <app-path> <output-dir>"); return 1; }
            var cdAppPath = Path.GetFullPath(args[argIdx]);
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --compile-dep requires <app-path> <output-dir>"); return 1; }
            var cdOutDir = Path.GetFullPath(args[argIdx]);
            argIdx++;
            var cdPkgPaths = new List<string>();
            while (argIdx < args.Length)
            {
                if (args[argIdx] == "--packages" && argIdx + 1 < args.Length)
                {
                    argIdx++;
                    cdPkgPaths.Add(Path.GetFullPath(args[argIdx]));
                    argIdx++;
                }
                else
                {
                    argIdx++;
                }
            }
            return AlRunner.DepCompiler.CompileDep(cdAppPath, cdOutDir, cdPkgPaths);
        }
        case "--stubs":
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --stubs requires a directory argument"); return 1; }
            options.StubPaths.Add(Path.GetFullPath(args[argIdx]));
            argIdx++;
            break;
        case "--packages":
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --packages requires a directory argument"); return 1; }
            options.PackagePaths.Add(Path.GetFullPath(args[argIdx]));
            argIdx++;
            break;
        case "-e":
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: -e requires an argument"); return 1; }
            options.InlineCode = args[argIdx];
            argIdx++;
            break;
        default:
            options.InputPaths.Add(args[argIdx]);
            argIdx++;
            break;
    }
}

var pipeline = new AlRunner.AlRunnerPipeline();
AlRunner.PipelineResult result;
try
{
    result = pipeline.Run(options);
}
catch (Exception ex)
{
    await AlRunner.TelemetryReporter.TryReportAsync(ex, options.OutputJson, noTelemetry);
    Console.Error.WriteLine($"Fatal: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

// Forward captured output to actual console
if (!string.IsNullOrEmpty(result.StdOut))
    Console.Write(result.StdOut);
if (!string.IsNullOrEmpty(result.StdErr))
    Console.Error.Write(result.StdErr);

// Report pipeline gaps (rewriter, compilation, runtime) via telemetry
await AlRunner.TelemetryReporter.TryReportPipelineGapsAsync(
    result.Tests, options.OutputJson, noTelemetry,
    rewriterErrors: result.RewriterErrors,
    compilationErrors: result.CompilationErrors);

return result.ExitCode;

void PrintGuide()
{
    Console.WriteLine("""
## AL Runner Test Guide

Use this guide when writing AL unit tests that run with al-runner — a standalone
test executor that needs no BC service tier, Docker, SQL Server, or license.

### What al-runner supports

- Pure-logic codeunits (arithmetic, string ops, record CRUD, enums, options)
- In-memory table store: Insert, Modify, Get, Delete, FindSet, FindFirst, FindLast, Next
- Temporary records — `Record "X" temporary` uses an isolated in-memory store per variable,
  separate from non-temporary records. `IsTemporary()` returns the correct value.
  `RecordRef.Open(tableId, true)` opens a temporary RecordRef.
- TransferFields, CountApprox, Consistent (no-op), FieldActive (true), AddLink/HasLinks/DeleteLinks
- FieldError(Field) / FieldError(Field, Text) — raise a field-level error message
  formatted as "<FieldCaption> <Message> in <TableCaption>: <PK>", catchable via asserterror
- TestField(Field) / TestField(Field, Value) — assert field is non-empty or equals a value.
  Also supports ErrorInfo overloads: TestField(Field, ErrorInfo) and TestField(Field, Value, ErrorInfo).
  The ErrorInfo is accepted as error context but the assertion logic is identical to the non-ErrorInfo variants.
- WritePermission/ReadPermission (true), SetPermissionFilter (no-op), LockTable (no-op)
- Composite primary keys, sort ordering (SetCurrentKey / SetAscending), CurrentKey, Ascending
- SETRANGE / SETFILTER filtering (=, <>, <, <=, >, >=, wildcards, OR via |)
- GetFilter(field), GetFilters, HasFilter — return active filter expressions
- GetPosition() / SetPosition(text) — serialise/restore primary-key cursor position;
  round-trip guaranteed; SetPosition with unparseable or non-existent position throws
- Record marking: `Rec.Mark(true/false)`, `Rec.Mark()` getter, `Rec.MarkedOnly(true)`,
  `Rec.ClearMarks()`. Marks are per record-variable; MarkedOnly gates FindSet/FindFirst/
  FindLast/Next/Count/IsEmpty to the marked subset.
- FlowField CalcFormula — CalcFields evaluates exist(), count(), sum(), and lookup() formulas
  against the in-memory table store. Where-clause conditions support field() and const() references.
- Cross-codeunit dispatch (Codeunit.Run, Codeunit.Run(id, Rec) with record parameter, direct codeunit variable calls)
- AL interfaces for dependency injection
- `asserterror` blocks + `GetLastErrorText()`
- ErrorInfo type — `ErrorInfo.Create()`, set `Message`, `DetailedMessage`, `FieldNo`, `Collectible`, etc.
  `Error(ErrorInfo)` throws with the message text. Collectible errors: mark `ErrorInfo.Collectible := true`
  and wrap the calling procedure with `[ErrorBehavior(ErrorBehavior::Collect)]` to collect instead of throw.
  `HasCollectedErrors()`, `GetCollectedErrors(clear)`, `ClearCollectedErrors()`, `IsCollectingErrors()` all work.
- Assert codeunit: AreEqual, AreNotEqual, IsTrue, IsFalse, ExpectedError, RecordIsEmpty, etc.
- OnValidate triggers on table fields
- Table procedures (custom procedures on table objects)
- IsolatedStorage (in-memory key-value store: Set, Get, Delete, Contains)
- TextBuilder (Append, AppendLine, ToText)
- Dialog variable (Open, Update, Close — all no-ops in standalone mode)
- RecordRef / FieldRef — Open, Close, Field(n).Value get/set, Insert, Modify,
  Delete, DeleteAll, FindSet+Next iteration, GetTable/SetTable, SetRange/SetFilter,
  RecRef := OtherRecRef assignment, SetLoadFields (no-op), Mark/MarkedOnly/ClearMarks,
  Rename, FieldExists, FieldCount, HasFilter, GetFilters, GetPosition,
  SetPosition, Ascending (get/set), ChangeCompany (no-op), ModifyAll, CurrentCompany,
  FieldRef.GetFilter, FieldRef.GetRangeMin, FieldRef.GetRangeMax, FieldRef.Record,
  KeyCount, KeyIndex(n), CurrentKeyIndex,
  RecRef.Name (real table name from AL source metadata),
  SystemIdNo, SystemCreatedAtNo, SystemCreatedByNo, SystemModifiedAtNo,
  SystemModifiedByNo (return well-known BC system field numbers).
  FieldRef also supports: IsEnum, EnumValueCount(), GetEnumValueName(index),
  GetEnumValueCaption(index), GetEnumValueOrdinal(index) — enum introspection
  using registered enum metadata. CalcSum() — sums a decimal field across all
  filtered records; result is available via the next Value read.
- KeyRef — FieldCount, FieldIndex(n), Record, Active (basic key metadata via KeyRef variable)
- Field metadata — Record.FieldCaption, Record.TableCaption, Record.TableName,
  FieldRef.Name, FieldRef.Caption, FieldRef.Type, FieldRef.Length return real values
  parsed from AL source table declarations (Caption property, field type, Text[N]/Code[N]
  length). Captions with embedded apostrophes (e.g. 'Vendor''s Name') are unescaped.
  Falls back to stub defaults for tables not parsed from source.
- JSON types: JsonObject, JsonArray, JsonToken, JsonValue — Add, Get, Contains,
  Remove, Replace, Count, WriteTo, ReadFrom, SelectToken, AsValue, AsText, AsInteger, etc.
- BLOB / InStream / OutStream — CreateInStream/CreateOutStream, HasValue, ReadText/WriteText
  (in-memory byte buffer; sufficient for text round-trip tests).
  InStream also supports Length, Position, ResetPosition for byte-level stream operations.
- HttpContent.WriteFrom(InStream) / ReadAs(var InStream) — compile and run in standalone
  mode; WriteFrom reads the in-memory stream bytes as text content; ReadAs returns a
  MockInStream populated with the stored content (round-trip from WriteFrom).
- HttpClient, HttpRequestMessage, HttpResponseMessage, HttpContent, HttpHeaders — all
  HTTP types are replaced with in-memory mocks. HttpContent.WriteFrom(Text)/ReadAs(var Text)
  round-trips text. HttpResponseMessage default status is 200. HttpHeaders supports
  Add/Contains/Remove. HttpClient.Send/Get/Post/Put/Delete/Patch throw
  NotSupportedException (use AL interface injection for HTTP-dependent code).
- File dialog stubs: UploadIntoStream (4-arg, 5-arg, 6-arg), DownloadFromStream (4 overloads) —
  all return false in standalone mode (no client UI). Compile and run without error.
- Library - Variable Storage (codeunit 131004) — Enqueue, DequeueText, DequeueInteger,
  DequeueDecimal, DequeueBoolean, DequeueDate, DequeueVariant, AssertEmpty, Clear, IsEmpty
- TestPage navigation — Caption, Editable, ValidationErrorCount(), First(), Last(), Previous(),
  GoToKey(), GoToRecord(), Next(), New(), Expand(), GetPart(), GetRecord(),
  Filter.SetFilter()/GetFilter(), field AsDecimal(), Enabled() (stubs; return true/no-op
  unless otherwise noted; Next()/Last()/Previous() return false, ValidationErrorCount() returns 0)
- TestPart (subpage accessed via TestPage.PartName) — all navigation and state methods:
  Caption, Editable, Enabled(), Visible(), First(), Last(), Next(), Previous(),
  New(), GoToRecord(), GoToKey(), Expand(), IsExpanded(), ValidationErrorCount(),
  GetValidationError(n), FindFirstField(field,value), FindNextField(field,value),
  FindPreviousField(field,value), GetField() (stubs; First/GoToRecord/GoToKey/FindFirstField
  return true; all others return false/0/empty; parts share MockTestPageHandle with TestPage;
  TestPart.Prev removed in BC runtime 13.0 / BC 26+ — not supported)
- TestRequestPage.GetDataItem(name) — BC emits GetDataItem("Report{N}DataItem{I}TableView")
  when AL accesses a report data-item property (e.g. RequestPage.Customer.SetFilter(Id,'1..10')).
  Returns a per-name MockTestPageFilter; supports SetFilter/GetFilter/SetCurrentKey/Ascending.
- Request page handler dispatch — [RequestPageHandler] intercepts Report.RunRequestPage() calls
- Report handler dispatch — [ReportHandler] intercepts Report.Run()/Report.RunModal() and
  report variable .Run()/.RunModal() calls. Handler receives TestRequestPage parameter.
  Reports run silently when no handler is registered.
- Report variables — SetTableView(), Run(), RunModal(), RunRequestPage() (dispatches handler),
  UseRequestPage(false) suppresses request page handler.
  CurrReport.Skip() and CurrReport.Break() are available inside report triggers.
  Report rendering and layout evaluation are not available.
  Report label fields and properties are preserved (accessible in generated code).
- Format() / Evaluate() type conversions, including picture strings:
  - Date tokens: `<Year4>`, `<Month,2>`, `<Day,2>`, `<Hours24,2>`, `<Minutes,2>`, `<Seconds,2>`
  - Decimal tokens: `<Precision,min:max>` (round/pad decimals), `<Standard Format,N>` (N=0 default, N=1 integer)
  - Multi-token decimal picture strings (e.g. `<Precision,2:2><Standard Format,0>`) — Precision wins when both are present
  - Time picture strings applied to Time variables (e.g. `Format(T, 0, '<Hours24,2>:<Minutes,2>')`)
- Session API: StartSession (dispatches codeunit synchronously, returns true), StopSession (no-op),
  IsSessionActive (returns false), Sleep (no-op)
- Built-in session functions: TenantId, SerialNumber (return empty string)
- CompanyName() — configurable: defaults to empty string, overridable via the `--company-name <name>`
  CLI flag, and AL tests can set it at runtime by calling
  `codeunit 131100 "AL Runner Config"` → `SetCompanyName(Name: Text)`.
  Reset back to the CLI default between tests.
- UserId() — configurable via `--user-id <value>` CLI flag (default: "TESTUSER")
- System & Session utilities:
  - Session.LogMessage() — no-op (telemetry not available without service tier)
  - Session.ApplicationArea() — returns empty string
  - Session.GetExecutionContext() / GetModuleExecutionContext() — returns ExecutionContext.Normal
  - Database.LockTimeout(bool) — no-op (no real database)
  - CompanyProperty.DisplayName() / UrlName() / ID() — configurable via AL Runner Config:
      SetCompanyDisplayName(Text) / GetCompanyDisplayName(): Text
      SetCompanyUrlName(Text) / GetCompanyUrlName(): Text
      SetCompanyId(Guid) / GetCompanyId(): Guid
    Defaults: "My Company" / "My%20Company" / fixed non-empty GUID; reset between tests.
  - ProductName.Full() / Short() / Marketing() — returns real BC product names
  - RoundDateTime(dt [, precision] [, direction]) — rounds DateTime with ms precision;
    direction: '>' (up), '<' (down), '=' (nearest, default). Default precision 1000ms.
  - NormalDate(date) / ClosingDate(date) — work natively via BC types
- GlobalLanguage() — returns and sets an in-memory language ID (default 1033 = ENU); reset between tests
- Partial compilation (skips unsupported object types like XMLport)
- Coverage reporting via `--coverage` (statement-level, outputs cobertura.xml)
- Fluent builder pattern: `exit(this)` in codeunit methods returning `Codeunit "Self"`
- Test handler functions: [ConfirmHandler], [MessageHandler], [ModalPageHandler], [RequestPageHandler],
  [SendNotificationHandler]
  - ConfirmHandler intercepts Confirm() calls, receives question text, sets reply
  - MessageHandler intercepts Message() calls, receives message text
  - ModalPageHandler intercepts Page.RunModal() calls, receives a TestPage handle,
    can set field values and invoke OK/Cancel actions; returns FormResult to caller
  - RequestPageHandler intercepts Report.RunRequestPage() calls
  - SendNotificationHandler intercepts Notification.Send() calls, receives notification
    (Message, GetData, HasData, Id accessible in handler)
- Query — single-dataitem queries work in-memory: Open reads from the mock table
  store, Read iterates the result set, Close releases it. SetFilter/SetRange
  filter rows; TopNumberOfRows limits the count. Column values are returned
  via GetColumnValueSafe. Multi-dataitem (JOIN) and aggregation are not supported.
  SaveAsCsv/SaveAsXml/SaveAsJson/SaveAsExcel still throw NotSupportedException.
- XmlPort variables — declaring XmlPort variables compiles; Source/Destination
  properties and Invoke() work without error. Import/Export (instance and static)
  throw NotSupportedException with actionable guidance.
  Use AL interface injection to abstract XmlPort I/O for testing.
- Notification — Message, Send, Recall (returns bool), SetData/GetData/HasData, AddAction, Id, Scope, Clear.
  Send dispatches to [SendNotificationHandler] if registered; otherwise no-op.
  Recall() returns true (standalone mode; no real recall possible).
  Clear(N) resets Message, Id, Scope, data, and actions to defaults.
  Data store is in-memory; Id auto-generates a Guid.
- BigText — MockBigText replaces NavBigText. AddText, GetSubText, TextPos, Length
  all work via in-memory StringBuilder. Note: TextPos is 1-based in AL.
- TaskScheduler — CreateTask (dispatches codeunit synchronously, invokes
  failureCodeunitId on exception, returns Guid), TaskExists (returns false),
  CancelTask (no-op), SetTaskReady (no-op)
- DataTransfer — SetTables, AddFieldValue, AddConstantValue, AddJoin, AddSourceFilter,
  CopyFields, CopyRows (all no-ops; requires real database for actual transfer)

### What al-runner does NOT support

- System Application business-logic codeunits are not reimplemented by al-runner.
  Auto-generated blank shells may let AL compile, but they do not provide real
  behaviour. Use a test-local stub / bring-your-own-stub pattern when tests need
  behaviorful dependencies such as image processing, file management, crypto,
  email, or similar SA services.
- Pages, Reports — stub them via `--stubs <dir>` or inject via AL interface
- XmlPort I/O (Import/Export) — XmlPort variables compile and properties work,
  but Import/Export require the BC service tier. Use AL interface injection to
  abstract XmlPort dependencies for testing.
- Query JOIN/aggregation — multi-dataitem queries (JOINs) and aggregation
  (Sum, Count, Average, Min, Max) are not supported. Single-dataitem queries work.
- HTTP / REST calls — HttpClient.Send/Get/Post etc. throw NotSupportedException;
  inject actual HTTP dependencies via AL interface. HttpContent text round-trip,
  HttpHeaders, HttpResponseMessage properties, and HttpRequestMessage construction
  all work without the service tier.
- Event subscribers — Custom IntegrationEvent/BusinessEvent dispatch works with
  IncludeSender support. Implicit DB trigger events (OnBefore/AfterInsert/Modify/
  Delete/Validate) fire. Subscriber parameters are forwarded. BindSubscription/
  UnbindSubscription work. Remaining gaps: OnBefore/AfterRenameEvent not yet fired;
  ModifyAll/DeleteAll skip per-row events.
- StrMenu is not supported
- Filter groups (FilterGroup)

### Writing a compatible test codeunit

1. Use `Subtype = Test` on the codeunit
2. Reference `Assert` as `Codeunit "Library Assert"` or `Codeunit Assert` (both are supported).
   Both the runner's built-in stub (ID 130) and BC's real ID (130002) are routed to MockAssert.
3. Reference `Library - Variable Storage` as `Codeunit "Library - Variable Storage"` for
   passing values between test setup and handler functions (Enqueue/DequeueText/etc.)
4. The following BC test toolkit codeunits are built-in (auto-loaded, no stubs needed):
   - `Library Assert` (130 / 130002) — AreEqual, IsTrue, IsFalse, ExpectedError, etc.
   - `Library - Variable Storage` (131004) — Enqueue/Dequeue for handler communication
   - `Any` (130500) — random test data generation (IntegerInRange, AlphanumericText, etc.)
   - `Library - Random` (130440) — pseudo-random numbers/dates/text (RandInt, RandDec, etc.)
   - `Library - Utility` (131003) — GenerateGUID, GenerateRandomCode/Code20/Text
   - `Library - Test Initialize` (132250) — integration event publishers for test setup hooks
5. Mark each test procedure with `[Test]`
5. Tests must be self-contained: insert test data, call logic, assert results
6. Use `asserterror` + `Assert.ExpectedError` for error path testing
7. For external dependencies (mail, HTTP, pages, XmlPort I/O), define an AL interface and
   inject a mock implementation in the test

### Stubs workflow

Stubs are empty or simplified AL object files (often codeunits, but any AL object
type) you provide so that al-runner can compile your source even when it depends on
ISV or BC objects that are not natively mocked by the runner.

**When stubs help vs when they don't:**

| Symptom | Root cause | Fix |
|---------|------------|-----|
| "Tip: use --stubs" in output | Compilation gap — runner excluded a file it couldn't compile | Generate and add stubs |
| AL compiler error: object not found | Missing symbol reference | Add --packages or generate a stub |
| `NotSupportedException` at runtime | Missing mock (runtime gap) | Inject via AL interface or skip |
| Test assertion fails | Logic bug | Fix the production code |

Stubs only fix **compilation** gaps — missing type/object symbols the AL compiler
needs to build the assembly. They cannot fix **runtime** gaps where al-runner has
no mock for a BC platform operation. For runtime gaps, inject the dependency via an
AL interface and provide a mock implementation in the test.

**Iterative workflow:**

Step 1 — Run normally and note the gaps:
```
al-runner ./src ./test
```
If a `.alpackages` directory exists in or near your source paths, al-runner
auto-detects it — no `--packages` flag required. Pass `--packages <dir>`
explicitly only when the packages are in a non-standard location.

If you see `Tip: use --stubs to provide stub AL files for unsupported dependencies`,
one or more objects were excluded from compilation. Stubs can restore them.

Step 2 — Generate stubs for only the codeunits your source references:
```
al-runner --generate-stubs .alpackages ./stubs ./src ./test
```
This reads `SymbolReference.json` from each `.app` in `.alpackages`, filters to
codeunits (and procedures) your source actually calls, and emits one `.al` stub
file per codeunit into `./stubs/`. Existing files are never overwritten, so
hand-edited stubs are preserved.

Step 3 — Review and edit the generated stubs as needed:
```
./stubs/Cod70100.ISVIntegrationMgt.al   ← one file per codeunit
```
Each generated stub has correct procedure signatures (parameter types, var,
return types) with minimal bodies (`exit(false)`, `exit('')`, etc.). For most
compilation gaps this is sufficient. If a test actually exercises the stubbed
procedure you may need to fill in a real return value.

Step 4 — Re-run with stubs:
```
al-runner --packages .alpackages --stubs ./stubs ./src ./test
```
The runner compiles your stubs alongside the source. Conflicting symbol packages
are excluded automatically.

Step 5 — Iterate. If tests still fail at runtime with `NotSupportedException`,
the codeunit uses an unsupported BC feature (Page, HTTP, etc.). Either:
- Skip that codeunit (don't include it in your test run), or
- Inject the dependency via an AL interface so the runner can use a mock.

**What `--generate-stubs` produces:**

- One `.al` file per codeunit found in the packages, named `CodNNNNN.Name.al`
  (Name is sanitized: only letters, digits, `-`, and `_` are kept; spaces and
  other punctuation are removed — e.g. "ISV Integration Mgt." → `ISVIntegrationMgt`)
- Correct procedure signatures: all parameters, `var` modifiers, `Record "X"`,
  `Enum "X"`, and return types
- Default `exit(...)` bodies (false for Boolean, 0 for Integer/Decimal, '' for Text/Code)
- Codeunits already natively mocked by al-runner (Library Assert 130/130002,
  Library - Variable Storage 131004, Any 130500, Library - Random 130440,
  Library - Utility 131003, Library - Test Initialize 132250) are skipped automatically
- Existing files in the output dir are never overwritten (re-run is safe)

**Maintaining stubs over time:**

- Keep the `./stubs/` directory in source control alongside your test code
- When a dependency package is updated, re-run `--generate-stubs` — new codeunits
  get new stub files; existing hand-edited files are left untouched
- Use source-filtered generation (`--generate-stubs .alpackages ./stubs ./src ./test`)
  rather than unfiltered (`--generate-stubs .alpackages ./stubs`) to keep the stub
  directory lean — only the procedures your source actually calls are emitted

**Example stub for an unsupported codeunit** (hand-written; generated stubs default
to `exit(false)` for Boolean — change return values as needed for your tests):
```al
codeunit 70100 "ISV Integration Mgt."
{
    procedure DoSomething(): Boolean
    begin
        exit(true);
    end;

    procedure GetStatus(ItemNo: Code[20]): Text[50]
    begin
        exit('');
    end;
}
```

### Automatic auto-stubs (symbol-table generation)

Even without `--stubs` or `--generate-stubs`, al-runner automatically generates stubs
for any referenced codeunit or table that has no compiled class. After the main AL
compilation, the runner:

1. Scans the generated C# for all referenced object IDs (codeunits, tables)
2. Queries the BC compiler's symbol table for each missing object
3. Generates AL stubs with **full method signatures** — correct parameter names, types,
   `var` modifiers, and return types — extracted from the symbol table metadata
4. For tables, emits the real primary-key fields with their declared type, length,
   and `AutoIncrement` setting so `Insert(true)` auto-assigns IDs the same way
   base BC does. Non-PK fields are not emitted; access them by id (`FieldRef`).
5. Compiles them in a second BC pass to produce proper scope classes with correct
   member IDs and default return values

This means calling methods on dependency objects (e.g., LibraryERM, Rest Client,
No. Series) returns proper typed defaults (0 for Integer, '' for Text, false for
Boolean) instead of null. The second pass only runs when stubs are needed — zero
overhead for self-contained tests.

The console output reports exactly which objects were auto-stubbed:
```
Auto-stubbed 5 dependency object(s) — methods return defaults:
  Codeunits: 5 (4 compiled via BC, 1 fallback)
```

When a test fails and the stack trace involves an auto-stubbed codeunit, the output
annotates it with guidance:
```
  ⚠ Called auto-stubbed codeunit 131300 "Library - ERM" (methods return defaults)
  Compile real implementations: al-runner --compile-dep <app>.app .deps --packages .alpackages/
```

**Escalation path** — when auto-stubs are not enough (e.g., your tests depend on real
logic from a dependency codeunit):

1. Use `--compile-dep` to compile the dependency .app to a rewritten DLL:
   ```
   al-runner --compile-dep Base.app .deps --packages .alpackages
   ```
2. Load the compiled DLL at runtime:
   ```
   al-runner --dep-dlls .deps ./src ./test
   ```

### Example test structure

```al
table 50100 "My Item"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Description"; Text[100]) { }
        field(3; "Unit Price"; Decimal) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

codeunit 50100 "Price Calculator"
{
    procedure CalcDiscount(Price: Decimal; Pct: Decimal): Decimal
    begin
        if Pct < 0 then
            Error('Discount cannot be negative');
        exit(Price - (Price * Pct / 100));
    end;
}

codeunit 50200 "Price Calculator Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Calc: Codeunit "Price Calculator";

    [Test]
    procedure TestCalcDiscount()
    begin
        Assert.AreEqual(90, Calc.CalcDiscount(100, 10), 'ten pct off 100');
        Assert.AreEqual(100, Calc.CalcDiscount(100, 0), 'zero discount');
    end;

    [Test]
    procedure TestNegativeDiscountErrors()
    begin
        asserterror Calc.CalcDiscount(100, -5);
        Assert.ExpectedError('Discount cannot be negative');
    end;
}
```

### CLI usage

```
al-runner ./src ./test                                    # run tests
al-runner --coverage ./src ./test                         # run with coverage report
al-runner --packages .alpackages ./src ./test             # with dependency symbols
al-runner --packages .alpackages --stubs ./stubs ./src ./test  # with stubs
al-runner -v ./src ./test                                 # verbose output
al-runner --dump-rewritten ./src ./test                   # inspect generated C#
al-runner --output-json ./src ./test                      # machine-readable JSON output
al-runner --output-junit results.xml ./src ./test         # JUnit XML report for CI
al-runner --run TestMyProcedure ./src ./test              # run a single test by name
al-runner --capture-values ./src ./test                   # capture variable values after each test
al-runner --iteration-tracking ./src ./test                   # track loop iterations
al-runner --server                                         # long-running JSON-RPC daemon (stdin/stdout)
al-runner --generate-stubs .alpackages ./stubs             # scaffold stubs from .app packages (all codeunits)
al-runner --generate-stubs .alpackages ./stubs ./src ./test  # only stubs referenced in source
al-runner --init-events ./src ./test                          # fire OnCompanyInitialize once, snapshot as DB baseline
al-runner --compile-dep MyDep.app .deps --packages .alpackages  # compile dependency .app to DLL
al-runner --dep-dlls .deps ./src ./test                         # run with pre-compiled dependency DLLs
```

### --init-events: lifecycle event pre-seeding

Pass `--init-events` when your extension has `[EventSubscriber]` methods that listen
to BC system lifecycle events and need to run their setup logic before tests run.

Events fired **once per runner invocation** (at startup, before the first test):
- `OnCompanyInitialize` from Codeunit 27 "Company-Initialize"
- `OnCompanyInitialize` from Codeunit 2 "Install"
- `OnInstallAppPerDatabase` / `OnInstallAppPerCompany` from the BC install dispatcher (2000000010)

After the subscribers finish, the runner captures a snapshot of the database state
and uses that snapshot as the baseline for every test. Test isolation (codeunit or
method) sits on top of the baseline: each isolation reset restores the snapshot
rather than re-firing subscribers. This matches BC's model where company-initialize
data persists across tests, and avoids the N× perf hit of re-running subscribers
per codeunit.

Scope note: SingleInstance codeunit state is **not** part of the baseline — init
subscribers must seed tables / IsolatedStorage, not codeunit fields. SingleInstance
codeunits are always reset between test codeunits, matching BC's session model.

The publisher codeunits (27, 2) do not need to be present in the assembly — al-runner
will look up subscribers by reflection and dispatch to them directly. Missing publisher
codeunits are silently skipped (graceful fallback, not a crash).

Use case: extensions like Cash365 that use `[EventSubscriber(ObjectType::Codeunit, 27, 'OnCompanyInitialize', '', false, false)]`
to create required setup records (number series, posting groups, etc.) before any
business logic runs. With `--init-events`, those subscribers fire automatically before
each test, eliminating the need to manually call setup procedures in every test.

```al
// This subscriber fires automatically with --init-events:
[EventSubscriber(ObjectType::Codeunit, 27, 'OnCompanyInitialize', '', false, false)]
local procedure OnCompanyInitialize()
begin
    // Create required setup records for your extension
    InsertDefaultSetup();
end;
```

### Machine-readable output (--output-json)

Produces a JSON object with per-test results including `name`, `status`
(pass/fail/error), `durationMs`, `message`, `stackTrace`, and `alSourceLine`
(AL source line where an error occurred). Also includes summary counts:
`passed`, `failed`, `errors`, `total`, `exitCode`. Suitable for integrating
al-runner into editors, CI systems, or other tooling.

**Status values:**
- `"pass"` — test ran and all assertions passed.
- `"fail"` — test ran and an assertion failed (real bug in your AL code).
- `"error"` — test could not run due to a tooling/configuration issue (e.g., a
  dependency codeunit was excluded from compilation, or an unsupported feature
  was encountered). This is NOT a test failure; fix the configuration or add stubs.

**`compilationErrors` field** (when present): a list of source files that were
excluded from the Roslyn compilation, each with the C# errors that caused
exclusion. Tests that call into excluded types produce `status: "error"`.
The `errors` count includes these tests. The `failed` count does not.

### JUnit XML output (--output-junit <path>)

Writes a standard JUnit XML file alongside normal console output:

```
al-runner --output-junit results.xml --packages .alpackages ./src ./test
```

GitHub Actions, Azure DevOps, and GitLab CI natively render JUnit XML as test
annotations, summaries, and trend graphs. Combined with `--coverage` (Cobertura),
this completes the CI integration story:
- Cobertura → coverage tab (via `--coverage`)
- JUnit → test results tab (via `--output-junit`)

**Element mapping:**
- `<failure>` — real assertion failure (a bug in your AL code).
- `<error>` — runner limitation or configuration gap (not a code bug).
- Tests are grouped by codeunit name as `<testsuite>` elements.
- Time values are in seconds.

### Server mode (--server)

Starts a long-running process that reads JSON-RPC requests from stdin and
writes responses to stdout, one JSON object per line. The server keeps the
transpiler warm and caches compiled assemblies by source file hash, so
subsequent runs of the same files reuse the cached assembly (`cached:true`
in the response).

Commands:
- `{"command":"runTests","sourcePaths":["./src","./test"]}` — run tests
- `{"command":"shutdown"}` — exit cleanly

Optional fields: `packagePaths`, `stubPaths`.

### Exit codes

| Code | Meaning |
|------|---------|
| 0 | All tests passed |
| 1 | Test assertion failures (real bugs in the code) or usage error |
| 2 | Runner limitations only (not with --strict; use in CI to catch regressions) |
| 3 | AL compilation error (the AL source itself does not compile) |

Use `--strict` in CI to treat runner limitations as failures:

```bash
al-runner --strict --packages .alpackages ./src ./test
# Exit 0 = all pass, Exit 1 = any failure (including runner limitations)
```

Without `--strict`, exit code 2 indicates runner limitations only:

```bash
al-runner --packages .alpackages ./src ./test
rc=$?
if [ $rc -eq 2 ]; then
  echo "Runner limitations only — not a build failure"
  exit 0
elif [ $rc -ne 0 ]; then
  exit $rc
fi
```

### Tips for AI agents

- When a test fails with `NotSupportedException`, the codeunit uses an unsupported
  feature. Create a stub or inject the dependency via an AL interface.
- Run `al-runner --dump-rewritten` to inspect the generated C# if you need to debug
  a transpilation issue.
- al-runner resets all in-memory tables between test methods — no cleanup needed.
- If al-runner says FAIL, the failure is real. If it says PASS, the direct logic is
  correct. Note: ModifyAll/DeleteAll do not fire per-row events, and OnRename events
  are not yet supported — the full BC pipeline catches these edge cases.

### Event subscribers

al-runner supports event subscribers for custom and implicit DB events:

**Custom events** — `[IntegrationEvent]` and `[BusinessEvent]` publishers dispatch
to `[EventSubscriber]` methods. Subscriber parameters (including `var` / `ByRef`)
are forwarded and mutations propagate back to the publisher scope.

**IncludeSender** — When `[IntegrationEvent(true, false)]` or `[BusinessEvent(true)]`
is used, the publishing codeunit instance is passed as the first subscriber parameter
(`sender: Codeunit "Publisher"`). Subscribers can read/write publisher state.

**Implicit DB events** — `OnBeforeInsertEvent`, `OnAfterInsertEvent`,
`OnBeforeModifyEvent`, `OnAfterModifyEvent`, `OnBeforeDeleteEvent`,
`OnAfterDeleteEvent`, `OnBeforeValidateEvent`, `OnAfterValidateEvent` fire from
record operations. The `Rec` and `xRec` references are passed to subscribers.
Subscribers may declare `var RunTrigger: Boolean` (BC compiles this as `ByRef<bool>`);
al-runner automatically coerces the plain `bool` into `ByRef<bool>` so the
subscriber is invoked correctly.

**Manual binding** — Codeunits with `EventSubscriberInstance = Manual` only fire
after `BindSubscription(Sub)`. Call `UnbindSubscription(Sub)` to stop. Bindings
are reset between tests.

**ConfirmHandler / MessageHandler / ModalPageHandler** — Supported via
`HandlerRegistry` for test codeunits. Register handlers in the `[Test]` attribute's
`HandlerFunctions` property.

### Reporting issues

al-runner is under active development and targets broad AL language compatibility.
If you encounter AL language features that fail to compile, produce unexpected
runtime errors, or behave differently from a real BC service tier, that is likely
a gap in the runner rather than a problem with the AL code.

When this happens:
1. Check the exit code — code 2 means the runner hit a known limitation (with
   `--strict`, this becomes code 1); code 1 or 3 means a real failure or compile
   error that may still be a runner bug.
2. Try a workaround if one is available (stub file, AL interface injection, or
   simplifying the affected AL construct).
3. Report the issue at https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues
   with the AL construct that fails and the error output. This helps prioritise
   which gaps to close next.

Do not tell users their AL code needs to be restructured to work around a runner
limitation. Restructuring is a last resort; the preferred fix is improving the
runner.
""");
}

// ===========================================================================
// Log helper: info is verbose-only, warn/error always shown
// ===========================================================================
public static class Log
{
    public static bool Verbose { get; set; }
    public static bool HasStubs { get; set; }
    public static void Info(string msg) { if (Verbose) Console.Error.WriteLine(msg); }
    public static void Warn(string msg) => Console.Error.WriteLine($"Warning: {msg}");
    public static void Error(string msg) => Console.Error.WriteLine($"Error: {msg}");
}

public static class Timer
{
    private static readonly System.Diagnostics.Stopwatch _total = System.Diagnostics.Stopwatch.StartNew();
    private static readonly System.Diagnostics.Stopwatch _stage = new();
    private static readonly List<(string Name, long Ms)> _stages = new();

    public static void StartStage(string name)
    {
        _stage.Restart();
    }

    public static void EndStage(string name)
    {
        _stage.Stop();
        _stages.Add((name, _stage.ElapsedMilliseconds));
    }

    public static void Print()
    {
        _total.Stop();
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Timing: {_total.ElapsedMilliseconds}ms total");
        foreach (var (name, ms) in _stages)
            Console.Error.WriteLine($"  {name,-30} {ms,6}ms");
    }
}

// ===========================================================================
// AL Transpiler: AL source -> C# source string (supports multi-object)
// ===========================================================================
public static class AlTranspiler
{
    /// <summary>
    /// The last Compilation object from TranspileMulti. Allows querying the BC
    /// compiler's symbol table for method signatures on dependency objects
    /// (codeunits, tables, etc.) that are referenced but not compiled from source.
    /// </summary>
    public static Compilation? LastCompilation { get; private set; }

    /// <summary>
    /// Format a BC AL diagnostic, prepending the source filename when a tree→path map is provided.
    /// Falls back to d.ToString() if the tree cannot be matched.
    /// </summary>
    private static string FormatAlDiagnostic(Diagnostic d, Dictionary<SyntaxTree, string>? treeToPath)
    {
        var s = d.ToString();
        if (treeToPath == null || treeToPath.Count == 0) return s;
        if (d.Location.SourceTree is SyntaxTree tree && treeToPath.TryGetValue(tree, out var path))
            return $"[{Path.GetFileName(path)}] {s}";
        return s;
    }

    /// <summary>
    /// Transpile a single AL source string (backward compat).
    /// </summary>
    public static string? Transpile(string alSource)
    {
        var result = TranspileMulti(new List<string> { alSource });
        if (result == null || result.Count == 0) return null;
        return result[0].Code;
    }

    /// <summary>
    /// Transpile multiple AL source strings together in a single compilation.
    /// Returns a list of (ObjectName, CSharpCode) pairs, one per emitted object.
    /// </summary>
    /// <param name="alSources">AL source code strings to transpile.</param>
    /// <param name="packagePaths">Directories containing .app files for symbol references (optional).</param>
    /// <param name="inputPaths">Input directories/file paths for auto-discovery of .alpackages (optional).</param>
    public static List<(string Name, string Code)>? TranspileMulti(
        List<string> alSources,
        List<string>? packagePaths = null,
        List<string>? inputPaths = null,
        SyntaxTreeCache? treeCache = null,
        List<string?>? sourceFilePaths = null)
    {
        // Parse all sources into syntax trees
        var syntaxTrees = new List<SyntaxTree>();
        bool hasErrors = false;

        var parsedResults = new (SyntaxTree tree, List<Diagnostic> diags)[alSources.Count];
        Parallel.For(0, alSources.Count, i =>
        {
            SyntaxTree tree;
            if (treeCache != null && sourceFilePaths != null && i < sourceFilePaths.Count && sourceFilePaths[i] != null)
                tree = treeCache.GetOrParse(sourceFilePaths[i]!, alSources[i]);
            else
                tree = SyntaxTree.ParseObjectText(alSources[i]);
            var diags = tree.GetDiagnostics().ToList();
            parsedResults[i] = (tree, diags);
        });

        // Build a map from tree object identity to source file path for diagnostic formatting.
        Dictionary<SyntaxTree, string>? treeToPath = null;
        if (sourceFilePaths != null)
        {
            treeToPath = new Dictionary<SyntaxTree, string>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < parsedResults.Length && i < sourceFilePaths.Count; i++)
            {
                if (sourceFilePaths[i] != null)
                    treeToPath[parsedResults[i].tree] = sourceFilePaths[i]!;
            }
        }

        for (int idx = 0; idx < parsedResults.Length; idx++)
        {
            var (tree, parseDiags) = parsedResults[idx];
            if (parseDiags.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                var fileLabel = sourceFilePaths != null && idx < sourceFilePaths.Count && sourceFilePaths[idx] != null
                    ? $" in {Path.GetFileName(sourceFilePaths[idx]!)}"
                    : "";
                Console.Error.WriteLine($"AL parse errors{fileLabel}:");
                foreach (var d in parseDiags.Where(d => d.Severity == DiagnosticSeverity.Error))
                    Console.Error.WriteLine($"  {d}");
                hasErrors = true;
            }
            syntaxTrees.Add(tree);
        }

        if (hasErrors)
            return null;

        // Extract app identity from input manifest (for correct InternalsVisibleTo resolution)
        var appIdentity = ExtractAppIdentity(inputPaths);

        // Extract feature flags from app.json (e.g. NoImplicitWith, TranslationFile)
        var appFeatures = ExtractFeatures(inputPaths);
        var compilerFeatures = MapCompilerFeatures(appFeatures);

        // Create compilation with all syntax trees
        var compilation = Compilation.Create(
            moduleName: appIdentity.Name,
            publisher: appIdentity.Publisher,
            version: appIdentity.Version,
            appId: appIdentity.AppId,
            syntaxTrees: syntaxTrees.ToArray(),
            options: new CompilationOptions(
                continueBuildOnError: true,
                target: CompilationTarget.OnPrem,
                // Exclude ReportLayout — the runner has no RDLC file system and
                // GenerateRdlcLayout crashes with NullReferenceException.
                generateOptions: CompilationGenerationOptions.Code | CompilationGenerationOptions.Navigation,
                compilerFeatures: compilerFeatures
            )
        );

        // --- Symbol reference support ---
        // Always call ResolvePackagePaths so that .alpackages directories adjacent to
        // the input paths are auto-discovered even when --packages is not specified.
        bool hasExplicitPackages = packagePaths != null && packagePaths.Count > 0;
        var allPackagePaths = ResolvePackagePaths(packagePaths, inputPaths);
        bool hasAnyPackages = allPackagePaths.Count > 0;

        if (!hasExplicitPackages && hasAnyPackages)
            Log.Info($"Auto-detected package directories: {string.Join(", ", allPackagePaths)}");

        // Populated during the "load all packages" path for use in the reactive conflict resolver.
        var loadedPackageSpecs = new List<PackageSpec>();
        Microsoft.Dynamics.Nav.CodeAnalysis.ISymbolReferenceLoader? refLoader = null;

        if (hasAnyPackages)
        {
            var depSpecs = DiscoverDependencies(inputPaths, forceResolve: hasExplicitPackages);

            // Filter out any dependency that is already present as AL source.
            // This is the common case when .alpackages contains the compiled .app of the
            // extension being compiled from source: without filtering, the BC compiler sees
            // the same objects twice (once from source, once from the .app symbol reference)
            // and emits AL0275 "ambiguous reference" errors.
            var sourceAppIds = ExtractAllSourceAppIds(inputPaths);
            if (sourceAppIds.Count > 0 && depSpecs.Count > 0)
            {
                var before = depSpecs.Count;
                depSpecs = depSpecs
                    .Where(s => !sourceAppIds.Contains(s.AppId))
                    .ToList();
                var skipped = before - depSpecs.Count;
                if (skipped > 0)
                    Log.Info($"  Skipped {skipped} package reference(s) already provided as AL source.");
            }

            Log.Info($"Symbol references: scanning {allPackagePaths.Count} package directories");
            foreach (var p in allPackagePaths)
                Log.Info($"  {p}");

            refLoader = ReferenceLoaderFactory.CreateReferenceLoader(allPackagePaths);

            if (depSpecs.Count > 0)
            {
                Log.Info($"Adding {depSpecs.Count} symbol reference specifications:");
                foreach (var spec in depSpecs)
                    Log.Info($"  {FormatSpec(spec)}");

                compilation = compilation
                    .WithReferenceLoader(refLoader)
                    .AddReferences(depSpecs.ToArray());
            }
            else
            {
                // No explicit dependencies in app.json — load all .app files from
                // the packages directory as symbol references. This handles the BC
                // convention where Application/System dependencies are implicit.
                // PackageScanner deduplicates: first by GUID (keeping highest version),
                // then by (publisher+name+version) to eliminate self-duplicates that
                // would otherwise produce AL0275 "ambiguous reference" errors.
                Log.Info("No explicit dependencies found. Loading all .app packages as symbols...");

                var scannedSpecs = PackageScanner.ScanForSpecs(
                    allPackagePaths,
                    excludeGuid: appIdentity.AppId,
                    excludeName: appIdentity.Name);

                loadedPackageSpecs.AddRange(scannedSpecs);

                if (loadedPackageSpecs.Count > 0)
                {
                    var allAppSpecs = loadedPackageSpecs
                        .Select(s => new SymbolReferenceSpecification(
                            s.Publisher, s.Name, s.Version,
                            false, s.AppId, false, ImmutableArray<Guid>.Empty))
                        .ToArray();
                    Log.Info($"  Loaded {allAppSpecs.Length} symbol packages (deduplicated by identity)");
                    compilation = compilation
                        .WithReferenceLoader(refLoader)
                        .AddReferences(allAppSpecs);
                }
                else
                {
                    compilation = compilation.WithReferenceLoader(refLoader);
                }
            }
        }

        // Check for declaration-level diagnostics before emit
        // AL0432/AL0433: obsolete/removed field references — not blocking
        // AL0791: unknown namespace in a `using` directive — not blocking when
        //   nothing from the namespace is actually resolved; real BC projects
        //   often carry legacy `using` lines the runner doesn't need to honor.
        //   Genuine uses of the unresolved namespace will still surface as
        //   separate unresolved-identifier errors, so this only silences the
        //   "no-op import" noise.
        var ignoredErrorIds = new HashSet<string> { "AL0432", "AL0433", "AL0791" };
        var declDiags = compilation.GetDeclarationDiagnostics().ToList();
        var declErrors = declDiags
            .Where(d => d.Severity == DiagnosticSeverity.Error && !ignoredErrorIds.Contains(d.Id))
            .ToList();

        // Reactive conflict resolution for AL0275/AL0197 ambiguous reference errors.
        //
        // Two distinct cases:
        //
        // 1. Self-duplicate (always handled): both sides of the AL0275 message name the
        //    same extension identity.  This happens when the packages directory contains
        //    copies of the same .app with different GUIDs.  PackageScanner already removes
        //    these proactively, but this path handles packages loaded via explicit
        //    SymbolReferenceSpecification (depSpecs) that bypass PackageScanner.
        //    Fix: rebuild with a PackageScanner-deduped spec list.
        //
        // 2. Stubs-vs-package conflict (stubs only): a stub defines the same object as a
        //    package. Fix: drop the conflicting package so the stub wins.
        if (declErrors.Any(d => d.Id is "AL0275" or "AL0197") && hasExplicitPackages && allPackagePaths.Count > 0)
        {
            var al0275Errors = declErrors.Where(d => d.Id is "AL0275" or "AL0197").ToList();

            // Case 1: self-duplicate AL0275 (no stubs required)
            var selfDuplicates = al0275Errors.Where(d => DiagnosticClassifier.IsSelfDuplicateAmbiguity(d.GetMessage())).ToList();
            if (selfDuplicates.Count > 0)
            {
                Log.Info($"Self-duplicate packages detected ({selfDuplicates.Count} AL0275 errors). Re-scanning with identity deduplication...");

                var deduped = PackageScanner.ScanForSpecs(
                    allPackagePaths,
                    excludeGuid: appIdentity.AppId,
                    excludeName: appIdentity.Name);

                var dedupedSpecs = deduped
                    .Select(s => new SymbolReferenceSpecification(
                        s.Publisher, s.Name, s.Version,
                        false, s.AppId, false, ImmutableArray<Guid>.Empty))
                    .ToArray();

                compilation = Compilation.Create(
                    moduleName: appIdentity.Name,
                    publisher: appIdentity.Publisher,
                    version: appIdentity.Version,
                    appId: appIdentity.AppId,
                    syntaxTrees: syntaxTrees.ToArray(),
                    options: new CompilationOptions(
                        continueBuildOnError: true,
                        target: CompilationTarget.OnPrem,
                        generateOptions: CompilationGenerationOptions.Code | CompilationGenerationOptions.Navigation,
                        compilerFeatures: compilerFeatures
                    ))
                    .WithReferenceLoader(refLoader!)
                    .AddReferences(dedupedSpecs);

                // Re-check declaration diagnostics
                declDiags = compilation.GetDeclarationDiagnostics().ToList();
                declErrors = declDiags
                    .Where(d => d.Severity == DiagnosticSeverity.Error && !ignoredErrorIds.Contains(d.Id))
                    .ToList();

                // Refresh al0275Errors for case 2 below
                al0275Errors = declErrors.Where(d => d.Id is "AL0275" or "AL0197").ToList();
            }

            // Case 2: stubs-vs-package conflict (only when stubs are loaded)
            if (Log.HasStubs && al0275Errors.Any())
            {
                // Extract conflicting extension names from error messages.
                // Format: "'X' is an ambiguous reference between 'X' defined by the extension
                //          'AppName by Publisher (Version)' and ..."
                var conflictingApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in al0275Errors)
                {
                    var msg = d.GetMessage();
                    foreach (var part in msg.Split("'"))
                    {
                        if (part.Contains(" by ") && part.Contains("(") &&
                            !part.Contains(appIdentity.Name))
                        {
                            var appName = part.Split(" by ")[0].Trim();
                            conflictingApps.Add(appName);
                        }
                    }
                }

                if (conflictingApps.Count > 0)
                {
                    Log.Info($"Stubs override {conflictingApps.Count} package(s): {string.Join(", ", conflictingApps)}");

                    var filteredSpecs = loadedPackageSpecs
                        .Where(s => !conflictingApps.Contains(s.Name))
                        .Select(s => new SymbolReferenceSpecification(
                            s.Publisher, s.Name, s.Version,
                            false, s.AppId, false, ImmutableArray<Guid>.Empty))
                        .ToArray();

                    compilation = Compilation.Create(
                        moduleName: appIdentity.Name,
                        publisher: appIdentity.Publisher,
                        version: appIdentity.Version,
                        appId: appIdentity.AppId,
                        syntaxTrees: syntaxTrees.ToArray(),
                        options: new CompilationOptions(
                            continueBuildOnError: true,
                            target: CompilationTarget.OnPrem,
                            generateOptions: CompilationGenerationOptions.Code | CompilationGenerationOptions.Navigation,
                            compilerFeatures: compilerFeatures
                        ))
                        .WithReferenceLoader(refLoader!)
                        .AddReferences(filteredSpecs);

                    // Re-check declaration diagnostics
                    declDiags = compilation.GetDeclarationDiagnostics().ToList();
                    declErrors = declDiags
                        .Where(d => d.Severity == DiagnosticSeverity.Error && !ignoredErrorIds.Contains(d.Id))
                        .ToList();

                }
            }
        }

        // Case 3 (independent of packages): cross-extension collision on extension objects.
        // When multiple extensions are compiled together in a single pass,
        // independently-valid extension objects (pageextension, tableextension, etc.)
        // with the same name from different extensions cause false AL0275/AL0197 errors.
        // In production BC these compile independently and never collide. Since
        // extension objects are never referenced by name in AL code, we suppress these.
        //
        // Two-pass approach: first collect extension-type AL0197 errors and group by
        // object name to verify they reference 2+ different extensions (truly cross-
        // extension), then suppress AL0275 only for those verified names.
        // Same-extension duplicates (1 extension identity) are NOT suppressed.
        // Non-extension types (Codeunit, Table, Page, etc.) are never suppressed.
        {
            // Pass 1: AL0197 — collect extension-type duplicates grouped by object name
            var al0197ExtType = declErrors
                .Where(d => d.Id == "AL0197")
                .Where(d => DiagnosticClassifier.IsCrossExtensionDuplicateDeclaration(d.GetMessage()))
                .ToList();

            // Group by object name and collect declaring extension identities per name.
            // Only names with 2+ different extension identities are truly cross-extension.
            var nameToExtensions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in al0197ExtType)
            {
                var info = DiagnosticClassifier.ExtractDuplicateDeclarationInfo(d.GetMessage());
                if (info is null) continue;
                if (!nameToExtensions.TryGetValue(info.Value.ObjectName, out var extSet))
                {
                    extSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    nameToExtensions[info.Value.ObjectName] = extSet;
                }
                extSet.Add(info.Value.ExtensionId);
            }

            var crossExtNames = new HashSet<string>(
                nameToExtensions.Where(kv => kv.Value.Count >= 2).Select(kv => kv.Key),
                StringComparer.OrdinalIgnoreCase);

            // Only suppress AL0197 errors whose object name is verified cross-extension
            var al0197CrossExt = al0197ExtType
                .Where(d =>
                {
                    var info = DiagnosticClassifier.ExtractDuplicateDeclarationInfo(d.GetMessage());
                    return info != null && crossExtNames.Contains(info.Value.ObjectName);
                })
                .ToList();

            // Pass 2: AL0275 — only suppress if the object name is a verified cross-extension name
            var al0275CrossExt = declErrors
                .Where(d => d.Id == "AL0275")
                .Where(d =>
                {
                    var msg = d.GetMessage();
                    if (!DiagnosticClassifier.IsCrossExtensionAmbiguity(msg)) return false;
                    var name = DiagnosticClassifier.ExtractAmbiguousObjectName(msg);
                    return name != null && crossExtNames.Contains(name);
                })
                .ToList();

            var crossExtErrors = al0197CrossExt.Concat(al0275CrossExt).ToList();
            if (crossExtErrors.Count > 0)
            {
                Log.Info($"Cross-extension name collisions suppressed ({crossExtErrors.Count} AL0275/AL0197 errors from different extensions compiled together).");
                declErrors = declErrors
                    .Where(d => !crossExtErrors.Contains(d))
                    .ToList();
            }
        }

        if (declErrors.Count > 0)
        {
            var missingObjects = declErrors
                .Where(d => d.Id == "AL0185")
                .Select(d => d.GetMessage())
                .Distinct()
                .ToList();
            var otherErrors = declErrors.Where(d => d.Id != "AL0185").ToList();

            if (missingObjects.Count > 0)
            {
                Console.Error.WriteLine($"Missing dependencies: {string.Join(", ", missingObjects)}");

                // If a stub with the same type+name exists under a different
                // namespace, tell the user — a namespace mismatch on a stub
                // presents identically to a missing dependency and is a
                // common setup mistake.
                if (AlRunner.StubIndex.HasAny)
                {
                    // AL0185 messages look like: Codeunit 'Type Helper' is missing
                    // Extract (type, name) heuristically and cross-check StubIndex.
                    var msgRegex = new System.Text.RegularExpressions.Regex(
                        @"^(?<type>Codeunit|Table|Enum|Interface|Page|Report|XmlPort|Query)\s+'(?<name>[^']+)'");
                    foreach (var msg in missingObjects)
                    {
                        var m = msgRegex.Match(msg);
                        if (!m.Success) continue;
                        var hits = AlRunner.StubIndex.Find(m.Groups["type"].Value, m.Groups["name"].Value);
                        foreach (var hit in hits)
                        {
                            var where = hit.Namespace is null
                                ? $"in stub '{Path.GetFileName(hit.SourcePath)}' (no namespace declared)"
                                : $"in stub '{Path.GetFileName(hit.SourcePath)}' under namespace '{hit.Namespace}'";
                            Console.Error.WriteLine(
                                $"  hint: a {hit.ObjectType} '{hit.ObjectName}' was found {where}; " +
                                "check that the namespace matches what the consumer imports.");
                        }
                    }
                }
            }

            if (otherErrors.Count > 0)
                Log.Info($"AL declaration errors ({otherErrors.Count} non-missing):");
            foreach (var d in otherErrors.Take(10))
                Log.Info($"  {FormatAlDiagnostic(d, treeToPath)}");
            if (otherErrors.Count > 10)
                Log.Info($"  ... and {otherErrors.Count - 10} more");

            // AL0197 for a non-extension type (Codeunit, Table, Page, etc.) is a genuine
            // duplicate-object error — two different apps define the same object.  This is
            // never valid and the real BC compiler rejects it.  Return null so the pipeline
            // exits with code 3 (AL compile error).
            //
            // Extension-type AL0197 errors (PageExtension, TableExtension, ProfileExtension,
            // etc.) are NOT flagged as genuine duplicates here because:
            //   • Cross-extension (two different app.json scopes defining the same extension
            //     object name) is a runner artifact — in real BC, each extension compiles
            //     independently and can legitimately share extension object names.
            //     These errors have already been handled (or ignored) above via Case 3 for
            //     package-backed scenarios, and via the IsCrossExtensionDuplicateDeclaration
            //     filter here for source-only multi-app compilations.
            //   • Same-extension (same app.json scope defining the same extension object name
            //     twice) is a genuine error — but it is caught BEFORE this point by the
            //     DetectSameExtensionDuplicates pre-scan in Pipeline.cs, which exits with
            //     code 3 before compilation begins.  Any same-extension extension-type AL0197
            //     that reaches this path indicates a gap in the pre-scan coverage; add the
            //     missing type to DetectSameExtensionDuplicates (Pipeline.cs) rather than
            //     changing this filter.
            var genuineDuplicates = declErrors
                .Where(d => d.Id == "AL0197" && !DiagnosticClassifier.IsCrossExtensionDuplicateDeclaration(d.GetMessage()))
                .ToList();
            if (genuineDuplicates.Count > 0)
            {
                Console.Error.WriteLine($"AL compilation errors — duplicate object declarations ({genuineDuplicates.Count}):");
                foreach (var d in genuineDuplicates.Take(10))
                    Console.Error.WriteLine($"  {FormatAlDiagnostic(d, treeToPath)}");
                if (genuineDuplicates.Count > 10)
                    Console.Error.WriteLine($"  ... and {genuineDuplicates.Count - 10} more");
                return null;
            }
        }

        // Store compilation for symbol table queries by auto-stub generation
        LastCompilation = compilation;

        var outputter = new CSharpCaptureOutputter();
        EmitResult? emitResult = null;
        try
        {
            emitResult = compilation.Emit(new EmitOptions(), outputter);
        }
        catch (AggregateException ex)
        {
            // The BC compiler throws AggregateException when some methods fail to emit.
            // This is expected when dependencies are partially resolved or have type mismatches.
            // Objects that were successfully emitted before the failure are still captured.
            var failedMethods = new List<string>();
            foreach (var inner in ex.Flatten().InnerExceptions)
            {
                if (inner is AggregateException innerAgg)
                {
                    foreach (var innerInner in innerAgg.Flatten().InnerExceptions)
                    {
                        failedMethods.Add(innerInner.Message);
                        if (Log.Verbose)
                            Log.Info($"  emit exception: {innerInner}");
                    }
                }
                else
                {
                    failedMethods.Add(inner.Message);
                    if (Log.Verbose)
                        Log.Info($"  emit exception: {inner}");
                }
            }
            Log.Info($"Partial transpilation: {failedMethods.Count} method(s) skipped");
            foreach (var msg in failedMethods.Take(10))
                Log.Info($"  {msg}");
            if (failedMethods.Count > 10)
                Log.Info($"  ... and {failedMethods.Count - 10} more");
        }

        if (outputter.CapturedObjects.Count == 0)
        {
            Console.Error.WriteLine("AL transpilation: no C# code was generated.");
            if (emitResult != null)
            {
                Console.Error.WriteLine("Emit diagnostics:");
                foreach (var d in emitResult.Diagnostics.Take(30))
                    Console.Error.WriteLine($"  {FormatAlDiagnostic(d, treeToPath)}");
            }
            return null;
        }

        // Report any non-error diagnostics for info
        if (emitResult != null)
        {
            var warnings = emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();
            if (warnings.Count > 0)
            {
                Log.Info($"AL compiler warnings ({warnings.Count}):");
                foreach (var d in warnings.Take(10))
                    Log.Info($"  {d.Id}: {d.GetMessage()}");
            }
        }

        var results = new List<(string Name, string Code)>();
        foreach (var obj in outputter.CapturedObjects)
        {
            if (obj.CSharpCode != null && obj.CSharpCode.Length > 0)
            {
                var code = Encoding.UTF8.GetString(obj.CSharpCode);
                results.Add((obj.SymbolName, code));
            }
        }

        return results;
    }

    /// <summary>
    /// Resolve all package directories: explicit --packages args + auto-discovered .alpackages.
    /// Auto-discovery of .alpackages only happens when --packages is also specified or when
    /// .alpackages directories exist in input paths.
    /// </summary>
    /// <summary>
    /// App identity extracted from manifest, used for Compilation.Create parameters.
    /// This matters for InternalsVisibleTo resolution (publisher must match).
    /// </summary>
    private record AppIdentity(string Name, string Publisher, Version Version, Guid AppId);

    /// <summary>
    /// Extract feature flags from the first app.json found by walking up from input paths.
    /// Returns features like "NoImplicitWith", "TranslationFile", "NoPromotedActionProperties", etc.
    /// These are passed to CompilationOptions so the BC compiler respects the app's feature flags.
    /// </summary>
    private static List<string> ExtractFeatures(List<string>? inputPaths)
    {
        if (inputPaths == null || inputPaths.Count == 0) return new List<string>();

        foreach (var inputPath in inputPaths)
        {
            var dir = Directory.Exists(inputPath) ? Path.GetFullPath(inputPath) : Path.GetDirectoryName(Path.GetFullPath(inputPath));
            while (dir != null)
            {
                var appJsonPath = Path.Combine(dir, "app.json");
                if (File.Exists(appJsonPath))
                {
                    try
                    {
                        var json = JsonDocument.Parse(File.ReadAllText(appJsonPath));
                        var root = json.RootElement;
                        if (root.TryGetProperty("features", out var featuresProp) && featuresProp.ValueKind == JsonValueKind.Array)
                        {
                            var features = new List<string>();
                            foreach (var f in featuresProp.EnumerateArray())
                            {
                                var val = f.GetString();
                                if (!string.IsNullOrEmpty(val))
                                    features.Add(val!);
                            }
                            if (features.Count > 0)
                            {
                                Log.Info($"Features from app.json: {string.Join(", ", features)}");
                                return features;
                            }
                        }
                    }
                    catch { /* fall through */ }
                }
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break; // filesystem root
                dir = parent;
            }
        }

        return [];
    }

    /// <summary>
    /// Maps app.json feature strings (e.g. "NoImplicitWith", "TranslationFile") to the
    /// CompilerFeatures flags enum used by the BC compiler.
    /// </summary>
    private static CompilerFeatures MapCompilerFeatures(List<string> features)
    {
        var result = CompilerFeatures.None;
        foreach (var feature in features)
        {
            result |= feature switch
            {
                "NoImplicitWith" => CompilerFeatures.NoImplicitWith,
                "NoPromotedActionProperties" => CompilerFeatures.NoPromotedActionProperties,
                "TranslationFile" => CompilerFeatures.GenerateXliffTranslationFile,
                _ => CompilerFeatures.None,
            };
        }
        if (result != CompilerFeatures.None)
            Log.Info($"CompilerFeatures: {result}");
        return result;
    }

    /// <summary>Format a SymbolReferenceSpecification for display.</summary>
    private static string FormatSpec(SymbolReferenceSpecification spec)
    {
        // Use reflection since the properties may not be public in all versions
        try
        {
            var type = spec.GetType();
            var publisher = type.GetProperty("Publisher")?.GetValue(spec)?.ToString() ?? "?";
            var name = type.GetProperty("Name")?.GetValue(spec)?.ToString() ?? "?";
            var version = type.GetProperty("Version")?.GetValue(spec)?.ToString() ?? "?";
            return $"{publisher}/{name} v{version}";
        }
        catch
        {
            return spec.ToString() ?? "?";
        }
    }

    /// <summary>
    /// Extract app identity from the first input that has a manifest (app.json or NavxManifest.xml).
    /// Falls back to generic defaults for self-contained spikes.
    /// </summary>
    private static AppIdentity ExtractAppIdentity(List<string>? inputPaths)
    {
        var defaults = new AppIdentity("AlRunnerApp", "AlRunner", new Version("1.0.0.0"), Guid.NewGuid());
        if (inputPaths == null || inputPaths.Count == 0) return defaults;

        foreach (var inputPath in inputPaths)
        {
            // Walk up the directory tree looking for app.json (like git finds .git)
            var dir = Directory.Exists(inputPath) ? Path.GetFullPath(inputPath) : Path.GetDirectoryName(Path.GetFullPath(inputPath));
            while (dir != null)
            {
                var appJsonPath = Path.Combine(dir, "app.json");
                if (File.Exists(appJsonPath))
                {
                    try
                    {
                        var json = JsonDocument.Parse(File.ReadAllText(appJsonPath));
                        var root = json.RootElement;
                        var name = root.TryGetProperty("name", out var n) ? n.GetString()! : defaults.Name;
                        var publisher = root.TryGetProperty("publisher", out var p) ? p.GetString()! : defaults.Publisher;
                        var version = root.TryGetProperty("version", out var v) ? Version.Parse(v.GetString()!) : defaults.Version;
                        var appId = root.TryGetProperty("id", out var id) ? Guid.Parse(id.GetString()!) : defaults.AppId;
                        return new AppIdentity(name, publisher, version, appId);
                    }
                    catch { /* fall through */ }
                }
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break; // filesystem root
                dir = parent;
            }

            // Try NavxManifest.xml (for .app file inputs, including Ready2Run packages)
            if (inputPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && File.Exists(inputPath))
            {
                try
                {
                    var doc = LoadNavxManifest(inputPath);
                    if (doc != null)
                    {
                        XNamespace ns = "http://schemas.microsoft.com/navx/2015/manifest";
                        var appElement = doc.Root?.Element(ns + "App");
                        if (appElement != null)
                        {
                            var name = appElement.Attribute("Name")?.Value ?? defaults.Name;
                            var publisher = appElement.Attribute("Publisher")?.Value ?? defaults.Publisher;
                            var versionStr = appElement.Attribute("Version")?.Value ?? "1.0.0.0";
                            var idStr = appElement.Attribute("Id")?.Value;
                            var appId = idStr != null ? Guid.Parse(idStr) : defaults.AppId;
                            return new AppIdentity(name, publisher, Version.Parse(versionStr), appId);
                        }
                    }
                }
                catch { /* fall through */ }
            }
        }

        return defaults;
    }

    /// <summary>
    /// Collect all app GUIDs from manifests (app.json or NavxManifest.xml) found in or near
    /// each input path.  Unlike <see cref="ExtractAppIdentity"/> (which returns the first match),
    /// this method walks every input path so that multi-project runs (e.g. main-app source +
    /// test-app source compiled together) produce a complete set of "source" app IDs.
    ///
    /// These IDs are used to filter symbol-reference specifications so that a .app package
    /// whose GUID matches an app already present in the source tree is never loaded a second
    /// time as a symbol reference (which would cause AL0275 duplicate-object errors).
    /// </summary>
    public static HashSet<Guid> ExtractAllSourceAppIds(List<string>? inputPaths)
    {
        var result = new HashSet<Guid>();
        if (inputPaths == null || inputPaths.Count == 0) return result;

        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var inputPath in inputPaths)
        {
            // Walk up the directory tree looking for app.json
            var dir = Directory.Exists(inputPath)
                ? Path.GetFullPath(inputPath)
                : Path.GetDirectoryName(Path.GetFullPath(inputPath));

            while (dir != null)
            {
                if (!seenDirs.Add(dir)) break; // already processed this dir

                var appJsonPath = Path.Combine(dir, "app.json");
                if (File.Exists(appJsonPath))
                {
                    try
                    {
                        var json = JsonDocument.Parse(File.ReadAllText(appJsonPath));
                        var root = json.RootElement;
                        if (root.TryGetProperty("id", out var idProp) && Guid.TryParse(idProp.GetString(), out var guid))
                            result.Add(guid);
                    }
                    catch { /* corrupt or unreadable — skip */ }
                    break; // found the manifest for this input — stop walking up
                }

                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break; // filesystem root
                dir = parent;
            }

            // Try NavxManifest.xml (for .app file inputs)
            if (inputPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && File.Exists(inputPath))
            {
                try
                {
                    var doc = LoadNavxManifest(inputPath);
                    if (doc != null)
                    {
                        XNamespace ns = "http://schemas.microsoft.com/navx/2015/manifest";
                        var appElement = doc.Root?.Element(ns + "App");
                        var idStr = appElement?.Attribute("Id")?.Value;
                        if (idStr != null && Guid.TryParse(idStr, out var guid))
                            result.Add(guid);
                    }
                }
                catch { /* fall through */ }
            }
        }

        return result;
    }

    public static List<string> ResolvePackagePaths(List<string>? explicitPaths, List<string>? inputPaths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add explicit --packages paths. Accepts both directories and individual .app files.
        // For individual .app files, the parent directory is added so PackageScanner can find them.
        if (explicitPaths != null)
        {
            foreach (var p in explicitPaths)
            {
                // Individual .app file — add its parent directory.
                if (File.Exists(p) && p.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                {
                    var parent = Path.GetDirectoryName(Path.GetFullPath(p));
                    if (parent != null) result.Add(parent);
                    continue;
                }

                if (!Directory.Exists(p)) continue;
                var fullPath = Path.GetFullPath(p);

                // Add the directory itself if it contains .app files
                if (Directory.GetFiles(fullPath, "*.app").Length > 0)
                    result.Add(fullPath);

                // Recursively scan for subdirectories containing .app files
                foreach (var subDir in Directory.GetDirectories(fullPath, "*", SearchOption.AllDirectories))
                {
                    if (Directory.GetFiles(subDir, "*.app").Length > 0)
                        result.Add(subDir);
                }
            }
        }

        // Auto-discover .alpackages directories relative to each input
        if (inputPaths != null)
        {
            foreach (var inputPath in inputPaths)
            {
                var dir = Directory.Exists(inputPath) ? inputPath : Path.GetDirectoryName(inputPath);
                if (dir == null) continue;

                // Check for .alpackages in the input directory itself
                var alPkg = Path.Combine(dir, ".alpackages");
                if (Directory.Exists(alPkg))
                    result.Add(Path.GetFullPath(alPkg));

                // Also check parent directory (common for project dirs)
                var parentDir = Path.GetDirectoryName(dir);
                if (parentDir != null)
                {
                    var parentAlPkg = Path.Combine(parentDir, ".alpackages");
                    if (Directory.Exists(parentAlPkg))
                        result.Add(Path.GetFullPath(parentAlPkg));
                }
            }
        }

        return result.ToList();
    }

    /// <summary>
    /// Discover dependency specifications from input paths by reading app.json and NavxManifest.xml.
    /// Only returns specs if actual dependencies are found (not just platform/application version).
    /// When forceResolve is true (--packages was explicitly given), always include platform/application refs.
    /// </summary>
    private static List<SymbolReferenceSpecification> DiscoverDependencies(List<string>? inputPaths, bool forceResolve = false)
    {
        var specs = new List<SymbolReferenceSpecification>();
        var platformSpecs = new List<SymbolReferenceSpecification>(); // platform + application refs
        var addedPlatform = false;
        var addedApplication = false;
        var addedDeps = new HashSet<string>();
        bool hasActualDeps = false;

        if (inputPaths == null || inputPaths.Count == 0)
            return specs;

        foreach (var inputPath in inputPaths)
        {
            // Try app.json (for directory inputs)
            var dir = Directory.Exists(inputPath) ? inputPath : Path.GetDirectoryName(inputPath);
            if (dir != null)
            {
                var appJsonPath = Path.Combine(dir, "app.json");
                if (File.Exists(appJsonPath))
                {
                    var depCount = specs.Count;
                    ParseAppJson(appJsonPath, specs, platformSpecs, ref addedPlatform, ref addedApplication, addedDeps);
                    if (specs.Count > depCount) hasActualDeps = true;
                    continue;
                }
            }

            // Try NavxManifest.xml (for .app file inputs)
            if (inputPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && File.Exists(inputPath))
            {
                var depCount = specs.Count;
                ParseNavxManifest(inputPath, specs, platformSpecs, ref addedPlatform, ref addedApplication, addedDeps);
                if (specs.Count > depCount) hasActualDeps = true;
            }
        }

        // Only include platform/application refs if we have actual deps or forceResolve
        if (hasActualDeps || forceResolve)
        {
            specs.InsertRange(0, platformSpecs);
            return specs;
        }

        return new List<SymbolReferenceSpecification>();
    }

    /// <summary>
    /// Parse app.json to extract platform/application versions and explicit dependencies.
    /// Platform/application specs go into platformSpecs; actual deps go into specs.
    /// </summary>
    private static void ParseAppJson(string appJsonPath, List<SymbolReferenceSpecification> specs,
        List<SymbolReferenceSpecification> platformSpecs,
        ref bool addedPlatform, ref bool addedApplication, HashSet<string> addedDeps)
    {
        try
        {
            var json = JsonDocument.Parse(File.ReadAllText(appJsonPath));
            var root = json.RootElement;

            // Platform reference
            if (!addedPlatform && root.TryGetProperty("platform", out var platformProp))
            {
                var platformVersion = Version.Parse(platformProp.GetString()!);
                platformSpecs.Add(SymbolReferenceSpecification.PlatformReference(platformVersion));
                addedPlatform = true;
            }

            // Application reference (if declared)
            if (!addedApplication && root.TryGetProperty("application", out var applicationProp))
            {
                var appVersion = Version.Parse(applicationProp.GetString()!);
                platformSpecs.Add(SymbolReferenceSpecification.ApplicationReference(appVersion));
                addedApplication = true;
            }

            // Explicit dependencies
            if (root.TryGetProperty("dependencies", out var depsProp) && depsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var dep in depsProp.EnumerateArray())
                {
                    var id = dep.GetProperty("id").GetString()!;
                    if (addedDeps.Contains(id)) continue;
                    addedDeps.Add(id);

                    var name = dep.GetProperty("name").GetString()!;
                    var publisher = dep.GetProperty("publisher").GetString()!;
                    var version = Version.Parse(dep.GetProperty("version").GetString()!);
                    var appGuid = Guid.Parse(id);

                    specs.Add(new SymbolReferenceSpecification(
                        publisher, name, version, false, appGuid, false, ImmutableArray<Guid>.Empty));
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to parse {appJsonPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Open a ZIP archive from an .app file, handling the NAVX header.
    /// Returns the byte array and zip offset so the caller can create a ZipArchive.
    /// </summary>
    private static (byte[] Data, int ZipOffset) ReadAppFile(byte[] fileBytes)
    {
        int zipOffset = 0;
        if (fileBytes.Length >= 8
            && fileBytes[0] == (byte)'N' && fileBytes[1] == (byte)'A'
            && fileBytes[2] == (byte)'V' && fileBytes[3] == (byte)'X')
        {
            zipOffset = (int)BitConverter.ToUInt32(fileBytes, 4);
        }
        return (fileBytes, zipOffset);
    }

    /// <summary>
    /// Load NavxManifest.xml from an .app file, handling Ready2Run packages (nested .app).
    /// Returns the parsed XDocument or null if no manifest is found.
    /// </summary>
    public static XDocument? LoadNavxManifest(string appPath)
    {
        var fileBytes = File.ReadAllBytes(appPath);
        var (data, zipOffset) = ReadAppFile(fileBytes);

        using var zipStream = new MemoryStream(data, zipOffset, data.Length - zipOffset);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var manifestEntry = zip.GetEntry("NavxManifest.xml");

        if (manifestEntry == null)
        {
            // Ready2Run package: look for nested .app file
            var nestedApp = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
            if (nestedApp != null)
            {
                using var nestedStream = nestedApp.Open();
                using var ms = new MemoryStream();
                nestedStream.CopyTo(ms);
                var nestedBytes = ms.ToArray();
                var (nestedData, nestedOffset) = ReadAppFile(nestedBytes);

                using var nestedZipStream = new MemoryStream(nestedData, nestedOffset, nestedData.Length - nestedOffset);
                using var nestedZip = new ZipArchive(nestedZipStream, ZipArchiveMode.Read);
                manifestEntry = nestedZip.GetEntry("NavxManifest.xml");
                if (manifestEntry != null)
                {
                    using var stream = manifestEntry.Open();
                    return XDocument.Load(stream);
                }
            }
            return null;
        }

        using var directStream = manifestEntry.Open();
        return XDocument.Load(directStream);
    }

    /// <summary>
    /// Extract dependency app GUIDs from an .app file's NavxManifest.xml.
    /// </summary>
    public static List<Guid> GetDependencyGuids(string appPath)
    {
        var result = new List<Guid>();
        try
        {
            var doc = LoadNavxManifest(appPath);
            if (doc == null) return result;
            XNamespace ns = "http://schemas.microsoft.com/navx/2015/manifest";
            var depsElement = doc.Root?.Element(ns + "Dependencies");
            if (depsElement == null) return result;
            foreach (var dep in depsElement.Elements(ns + "Dependency"))
            {
                var idStr = dep.Attribute("Id")?.Value;
                if (idStr != null && Guid.TryParse(idStr, out var depGuid))
                    result.Add(depGuid);
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Parse NavxManifest.xml from an .app file to extract platform/application versions and dependencies.
    /// Platform/application specs go into platformSpecs; actual deps go into specs.
    /// Handles Ready2Run packages (nested .app) automatically.
    /// </summary>
    private static void ParseNavxManifest(string appPath, List<SymbolReferenceSpecification> specs,
        List<SymbolReferenceSpecification> platformSpecs,
        ref bool addedPlatform, ref bool addedApplication, HashSet<string> addedDeps)
    {
        try
        {
            var doc = LoadNavxManifest(appPath);
            if (doc == null) return;
            XNamespace ns = "http://schemas.microsoft.com/navx/2015/manifest";

            var appElement = doc.Root?.Element(ns + "App");
            if (appElement == null) return;

            // Platform reference
            if (!addedPlatform)
            {
                var platformStr = appElement.Attribute("Platform")?.Value;
                if (platformStr != null)
                {
                    platformSpecs.Add(SymbolReferenceSpecification.PlatformReference(Version.Parse(platformStr)));
                    addedPlatform = true;
                }
            }

            // Application reference
            if (!addedApplication)
            {
                var applicationStr = appElement.Attribute("Application")?.Value;
                if (applicationStr != null)
                {
                    platformSpecs.Add(SymbolReferenceSpecification.ApplicationReference(Version.Parse(applicationStr)));
                    addedApplication = true;
                }
            }

            // Dependencies
            var depsElement = doc.Root?.Element(ns + "Dependencies");
            if (depsElement != null)
            {
                foreach (var dep in depsElement.Elements(ns + "Dependency"))
                {
                    var id = dep.Attribute("Id")?.Value;
                    if (id == null || addedDeps.Contains(id)) continue;
                    addedDeps.Add(id);

                    var name = dep.Attribute("Name")?.Value ?? "";
                    var publisher = dep.Attribute("Publisher")?.Value ?? "";
                    var versionStr = dep.Attribute("MinVersion")?.Value ?? "1.0.0.0";
                    var appGuid = Guid.Parse(id);

                    specs.Add(new SymbolReferenceSpecification(
                        publisher, name, Version.Parse(versionStr), false, appGuid, false, ImmutableArray<Guid>.Empty));
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to parse NavxManifest.xml from {appPath}: {ex.Message}");
        }
    }
}

// ===========================================================================
// SourceLineMapper: maps Roslyn C# line numbers back to AL source lines
// ===========================================================================

/// <summary>
/// Builds a mapping from (C# file name, C# line number) to AL source line number
/// by correlating StmtHit(N)/CStmtHit(N) calls in rewritten C# with SourceSpans
/// from the pre-rewrite generated C#. Used to translate Roslyn compilation errors
/// into meaningful AL line references.
/// </summary>
public static class SourceLineMapper
{
    /// <summary>
    /// Per-file sorted list of (C# line number, AL source line number).
    /// File key is the base name (e.g. "PayrollImportMgtRSABG") matching the
    /// Roslyn file path stem (before ".cs").
    /// </summary>
    public static Dictionary<string, List<(int CSharpLine, int AlLine)>> Mappings { get; } = new();

    /// <summary>
    /// Direct (scope name, statement ID) -> (AL line, AL column) mapping. Populated during Build().
    /// </summary>
    private static Dictionary<(string Scope, int StmtId), (int Line, int Column)> _sourceSpans = new();

    /// <summary>
    /// Build the mapping from pre-rewrite C# (for SourceSpans) and post-rewrite C#
    /// (for StmtHit line positions). Both lists must use the same Name keys.
    /// </summary>
    public static void Build(
        List<(string Name, string Code)> preRewriteCSharp,
        List<(string Name, string Code)> postRewriteCSharp)
    {
        Mappings.Clear();
        _sourceSpans.Clear();

        // Step 1: Parse SourceSpans from pre-rewrite code to get (scope, stmtIndex) -> (AL line, col)
        var sourceSpans = CoverageReport.ParseSourceSpansWithColumns(preRewriteCSharp);
        _sourceSpans = new Dictionary<(string Scope, int StmtId), (int Line, int Column)>(sourceSpans);

        // Step 2: For each post-rewrite file, scan for StmtHit(N) / CStmtHit(N) calls
        // and map the C# line to the AL line via the sourceSpans
        var stmtPattern = new System.Text.RegularExpressions.Regex(
            @"\b(?:StmtHit|CStmtHit)\((\d+)\)");
        var classPattern = new System.Text.RegularExpressions.Regex(
            @"class\s+(\w+_Scope(?:_\w+)?)");

        foreach (var (name, code) in postRewriteCSharp)
        {
            var entries = new List<(int CSharpLine, int AlLine)>();
            string? currentScope = null;
            var lines = code.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var classMatch = classPattern.Match(lines[i]);
                if (classMatch.Success)
                {
                    currentScope = classMatch.Groups[1].Value;
                    continue;
                }

                if (currentScope != null)
                {
                    var stmtMatch = stmtPattern.Match(lines[i]);
                    if (stmtMatch.Success)
                    {
                        int stmtIndex = int.Parse(stmtMatch.Groups[1].Value);
                        if (sourceSpans.TryGetValue((currentScope, stmtIndex), out var alPos))
                        {
                            entries.Add((i + 1, alPos.Line)); // C# lines are 1-based
                        }
                    }
                }
            }

            if (entries.Count > 0)
            {
                entries.Sort((a, b) => a.CSharpLine.CompareTo(b.CSharpLine));
                Mappings[name] = entries;
            }
        }
    }

    /// <summary>
    /// Look up the nearest AL source line for a given C# file and line number.
    /// Returns null if no mapping exists for this file.
    /// </summary>
    public static int? GetAlLine(string csharpFilePath, int csharpLine)
    {
        // Extract base name from path (e.g. "PayrollImportMgtRSABG.cs" -> "PayrollImportMgtRSABG")
        var baseName = Path.GetFileNameWithoutExtension(csharpFilePath);

        if (!Mappings.TryGetValue(baseName, out var entries) || entries.Count == 0)
            return null;

        // Binary search for the nearest entry
        int lo = 0, hi = entries.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (entries[mid].CSharpLine < csharpLine)
                lo = mid + 1;
            else
                hi = mid;
        }

        // Check neighbors to find closest
        int bestIdx = lo;
        if (bestIdx > 0)
        {
            int distCurrent = Math.Abs(entries[bestIdx].CSharpLine - csharpLine);
            int distPrev = Math.Abs(entries[bestIdx - 1].CSharpLine - csharpLine);
            if (distPrev < distCurrent)
                bestIdx = bestIdx - 1;
        }

        return entries[bestIdx].AlLine;
    }

    /// <summary>
    /// Look up the AL source line directly from a scope name and statement ID.
    /// Uses the sourceSpans data populated during Build().
    /// </summary>
    public static int? GetAlLineFromStatement(string scopeName, int stmtId)
    {
        if (_sourceSpans.TryGetValue((scopeName, stmtId), out var alPos))
            return alPos.Line;
        return null;
    }

    /// <summary>
    /// Look up the AL source (line, column) from a scope name and statement ID.
    /// Returns null if the scope/stmt isn't mapped.
    /// </summary>
    public static (int Line, int Column)? GetAlPositionFromStatement(string scopeName, int stmtId)
    {
        if (_sourceSpans.TryGetValue((scopeName, stmtId), out var alPos))
            return alPos;
        return null;
    }

    /// <summary>
    /// Reverse lookup: find all (scopeName, stmtId) pairs that map to the
    /// given AL source file and line. Used by the DAP server to translate
    /// an IDE breakpoint (file, line) into runtime breakpoint registrations.
    ///
    /// <paramref name="alSourceFile"/> is matched against the AL object name
    /// (as registered by <see cref="SourceFileMapper"/>) and also against
    /// plain file path suffixes.
    /// </summary>
    public static List<(string ScopeName, int StmtId)> FindStatementsForAlLine(
        string alSourceFile, int alLine)
    {
        var results = new List<(string ScopeName, int StmtId)>();
        foreach (var kv in _sourceSpans)
        {
            if (kv.Value.Line != alLine)
                continue;

            // The scope name encodes the AL object (e.g. "Codeunit1_MyProc_Scope_abc").
            // Resolve the associated source file via SourceFileMapper and compare.
            var objectName = SourceFileMapper.GetObjectForClass(kv.Key.Scope);
            var mappedFile = SourceFileMapper.GetFile(objectName);
            if (mappedFile == null)
            {
                // If no exact file mapping, fall back to scope-based lookup.
                mappedFile = SourceFileMapper.GetFileForScope(kv.Key.Scope, BuildScopeToObjectMap());
            }

            if (mappedFile != null && FilePathMatches(mappedFile, alSourceFile))
                results.Add((kv.Key.Scope, kv.Key.StmtId));
        }
        return results;
    }

    /// <summary>
    /// Build a scope-name → object-name dictionary from SourceFileMapper's internal
    /// state. Used by FindStatementsForAlLine as a fallback when direct class lookup
    /// doesn't resolve.
    /// </summary>
    private static Dictionary<string, string> BuildScopeToObjectMap()
    {
        // We don't have direct access to SourceFileMapper's internal maps, so we
        // build a reverse lookup from the _sourceSpans keys — each scope name
        // can be resolved via GetObjectForClass.
        var result = new Dictionary<string, string>();
        foreach (var scope in _sourceSpans.Keys.Select(k => k.Scope).Distinct())
        {
            var obj = SourceFileMapper.GetObjectForClass(scope);
            result[scope] = obj;
        }
        return result;
    }

    private static bool FilePathMatches(string mappedFile, string requested)
    {
        // Normalize separators
        mappedFile = mappedFile.Replace('\\', '/');
        requested = requested.Replace('\\', '/');

        // Exact match or suffix match (e.g. "src/Foo.al" matches "Foo.al")
        return string.Equals(mappedFile, requested, StringComparison.OrdinalIgnoreCase)
            || mappedFile.EndsWith("/" + requested, StringComparison.OrdinalIgnoreCase)
            || requested.EndsWith("/" + mappedFile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(mappedFile), Path.GetFileName(requested),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Format a Roslyn diagnostic with AL source line context.
    /// Returns the original diagnostic string with AL line info appended.
    /// </summary>
    public static string FormatDiagnostic(Microsoft.CodeAnalysis.Diagnostic diagnostic)
    {
        var original = diagnostic.ToString();
        var filePath = diagnostic.Location.SourceTree?.FilePath;
        if (filePath == null) return original;

        var lineSpan = diagnostic.Location.GetLineSpan();
        int csharpLine = lineSpan.StartLinePosition.Line + 1; // 0-based to 1-based

        var alLine = GetAlLine(filePath, csharpLine);
        if (alLine == null) return original;

        var baseName = Path.GetFileNameWithoutExtension(filePath);
        // C# column from Roslyn is 0-based; promote to 1-based so the output
        // matches editor conventions. Column is advisory — it's the C# column
        // of the rewritten code, not the original AL, but it's still a useful
        // localisation hint within the AL line.
        int csharpCol = lineSpan.StartLinePosition.Character + 1;
        return $"{original}  [AL line ~{alLine} col {csharpCol} in {baseName}]";
    }
}

// ===========================================================================
// Roslyn In-Memory Compiler (supports multiple C# source strings)
// ===========================================================================
public static class RoslynCompiler
{
    /// <summary>Bundles the compiled assembly with its collectible ALC so callers can unload it.</summary>
    public record CompileResult(Assembly Assembly, System.Runtime.Loader.AssemblyLoadContext LoadContext);

    public static CompileResult? Compile(string csharpSource) =>
        Compile(new List<(string Name, string Code)> { ("source", csharpSource) });

    public static CompileResult? Compile(List<(string Name, string Code)> namedSources)
    {
        // Parse source strings into syntax trees, then delegate to the tree-based overload
        var nameCount = new Dictionary<string, int>();
        var syntaxTrees = namedSources.Select((src, idx) =>
        {
            var baseName = src.Name;
            if (nameCount.TryGetValue(baseName, out int count))
            {
                nameCount[baseName] = count + 1;
                baseName = $"{baseName}_{count + 1}";
            }
            else
            {
                nameCount[baseName] = 1;
            }
            return Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                src.Code, path: $"{baseName}.cs");
        }).ToList();

        return CompileFromTrees(syntaxTrees);
    }

    /// <summary>
    /// Compile from pre-built SyntaxTrees (avoids re-parsing rewritten C#).
    /// Trees are re-rooted with deduplicated file paths for readable diagnostics.
    /// Optionally accepts pre-loaded MetadataReferences to skip redundant loading.
    /// If <paramref name="errorSink"/> is provided, compiler error messages are also added to it.
    /// </summary>
    internal static CompileResult? Compile(List<(string Name, Microsoft.CodeAnalysis.SyntaxTree Tree)> namedTrees,
        List<Microsoft.CodeAnalysis.MetadataReference>? preloadedReferences = null,
        IList<string>? errorSink = null)
    {
        // Assign deduplicated file paths to trees for readable Roslyn diagnostics
        var nameCount = new Dictionary<string, int>();
        var syntaxTrees = namedTrees.Select(t =>
        {
            var baseName = t.Name;
            if (nameCount.TryGetValue(baseName, out int count))
            {
                nameCount[baseName] = count + 1;
                baseName = $"{baseName}_{count + 1}";
            }
            else
            {
                nameCount[baseName] = 1;
            }
            // Re-root the tree with a file path for diagnostics
            return t.Tree.WithFilePath($"{baseName}.cs");
        }).ToList();

        return CompileFromTrees(syntaxTrees, preloadedReferences, errorSink);
    }

    /// <summary>
    /// Prepare MetadataReferences for Roslyn compilation. Can be called early
    /// (e.g. in parallel with rewriting) since reference loading is independent
    /// of the source code being compiled.
    /// </summary>
    /// <summary>
    /// Curated set of .NET runtime assemblies needed by BC-generated C# code.
    /// Using a curated set instead of all 160+ System.*.dll reduces both reference
    /// loading time and Roslyn semantic analysis overhead.
    /// </summary>
    private static readonly HashSet<string> RequiredRuntimeAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        // Core type system
        "System.Runtime.dll",
        "System.Runtime.Extensions.dll",
        "System.Runtime.InteropServices.dll",
        "System.Runtime.InteropServices.RuntimeInformation.dll",
        // Collections (used in generated code via System.Collections + System.Collections.Generic)
        "System.Collections.dll",
        "System.Collections.Concurrent.dll",
        "System.Collections.Immutable.dll",
        "System.Collections.NonGeneric.dll",
        "System.Collections.Specialized.dll",
        // String/text
        "System.Text.RegularExpressions.dll",
        "System.Text.Encoding.dll",
        "System.Text.Encoding.Extensions.dll",
        // Threading (used in generated code via System.Threading.Tasks)
        "System.Threading.dll",
        "System.Threading.Tasks.dll",
        "System.Threading.Tasks.Extensions.dll",
        "System.Threading.Thread.dll",
        // LINQ and expressions (used by BC runtime types)
        "System.Linq.dll",
        "System.Linq.Expressions.dll",
        // I/O (used by BC types like InStream/OutStream)
        "System.IO.dll",
        "System.IO.Compression.dll",
        // Component model (used by BC runtime)
        "System.ComponentModel.dll",
        "System.ComponentModel.Primitives.dll",
        "System.ComponentModel.TypeConverter.dll",
        // Object model (BC runtime uses dynamic/DLR)
        "System.ObjectModel.dll",
        // Console (used by AlDialog.Message)
        "System.Console.dll",
        // XML (used by XmlPort types)
        "System.Xml.dll",
        "System.Xml.Linq.dll",
        "System.Xml.ReaderWriter.dll",
        "System.Xml.XDocument.dll",
        // Globalization (used by format helpers)
        "System.Globalization.dll",
        // Memory (may be needed by BC types)
        "System.Memory.dll",
        "System.Buffers.dll",
        // Diagnostics (used by some BC types)
        "System.Diagnostics.Debug.dll",
        "System.Diagnostics.Tracing.dll",
        // Facades
        "netstandard.dll",
        "mscorlib.dll",
        "System.dll",
        "System.Core.dll",
        "Microsoft.CSharp.dll",
        // Data (BC types reference System.Data internally)
        "System.Data.Common.dll",
        // Net (BC types reference System.Net)
        "System.Net.Primitives.dll",
        "System.Net.Http.dll",
        // Security
        "System.Security.Cryptography.dll",
        "System.Security.Cryptography.Algorithms.dll",
        "System.Security.Cryptography.Primitives.dll",
    };

    public static List<Microsoft.CodeAnalysis.MetadataReference> LoadReferences()
    {
        var references = new System.Collections.Concurrent.ConcurrentBag<Microsoft.CodeAnalysis.MetadataReference>();

        // Collect curated .NET runtime DLLs
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var runtimeDlls = new List<string>();
        foreach (var name in RequiredRuntimeAssemblies)
        {
            var path = Path.Combine(runtimeDir, name);
            if (File.Exists(path))
                runtimeDlls.Add(path);
        }

        var serviceTierPath = FindServiceTierPath();
        var bcDlls = serviceTierPath != null
            ? Directory.GetFiles(serviceTierPath, "Microsoft.Dynamics.Nav.*.dll")
                .Concat(Directory.GetFiles(serviceTierPath, "Microsoft.BusinessCentral.*.dll"))
                .ToList()
            : new List<string>();

        // Load all references in parallel
        var allDlls = runtimeDlls.Concat(bcDlls).ToList();
        System.Threading.Tasks.Parallel.ForEach(allDlls, dllPath =>
        {
            try { references.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(dllPath)); }
            catch { /* Skip DLLs that can't be loaded as metadata */ }
        });

        // These two are small, add sequentially
        references.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        references.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
            typeof(AlRunner.Runtime.AlScope).Assembly.Location));

        return references.ToList();
    }

    private static CompileResult? CompileFromTrees(List<Microsoft.CodeAnalysis.SyntaxTree> syntaxTrees)
    {
        return CompileFromTrees(syntaxTrees, null);
    }

    internal static CompileResult? CompileFromTrees(List<Microsoft.CodeAnalysis.SyntaxTree> syntaxTrees,
        List<Microsoft.CodeAnalysis.MetadataReference>? preloadedReferences,
        IList<string>? errorSink = null)
    {
        var references = preloadedReferences ?? LoadReferences();

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "AlRunnerGenerated",
            syntaxTrees,
            references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary)
                .WithAllowUnsafe(true));

        using var ms = new MemoryStream(512 * 1024);
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .ToList();
            Console.Error.WriteLine($"Roslyn compilation failed ({errors.Count} errors):");
            foreach (var d in errors)
            {
                var formatted = SourceLineMapper.FormatDiagnostic(d);
                Console.Error.WriteLine($"  {formatted}");
                errorSink?.Add(formatted);
            }
            Console.Error.WriteLine();
            Console.Error.WriteLine("  ⚑ These errors may indicate AL constructs not yet handled by the runner's rewriter.");
            Console.Error.WriteLine("  Use --dump-rewritten to inspect the rewritten C# code.");
            Console.Error.WriteLine("  You may be prompted to report this via telemetry in interactive mode (run with --no-telemetry to opt out).");
            return null;
        }

        ms.Seek(0, SeekOrigin.Begin);
        // Collectible ALC: the assembly (and its ALC) stays alive as long as any
        // reference to the Assembly or ALC exists (e.g., in CompilationCache).
        // Callers must call alc.Unload() when the assembly is no longer needed.
        var alc = new System.Runtime.Loader.AssemblyLoadContext($"TestRun_{Guid.NewGuid():N}", isCollectible: true);
        var assembly = alc.LoadFromStream(ms);
        return new CompileResult(assembly, alc);
    }

    private const string BcArtifactVersion = "27.5.46862.48827";
    private const string BcArtifactUrl = "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/sandbox/" + BcArtifactVersion + "/platform";

    private static string? FindServiceTierPath()
    {
        // 1. Explicit override via environment variable
        var envPath = Environment.GetEnvironmentVariable("BC_SERVICE_TIER_PATH");
        if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath))
            return envPath;

        // 2. Local artifacts directory (relative to CWD or binary)
        const string relPath = "artifacts/sandbox/" + BcArtifactVersion + "/platform/ServiceTier/PFiles64/Microsoft Dynamics NAV/270/Service";
        var candidates = new[]
        {
            Path.GetFullPath(relPath),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", relPath)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../..", relPath)),
        };

        foreach (var c in candidates)
        {
            if (Directory.Exists(c))
                return c;
        }

        // 3. Cross-platform cache directory
        var cacheDir = ArtifactDownloader.GetDefaultCacheDir(BcArtifactVersion);
        if (Directory.Exists(cacheDir) && Directory.GetFiles(cacheDir, "Microsoft.Dynamics.Nav.Types.dll").Length > 0)
            return cacheDir;

        // 4. Auto-download via HTTP range requests
        Log.Info("BC Service Tier DLLs not found locally. Downloading...");
        var downloaded = ArtifactDownloader.DownloadServiceTierDlls(BcArtifactUrl, cacheDir);
        if (downloaded != null)
            return downloaded;

        Console.Error.WriteLine("Error: Could not obtain BC service tier DLLs.");
        Console.Error.WriteLine("  Set BC_SERVICE_TIER_PATH environment variable to provide them manually.");
        return null;
    }
}

// ===========================================================================
// Executor: find OnRun scope, create instance, invoke
// ===========================================================================
public static class Executor
{
    /// <summary>
    /// Scan rewritten C# sources for StmtHit(N) and CStmtHit(N) call sites
    /// to register total statement count for coverage tracking.
    /// </summary>
    public static void RegisterStatements(List<(string Name, string Code)> rewrittenSources)
    {
        var stmtPattern = new System.Text.RegularExpressions.Regex(
            @"\b(?:StmtHit|CStmtHit)\((\d+)\)");
        var classPattern = new System.Text.RegularExpressions.Regex(
            @"class\s+(\w+_Scope(?:_\w+)?)");

        foreach (var (name, code) in rewrittenSources)
        {
            string? currentScope = null;
            foreach (var line in code.Split('\n'))
            {
                var classMatch = classPattern.Match(line);
                if (classMatch.Success)
                    currentScope = classMatch.Groups[1].Value;

                if (currentScope == null) continue;

                foreach (System.Text.RegularExpressions.Match m in stmtPattern.Matches(line))
                {
                    var id = int.Parse(m.Groups[1].Value);
                    AlRunner.Runtime.AlScope.RegisterStatement(currentScope, id);
                }
            }
        }
    }

    /// <summary>Print coverage report showing per-codeunit and overall coverage.</summary>
    public static void PrintCoverageReport()
    {
        var (hit, total) = AlRunner.Runtime.AlScope.GetOverallCoverage();
        if (total == 0)
        {
            Console.WriteLine("\nCoverage: no statements tracked");
            return;
        }

        var byType = AlRunner.Runtime.AlScope.GetCoverageByType();

        // Group scope classes by parent codeunit for readable output
        // Scope names: ApplyDiscount_Scope_123456 -> belongs to parent codeunit class
        Console.WriteLine();
        Console.WriteLine("Coverage:");

        // Map scope names to readable names (strip _Scope_NNNN suffix)
        foreach (var (typeName, typeHit, typeTotal) in byType)
        {
            // Skip test scope classes — only show coverage for code under test
            if (typeName.StartsWith("Test", StringComparison.OrdinalIgnoreCase))
                continue;

            var displayName = typeName;
            var scopeIdx = typeName.IndexOf("_Scope_");
            if (scopeIdx > 0)
                displayName = typeName.Substring(0, scopeIdx);

            var pct = typeTotal > 0 ? (typeHit * 100 / typeTotal) : 0;
            Console.WriteLine($"  {displayName,-40} {typeHit,3}/{typeTotal,-3} statements ({pct}%)");
        }

        // Overall (excluding test scopes)
        var nonTestHit = byType.Where(b => !b.TypeName.StartsWith("Test", StringComparison.OrdinalIgnoreCase)).Sum(b => b.Hit);
        var nonTestTotal = byType.Where(b => !b.TypeName.StartsWith("Test", StringComparison.OrdinalIgnoreCase)).Sum(b => b.Total);
        if (nonTestTotal > 0)
        {
            var overallPct = nonTestHit * 100 / nonTestTotal;
            Console.WriteLine($"  {"TOTAL",-40} {nonTestHit,3}/{nonTestTotal,-3} statements ({overallPct}%)");
        }
    }

    /// <summary>
    /// Number of times <see cref="FireInitEvent"/> was invoked during the most
    /// recent <see cref="RunTests"/> call. Exposed so tests can prove that
    /// init-events fire only once per run (not once per codeunit/test).
    /// </summary>
    public static int InitEventFireCount { get; private set; }

    /// <summary>
    /// Fire an init-lifecycle event, silently swallowing "record already exists"
    /// errors from subscribers.
    ///
    /// Why: the runner fires both codeunit-2 and codeunit-27 OnCompanyInitialize
    /// events in a single init cycle. Real-world extension subscribers sometimes
    /// unconditionally call Record.Insert() without a prior Get/FindFirst guard.
    /// When the same subscriber (or two subscribers inserting the same PK) fires
    /// more than once per init cycle, the second Insert() raises "already exists".
    /// Aborting the entire test run for that would be too harsh — BC itself ignores
    /// double-init scenarios. We swallow duplicate-PK errors during init firing only.
    /// </summary>
    private static void FireInitEvent(int publisherId, string eventName)
    {
        InitEventFireCount++;
        try
        {
            AlRunner.Runtime.AlCompat.FireEvent(publisherId, eventName);
        }
        catch (Exception ex) when (ex.Message.Contains("already exists"))
        {
            // Duplicate-PK from an always-insert subscriber — harmless during init.
            // The first insert succeeded; swallow the rest.
        }
    }

    public static List<AlRunner.TestResult> RunTests(Assembly assembly, bool captureValues = false, string? runProcedure = null, bool initEvents = false, AlRunner.TestIsolation testIsolation = AlRunner.TestIsolation.Codeunit, int testTimeoutSeconds = 0)
    {
        // Find test methods using [NavTest] attribute on the parent method,
        // then find the corresponding _Scope_ nested class.
        var testScopes = new List<(string TestName, Type ScopeType, Type ParentType)>();

        foreach (var type in assembly.GetTypes())
        {
            // Find methods with [NavTest] attribute
            var testMethodNames = new HashSet<string>();
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttributes().Any(a => a.GetType().Name == "NavTestAttribute"))
                {
                    testMethodNames.Add(method.Name);
                }
            }

            // Find corresponding scope classes for test methods.
            // Only include scopes whose parent method bears [NavTest] — the
            // StartsWith("Test") fallback caused phantom tests for any
            // procedure named Test* on tables, pages, or non-test codeunits
            // (issue #1420).
            foreach (var nested in type.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            {
                var name = nested.Name;
                if (name.Contains("_Scope_") && !name.Contains("OnRun_Scope"))
                {
                    var scopeIdx = name.IndexOf("_Scope_");
                    var testName = name.Substring(0, scopeIdx);
                    // Include ONLY if the parent method has [NavTest] attribute.
                    if (testMethodNames.Contains(testName))
                    {
                        testScopes.Add((testName, nested, type));
                    }
                }
            }
        }

        // Filter to a single procedure if requested
        if (runProcedure != null)
        {
            testScopes = testScopes.Where(t =>
                t.TestName.Equals(runProcedure, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (testScopes.Count == 0)
        {
            var msg = runProcedure != null
                ? $"Error: Procedure '{runProcedure}' not found in the generated code."
                : "Error: No test methods found in the generated code.";
            Console.Error.WriteLine(msg);
            Log.Info("Available types:");
            foreach (var t in assembly.GetTypes())
            {
                Log.Info($"  {t.FullName}");
                foreach (var n in t.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                    Log.Info($"    {n.Name}");
            }
            return new List<AlRunner.TestResult>();
        }

        var results = new List<AlRunner.TestResult>();

        // Reset the init-event firing counter so each RunTests invocation reports
        // a clean count (used by tests to assert "fires once, not once-per-codeunit").
        InitEventFireCount = 0;

        // Track codeunit-level isolation state.
        // Null means no reset has happened yet (first test).
        Type? currentCodeunitType = null;
        bool firstReset = true;

        // --init-events baseline: init subscribers fire exactly once at startup,
        // the resulting DB state is captured, and every subsequent isolation reset
        // restores that baseline instead of re-running the subscribers. This matches
        // BC's model where company-initialisation data persists across tests, and
        // removes the N× perf hit of re-firing subscribers per codeunit (#1220).
        Dictionary<int, List<Dictionary<int, NavValue>>>? initTablesBaseline = null;
        Dictionary<string, string>? initStorageBaseline = null;


        foreach (var (testName, scopeType, parentType) in testScopes)
        {
            // Resolve the AL codeunit name for grouping in JUnit output
            var codeunitName = AlRunner.SourceFileMapper.GetObjectForClass(parentType.Name);

            // ---------------------------------------------------------------
            // Isolation logic
            // Codeunit isolation (BC default):  reset tables between codeunits.
            //   Within a codeunit all test methods share the same table state.
            // Method isolation (legacy):         reset tables before every test.
            // ---------------------------------------------------------------
            bool doTableReset = testIsolation == AlRunner.TestIsolation.Method
                || firstReset
                || parentType != currentCodeunitType;

            if (doTableReset)
            {
                // Always reset session-scoped state (SingleInstance codeunits, variable
                // storage). These are documented as not surviving init-events — init
                // subscribers must seed table/isolated-storage data, not codeunit state.
                AlRunner.Runtime.MockVariableStorage.Reset();
                AlRunner.Runtime.MockCodeunitHandle.ResetSingleInstances();

                if (initEvents && initTablesBaseline != null)
                {
                    // Fast path: restore from the init-events baseline captured on
                    // the first reset. No re-firing of subscribers — the snapshotted
                    // DB state is the contract. Test isolation sits on top of it.
                    AlRunner.Runtime.MockRecordHandle.RestoreSnapshot(initTablesBaseline);
                    AlRunner.Runtime.MockIsolatedStorage.RestoreSnapshot(initStorageBaseline!);
                }
                else
                {
                    // Reset persistent state (tables, isolated storage)
                    AlRunner.Runtime.MockRecordHandle.ResetAll();
                    AlRunner.Runtime.MockIsolatedStorage.ResetAll();

                    // Pre-seed system tables (e.g. User table 2000000120) so that common
                    // AL patterns like User.Get(UserSecurityId()) work out of the box.
                    AlRunner.Runtime.MockRecordHandle.SeedSystemTables();

                    // Fire lifecycle integration events ONCE per run when --init-events
                    // is set, then capture the resulting DB state as the baseline that
                    // subsequent resets restore from.
                    //
                    // Publisher IDs from [NavEventSubscriber] attributes in BC-generated C#:
                    //   OnInstallAppPerDatabase → publisher 2000000010 (BC internal install dispatcher)
                    //   OnInstallAppPerCompany  → publisher 2000000010 (BC internal install dispatcher)
                    //   OnCompanyInitialize     → publisher 2 or 27 (differs across BC versions)
                    if (initEvents)
                    {
                        FireInitEvent(2000000010, "OnInstallAppPerDatabase");
                        FireInitEvent(2000000010, "OnInstallAppPerCompany");
                        FireInitEvent(2, "OnCompanyInitialize");
                        FireInitEvent(27, "OnCompanyInitialize");

                        initTablesBaseline = AlRunner.Runtime.MockRecordHandle.Snapshot();
                        initStorageBaseline = AlRunner.Runtime.MockIsolatedStorage.Snapshot();
                    }
                }

                currentCodeunitType = parentType;
                firstReset = false;
            }

            // Always reset per-test non-persistent state (error state, handlers, session, etc.)
            AlRunner.Runtime.AlScope.ResetLastStatement();
            AlRunner.Runtime.AlScope.LastErrorText = "";
            AlRunner.Runtime.AlScope.LastErrorCode = "";
            AlRunner.Runtime.AlScope.SetWorkDate(Microsoft.Dynamics.Nav.Runtime.NavDate.Default);
            AlRunner.Runtime.AlScope.ResetCollectedErrors();
            AlRunner.Runtime.HandlerRegistry.Reset();
            AlRunner.Runtime.MockSession.Reset();
            AlRunner.Runtime.MockLanguage.Reset();
            AlRunner.Runtime.EventSubscriberRegistry.ResetBindings();

            try
            {
                // Create the parent codeunit instance (needed by scope constructor)
                var parent = RuntimeHelpers.GetUninitializedObject(parentType);

                // Call InitializeComponent() if it exists (initializes codeunit handles)
                var initMethod = parentType.GetMethod("InitializeComponent",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (initMethod != null)
                    initMethod.Invoke(parent, null);

                // Call OnClear() to initialize all application member variables (field initializers
                // are not run by GetUninitializedObject — OnClear resets them to their declared defaults,
                // including NavOption fields with inline option metadata). Issue #1488.
                var onClearMethod = parentType.GetMethod("OnClear",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (onClearMethod != null)
                    try { onClearMethod.Invoke(parent, null); } catch { /* ignore — OnClear is optional */ }

                // Register test handlers (ConfirmHandler, MessageHandler, etc.)
                // The [NavTest] attribute has a Handlers property with comma-separated handler names.
                var testMethod = parentType.GetMethod(testName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (testMethod != null)
                {
                    var navTestAttr = testMethod.GetCustomAttributes()
                        .FirstOrDefault(a => a.GetType().Name == "NavTestAttribute");
                    if (navTestAttr != null)
                    {
                        var handlersProp = navTestAttr.GetType().GetProperty("Handlers");
                        var handlers = handlersProp?.GetValue(navTestAttr) as string;
                        if (!string.IsNullOrWhiteSpace(handlers))
                        {
                            AlRunner.Runtime.HandlerRegistry.RegisterHandlers(parent, parentType, handlers);
                        }
                    }
                }

                // Find the scope constructor
                var ctors = scopeType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                object scope;
                if (ctors.Length > 0 && ctors[0].GetParameters().Length > 0)
                {
                    // Constructor takes the parent codeunit as first parameter, and may have
                    // additional parameters for AL procedure parameters captured by the scope
                    // (e.g. a helper procedure named "TestXxx" with parameters).
                    // Supply the parent for the first param and default values for the rest
                    // to avoid "Parameter count mismatch" when extra params are present.
                    var ctorParams = ctors[0].GetParameters();
                    var args = new object?[ctorParams.Length];
                    args[0] = parent;
                    for (int i = 1; i < ctorParams.Length; i++)
                    {
                        var pt = ctorParams[i].ParameterType;
                        args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                    }
                    scope = ctors[0].Invoke(args);
                }
                else if (ctors.Length > 0)
                {
                    // Parameterless constructor - invoke it to initialize fields
                    scope = ctors[0].Invoke(Array.Empty<object>());
                }
                else
                {
                    scope = RuntimeHelpers.GetUninitializedObject(scopeType);
                }

                var onRunMethod = scopeType.GetMethod("OnRun",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                if (onRunMethod == null)
                {
                    results.Add(new AlRunner.TestResult
                    {
                        Name = testName,
                        Status = AlRunner.TestStatus.Fail,
                        Message = $"OnRun() method not found on {scopeType.Name}",
                        CodeunitName = codeunitName
                    });
                    continue;
                }

                // Detect [ErrorBehavior(ErrorBehavior::Collect)] on the test procedure.
                // BC generates: scope.RunBehavior(false, null, ErrorBehavior.Collect) where
                // ErrorBehavior.Collect = 1 is boxed before the call.  In IL this produces
                // ldc.i4.1 (0x17) immediately followed by box (0x8C).  The executor bypasses
                // RunBehavior() and calls OnRun() directly, so we must activate collecting
                // mode manually using AlScope.RunWithCollecting().
                bool collectingTest = false;
                if (testMethod != null)
                {
                    var ilBytes = testMethod.GetMethodBody()?.GetILAsByteArray();
                    if (ilBytes != null)
                    {
                        for (int i = 0; i < ilBytes.Length - 1; i++)
                        {
                            if (ilBytes[i] == 0x17 && ilBytes[i + 1] == 0x8C) // ldc.i4.1 + box
                            {
                                collectingTest = true;
                                break;
                            }
                        }
                    }
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var timeoutMs = testTimeoutSeconds > 0 ? testTimeoutSeconds * 1000 : 0;

                if (timeoutMs > 0)
                {
                    // Run with per-test timeout: use a background thread so we can
                    // abandon it on timeout without blocking the test runner.
                    // Track which auto-stubbed codeunits are accessed during this test
                    AlRunner.Runtime.MockCodeunitHandle.ResetAutoStubTracking();
                    Exception? threadEx = null;
                    (string, int)? threadLastStmt = null;
                    Dictionary<string, int>? threadScopeTracking = null;
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            if (collectingTest)
                                AlRunner.Runtime.AlScope.RunWithCollecting(() => onRunMethod.Invoke(scope, null));
                            else
                                onRunMethod.Invoke(scope, null);
                        }
                        catch (Exception ex) { threadEx = ex; }
                        finally
                        {
                            // Capture ThreadStatic state before thread exits
                            threadLastStmt = AlRunner.Runtime.AlScope.LastStatementHit;
                            threadScopeTracking = AlRunner.Runtime.AlScope.GetScopeTracking();
                        }
                    });
                    thread.IsBackground = true;
                    thread.Start();
                    if (!thread.Join(timeoutMs))
                    {
                        sw.Stop();

                        // Capture where the code was stuck
                        var lastHit = AlRunner.Runtime.AlScope.LastStatementHit;
                        var locationInfo = "";
                        if (lastHit != null)
                        {
                            var (scopeName, stmtId) = lastHit.Value;
                            var alLine = SourceLineMapper.GetAlLineFromStatement(scopeName, stmtId);
                            var objectName = FormatSingleFrame($"at {scopeName}");
                            locationInfo = alLine != null
                                ? $"\n  Last AL statement: {objectName} (line {alLine})"
                                : $"\n  Last AL scope: {objectName}";
                        }

                        // List auto-stubbed codeunits accessed during this test
                        var accessed = AlRunner.Runtime.MockCodeunitHandle.AccessedAutoStubs;
                        var stubDetails = "";
                        if (accessed != null && !accessed.IsEmpty)
                        {
                            var stubNames = accessed.Distinct()
                                .Where(id => AlRunnerPipeline.AutoStubbedCodeunits.ContainsKey(id))
                                .Select(id => $"{id} \"{AlRunnerPipeline.AutoStubbedCodeunits[id]}\"")
                                .OrderBy(s => s)
                                .ToList();
                            if (stubNames.Count > 0)
                                stubDetails = $"\n  Auto-stubbed codeunits called during this test:\n    " +
                                    string.Join("\n    ", stubNames) +
                                    "\n  Provide implementations for these via --stubs or --dep-dlls.";
                        }

                        var hint = string.IsNullOrEmpty(stubDetails) && string.IsNullOrEmpty(locationInfo)
                            ? " Use --test-timeout 0 to disable timeout, or increase with --test-timeout <seconds>."
                            : "";

                        throw new TimeoutException(
                            $"Test exceeded {testTimeoutSeconds}s timeout." +
                            locationInfo + stubDetails + hint);
                    }
                    // Propagate ThreadStatic state from test thread to main thread
                    if (threadLastStmt != null)
                        AlRunner.Runtime.AlScope.SetLastStatement(threadLastStmt.Value.Item1, threadLastStmt.Value.Item2);
                    if (threadScopeTracking != null)
                        AlRunner.Runtime.AlScope.SetScopeTracking(threadScopeTracking);
                    if (threadEx != null)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(threadEx).Throw();
                }
                else
                {
                    // No timeout: run synchronously (default)
                    if (collectingTest)
                        AlRunner.Runtime.AlScope.RunWithCollecting(() => onRunMethod.Invoke(scope, null));
                    else
                        onRunMethod.Invoke(scope, null);
                }
                sw.Stop();

                // Capture variable values from scope fields if enabled
                if (captureValues)
                    CaptureFieldValues(scope, scopeType, testName, AlRunner.SourceFileMapper.GetObjectForClass(parentType.Name));

                results.Add(new AlRunner.TestResult
                {
                    Name = testName,
                    Status = AlRunner.TestStatus.Pass,
                    DurationMs = sw.ElapsedMilliseconds,
                    CodeunitName = codeunitName
                });
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex as Exception;
                while (inner is TargetInvocationException tie && tie.InnerException != null)
                    inner = tie.InnerException;

                if (inner is NotSupportedException)
                {
                    results.Add(new AlRunner.TestResult
                    {
                        Name = testName,
                        Status = AlRunner.TestStatus.Error,
                        Message = $"{inner!.GetType().Name}: {inner.Message}",
                        StackTrace = FormatStackFrames(inner),
                        AlSourceLine = FindAlSourceLine(inner),
                        AlSourceColumn = FindAlSourceColumn(inner),
                        CodeunitName = codeunitName
                    });
                }
                else if (IsRunnerError(inner!) || IsLikelyRunnerLimitation(inner!))
                {
                    results.Add(new AlRunner.TestResult
                    {
                        Name = testName,
                        Status = AlRunner.TestStatus.Error,
                        Message = $"{inner!.GetType().Name}: {inner.Message}",
                        StackTrace = FormatStackFrames(inner),
                        AlSourceLine = FindAlSourceLine(inner),
                        AlSourceColumn = FindAlSourceColumn(inner),
                        IsRunnerBug = true,
                        CodeunitName = codeunitName
                    });
                }
                else
                {
                    results.Add(new AlRunner.TestResult
                    {
                        Name = testName,
                        Status = AlRunner.TestStatus.Fail,
                        Message = inner!.Message,
                        StackTrace = FormatStackFrames(inner),
                        AlSourceLine = FindAlSourceLine(inner),
                        AlSourceColumn = FindAlSourceColumn(inner),
                        CodeunitName = codeunitName
                    });
                }
            }
            catch (Exception ex)
            {
                if (IsRunnerError(ex) || IsLikelyRunnerLimitation(ex))
                {
                    results.Add(new AlRunner.TestResult
                    {
                        Name = testName,
                        Status = AlRunner.TestStatus.Error,
                        Message = $"{ex.GetType().Name}: {ex.Message}",
                        StackTrace = FormatStackFrames(ex),
                        AlSourceLine = FindAlSourceLine(ex),
                        AlSourceColumn = FindAlSourceColumn(ex),
                        IsRunnerBug = true,
                        CodeunitName = codeunitName
                    });
                }
                else
                {
                    results.Add(new AlRunner.TestResult
                    {
                        Name = testName,
                        Status = AlRunner.TestStatus.Fail,
                        Message = ex.Message,
                        StackTrace = FormatStackFrames(ex),
                        AlSourceLine = FindAlSourceLine(ex),
                        AlSourceColumn = FindAlSourceColumn(ex),
                        CodeunitName = codeunitName
                    });
                }
            }
        }

        return results;
    }

    /// <summary>Print human-readable test results to console.</summary>
    public static void PrintResults(List<AlRunner.TestResult> results, long? totalMs = null, bool verbose = false, bool strict = false)
    {
        // Deduplicate repeated error messages: show each unique message once as a WARN block,
        // then print compact "ERROR TestName (blocked)" lines for affected tests.
        // Only active in non-verbose mode; verbose falls back to full per-test detail.
        var dedupMessages = new HashSet<string>();
        if (!verbose)
        {
            var errorMessageCounts = results
                .Where(r => r.Status == AlRunner.TestStatus.Error && r.Message != null)
                .GroupBy(r => r.Message!)
                .Where(g => g.Count() >= 2)
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var (msg, count) in errorMessageCounts)
            {
                dedupMessages.Add(msg);
                Console.WriteLine($"WARN  {msg}");
                Console.WriteLine($"      ({count} tests blocked — use -v for per-test details)");
                Console.WriteLine();
            }
        }

        foreach (var r in results)
        {
            switch (r.Status)
            {
                case AlRunner.TestStatus.Pass:
                    Console.WriteLine($"PASS  {r.Name} ({r.DurationMs}ms)");
                    break;
                case AlRunner.TestStatus.Fail:
                    Console.WriteLine($"FAIL  {r.Name}");
                    if (r.Message != null) Console.WriteLine($"      {r.Message}");
                    if (LooksLikeFrameworkVersionMismatch(r.Message))
                        Console.WriteLine($"      ℹ This BC version requires a newer .NET runtime. Install .NET 10: https://dotnet.microsoft.com/download/dotnet/10.0");
                    if (r.StackTrace != null) PrintFilteredStackTrace(r.StackTrace);
                    PrintAutoStubWarningIfRelevant(r.StackTrace);
                    break;
                case AlRunner.TestStatus.Error:
                    if (!verbose && r.Message != null && dedupMessages.Contains(r.Message))
                    {
                        Console.WriteLine($"ERROR {r.Name} (blocked)");
                    }
                    else
                    {
                        Console.WriteLine($"ERROR {r.Name}");
                        if (r.Message != null) Console.WriteLine($"      {r.Message}");
                        if (LooksLikeFrameworkVersionMismatch(r.Message))
                            Console.WriteLine($"      ℹ This BC version requires a newer .NET runtime. Install .NET 10: https://dotnet.microsoft.com/download/dotnet/10.0");
                        else if (r.IsRunnerBug)
                            Console.WriteLine($"      ⚑ Runner limitation — update al-runner or file an issue at https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues");
                        else
                            Console.WriteLine($"      Inject this dependency via an AL interface.");
                        if (r.StackTrace != null) PrintFilteredStackTrace(r.StackTrace);
                    }
                    break;
            }
        }

        // In strict mode, print a consolidated blocked-test summary right before the
        // final line so CI logs clearly show WHY the run failed.
        var blockedResults = results
            .Where(r => r.Status == AlRunner.TestStatus.Error)
            .ToList();
        if (strict && blockedResults.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"--- Blocked tests ({blockedResults.Count}) — failing CI with --strict ---");
            var groups = blockedResults
                .GroupBy(r => r.Message ?? "(unknown)")
                .OrderBy(g => g.Key);
            foreach (var group in groups)
            {
                Console.WriteLine($"  Cause: {group.Key}");
                foreach (var r in group)
                    Console.WriteLine($"    × {r.Name}");
            }
        }

        var passed = results.Count(r => r.Status == AlRunner.TestStatus.Pass);
        var failed = results.Count(r => r.Status == AlRunner.TestStatus.Fail);
        var blocked = results.Count(r => r.Status == AlRunner.TestStatus.Error && r.IsRunnerBug);
        var errors = results.Count(r => r.Status == AlRunner.TestStatus.Error && !r.IsRunnerBug);
        var timeStr = totalMs.HasValue ? $" in {(totalMs.Value / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s" : "";
        var parts = new System.Collections.Generic.List<string> { $"{passed} passed" };
        if (failed > 0) parts.Add($"{failed} failed");
        if (blocked > 0) parts.Add($"{blocked} blocked (runner limitation)");
        if (errors > 0) parts.Add($"{errors} errors");
        Console.WriteLine();
        Console.WriteLine(string.Join(", ", parts) + timeStr);
    }

    /// <summary>
    /// Compute exit code from test results.
    /// 0 = all passed; 1 = assertion failures (real bugs); 2 = runner limitations only (no failures).
    /// When strict is true, runner limitations also return 1 instead of 2.
    /// </summary>
    public static int ExitCode(List<AlRunner.TestResult> results, bool strict = false)
    {
        if (results.Count == 0) return 1;
        if (results.Any(r => r.Status == AlRunner.TestStatus.Fail)) return 1;
        if (results.Any(r => r.Status != AlRunner.TestStatus.Pass)) return strict ? 1 : 2;
        return 0;
    }

    private static void CaptureFieldValues(object scope, Type scopeType, string testName, string objectName)
    {
        foreach (var field in scopeType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            // Skip internal fields
            if (field.Name.StartsWith("__") || field.Name == "me") continue;
            // Skip parent codeunit reference
            if (field.Name == "_parent") continue;
            // Skip internal runtime types — codeunit/record handles, variants, etc.
            // Their .ToString() returns implementation details, not user-meaningful values.
            var typeName = field.FieldType.Name;
            if (typeName == "ITreeObject" || typeName.StartsWith("NavMethodScope")) continue;
            if (typeName.StartsWith("Mock")) continue;
            // Skip BC plumbing fields (β/γ prefixed)
            if (field.Name.Length > 0 && (field.Name[0] == '\u03b2' || field.Name[0] == '\u03b3')) continue;

            try
            {
                var value = field.GetValue(scope);
                AlRunner.Runtime.ValueCapture.Capture(testName, objectName, field.Name, value, 0);
            }
            catch { /* skip fields that can't be read */ }
        }
    }

    /// <summary>
    /// Get the AL source line from the last StmtHit that was executed before the error.
    /// Uses SourceLineMapper to resolve the statement ID to an AL line number.
    /// </summary>
    private static int? FindAlSourceLine(Exception _)
    {
        var lastHit = AlRunner.Runtime.AlScope.LastStatementHit;
        if (lastHit == null) return null;
        var (typeName, stmtId) = lastHit.Value;
        return SourceLineMapper.GetAlLineFromStatement(typeName, stmtId);
    }

    /// <summary>
    /// Returns true when the exception originates from AlRunner.Runtime mock code,
    /// or from a missing BC runtime DLL (FileNotFoundException / FileLoadException
    /// for a Microsoft.Dynamics.Nav.* or Microsoft.BusinessCentral.* assembly),
    /// indicating a runner limitation rather than a user test logic failure.
    /// These should be reported as <see cref="AlRunner.TestStatus.Error"/> with
    /// <see cref="AlRunner.TestResult.IsRunnerBug"/> = true.
    /// </summary>
    public static bool IsRunnerError(Exception ex)
    {
        if (ex is InvalidOperationException &&
            ex.StackTrace?.Contains("AlRunner.Runtime.Mock") == true)
            return true;

        if (IsMissingBcRuntimeDll(ex))
            return true;

        // Walk InnerException (e.g. TypeInitializationException wrapping FileNotFoundException)
        if (ex.InnerException != null && IsRunnerError(ex.InnerException))
            return true;

        // AggregateException can wrap multiple load failures.
        if (ex is AggregateException agg)
            foreach (var inner in agg.InnerExceptions)
                if (inner != null && IsRunnerError(inner))
                    return true;

        // ReflectionTypeLoadException exposes nested loader failures via LoaderExceptions.
        if (ex is ReflectionTypeLoadException rtle)
            foreach (var loader in rtle.LoaderExceptions)
                if (loader != null && IsRunnerError(loader))
                    return true;

        return false;
    }

    /// <summary>
    /// Returns true when the exception is likely a runner limitation based on
    /// heuristic analysis of the exception type or call-stack origin.
    /// Used as a generic catch-all for unrecognized exceptions that fall through
    /// the specific handlers so they are reported as <see cref="AlRunner.TestStatus.Error"/>
    /// rather than <see cref="AlRunner.TestStatus.Fail"/>.
    /// <list type="bullet">
    ///   <item><term><see cref="MissingMethodException"/> / <see cref="MissingMemberException"/></term>
    ///     <description>A BC runtime method exists in the AL language but has not yet been
    ///     mocked by the runner — always a runner limitation.</description></item>
    ///   <item><term>Exception originating from BC runtime DLLs</term>
    ///     <description>When the <em>innermost</em> stack frame (where the exception was thrown)
    ///     belongs to <c>Microsoft.Dynamics.Nav.*</c> or <c>Microsoft.BusinessCentral.*</c>,
    ///     the crash happened inside BC service-tier code that requires context (e.g.
    ///     <c>NavSession</c>) that the runner does not provide.
    ///     Only the first frame is examined to avoid false positives from BC runtime frames
    ///     that appear further up the stack during normal AL execution.</description></item>
    /// </list>
    /// </summary>
    public static bool IsLikelyRunnerLimitation(Exception ex)
    {
        // A missing method means a BC runtime call wasn't intercepted by the rewriter
        // or mocked in the Runtime layer — always a runner gap.
        if (ex is MissingMethodException or MissingMemberException)
            return true;

        // An exception whose innermost frame originates from BC runtime DLLs indicates
        // the runner is calling service-tier code without the required context.
        // Only the innermost frame (frame 0) is checked so that BC frames appearing
        // deeper in the call stack during normal AL execution do not produce false positives.
        var throwingNamespace = new System.Diagnostics.StackTrace(ex)
            .GetFrame(0)?
            .GetMethod()?
            .DeclaringType?
            .Namespace;
        if (throwingNamespace != null &&
            (throwingNamespace.StartsWith("Microsoft.Dynamics.Nav.", StringComparison.Ordinal) ||
             throwingNamespace.StartsWith("Microsoft.BusinessCentral.", StringComparison.Ordinal)))
            return true;

        return false;
    }

    public static bool IsMissingBcRuntimeAssemblyName(string? value) =>
        value?.Contains("Microsoft.Dynamics.Nav.", StringComparison.Ordinal) == true ||
        value?.Contains("Microsoft.BusinessCentral.", StringComparison.Ordinal) == true;

    /// <summary>
    /// Returns true when the exception represents a missing BC runtime DLL.
    /// Prefers the structured FileName property over the (potentially localised) Message.
    /// </summary>
    public static bool IsMissingBcRuntimeDll(Exception ex) => ex switch
    {
        System.IO.FileNotFoundException fnf =>
            IsMissingBcRuntimeAssemblyName(fnf.FileName) ||
            IsMissingBcRuntimeAssemblyName(fnf.Message),
        System.IO.FileLoadException fle =>
            IsMissingBcRuntimeAssemblyName(fle.FileName) ||
            IsMissingBcRuntimeAssemblyName(fle.Message),
        _ => false
    };

    /// <summary>
    /// Returns true when a test result message looks like a .NET framework assembly
    /// version mismatch — e.g. BC 28 DLLs requesting System.Text.Json 10.0 on .NET 8.
    /// <summary>
    /// Print a filtered stack trace that shows AL-relevant frames instead of
    /// the full C# stack. Strips internal AlRunner frames and highlights the
    /// AL object (Codeunit/Record/Page) that caused the error.
    /// </summary>
    private static void PrintFilteredStackTrace(string stackTrace)
    {
        var lines = stackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var printed = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            // Skip internal runtime frames — keep only AL-generated code and Assert frames
            if (line.Contains("AlRunner.Runtime.") && !line.Contains("MockAssert"))
                continue;
            if (line.Contains("System.Reflection."))
                continue;
            if (line.Contains("System.RuntimeMethodHandle."))
                continue;
            Console.WriteLine($"      {FormatSingleFrame(line)}");
            printed++;
        }
        if (printed == 0 && lines.Length > 0)
        {
            // Fallback: print first few raw lines if nothing matched
            foreach (var line in lines.Take(10))
                Console.WriteLine($"      {line.Trim()}");
        }
    }

    /// <summary>
    /// If the stack trace references any auto-stubbed codeunit, print a warning
    /// explaining that the test likely failed because stubbed methods return defaults.
    /// </summary>
    private static void PrintAutoStubWarningIfRelevant(string? stackTrace)
    {
        if (stackTrace == null || AlRunnerPipeline.AutoStubbedCodeunits.Count == 0) return;

        var involvedStubs = new List<(int Id, string Name)>();
        foreach (var (id, name) in AlRunnerPipeline.AutoStubbedCodeunits)
        {
            if (stackTrace.Contains($"Codeunit{id}"))
                involvedStubs.Add((id, name));
        }

        if (involvedStubs.Count == 0) return;

        foreach (var (id, name) in involvedStubs)
            Console.WriteLine($"      ⚠ Called auto-stubbed codeunit {id} \"{name}\" (methods return defaults — no records created).");
        Console.WriteLine($"      Compile real implementations: al-runner --compile-dep <app>.app .deps --packages .alpackages/");
    }

    /// Used to print actionable guidance; does NOT change error classification.
    /// </summary>
    public static bool LooksLikeFrameworkVersionMismatch(string? message)
    {
        if (message == null) return false;
        if (!message.Contains("Could not load file or assembly", StringComparison.OrdinalIgnoreCase))
            return false;
        return message.Contains("'System.", StringComparison.Ordinal) ||
               message.Contains("'Microsoft.Extensions.", StringComparison.Ordinal);
    }

    /// <summary>
    /// Get the AL source column from the last StmtHit that was executed before
    /// the error. Column info is sourced from the same source-span encoding
    /// used by <see cref="FindAlSourceLine"/>.
    /// </summary>
    private static int? FindAlSourceColumn(Exception _)
    {
        var lastHit = AlRunner.Runtime.AlScope.LastStatementHit;
        if (lastHit == null) return null;
        var (typeName, stmtId) = lastHit.Value;
        var pos = SourceLineMapper.GetAlPositionFromStatement(typeName, stmtId);
        return pos?.Column > 0 ? pos?.Column : null;
    }

    private static string? FormatStackFrames(Exception ex)
    {
        var allFrames = ex.StackTrace?.Split('\n')
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrEmpty(f))
            .ToList();
        if (allFrames == null || allFrames.Count == 0) return null;

        // Prefer AL-generated frames (BusinessApplication namespace) over runtime internals
        var alFrames = allFrames
            .Where(f => f.Contains("BusinessApplication.") || f.Contains("MockAssert"))
            .ToList();

        var frames = alFrames.Count > 0 ? alFrames : allFrames.Take(10).ToList();
        return string.Join("\n", frames.Select(f => $"      {FormatSingleFrame(f)}")) + "\n";
    }

    /// <summary>
    /// Regex matching a BC-generated scope class name embedded in a .NET stack frame.
    ///
    /// Scope classes are nested inside the outer codeunit class, so the .NET stack frame shows:
    ///   at Namespace.Codeunit72336687.CreateTestJournalLine_Scope_abc.OnRun()
    ///
    /// After namespace stripping ("at Microsoft.Dynamics.Nav.BusinessApplication." → "at "):
    ///   at Codeunit72336687.CreateTestJournalLine_Scope_abc.OnRun()
    ///
    /// Captures:
    ///   Group 1 — codeunit ID digits             e.g. "72336687"
    ///   Group 2 — full scope class name           e.g. "CreateTestJournalLine_Scope_abc"
    ///   Group 3 — procedure name (lazy min match) e.g. "CreateTestJournalLine"
    ///
    /// The trailing dot after group 2 anchors the match so we don't run past the class name
    /// into the method call portion of the frame.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex _scopeClassPattern =
        new(@"Codeunit(\d+)\.((\w+?)_Scope(?:_\w+)?)\.",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Format a single stack frame for AL-level readability.
    ///
    /// When the frame contains a recognized BC scope class (CodeunitNNNNN.ProcName_Scope_hash.),
    /// produces: at Codeunit "Name".Procedure() line N in File.al
    /// using per-scope last-statement tracking (populated by StmtHit/CStmtHit) and
    /// SourceLineMapper / SourceFileMapper for resolution.
    ///
    /// For all other frames:
    /// - Strip C# namespace prefix
    /// - Replace CodeunitNNNNN with Codeunit "Name" (via CodeunitNameRegistry)
    /// - Replace RecordNNNNN with Record "Name" (via TableFieldRegistry)
    /// - Strip _Scope_ suffixes
    /// </summary>
    private static string FormatSingleFrame(string frame)
    {
        var clean = frame.Replace("at Microsoft.Dynamics.Nav.BusinessApplication.", "at ");

        // Try to produce a full AL-level frame: at Codeunit "Name".Proc() line N in File.al
        // Scope class frames look like: at Codeunit72336687.CreateTestJournalLine_Scope_abc.OnRun()
        var scopeMatch = _scopeClassPattern.Match(clean);
        if (scopeMatch.Success)
        {
            var codeunitIdStr  = scopeMatch.Groups[1].Value; // e.g. "72336687"
            var scopeClassName = scopeMatch.Groups[2].Value; // e.g. "CreateTestJournalLine_Scope_abc"
            var procName       = scopeMatch.Groups[3].Value; // e.g. "CreateTestJournalLine"

            // Resolve codeunit name
            string codeunitLabel;
            if (int.TryParse(codeunitIdStr, out var codeunitId))
            {
                var name = AlRunner.Runtime.CodeunitNameRegistry.GetNameById(codeunitId);
                codeunitLabel = name != null ? $"Codeunit \"{name}\"" : $"Codeunit{codeunitIdStr}";
            }
            else
            {
                codeunitLabel = $"Codeunit{codeunitIdStr}";
            }

            // Skip OnRun scopes — these are the codeunit body, not a named procedure.
            // Showing ".OnRun()" would be confusing; fall through to the old format.
            var procPart = procName != "OnRun" ? $".{procName}()" : "";

            // Resolve AL line and file from per-scope last-statement tracking.
            // scopeClassName (e.g. "CreateTestJournalLine_Scope_abc") matches the key stored
            // by StmtHit via GetType().Name on the nested scope class.
            var lineInfo = "";
            var fileInfo = "";
            var lastStmtId = AlRunner.Runtime.AlScope.GetLastStmtForScope(scopeClassName);
            if (lastStmtId.HasValue)
            {
                var alPos = SourceLineMapper.GetAlPositionFromStatement(scopeClassName, lastStmtId.Value);
                if (alPos.HasValue)
                {
                    lineInfo = $" line {alPos.Value.Line}";

                    // Resolve file: look up codeunit class name → AL object name → file
                    var codeunitClassName = $"Codeunit{codeunitIdStr}";
                    var objectName = SourceFileMapper.GetObjectForClass(codeunitClassName);
                    var file = SourceFileMapper.GetFile(objectName);
                    if (file != null)
                        fileInfo = $" in {Path.GetFileName(file)}";
                }
            }

            return $"at {codeunitLabel}{procPart}{lineInfo}{fileInfo}";
        }

        // Fallback: resolve object names but keep the raw frame shape
        // Resolve Codeunit IDs to names: Codeunit72336722 → Codeunit "EventSubsCABQR"
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"Codeunit(\d+)", m =>
        {
            if (int.TryParse(m.Groups[1].Value, out var id))
            {
                var name = AlRunner.Runtime.CodeunitNameRegistry.GetNameById(id);
                if (name != null) return $"Codeunit \"{name}\"";
            }
            return m.Value;
        });

        // Resolve Record IDs to names: Record72336618 → Record "Payment Request"
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"Record(\d+)", m =>
        {
            if (int.TryParse(m.Groups[1].Value, out var id))
            {
                var name = AlRunner.Runtime.TableFieldRegistry.GetTableName(id);
                if (name != null) return $"Record \"{name}\"";
            }
            return m.Value;
        });

        // Strip _Scope_ suffixes for readability
        var scopeIdx = clean.IndexOf("_Scope_");
        if (scopeIdx > 0 && clean.StartsWith("at "))
        {
            var dotIdx = clean.LastIndexOf('.', scopeIdx);
            if (dotIdx > 3)
                clean = "at " + clean.Substring(3, dotIdx - 3);
        }

        return clean;
    }

    /// <summary>Run the first OnRun trigger found in the assembly (inline-code mode).</summary>
    public static int RunOnRun(Assembly assembly, bool captureValues = false)
    {
        Type? scopeType = null;
        foreach (var type in assembly.GetTypes())
        {
            foreach (var nested in type.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (nested.Name.Contains("OnRun_Scope"))
                {
                    scopeType = nested;
                    break;
                }
            }
            if (scopeType != null) break;
        }
        if (scopeType == null)
        {
            Console.Error.WriteLine("Error: No OnRun trigger found in the generated code.");
            return 1;
        }
        return RunOnRunScope(scopeType, captureValues);
    }

    /// <summary>Run a named codeunit's OnRun trigger explicitly (--run-codeunit mode).</summary>
    public static int RunOnRun(Assembly assembly, string codeunitName, bool captureValues = false)
    {
        // Find the codeunit class whose name matches (case-insensitive)
        Type? parentType = null;
        foreach (var type in assembly.GetTypes())
        {
            var displayName = type.Name;
            var nameAttr = type.GetCustomAttributes()
                .FirstOrDefault(a => a.GetType().Name is "NavObjectNameAttribute" or "NavDisplayNameAttribute");
            if (nameAttr != null)
            {
                var nameProp = nameAttr.GetType().GetProperty("Name") ?? nameAttr.GetType().GetProperty("DisplayName");
                displayName = nameProp?.GetValue(nameAttr) as string ?? displayName;
            }
            if (string.Equals(displayName, codeunitName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type.Name, codeunitName, StringComparison.OrdinalIgnoreCase))
            {
                parentType = type;
                break;
            }
        }

        if (parentType == null)
        {
            Console.Error.WriteLine($"Error: Codeunit '{codeunitName}' not found in the generated code.");
            Console.Error.WriteLine("Available codeunits:");
            foreach (var t in assembly.GetTypes().Where(t => t.Name.StartsWith("Codeunit", StringComparison.OrdinalIgnoreCase)))
                Console.Error.WriteLine($"  {t.Name}");
            return 1;
        }

        // Codeunits with TableNo set require a record to be passed in — skip them.
        var tableNoAttr = parentType.GetCustomAttributes()
            .FirstOrDefault(a => a.GetType().Name == "NavObjectTableNoAttribute");
        if (tableNoAttr != null)
        {
            var tableNoProp = tableNoAttr.GetType().GetProperty("TableNo") ?? tableNoAttr.GetType().GetProperty("Value");
            var tableNo = tableNoProp?.GetValue(tableNoAttr);
            Console.Error.WriteLine($"Skipping '{codeunitName}': TableNo = {tableNo} — this codeunit requires a record to be passed in and cannot run standalone.");
            Console.Error.WriteLine("Call it from a test codeunit via Codeunit.Run() with a populated record instead.");
            return 2;
        }

        var scopeType = parentType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(n => n.Name.Contains("OnRun_Scope"));

        if (scopeType == null)
        {
            Console.Error.WriteLine($"Error: Codeunit '{codeunitName}' has no OnRun trigger.");
            return 1;
        }
        return RunOnRunScope(scopeType, captureValues);
    }

    private static int RunOnRunScope(Type scopeType, bool captureValues)
    {
        var parentType = scopeType.DeclaringType!;
        var parent = RuntimeHelpers.GetUninitializedObject(parentType);

        // Call InitializeComponent() if it exists
        var initMethod = parentType.GetMethod("InitializeComponent",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        initMethod?.Invoke(parent, null);

        // Try to create scope via constructor (which initializes fields like MockRecordHandle)
        object? scope = null;
        foreach (var ctor in scopeType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            var ctorParams = ctor.GetParameters();
            try
            {
                var ctorArgs = new object?[ctorParams.Length];
                for (int i = 0; i < ctorParams.Length; i++)
                {
                    var pt = ctorParams[i].ParameterType;
                    if (pt == parentType)
                        ctorArgs[i] = parent;
                    else if (pt.IsValueType)
                        ctorArgs[i] = Activator.CreateInstance(pt);
                    else
                        ctorArgs[i] = null;
                }
                scope = ctor.Invoke(ctorArgs);
                break;
            }
            catch { /* try next constructor */ }
        }

        // Fallback: GetUninitializedObject if no constructor worked
        if (scope == null)
            scope = RuntimeHelpers.GetUninitializedObject(scopeType);

        var onRunMethod = scopeType.GetMethod("OnRun",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        if (onRunMethod == null)
        {
            Console.Error.WriteLine($"Error: OnRun() method not found on {scopeType.Name}");
            return 1;
        }

        try
        {
            onRunMethod.Invoke(scope, null);
            if (captureValues)
                CaptureFieldValues(scope, scopeType, "OnRun", AlRunner.SourceFileMapper.GetObjectForClass(parentType.Name));
            return 0;
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            // Check if this is an AL Error() call (we throw System.Exception from AlDialog.Error)
            if (inner is Exception alError && inner.StackTrace?.Contains("AlDialog.Error") == true)
            {
                Console.Error.WriteLine($"Error: {inner.Message}");
                return 1;
            }
            Console.Error.WriteLine($"Runtime error: {inner.GetType().Name}: {inner.Message}");
            Console.Error.WriteLine(inner.StackTrace);
            return 1;
        }
    }
}


// ===========================================================================
// C# Capture Outputter (from TranspilerSpike)
// ===========================================================================
public record CapturedObject(string SymbolName, byte[]? CSharpCode, string? Metadata, string? DebugCode);

public class CSharpCaptureOutputter : CodeModuleOutputter
{
    public List<CapturedObject> CapturedObjects { get; } = new();

    public CSharpCaptureOutputter() : base(new EmitOptions()) { }

    public override void InitializeModule(IModuleSymbol moduleSymbol) { }

    public override void AddApplicationObject(IApplicationObjectTypeSymbol symbol, byte[] code, string metadata, string debugCode)
    {
        CapturedObjects.Add(new CapturedObject(symbol.Name, code, metadata, debugCode));
    }

    public override void AddProfileObject(ISymbol symbol, byte[] code, string metadata, string debugCode) { }
    public override void AddNavigationObject(string content) { }
    public override void AddExternalBusinessEvent(string content) { }
    public override void AddMovedObjects(string content) { }
    public override void FinalizeModule() { }
    public override ImmutableArray<Diagnostic> GetDiagnostics() => ImmutableArray<Diagnostic>.Empty;
}

// ===========================================================================
// App Package Reader: extract AL source from .app files (ZIP archives)
// ===========================================================================
public static class AppPackageReader
{
    /// <summary>
    /// Extract all .al source files from a BC .app package.
    /// .app files have a NAVX header followed by a ZIP archive.
    /// The ZIP contains AL source in the src/ directory.
    /// Returns a list of (FileName, SourceCode) pairs, sorted by name.
    /// </summary>
    public static List<(string Name, string Source)> ExtractAlSources(string appPath)
    {
        var results = new List<(string Name, string Source)>();

        var fileBytes = File.ReadAllBytes(appPath);
        int zipOffset = 0;

        // .app files have a NAVX header: 4-byte magic "NAVX" + 4-byte LE uint32 total header size
        // ZipArchive reads the End of Central Directory from the end of the stream,
        // so we must give it a stream containing only the ZIP data.
        if (fileBytes.Length >= 8
            && fileBytes[0] == (byte)'N' && fileBytes[1] == (byte)'A'
            && fileBytes[2] == (byte)'V' && fileBytes[3] == (byte)'X')
        {
            zipOffset = (int)BitConverter.ToUInt32(fileBytes, 4);
        }

        var alEntries = ExtractAlFromNavx(fileBytes, zipOffset);
        if (alEntries.Count > 0)
            return alEntries;

        // Ready2Run package: no AL source in outer package, look for nested .app
        using var zipStream = new MemoryStream(fileBytes, zipOffset, fileBytes.Length - zipOffset);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var nestedApp = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            && !e.FullName.Contains('/'));
        if (nestedApp != null)
        {
            using var nestedStream = nestedApp.Open();
            using var ms = new MemoryStream();
            nestedStream.CopyTo(ms);
            var nestedBytes = ms.ToArray();
            int nestedOffset = 0;
            if (nestedBytes.Length >= 8 && nestedBytes[0] == (byte)'N' && nestedBytes[1] == (byte)'A'
                && nestedBytes[2] == (byte)'V' && nestedBytes[3] == (byte)'X')
                nestedOffset = (int)BitConverter.ToUInt32(nestedBytes, 4);
            return ExtractAlFromNavx(nestedBytes, nestedOffset);
        }

        return results;
    }

    private static List<(string Name, string Source)> ExtractAlFromNavx(byte[] data, int zipOffset)
    {
        var results = new List<(string Name, string Source)>();
        using var zipStream = new MemoryStream(data, zipOffset, data.Length - zipOffset);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries
            .Where(e => e.FullName.StartsWith("src/", StringComparison.OrdinalIgnoreCase)
                     && e.FullName.EndsWith(".al", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName))
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var source = reader.ReadToEnd();
            results.Add((entry.Name, source));
        }
        return results;
    }
}

// ===========================================================================
// Kernel32 Shim (for Linux compatibility with BC DLLs)
// ===========================================================================
public static class Kernel32Shim
{
    private static IntPtr _handle = IntPtr.Zero;
    private static bool _registered = false;

    public static void EnsureRegistered()
    {
        if (_registered) return;
        _registered = true;

        var bcAssemblies = new HashSet<string>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name ?? "";
            if (name.Contains("Nav.Types") || name.Contains("Nav.Ncl") ||
                name.Contains("Nav.Runtime") || name.Contains("Nav.Core") ||
                name.Contains("Nav.Common") || name.Contains("Nav.Language"))
            {
                if (bcAssemblies.Add(name))
                {
                    try { NativeLibrary.SetDllImportResolver(asm, Resolver); }
                    catch (InvalidOperationException) { }
                }
            }
        }

        PreTriggerStaticCtors();
    }

    private static void PreTriggerStaticCtors()
    {
        try
        {
            var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name?.Contains("Nav.Types") == true);
            if (typesAsm != null)
            {
                var wlh = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.WindowsLanguageHelper");
                if (wlh != null) RuntimeHelpers.RunClassConstructor(wlh.TypeHandle);
            }
        }
        catch (TypeInitializationException) { }

        try
        {
            var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name?.Contains("Nav.Ncl") == true);
            if (nclAsm != null)
            {
                var navEnv = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavEnvironment");
                if (navEnv != null) RuntimeHelpers.RunClassConstructor(navEnv.TypeHandle);
            }
        }
        catch (TypeInitializationException) { }
    }

    public static IntPtr Resolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (string.Equals(libraryName, "kernel32.dll", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(libraryName, "kernel32", StringComparison.OrdinalIgnoreCase))
            return GetOrCreate();
        return IntPtr.Zero;
    }

    private static IntPtr GetOrCreate()
    {
        if (_handle != IntPtr.Zero) return _handle;

        var shimDir = Path.Combine(Path.GetTempPath(), "bc-kernel32-shim");
        Directory.CreateDirectory(shimDir);

        var cFile = Path.Combine(shimDir, "kernel32_shim.c");
        var soFile = Path.Combine(shimDir, "libkernel32.so");

        if (!File.Exists(soFile))
        {
            File.WriteAllText(cFile, SHIM_SOURCE);
            var psi = new System.Diagnostics.ProcessStartInfo("gcc",
                $"-shared -fPIC -o \"{soFile}\" \"{cFile}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false
            };
            var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(10000);
            if (proc.ExitCode != 0)
            {
                var err = proc.StandardError.ReadToEnd();
                throw new InvalidOperationException($"gcc failed: {err}");
            }
        }

        _handle = NativeLibrary.Load(soFile);
        return _handle;
    }

    private const string SHIM_SOURCE = @"
#include <stdint.h>
#include <string.h>
typedef uint16_t WCHAR;
static void u16copy(WCHAR* dst, const char* src, int max) {
    int i;
    for (i = 0; src[i] && i < max - 1; i++) dst[i] = (WCHAR)src[i];
    if (i < max) dst[i] = 0;
}
int LCIDToLocaleName(uint32_t lcid, WCHAR* buf, int bufSize, uint32_t flags) {
    const char* name = 0;
    switch (lcid) {
        case 1033: name = ""en-US""; break;
        case 1031: name = ""de-DE""; break;
        case 1036: name = ""fr-FR""; break;
        case 1034: name = ""es-ES""; break;
        case 1040: name = ""it-IT""; break;
        case 1043: name = ""nl-NL""; break;
        case 1044: name = ""nb-NO""; break;
        case 1045: name = ""pl-PL""; break;
        case 1046: name = ""pt-BR""; break;
        case 1049: name = ""ru-RU""; break;
        case 1053: name = ""sv-SE""; break;
        case 2052: name = ""zh-CN""; break;
        case 2057: name = ""en-GB""; break;
        case 0: case 127: name = """"; break;
        default: return 0;
    }
    int len = strlen(name);
    if (!buf || bufSize == 0) return len + 1;
    u16copy(buf, name, bufSize);
    return len + 1;
}
uint32_t GetLastError(void) { return 0; }
void SetLastError(uint32_t e) { }
";
}
