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
    Console.Error.WriteLine("  --stubs <dir>         Override dependency objects with stub AL files");
    Console.Error.WriteLine("  --dump-csharp         Print generated C# (before rewriting) and exit");
    Console.Error.WriteLine("  --dump-rewritten      Print rewritten C# (after rewriting) and exit");
    Console.Error.WriteLine("  -e '<al code>'        Run inline AL code");
    Console.Error.WriteLine("  -v, --verbose         Show detailed transpilation and compilation output");
    Console.Error.WriteLine("  --guide               Print test-writing guide for AI coding agents");
    Console.Error.WriteLine("  -h, --help            Show this help");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Examples:");
    Console.Error.WriteLine("  al-runner ./src ./test                     Run tests");
    Console.Error.WriteLine("  al-runner --coverage ./src ./test          Run tests with coverage");
    Console.Error.WriteLine("  al-runner --packages .alpackages ./src     Run with dependencies");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Test codeunits (Subtype = Test) are auto-detected.");
    Console.Error.WriteLine("BC Service Tier DLLs are auto-downloaded on first run.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("For AI agents: run `al-runner --guide` for a complete test-writing reference.");
    return args.Length == 0 ? 1 : 0;
}

// Parse arguments
bool dumpCSharp = false;
bool dumpRewritten = false;
bool showCoverage = false;
bool verbose = false;
var stubPaths = new List<string>();
var alSources = new List<string>();
var assertStubSources = new List<string>(); // Assert stub AL sources to transpile separately when Assert.app is in packages
var packagePaths = new List<string>();
var inputPaths = new List<string>(); // track input dirs/files for auto-discovery
// Each input group = one .app or directory that should be compiled as a separate AL compilation
var inputGroups = new List<(string Path, List<string> Sources)>();

int argIdx = 0;
while (argIdx < args.Length)
{
    switch (args[argIdx])
    {
        case "--dump-csharp":
            dumpCSharp = true;
            argIdx++;
            break;
        case "--dump-rewritten":
            dumpRewritten = true;
            argIdx++;
            break;
        case "--guide":
            PrintGuide();
            return 0;
        case "--coverage":
            showCoverage = true;
            argIdx++;
            break;
        case "--verbose":
        case "-v":
            verbose = true;
            Log.Verbose = true;
            argIdx++;
            break;
        case "--stubs":
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --stubs requires a directory argument"); return 1; }
            var stubPath = Path.GetFullPath(args[argIdx]);
            if (!Directory.Exists(stubPath)) { Console.Error.WriteLine($"Error: stubs directory not found: {stubPath}"); return 1; }
            stubPaths.Add(stubPath);
            Log.HasStubs = true;
            // Load stub AL files as source
            var stubFiles = Directory.GetFiles(stubPath, "*.al", SearchOption.AllDirectories).OrderBy(f => f).ToList();
            Log.Info($"Loading {stubFiles.Count} stub files from {stubPath}");
            foreach (var sf in stubFiles)
            {
                var stubSrc = File.ReadAllText(sf);
                alSources.Add(stubSrc);
                // Add to all input groups
                foreach (var g in inputGroups)
                    g.Sources.Add(stubSrc);
            }
            argIdx++;
            break;
        case "--packages":
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: --packages requires a directory argument"); return 1; }
            var pkgPath = Path.GetFullPath(args[argIdx]);
            if (!Directory.Exists(pkgPath)) { Console.Error.WriteLine($"Error: packages directory not found: {pkgPath}"); return 1; }
            packagePaths.Add(pkgPath);
            argIdx++;
            break;
        case "-e":
            argIdx++;
            if (argIdx >= args.Length) { Console.Error.WriteLine("Error: -e requires an argument"); return 1; }
            var inlineCode = args[argIdx];
            // Auto-wrap bare AL statements in a codeunit
            if (!inlineCode.TrimStart().StartsWith("codeunit", StringComparison.OrdinalIgnoreCase) &&
                !inlineCode.TrimStart().StartsWith("table", StringComparison.OrdinalIgnoreCase))
            {
                inlineCode = $"codeunit 99 __Inline {{ trigger OnRun() begin {inlineCode} end; }}";
            }
            alSources.Add(inlineCode);
            argIdx++;
            break;
        default:
            var path = args[argIdx];
            if (path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                // Extract AL source files from .app package (ZIP archive)
                var extracted = AppPackageReader.ExtractAlSources(path);
                if (extracted.Count == 0)
                {
                    Console.Error.WriteLine($"Error: no .al files found in app package {path}");
                    return 1;
                }
                Log.Info($"Loading {extracted.Count} AL files from {Path.GetFileName(path)}");
                var groupSources = new List<string>();
                foreach (var (name, source) in extracted)
                {
                    Log.Info($"  {name}");
                    alSources.Add(source);
                    groupSources.Add(source);
                }
                var fullPath = Path.GetFullPath(path);
                inputPaths.Add(fullPath);
                inputGroups.Add((fullPath, groupSources));
            }
            else if (Directory.Exists(path))
            {
                // Load all .al files from directory
                var alFiles = Directory.GetFiles(path, "*.al", SearchOption.AllDirectories)
                    .OrderBy(f => f) // deterministic order
                    .ToList();
                if (alFiles.Count == 0)
                {
                    Console.Error.WriteLine($"Error: no .al files found in directory {path}");
                    return 1;
                }
                Log.Info($"Loading {alFiles.Count} AL files from {path}");
                var groupSources = new List<string>();
                foreach (var f in alFiles)
                {
                    Log.Info($"  {Path.GetFileName(f)}");
                    var src = File.ReadAllText(f);
                    alSources.Add(src);
                    groupSources.Add(src);
                }
                var fullPath = Path.GetFullPath(path);
                inputPaths.Add(fullPath);
                inputGroups.Add((fullPath, groupSources));
            }
            else if (File.Exists(path))
            {
                var src = File.ReadAllText(path);
                alSources.Add(src);
                var fullPath = Path.GetFullPath(Path.GetDirectoryName(path)!);
                inputPaths.Add(fullPath);
                inputGroups.Add((fullPath, new List<string> { src }));
            }
            else
            {
                Console.Error.WriteLine($"Error: file or directory not found: {path}");
                return 1;
            }
            argIdx++;
            break;
    }
}

if (alSources.Count == 0)
{
    Console.Error.WriteLine("Error: no AL source provided");
    return 1;
}

// ---------------------------------------------------------------------------
// Auto-discover dependency .app files from --packages directories.
// Reads the NavxManifest.xml of each input .app to find dependencies,
// then recursively finds matching .app files in the package directories
// and adds them as additional input groups for transpilation.
// ---------------------------------------------------------------------------
if (packagePaths.Count > 0 && inputGroups.Any(g => g.Path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)))
{
    // Build index: app GUID -> .app file path from all package directories
    var appIndex = new Dictionary<Guid, string>();
    foreach (var pkgDir in packagePaths)
    {
        foreach (var appFile in Directory.GetFiles(pkgDir, "*.app", SearchOption.AllDirectories))
        {
            try
            {
                var doc = AlTranspiler.LoadNavxManifest(appFile);
                if (doc == null) continue;
                XNamespace ns = "http://schemas.microsoft.com/navx/2015/manifest";
                var appElement = doc.Root?.Element(ns + "App");
                var idStr = appElement?.Attribute("Id")?.Value;
                if (idStr != null && Guid.TryParse(idStr, out var appGuid))
                {
                    // Prefer non-test apps (Source over Test) by not overwriting
                    if (!appIndex.ContainsKey(appGuid))
                        appIndex[appGuid] = appFile;
                }
            }
            catch { /* skip unreadable .app files */ }
        }
    }

    // Collect GUIDs of apps already provided as inputs
    var inputAppGuids = new HashSet<Guid>();
    foreach (var group in inputGroups)
    {
        if (!group.Path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
        try
        {
            var doc = AlTranspiler.LoadNavxManifest(group.Path);
            if (doc == null) continue;
            XNamespace ns = "http://schemas.microsoft.com/navx/2015/manifest";
            var idStr = doc.Root?.Element(ns + "App")?.Attribute("Id")?.Value;
            if (idStr != null && Guid.TryParse(idStr, out var appGuid))
                inputAppGuids.Add(appGuid);
        }
        catch { }
    }

    // Resolve dependencies transitively
    var toProcess = new Queue<Guid>();
    var discovered = new HashSet<Guid>(inputAppGuids);

    // Seed: deps of all input apps
    foreach (var group in inputGroups)
    {
        if (!group.Path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
        foreach (var depGuid in AlTranspiler.GetDependencyGuids(group.Path))
        {
            if (discovered.Add(depGuid))
                toProcess.Enqueue(depGuid);
        }
    }

    // BFS to find transitive deps
    var autoDiscovered = new List<(Guid Id, string Path)>();
    while (toProcess.Count > 0)
    {
        var depGuid = toProcess.Dequeue();
        if (!appIndex.TryGetValue(depGuid, out var depAppPath)) continue;
        autoDiscovered.Add((depGuid, depAppPath));

        // Also resolve this dep's dependencies
        foreach (var transDepGuid in AlTranspiler.GetDependencyGuids(depAppPath))
        {
            if (discovered.Add(transDepGuid))
                toProcess.Enqueue(transDepGuid);
        }
    }

    // Add discovered deps as input groups
    if (autoDiscovered.Count > 0)
    {
        Log.Info($"\nAuto-discovered {autoDiscovered.Count} dependency app(s) for transpilation:");
        foreach (var (id, depPath) in autoDiscovered)
        {
            var fileName = Path.GetFileName(depPath);
            Log.Info($"  {fileName}");

            var extracted = AppPackageReader.ExtractAlSources(depPath);
            if (extracted.Count == 0) continue;

            var groupSources = new List<string>();
            foreach (var (name, source) in extracted)
            {
                alSources.Add(source);
                groupSources.Add(source);
            }
            var fullPath = Path.GetFullPath(depPath);
            inputPaths.Add(fullPath);
            inputGroups.Add((fullPath, groupSources));
        }
    }
}

// ---------------------------------------------------------------------------
// Step 0: Register kernel32 shim (needed for BC DLLs on Linux)
// ---------------------------------------------------------------------------
Timer.StartStage("Kernel32 shim");
Kernel32Shim.EnsureRegistered();
Timer.EndStage("Kernel32 shim");

// ---------------------------------------------------------------------------
// Auto-include AL stubs (e.g., Library Assert) from the stubs/ directory
// These provide AL declarations for runtime-mocked codeunits so user code compiles.
// ---------------------------------------------------------------------------
{
    // Skip stubs if the packages already contain the real Assert codeunit
    bool packagesHaveAssert = packagePaths.Any(p =>
        Directory.Exists(p) &&
        Directory.GetFiles(p, "*.app", SearchOption.AllDirectories)
            .Any(f => Path.GetFileName(f).Contains("Assert", StringComparison.OrdinalIgnoreCase) ||
                       Path.GetFileName(f).Contains("TestLibraries", StringComparison.OrdinalIgnoreCase)));
    // Also check .alpackages directories discovered from input paths
    if (!packagesHaveAssert)
    {
        foreach (var ip in inputPaths)
        {
            var dir = Directory.Exists(ip) ? ip : Path.GetDirectoryName(ip);
            while (dir != null)
            {
                var alPkgs = Path.Combine(dir, ".alpackages");
                if (Directory.Exists(alPkgs) &&
                    Directory.GetFiles(alPkgs, "*.app", SearchOption.AllDirectories)
                        .Any(f => Path.GetFileName(f).Contains("Assert", StringComparison.OrdinalIgnoreCase)))
                {
                    packagesHaveAssert = true;
                    break;
                }
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            if (packagesHaveAssert) break;
        }
    }

    if (!packagesHaveAssert)
    {
        var stubsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "stubs");
        if (!Directory.Exists(stubsDir))
            stubsDir = Path.Combine(AppContext.BaseDirectory, "stubs");
        if (Directory.Exists(stubsDir))
        {
            Log.Info("Loading Assert stubs (no Assert.app found in packages)");
            foreach (var stubFile in Directory.GetFiles(stubsDir, "*.al", SearchOption.TopDirectoryOnly).OrderBy(f => f))
            {
                var src = File.ReadAllText(stubFile);
                alSources.Add(src);
                foreach (var group in inputGroups)
                    group.Sources.Add(src);
            }
        }
    }
    else
    {
        // When Assert.app is in packages, we still need the stub's transpiled C# in
        // the Roslyn compilation so that Codeunit130 exists in the final assembly.
        // FindAssertMethodName() relies on inspecting the Codeunit130 nested scope
        // classes to distinguish IsTrue from IsFalse, AreEqual from AreNotEqual, etc.
        // We load the stub AL code into a separate list that gets transpiled and added
        // to the C# compilation, but NOT to the AL sources (which would conflict with
        // the Assert.app symbols).
        var stubsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "stubs");
        if (!Directory.Exists(stubsDir))
            stubsDir = Path.Combine(AppContext.BaseDirectory, "stubs");
        if (Directory.Exists(stubsDir))
        {
            foreach (var stubFile in Directory.GetFiles(stubsDir, "*.al", SearchOption.TopDirectoryOnly).OrderBy(f => f))
                assertStubSources.Add(File.ReadAllText(stubFile));
        }
        Log.Info("Skipping Assert stubs for AL compilation (real Assert.app found in packages)");
    }
}

Timer.StartStage("AL transpilation");
// ---------------------------------------------------------------------------
// Step 1: Transpile AL -> C#
// When --packages is specified and there are multiple input groups (e.g. multiple .app files),
// each group is transpiled as its own AL compilation to avoid ambiguous reference errors.
// Other groups act as additional symbol references for each compilation.
// ---------------------------------------------------------------------------
List<(string Name, string Code)>? generatedCSharpList;

bool hasExplicitPackages = packagePaths.Count > 0;
// Multi-app mode only when inputs are .app files (not source directories).
// Source directories should compile together so internal procedures are visible.
bool hasAppInputs = inputGroups.Any(g => g.Path.EndsWith(".app", StringComparison.OrdinalIgnoreCase));
if (hasExplicitPackages && inputGroups.Count > 1 && hasAppInputs)
{
    // Multi-app mode: transpile each input group separately
    generatedCSharpList = new List<(string Name, string Code)>();
    for (int gi = 0; gi < inputGroups.Count; gi++)
    {
        var group = inputGroups[gi];
        Log.Info($"\n--- Transpiling group {gi + 1}/{inputGroups.Count}: {Path.GetFileName(group.Path)} ---");

        // Other input groups' .app files act as additional package paths for this group
        var groupPackagePaths = new List<string>(packagePaths);
        for (int oi = 0; oi < inputGroups.Count; oi++)
        {
            if (oi == gi) continue;
            var otherPath = inputGroups[oi].Path;
            // If it's a .app file, add its parent directory as a package path
            if (otherPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                var parentDir = Path.GetDirectoryName(otherPath)!;
                if (!groupPackagePaths.Contains(parentDir))
                    groupPackagePaths.Add(parentDir);
            }
            else
            {
                // It's a directory — check if it has .alpackages or .app files
                if (!groupPackagePaths.Contains(otherPath))
                    groupPackagePaths.Add(otherPath);
            }
        }

        var groupInputPaths = new List<string> { group.Path };
        var groupResult = AlTranspiler.TranspileMulti(group.Sources, groupPackagePaths, groupInputPaths);
        if (groupResult != null && groupResult.Count > 0)
        {
            Log.Info($"  Transpiled {groupResult.Count} AL objects");
            generatedCSharpList.AddRange(groupResult);
        }
        else
        {
            Console.Error.WriteLine($"  Warning: no C# generated for {Path.GetFileName(group.Path)}");
        }
    }

    if (generatedCSharpList.Count == 0)
    {
        Console.Error.WriteLine("Error: no C# code generated from any input group");
        return 1;
    }
}
else
{
    // Single-compilation mode: all sources compiled together
    generatedCSharpList = AlTranspiler.TranspileMulti(alSources, packagePaths, inputPaths);
    if (generatedCSharpList == null || generatedCSharpList.Count == 0)
        return 1;
}

// If Assert stubs were loaded separately (Assert.app found in packages), transpile
// them now and add to the C# list. The stub provides Codeunit130 type so that
// FindAssertMethodName() can resolve method names (IsTrue vs IsFalse, etc.) at runtime.
if (assertStubSources.Count > 0)
{
    var stubCSharp = AlTranspiler.TranspileMulti(assertStubSources, packagePaths, inputPaths);
    if (stubCSharp != null)
    {
        generatedCSharpList.AddRange(stubCSharp);
        Log.Info($"Added {stubCSharp.Count} Assert stub(s) for runtime dispatch");
    }
}

Log.Info($"\nTranspiled {generatedCSharpList.Count} AL objects to C#");

if (dumpCSharp)
{
    foreach (var (name, code) in generatedCSharpList)
    {
        Console.WriteLine($"=== Generated C# for {name} (before rewriting) ===");
        Console.WriteLine(code);
        Console.WriteLine($"=== End {name} ===\n");
    }
}

Timer.EndStage("AL transpilation");
Timer.StartStage("Roslyn rewriting");
// ---------------------------------------------------------------------------
// Step 2: Rewrite C# for standalone execution (parallel, returning SyntaxTrees)
//         Also start loading MetadataReferences in parallel since they are
//         independent of the rewritten source code.
// ---------------------------------------------------------------------------
var refsTask = System.Threading.Tasks.Task.Run(() => RoslynCompiler.LoadReferences());

var rewrittenTrees = new (string Name, Microsoft.CodeAnalysis.SyntaxTree Tree)[generatedCSharpList.Count];
System.Threading.Tasks.Parallel.For(0, generatedCSharpList.Count, i =>
{
    var (name, code) = generatedCSharpList[i];
    rewrittenTrees[i] = (name, RoslynRewriter.RewriteToTree(code));
});
var rewrittenTreeList = rewrittenTrees.ToList();

// Lazily convert rewritten trees to string form (only needed for dump/debug/mapper)
List<(string Name, string Code)>? _rewrittenStringList = null;
List<(string Name, string Code)> GetRewrittenStrings()
{
    if (_rewrittenStringList == null)
    {
        _rewrittenStringList = rewrittenTreeList
            .Select(t => (t.Name, t.Tree.GetRoot().ToFullString()))
            .ToList();
    }
    return _rewrittenStringList;
}

if (dumpRewritten)
{
    foreach (var (name, code) in GetRewrittenStrings())
    {
        Console.WriteLine($"=== Rewritten C# for {name} ===");
        Console.WriteLine(code);
        Console.WriteLine($"=== End {name} ===\n");
    }
}

Timer.EndStage("Roslyn rewriting");
Timer.StartStage("Roslyn compilation");
// ---------------------------------------------------------------------------
// Step 3: Build AL source line mapping and compile rewritten C# with Roslyn
// ---------------------------------------------------------------------------
SourceLineMapper.Build(generatedCSharpList, GetRewrittenStrings());
var preloadedRefs = refsTask.Result;
var assembly = RoslynCompiler.Compile(rewrittenTreeList, preloadedRefs);
if (assembly == null)
{
    // Dump rewritten C# for debugging if verbose
    if (!dumpRewritten && verbose)
    {
        Console.Error.WriteLine("\n--- Rewritten C# (for debugging compilation failure) ---");
        foreach (var (name, code) in GetRewrittenStrings())
        {
            Console.Error.WriteLine($"=== {name} ===");
            Console.Error.WriteLine(code);
        }
    }
    return 1;
}

Timer.EndStage("Roslyn compilation");
Timer.StartStage("Test execution");
// ---------------------------------------------------------------------------
// Step 4: Detect mode and execute
// ---------------------------------------------------------------------------
// Set current assembly for cross-codeunit calls
AlRunner.Runtime.MockCodeunitHandle.CurrentAssembly = assembly;

// Register total statement count for coverage tracking
Executor.RegisterStatements(GetRewrittenStrings());

// Auto-detect test codeunits: check if any AL source contains "Subtype = Test"
bool hasTests = alSources.Any(s => s.Contains("Subtype = Test"));

int exitCode;
if (hasTests)
{
    exitCode = Executor.RunTests(assembly);
    if (showCoverage)
    {
        Executor.PrintCoverageReport();

        // Write Cobertura XML for editor/CI integration
        var sourceSpans = CoverageReport.ParseSourceSpans(generatedCSharpList!);
        var (hitStmts, totalStmts) = AlRunner.Runtime.AlScope.GetCoverageSets();

        // Collect AL source file paths for file mapping
        var alFilePaths = new List<string>();
        foreach (var inputPath in inputPaths)
        {
            if (Directory.Exists(inputPath))
                alFilePaths.AddRange(Directory.GetFiles(inputPath, "*.al", SearchOption.AllDirectories));
            else if (File.Exists(inputPath))
                alFilePaths.Add(inputPath);
        }

        var objectToFile = CoverageReport.MapObjectsToFiles(generatedCSharpList!, alFilePaths);
        CoverageReport.WriteCobertura("cobertura.xml", sourceSpans, hitStmts, totalStmts, objectToFile);
        Log.Info("Coverage report: cobertura.xml");
    }
}
else
{
    exitCode = Executor.RunOnRun(assembly);
}

Timer.EndStage("Test execution");
Timer.Print();
return exitCode;

void PrintGuide()
{
    Console.WriteLine("""
## AL Runner Test Guide

Use this guide when writing AL unit tests that run with al-runner — a standalone
test executor that needs no BC service tier, Docker, SQL Server, or license.

### What al-runner supports

- Pure-logic codeunits (arithmetic, string ops, record CRUD, enums, options)
- In-memory table store: Insert, Modify, Get, Delete, FindSet, FindFirst, FindLast, Next
- Composite primary keys, sort ordering (SetCurrentKey / SetAscending)
- SETRANGE / SETFILTER filtering (=, <>, <, <=, >, >=, wildcards, OR via |)
- Cross-codeunit dispatch (Codeunit.Run, direct codeunit variable calls)
- AL interfaces for dependency injection
- `asserterror` blocks + `GetLastErrorText()`
- Assert codeunit: AreEqual, AreNotEqual, IsTrue, IsFalse, ExpectedError, RecordIsEmpty, etc.
- OnValidate triggers on table fields
- Table procedures (custom procedures on table objects)
- IsolatedStorage (in-memory key-value store: Set, Get, Delete, Contains)
- TextBuilder (Append, AppendLine, ToText)
- Format() / Evaluate() type conversions
- Partial compilation (skips unsupported object types like XMLport)
- Coverage reporting via `--coverage` (statement-level, outputs cobertura.xml)

### What al-runner does NOT support

- Pages, Reports, XMLports — stub them via `--stubs <dir>` or inject via AL interface
- HTTP / REST calls — inject via AL interface
- Event subscribers — OnAfterModify, OnAfterInsert, etc. do not fire
- Confirm() returns true always; StrMenu is not supported
- RecordRef / FieldRef — stubs compile but do not function
- BLOB / InStream / OutStream operations
- Filter groups (FilterGroup)

### Writing a compatible test codeunit

1. Use `Subtype = Test` on the codeunit
2. Reference `Assert` as `Codeunit Assert` (not `Library Assert`)
3. Mark each test procedure with `[Test]`
4. Tests must be self-contained: insert test data, call logic, assert results
5. Use `asserterror` + `Assert.ExpectedError` for error path testing
6. For external dependencies (mail, HTTP, pages), define an AL interface and
   inject a mock implementation in the test

### Handling unsupported dependencies

Use `--stubs <dir>` to provide stub AL files that replace ISV or unsupported objects.
Each stub declares the same object ID and name but with a simplified body. The runner
auto-excludes conflicting symbol packages when stubs are loaded.

Example stub for an unsupported codeunit:
```al
codeunit 70100 "ISV Integration Mgt."
{
    procedure DoSomething(): Boolean
    begin
        exit(true);
    end;
}
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
```

### Tips for AI agents

- When a test fails with `NotSupportedException`, the codeunit uses an unsupported
  feature. Create a stub or inject the dependency via an AL interface.
- Run `al-runner --dump-rewritten` to inspect the generated C# if you need to debug
  a transpilation issue.
- al-runner resets all in-memory tables between test methods — no cleanup needed.
- If al-runner says FAIL, the failure is real. If it says PASS, the direct logic is
  correct but implicit event side-effects are not tested (run the full BC pipeline).
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
        List<string>? inputPaths = null)
    {
        // Parse all sources into syntax trees
        var syntaxTrees = new List<SyntaxTree>();
        bool hasErrors = false;

        foreach (var src in alSources)
        {
            var tree = SyntaxTree.ParseObjectText(src);
            var parseDiags = tree.GetDiagnostics().ToList();
            if (parseDiags.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                Log.Info("AL parse errors:");
                foreach (var d in parseDiags.Where(d => d.Severity == DiagnosticSeverity.Error))
                    Log.Info($"  {d}");
                hasErrors = true;
            }
            syntaxTrees.Add(tree);
        }

        if (hasErrors)
            return null;

        // Extract app identity from input manifest (for correct InternalsVisibleTo resolution)
        var appIdentity = ExtractAppIdentity(inputPaths);

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
                generateOptions: CompilationGenerationOptions.All
            )
        );

        // --- Symbol reference support ---
        // Only enable symbol references when --packages is explicitly provided.
        // This avoids conflicts when compiling self-contained multi-project spikes from source.
        bool hasExplicitPackages = packagePaths != null && packagePaths.Count > 0;
        var allPackagePaths = hasExplicitPackages ? ResolvePackagePaths(packagePaths, inputPaths) : new List<string>();
        var appsByGuid = new Dictionary<Guid, (string Publisher, string Name, Version Version)>();
        Microsoft.Dynamics.Nav.CodeAnalysis.ISymbolReferenceLoader? refLoader = null;

        if (hasExplicitPackages)
        {
            var depSpecs = DiscoverDependencies(inputPaths, forceResolve: true);

            if (allPackagePaths.Count > 0)
            {
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
                    // Deduplicates by app GUID, keeping the highest version.
                    Log.Info("No explicit dependencies found. Loading all .app packages as symbols...");
                    // Exclude the app being compiled (its source is already in the syntax trees)
                    var selfName = appIdentity.Name.ToLowerInvariant();
                    var selfGuid = appIdentity.AppId;
                    appsByGuid.Clear();
                    foreach (var pkgDir in allPackagePaths)
                    {
                        foreach (var appFile in Directory.GetFiles(pkgDir, "*.app", SearchOption.AllDirectories))
                        {
                            try
                            {
                                var doc = LoadNavxManifest(appFile);
                                if (doc == null) continue;
                                XNamespace ns = "http://schemas.microsoft.com/navx/2015/manifest";
                                var appElement = doc.Root?.Element(ns + "App");
                                var idStr = appElement?.Attribute("Id")?.Value;
                                var appName = appElement?.Attribute("Name")?.Value ?? "";
                                var appPublisher = appElement?.Attribute("Publisher")?.Value ?? "";
                                var appVersionStr = appElement?.Attribute("Version")?.Value ?? "1.0.0.0";
                                if (idStr != null && Guid.TryParse(idStr, out var appGuid)
                                    && appGuid != selfGuid
                                    && !string.Equals(appName, appIdentity.Name, StringComparison.OrdinalIgnoreCase))
                                {
                                    var ver = Version.Parse(appVersionStr);
                                    if (!appsByGuid.TryGetValue(appGuid, out var existing) || ver > existing.Version)
                                        appsByGuid[appGuid] = (appPublisher, appName, ver);
                                }
                            }
                            catch { }
                        }
                    }

                    if (appsByGuid.Count > 0)
                    {
                        var allAppSpecs = appsByGuid.Select(kv =>
                            new SymbolReferenceSpecification(
                                kv.Value.Publisher, kv.Value.Name, kv.Value.Version,
                                false, kv.Key, false, ImmutableArray<Guid>.Empty))
                            .ToArray();
                        Log.Info($"  Loaded {allAppSpecs.Length} symbol packages (deduplicated by app ID, latest version)");
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
            else
            {
                Console.Error.WriteLine("Warning: --packages specified but no package directories with .app files found.");
            }
        }

        // Check for declaration-level diagnostics before emit
        // AL0432 (obsolete/removed field) is not a blocking error for the runner
        var ignoredErrorIds = new HashSet<string> { "AL0432", "AL0433" };
        var declDiags = compilation.GetDeclarationDiagnostics().ToList();
        var declErrors = declDiags
            .Where(d => d.Severity == DiagnosticSeverity.Error && !ignoredErrorIds.Contains(d.Id))
            .ToList();

        // When stubs are loaded: detect ambiguity conflicts (AL0275/AL0197) and resolve
        // by removing the conflicting symbol package, then retrying compilation
        if (Log.HasStubs && declErrors.Any(d => d.Id is "AL0275" or "AL0197"))
        {
            // Extract conflicting extension names from error messages
            // Format: "'X' is an ambiguous reference between 'X' defined by the extension 'AppName by Publisher (Version)' and ..."
            var conflictingApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in declErrors.Where(d => d.Id is "AL0275" or "AL0197"))
            {
                var msg = d.GetMessage();
                // Extract extension names: 'AppName by Publisher (Version)'
                foreach (var part in msg.Split("'"))
                {
                    if (part.Contains(" by ") && part.Contains("(") &&
                        !part.Contains(appIdentity.Name))
                    {
                        // Extract just the app name before " by "
                        var appName = part.Split(" by ")[0].Trim();
                        conflictingApps.Add(appName);
                    }
                }
            }

            if (conflictingApps.Count > 0 && hasExplicitPackages)
            {
                Log.Info($"Stubs override {conflictingApps.Count} package(s): {string.Join(", ", conflictingApps)}");

                // Rebuild compilation without conflicting packages
                // Re-discover dependencies excluding conflicted app names
                if (allPackagePaths.Count > 0)
                {
                    var filteredSpecs = appsByGuid
                        .Where(kv => !conflictingApps.Contains(kv.Value.Name))
                        .Select(kv => new SymbolReferenceSpecification(
                            kv.Value.Publisher, kv.Value.Name, kv.Value.Version,
                            false, kv.Key, false, ImmutableArray<Guid>.Empty))
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
                            generateOptions: CompilationGenerationOptions.All
                        ))
                        .WithReferenceLoader(refLoader)
                        .AddReferences(filteredSpecs);
                }

                // Re-check declaration diagnostics
                declDiags = compilation.GetDeclarationDiagnostics().ToList();
                declErrors = declDiags
                    .Where(d => d.Severity == DiagnosticSeverity.Error && !ignoredErrorIds.Contains(d.Id))
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
                Console.Error.WriteLine($"Missing dependencies: {string.Join(", ", missingObjects)}");

            if (otherErrors.Count > 0)
                Log.Info($"AL declaration errors ({otherErrors.Count} non-missing):");
            foreach (var d in otherErrors.Take(10))
                Log.Info($"  {d.Id}: {d.GetMessage()}");
            if (otherErrors.Count > 10)
                Log.Info($"  ... and {otherErrors.Count - 10} more");
        }

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
                        failedMethods.Add(innerInner.Message);
                }
                else
                {
                    failedMethods.Add(inner.Message);
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
            Log.Info("No C# code was generated.");
            if (emitResult != null)
            {
                Log.Info("Emit diagnostics:");
                foreach (var d in emitResult.Diagnostics.Take(30))
                    Log.Info($"  [{d.Severity}] {d.Id}: {d.GetMessage()}");
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

    private static List<string> ResolvePackagePaths(List<string>? explicitPaths, List<string>? inputPaths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add explicit --packages paths (scan recursively for subdirectories containing .app files)
        if (explicitPaths != null)
        {
            foreach (var p in explicitPaths)
            {
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
    /// Build the mapping from pre-rewrite C# (for SourceSpans) and post-rewrite C#
    /// (for StmtHit line positions). Both lists must use the same Name keys.
    /// </summary>
    public static void Build(
        List<(string Name, string Code)> preRewriteCSharp,
        List<(string Name, string Code)> postRewriteCSharp)
    {
        Mappings.Clear();

        // Step 1: Parse SourceSpans from pre-rewrite code to get (scope, stmtIndex) -> AL line
        var sourceSpans = CoverageReport.ParseSourceSpans(preRewriteCSharp);

        // Step 2: For each post-rewrite file, scan for StmtHit(N) / CStmtHit(N) calls
        // and map the C# line to the AL line via the sourceSpans
        var stmtPattern = new System.Text.RegularExpressions.Regex(
            @"\b(?:StmtHit|CStmtHit)\((\d+)\)");
        var classPattern = new System.Text.RegularExpressions.Regex(
            @"class\s+(\w+_Scope_\w+)");

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
                        if (sourceSpans.TryGetValue((currentScope, stmtIndex), out int alLine))
                        {
                            entries.Add((i + 1, alLine)); // C# lines are 1-based
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
        return $"{original}  [AL line ~{alLine} in {baseName}]";
    }
}

// ===========================================================================
// Roslyn In-Memory Compiler (supports multiple C# source strings)
// ===========================================================================
public static class RoslynCompiler
{
    /// <summary>
    /// Maps excluded source file paths to the Roslyn errors that caused exclusion.
    /// Populated during iterative retry so that runtime "not found" errors can
    /// explain WHY a codeunit or record type was excluded from compilation.
    /// </summary>
    public static Dictionary<string, List<string>> ExcludedFiles { get; } = new();

    /// <summary>
    /// Look up exclusion info for a type name fragment (e.g. "Codeunit74320" or "Record50100").
    /// First checks file names for a direct match, then checks error messages for references
    /// to the type, and finally falls back to listing all excluded files if any exist.
    /// Returns a multi-line explanation or null if no files were excluded.
    /// </summary>
    public static string? GetExclusionInfo(string typeNameFragment)
    {
        if (ExcludedFiles.Count == 0) return null;

        // 1. Direct match: file name contains the fragment (e.g. "Codeunit74320.cs")
        var matches = ExcludedFiles
            .Where(kv => Path.GetFileNameWithoutExtension(kv.Key)
                .Contains(typeNameFragment, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 2. Indirect match: error messages reference the type name
        if (matches.Count == 0)
        {
            matches = ExcludedFiles
                .Where(kv => kv.Value.Any(err =>
                    err.Contains(typeNameFragment, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        // 3. Fallback: if there are excluded files but no specific match, show all
        if (matches.Count == 0)
            matches = ExcludedFiles.ToList();

        var sb = new System.Text.StringBuilder();
        if (matches.Count <= 5)
        {
            foreach (var (file, errors) in matches)
            {
                sb.AppendLine($"      {Path.GetFileName(file)} was excluded during compilation.");
                foreach (var err in errors.Take(3))
                    sb.AppendLine($"      {err}");
            }
        }
        else
        {
            sb.AppendLine($"      {matches.Count} source file(s) were excluded during compilation.");
            // Show the first few with their errors
            foreach (var (file, errors) in matches.Take(3))
            {
                sb.AppendLine($"      {Path.GetFileName(file)}: {(errors.Count > 0 ? errors[0] : "unknown error")}");
            }
            sb.AppendLine($"      ... and {matches.Count - 3} more. Run with -v for full list.");
        }
        sb.Append("      Tip: use --stubs to provide stub AL files for unsupported dependencies.");
        return sb.ToString();
    }

    public static Assembly? Compile(string csharpSource) =>
        Compile(new List<(string Name, string Code)> { ("source", csharpSource) });

    public static Assembly? Compile(List<(string Name, string Code)> namedSources)
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
    /// </summary>
    public static Assembly? Compile(List<(string Name, Microsoft.CodeAnalysis.SyntaxTree Tree)> namedTrees,
        List<Microsoft.CodeAnalysis.MetadataReference>? preloadedReferences = null)
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

        return CompileFromTrees(syntaxTrees, preloadedReferences);
    }

    /// <summary>
    /// Prepare MetadataReferences for Roslyn compilation. Can be called early
    /// (e.g. in parallel with rewriting) since reference loading is independent
    /// of the source code being compiled.
    /// </summary>
    public static List<Microsoft.CodeAnalysis.MetadataReference> LoadReferences()
    {
        var references = new System.Collections.Concurrent.ConcurrentBag<Microsoft.CodeAnalysis.MetadataReference>();

        // Collect all DLL paths to load
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var runtimeDlls = Directory.GetFiles(runtimeDir, "*.dll")
            .Where(dllPath =>
            {
                var fileName = Path.GetFileName(dllPath);
                return fileName.StartsWith("System.", StringComparison.Ordinal) ||
                       fileName is "netstandard.dll" or "mscorlib.dll" or "Microsoft.CSharp.dll";
            })
            .ToList();

        var serviceTierPath = FindServiceTierPath();
        var bcDlls = serviceTierPath != null
            ? Directory.GetFiles(serviceTierPath, "Microsoft.Dynamics.Nav.*.dll").ToList()
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

    private static Assembly? CompileFromTrees(List<Microsoft.CodeAnalysis.SyntaxTree> syntaxTrees)
    {
        return CompileFromTrees(syntaxTrees, null);
    }

    internal static Assembly? CompileFromTrees(List<Microsoft.CodeAnalysis.SyntaxTree> syntaxTrees,
        List<Microsoft.CodeAnalysis.MetadataReference>? preloadedReferences)
    {
        // Clear any exclusion info from previous compilations
        ExcludedFiles.Clear();

        var references = preloadedReferences ?? LoadReferences();

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "AlRunnerGenerated",
            syntaxTrees,
            references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary)
                .WithAllowUnsafe(true));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .ToList();
            Log.Info($"Roslyn compilation failed ({errors.Count} errors):");
            foreach (var d in errors.Take(30))
                Log.Info($"  {SourceLineMapper.FormatDiagnostic(d)}");

            // Iteratively remove error-producing source files and recompile.
            // Each round only removes files with DIRECT errors, preserving files
            // that are error-free themselves but were compiled alongside broken ones.
            var currentTrees = new List<Microsoft.CodeAnalysis.SyntaxTree>(syntaxTrees);
            var allExcluded = new HashSet<string>();

            for (int round = 1; round <= 5; round++)
            {
                var errorTreePaths = errors
                    .Select(d => d.Location.SourceTree?.FilePath)
                    .Where(p => p != null)
                    .Distinct()
                    .ToHashSet();

                if (errorTreePaths.Count == 0 || errorTreePaths.Count >= currentTrees.Count)
                    break;

                // Record which files are being excluded and why
                foreach (var p in errorTreePaths)
                {
                    allExcluded.Add(p!);
                    if (!ExcludedFiles.ContainsKey(p!))
                    {
                        ExcludedFiles[p!] = errors
                            .Where(d => d.Location.SourceTree?.FilePath == p)
                            .Take(3)
                            .Select(d => SourceLineMapper.FormatDiagnostic(d))
                            .ToList();
                    }
                }

                currentTrees = currentTrees
                    .Where(t => !errorTreePaths.Contains(t.FilePath))
                    .ToList();

                Log.Info($"Retry round {round}: removed {errorTreePaths.Count} file(s), {currentTrees.Count} remaining");

                var retryCompilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                    "AlRunnerGenerated",
                    currentTrees,
                    references,
                    new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                        Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary)
                        .WithAllowUnsafe(true));

                using var retryMs = new MemoryStream();
                var retryResult = retryCompilation.Emit(retryMs);

                if (retryResult.Success)
                {
                    Log.Info($"Compilation succeeded after excluding {allExcluded.Count} file(s).");
                    retryMs.Seek(0, SeekOrigin.Begin);
                    return Assembly.Load(retryMs.ToArray());
                }

                errors = retryResult.Diagnostics
                    .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    .ToList();
                Log.Info($"  {errors.Count} error(s) remain");
            }

            Log.Info($"Compilation failed after iterative retry. {allExcluded.Count} file(s) excluded, {errors.Count} error(s) remain.");
            return null;
        }

        ms.Seek(0, SeekOrigin.Begin);
        return Assembly.Load(ms.ToArray());
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
            @"class\s+(\w+_Scope_\w+)");

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

    public static int RunTests(Assembly assembly)
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

            // Find corresponding scope classes for test methods
            foreach (var nested in type.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            {
                var name = nested.Name;
                if (name.Contains("_Scope_") && !name.Contains("OnRun_Scope"))
                {
                    var scopeIdx = name.IndexOf("_Scope_");
                    var testName = name.Substring(0, scopeIdx);
                    // Include if method has [NavTest] attribute OR starts with "Test" (fallback)
                    if (testMethodNames.Contains(testName) ||
                        testName.StartsWith("Test", StringComparison.OrdinalIgnoreCase))
                    {
                        testScopes.Add((testName, nested, type));
                    }
                }
            }
        }

        if (testScopes.Count == 0)
        {
            Console.Error.WriteLine("Error: No test methods found in the generated code.");
            Log.Info("Available types:");
            foreach (var t in assembly.GetTypes())
            {
                Log.Info($"  {t.FullName}");
                foreach (var n in t.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                    Log.Info($"    {n.Name}");
            }
            return 1;
        }

        int passed = 0;
        int failed = 0;
        int errors = 0;

        foreach (var (testName, scopeType, parentType) in testScopes)
        {
            // Reset in-memory state before each test
            AlRunner.Runtime.MockRecordHandle.ResetAll();
            AlRunner.Runtime.MockIsolatedStorage.ResetAll();

            try
            {
                // Create the parent codeunit instance (needed by scope constructor)
                var parent = RuntimeHelpers.GetUninitializedObject(parentType);

                // Call InitializeComponent() if it exists (initializes codeunit handles)
                var initMethod = parentType.GetMethod("InitializeComponent",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (initMethod != null)
                    initMethod.Invoke(parent, null);

                // Find the scope constructor
                var ctors = scopeType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                object scope;
                if (ctors.Length > 0 && ctors[0].GetParameters().Length > 0)
                {
                    // Constructor takes the parent codeunit
                    scope = ctors[0].Invoke(new[] { parent });
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
                    Console.WriteLine($"FAIL  {testName}");
                    Console.WriteLine($"      OnRun() method not found on {scopeType.Name}");
                    failed++;
                    continue;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                onRunMethod.Invoke(scope, null);
                sw.Stop();
                Console.WriteLine($"PASS  {testName} ({sw.ElapsedMilliseconds}ms)");
                passed++;
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex as Exception;
                while (inner is TargetInvocationException tie && tie.InnerException != null)
                    inner = tie.InnerException;

                if (inner is NotSupportedException)
                {
                    // Unsupported feature — runner cannot execute
                    Console.WriteLine($"ERROR {testName}");
                    Console.WriteLine($"      {inner!.GetType().Name}: {inner.Message}");
                    Console.WriteLine($"      Inject this dependency via an AL interface.");
                    PrintStackFrames(inner, parentType.Name, testName);
                    errors++;
                }
                else if (inner is AlRunner.Runtime.AssertException)
                {
                    // Assertion failure — real test failure
                    Console.WriteLine($"FAIL  {testName}");
                    Console.WriteLine($"      {inner!.Message}");
                    PrintStackFrames(inner, parentType.Name, testName);
                    failed++;
                }
                else
                {
                    Console.WriteLine($"FAIL  {testName}");
                    Console.WriteLine($"      {inner!.Message}");
                    PrintStackFrames(inner, parentType.Name, testName);
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL  {testName}");
                Console.WriteLine($"      {ex.Message}");
                PrintStackFrames(ex, parentType.Name, testName);
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Results: {passed} passed, {failed} failed, {errors} errors, {passed + failed + errors} total");
        return (failed + errors) > 0 ? 1 : 0;
    }

    private static void PrintStackFrames(Exception ex, string parentTypeName, string testName)
    {
        var frames = ex.StackTrace?.Split('\n')
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrEmpty(f))
            .Take(3);
        if (frames != null)
        {
            foreach (var f in frames)
                Console.WriteLine($"      {f}");
        }
    }

    public static int RunOnRun(Assembly assembly)
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
            Log.Info("Available types:");
            foreach (var t in assembly.GetTypes())
            {
                Log.Info($"  {t.FullName}");
                foreach (var n in t.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                    Log.Info($"    {n.Name}");
            }
            return 1;
        }

        // Create the parent codeunit and scope using the constructor
        // (the scope constructor initializes local record variables etc.)
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
