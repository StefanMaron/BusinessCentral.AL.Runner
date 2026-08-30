// Program — orchestrates the v2 pipeline:
//   1. Parse CLI (caches, bundles, --precompile subcommand).
//   2. If --precompile: dispatch single-app compile-to-DLL and exit.
//   3. Apply BC runtime patches once (BcRuntime).
//   4. For each top-level arg (a "bundle" — typically tests/bucket-N/<category>):
//        locate the bucket-root app.json (climb the path)
//        resolve declared deps via DependencyResolver
//        load deps via DependencyLoader (3-tier resolution)
//        SetResolvedDeps on BcCompiler so compile-time symbols mirror runtime
//        iterate suites: emit → compile → run → aggregate
//   5. Reporter writes JSON.
//
// Usage:
//   Runner [--out PATH] [--package-cache PATH ...] <bundle-dir>...
//   Runner --precompile <input.app> --out <output.dll>
using System.Reflection;
using AlRunner;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

// Diagnostic: AL_RUNNER_DIAG_FIRSTCHANCE=<substring> prints the FULL stack of
// every first-chance exception whose type name contains the substring (use e.g.
// "NullReference"). Invaluable when a rethrow/finally collapses the original
// throw-site frames.
if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_FIRSTCHANCE") is string fcFilter
    && fcFilter.Length > 0)
{
    AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
    {
        if (e.Exception.GetType().Name.Contains(fcFilter, StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine($"[first-chance] {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}\n----");
    };
}

// Opt-in per-bundle / per-process cost instrumentation (issue #1825). Installed
// before the --help / --guide / --version fast paths on purpose: those return
// before any BC type loads, so their rows measure the bare process floor (host
// startup + the full-opt JIT <TieredCompilation>false</TieredCompilation> forces)
// with zero phases — the baseline every residual is read against. Completely inert
// unless AL_RUNNER_PHASE_LOG names a path. See AlRunner/Infrastructure/PhaseLog.cs.
AlRunner.Infrastructure.PhaseLog.Install();

if (args.Length == 0 || args[0] == "--help" || args[0] == "-h" || args[0] == "help")
{
    PrintHelp(args.Length == 0 ? Console.Error : Console.Out);
    return args.Length == 0 ? 2 : 0;
}

// The agent-facing operating manual. Advertised by CLAUDE.md and the
// al-runner-workflow skill; handled here (before the R2R re-exec and any BC type
// load) so it is instant and works on a machine with no artifacts provisioned.
if (args[0] == "--guide")
{
    PrintGuide(Console.Out);
    return 0;
}

// -v/-V accepted alongside --version and bare "version" (#2072), matching the
// three-spelling treatment --help already gets at the top of this file. -v is
// free: --verbose (line ~348) is matched only as its long form and has no
// short alias, so there is no ambiguity to resolve here.
if (args[0] == "--version" || args[0] == "-v" || args[0] == "-V" || args[0] == "version")
{
    Console.WriteLine(VersionString());
    return 0;
}

// ── Early validation of the BC selection flags ────────────────────────────────
// Must run BEFORE the Cecil re-exec below, because that re-exec rewrites
// `--artifact-path <std-cache>` into `--bc-version` for the child (see
// RewriteArtifactPathArg) — which would otherwise mask the mutual-exclusion error.
if (args.Contains("--bc-version") && args.Contains("--artifact-path"))
{
    Console.Error.WriteLine("--bc-version and --artifact-path are mutually exclusive (pick a version OR an explicit path).");
    return 2;
}

// NOTE: there used to be a second re-exec here, before the Cecil one, whose only job was
// to restart the process with DOTNET_ReadyToRun=0 so that "hooks fire deterministically" —
// the concern being that R2R-precompiled native code inlines past a patched method and the
// interception silently no-ops. It was removed once it was measured to be defending
// nothing:
//
//   * There are no JmpHooks left to bypass. JmpHook.ComputeDisabled() hard-returns true
//     and a real run prints "STARTUP-READY: 0 hooks applied". Cecil patches live IN THE IL,
//     so any tier and any precompiled image compiles the already-patched body.
//   * BC's service-tier DLLs carry no R2R native code to inline from in the first place.
//     Microsoft ships them IL-only — Ncl.dll and Types.dll both read machine=0x14c with a
//     zero-size CorHeader.ManagedNativeHeader on every BC version checked. The rewritten
//     Ncl is byte-array loaded and additionally header-stripped (NclCecilRewrite
//     .StripR2RHeader), so it could not use precompiled code even if MS shipped it.
//
// What the flag DID do was suppress the .NET framework's own R2R images, forcing ~3,300
// extra methods through the JIT on every spawn, plus one whole extra OS process. Removing
// it: 2076/2076 corpus fail-set unchanged, one cached test 9.50s -> 8.61s warm. See
// AlRunner.Tests/StartupJitModeTests. Anyone needing the old behaviour can still preset
// DOTNET_ReadyToRun=0 in the environment — the CLR honours it without our help.

// ── --server mode: long-running JSON-RPC daemon over stdin/stdout (the VS Code
// extension depends on this flag). The protocol requires stdout to carry ONLY the
// newline-delimited JSON — so capture the real stdin/stdout now and redirect ALL
// human-readable output (banners, [cache] lines, BC patch logs) to stderr, BEFORE
// Log.Install and any Console.Write. This also survives the cold-start Cecil
// re-exec: the child inherits these OS handles, so the protocol still flows.
bool serverMode = args.Contains("--server");
System.IO.TextReader? serverStdin = null;
System.IO.TextWriter? serverStdout = null;
if (serverMode)
{
    serverStdin = Console.In;
    serverStdout = Console.Out;
    Console.SetOut(Console.Error);
}

// ── --dap [port|stdio]: Debug Adapter Protocol server (issue #1642; stdio transport
// added for #2058) — restores v1's AL breakpoint debugging. Two transports:
//   --dap [PORT]  TCP on 127.0.0.1:PORT (default 4711, v1's default, see
//                 docs/archive/dap.md). That IS the DAP transport every socket-based
//                 DAP client expects, so there is no protocol reason to redirect
//                 Console here — this branch is unchanged from before #2058.
//   --dap stdio   speaks DAP over the process's own stdin/stdout (issue #2058, for
//                 VS Code's DebugAdapterExecutable — no port to pick, no readiness
//                 race polling for a free port or a "listening" line). Stdout
//                 becomes the DAP channel the instant this is selected, so —
//                 exactly like --server above — the raw OS stdin/stdout handles
//                 must be captured via Console.OpenStandardInput()/OpenStandardOutput()
//                 RIGHT NOW, before Log.Install or any Console.Write runs, and
//                 Console.Out redirected to Console.Error so every startup banner
//                 (including RunDapLoop's own readiness line) lands on stderr
//                 instead. Capturing the raw Stream directly — not Console.Out —
//                 means the transport's byte channel can never be intercepted by
//                 anything that already cached a Console.Out reference; it also
//                 gives DapTransport exactly the Stream-based input its constructor
//                 already wants (see DapTransport.cs's own header), rather than the
//                 TextReader/TextWriter pair --server hands to RunServerLoop.
bool dapMode = args.Contains("--dap");
int dapPort = 4711;
bool dapStdioMode = false;
System.IO.Stream? dapStdioInput = null;
System.IO.Stream? dapStdioOutput = null;
if (dapMode)
{
    var dapFlagIndex = Array.IndexOf(args, "--dap");
    if (dapFlagIndex >= 0 && dapFlagIndex + 1 < args.Length)
    {
        var dapArg = args[dapFlagIndex + 1];
        if (string.Equals(dapArg, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            dapStdioMode = true;
            dapStdioInput = Console.OpenStandardInput();
            dapStdioOutput = Console.OpenStandardOutput();
            Console.SetOut(Console.Error);
        }
        else if (int.TryParse(dapArg, out var parsedDapPort))
        {
            dapPort = parsedDapPort;
        }
    }
}
if (serverMode && dapMode)
{
    Console.Error.WriteLine("--server and --dap are mutually exclusive (both are long-running session modes; pick one).");
    return 2;
}

// Output filters must be installed BEFORE any other code prints to Console.
// Reads AL_RUNNER_VERBOSE env var by default; --verbose flag overrides below.
AlRunner.Log.Install();

// Per-test output mode. Default (V1 parity): print PASS and FAIL lines.
// Inverted by --failures-only or AL_RUNNER_FAILURES_ONLY=1 for large-corpus runs
// where the PASS list is too noisy. --show-pass retained as a no-op for back-compat.
bool showPass = Environment.GetEnvironmentVariable("AL_RUNNER_FAILURES_ONLY") != "1";

// AL_RUNNER_TRACE_NRE=1 — log every first-chance NullReferenceException with its
// full stack trace before it gets swallowed by AL `asserterror` / test machinery.
// AL_RUNNER_TRACE_NRE=2 additionally prints Environment.StackTrace — at first
// chance the exception's OWN trace holds only the throwing frame, which names the
// crashing method but never the BC caller that led there (the caller chain is what
// identifies the missing skeleton state). Costly, so it stays behind the "2" level.
{
    var traceNre = Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_NRE");
    if (traceNre == "1" || traceNre == "2")
    {
        bool withCallers = traceNre == "2";
        AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
        {
            if (e.Exception is NullReferenceException or ArgumentNullException)
            {
                Console.Error.WriteLine($"[FCE-NRE] {e.Exception}");
                // BC's DLLs ship without PDBs, so the managed trace above names the method
                // but gives no position inside it — and a method like NavRecord.InsertAsync
                // dereferences a dozen different fields. The IL offset is the only thing that
                // says WHICH one, and it maps straight onto `ilspycmd --il` output.
                foreach (var f in new System.Diagnostics.StackTrace(e.Exception, false).GetFrames())
                {
                    var m = f.GetMethod();
                    if (m == null) continue;
                    Console.Error.WriteLine(
                        $"[FCE-NRE]   IL_{f.GetILOffset():X4}  {m.DeclaringType?.FullName}.{m.Name}");
                }
                if (withCallers)
                    Console.Error.WriteLine($"[FCE-NRE] callers:\n{Environment.StackTrace}");
            }
        };
    }
}

// ── --precompile subcommand ────────────────────────────────────────────────
if (args[0] == "--precompile")
{
    return RunPrecompile(args.Skip(1).ToArray());
}

// ── --emit-app subcommand (debug tool: emit a bundle dir as a .app in-process) ──
// Usage: --emit-app <bundleDir> <outPath> [--package-cache PATH ...]
if (args[0] == "--emit-app")
{
    return RunEmitApp(args.Skip(1).ToArray());
}

// Failure classification (the FAILURE CLASSIFICATION block + v2-classification.json)
// is a runner-development diagnostic, not something end users care about. Default off.
// Enable by passing --out PATH (which sets the JSON output path) or --classify (which
// turns on the printed block without writing a file). See --help.
string? outPath = null;
bool printClassification = false;
// --output-json: replace the normal text output with v1-shaped per-test JSON on stdout.
// --output-junit PATH: additionally write a JUnit XML report — independent of --output-json.
bool outputJson = false;
string? outputJunitPath = null;
// --coverage: statement-level coverage via BC's own StmtHit instrumentation (issue
// #1922, first slice of #1640). Writes Cobertura XML to --coverage-out (default
// cobertura.xml in the working directory) after the run, plus a console table.
bool coverageEnabled = false;
string coverageOutputPath = "cobertura.xml";
var bundles = new List<string>();
var packageCacheArgs = new List<string>();
// Bundled mode is the canonical fast path (5-7× faster, parity-verified across
// all 4 sub-buckets). `--per-suite` falls back to the legacy per-Compilation
// path; kept for one cycle for diagnostic comparisons. `--bundled` accepted as
// a no-op alias for backwards compatibility — will be removed.
bool bundledMode = true;
// Spike B keystone: AL-output cache. By default, bundled-mode writes
// its emitted DLL to <cacheDir>/<key>.dll and on a subsequent invocation
// short-circuits Emit+Compile by loading that DLL directly. The key is a hash
// of (all .al source files contributing to the bundle, the resolved-deps list,
// the runner assembly mtime). See `precompiled-dll-respect.md` —
// "Our AL output is meant to be cacheable".
// AlRunner.Infrastructure.AlRunnerPaths.UserHome throws loudly (issue #2114) rather than
// silently handing back a relative path when $HOME names a directory that does not exist.
// Caught HERE (not left to propagate) because nothing wraps top-level statements at this
// point in the file — an uncaught exception this early reproduces the exact bug being
// fixed (an unhandled .NET exception aborts the process instead of a documented exit).
string? alCacheDir;
try
{
    alCacheDir = Path.Combine(
        AlRunner.Infrastructure.AlRunnerPaths.UserHome,
        ".cache", "al-runner", "al-out");
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
// #1821: mirrors alCacheDir, but only ever set by an explicit --cache flag (never by
// the default init above, never by --no-cache) — see the --cache parsing branch below
// and AlRunner.Infrastructure.CacheRoots for what this drives.
string? cacheRootOverride = null;
// --print-cache-key (issue #1851): a diagnostic/test-support mode. Reaches the SAME
// ComputeAlCacheKey call, with the SAME arguments, that a real run would use for the
// first app group it processes — then prints it and exits, before Emit+Compile even
// starts. Exists so callers that only need to assert a property of the KEY (not of a
// compiled DLL) don't have to pay for a full cold AL compile to get one. There is no
// second/parallel key computation — see the call site below, unchanged from the normal
// path up to and including the ComputeAlCacheKey call itself.
bool printCacheKeyOnly = false;
// Test isolation mode — default matches BC's "Test Runner - Isol. Codeunit" (130450).
var isolation = AlRunner.TestIsolation.Codeunit;
// Exit non-zero if any test fails or a bucket fails to compile/execute — matches v1/main
// semantics so CI shell loops (`&&`, `set -e`, GitHub Actions step failure) work by exit
// code alone, same as before. --no-strict-exit opts back into the old always-0 behaviour
// for tooling that only wants to parse the JSON output regardless of outcome. --strict is
// kept as a no-op alias (it's now the default) so existing invocations don't break.
bool strictExitCode = true;
// --test PATTERN: substring filter applied to "Codeunit.Method" — case-insensitive.
string? testFilter = null;
// --test-timeout SECONDS: per-test timeout override (v1 carryover; v2 previously
// hardcoded 60s with no CLI override — see #1648). Takes precedence over the
// AL_RUNNER_TEST_TIMEOUT_SEC env var. Null = env var / 60s default.
int? testTimeoutSeconds = null;
// --watch: stay resident with warm dependencies and re-run IN-PROCESS on every .al
// change. Each cycle resets the per-bundle caches (BcRuntime.ResetForNewBundleReload),
// re-emits warm (~1.6s — BcCompiler loader-signature reuse keeps the ~40s dep symbol
// load out of the loop) and runs in the same process. This replaced the original
// child-process model (which re-paid ~14s of runtime dep-loading per save); the
// same-bundle in-process reload is now safe because the type finders prefer the current
// test assembly. Net: ~seconds per save instead of a cold re-run.
bool watchMode = false;
// --tdd (issue #1997): local-development-only flag, off by default. Normally a test
// referencing a not-yet-implemented table field / procedure / enum value is a
// method-body compile ERROR, which drops the WHOLE app group (BC's ContinueBuildOnError
// does not cover method bodies — see BcCompiler.Emit's emit-retry-loop comment) and the
// run reports a compile failure with zero test results, not a failing test. --tdd keeps
// the recovered sources for the objects that DID compile and turns every [Test]
// procedure inside an object that could NOT be recovered into a synthetic FAILED
// TestResult naming the AL diagnostic that broke it — see TddSupport.BuildFailedTests
// and Program.cs's EMIT-EXCLUDED handling below. Not recommended for CI: it exists so a
// red-green TDD cycle can start with an honestly red test, not a compile failure.
bool tddMode = false;
// --dump-csharp DIR: write the emitted C# (BC Compilation.Emit output, post-BcAssembler
// polyfill injection) to disk for every bundle compile. Useful for debugging codegen.
string? dumpCsharpDir = null;
// --bc-version X / --artifact-path DIR: BC artifact/version selection overrides.
// Default (neither set) = latest version in the artifacts cache. --bc-version accepts a
// prefix ("28.1") or full version; --artifact-path points at an explicit artifact root
// (the dir containing platform/ + w1/). Mutually exclusive. Resolved into the
// process-global BcArtifacts selection below, before any resolver runs.
string? bcVersionArg = null;
string? artifactPathArg = null;
// Extra preprocessor symbols supplied via --define SYM / --preprocessor-symbols A,B,C.
// Validated as AL identifiers and merged with CLEANSCHEMA1..25 in BcCompiler.
var extraPreprocessorSymbols = new List<string>();
// --expectations DIR: test-expectations manifest directory (issue #1734; schema in
// docs/expectations.md). Null = auto-probe below (walk up from each bundle path,
// then cwd, looking for a tests/expectations sibling — #1984); only an existing
// directory activates classification, so ordinary runs outside this repo are
// untouched.
string? expectationsDirArg = null;
// --count-baseline PATH: opt-in test/app-group expected-count manifest (issue #1880;
// see AlRunner/Infrastructure/CountBaseline.cs for the schema and rationale). Unlike
// --expectations there is NO auto-probed default — a baseline built for a full-corpus
// leg must never silently fire on a narrower invocation of the same directory (the
// xmlport-isolation CI leg passes --test against the SAME al-language root), so this
// only ever activates when the caller explicitly opts in.
string? countBaselinePath = null;
// `provision` subcommand: `al-runner provision [<project>]` provisions the BC artifacts
// for the project's version and exits (no test run). `--auto-provision` provisions on the
// fly when artifacts are missing, then continues the normal run.
//
// Issue #2024 (item 2): auto-provisioning is ON BY DEFAULT. Since PR #2023/#2026 the
// packaged tool ships none of the BC engine assemblies — they resolve ONLY from
// ~/.local/share/al-runner/artifacts/<version>/, populated by nothing but provisioning
// itself. A first-time `dotnet tool install` user with an empty cache has no copy
// anywhere, so opt-in provisioning (the pre-#2024 default) meant a clean install could
// never run a single test without the user first discovering `--auto-provision` exists.
// `--no-auto-provision` is the explicit opt-out for offline/air-gapped environments,
// where reaching the network unasked for gigabyte-scale artifacts is a real problem —
// see docs/scope.md and .claude/rules/loud-failures.md (a refused/failed provision must
// still fail loud with an actionable, tool-install-valid fix command, never silently).
// `--auto-provision` itself is kept as an explicit, redundant-with-the-default alias for
// back-compat with existing scripts/docs that already pass it.
bool provisionSubcommand = args.Length > 0 && args[0] == "provision";
bool autoProvision = true;
// Issue #2085: `provision --platform-apps` / `--test-apps` / `--service-tier` [--force]
// force-download ONE specific artifact set into its canonical directory, bypassing
// need-detection entirely. This is the tool-install-valid replacement for
// `dotnet run --project tools/DownloadArtifacts -- <mode> <ver> <dir>`, which requires a
// source checkout that a `dotnet tool install -g` user never has — see the issue for the
// measured dead-end. `--resolve-version PREFIX` mirrors the CLI's `resolve-version` mode.
// All four are only meaningful under the `provision` subcommand; validated below.
bool provisionPlatformApps = false;
bool provisionTestApps = false;
bool provisionServiceTier = false;
bool provisionForce = false;
string? provisionResolveVersionPrefix = null;
bool provisionHelp = false;
for (int i = 0; i < args.Length; i++)
{
    if (i == 0 && args[i] == "provision") { continue; } // consumed as subcommand
    if (provisionSubcommand && (args[i] == "--help" || args[i] == "-h")) { provisionHelp = true; continue; }
    if (args[i] == "--platform-apps") { provisionPlatformApps = true; continue; }
    if (args[i] == "--test-apps") { provisionTestApps = true; continue; }
    if (args[i] == "--service-tier") { provisionServiceTier = true; continue; }
    if (args[i] == "--force") { provisionForce = true; continue; }
    if (args[i] == "--resolve-version" && i + 1 < args.Length) { provisionResolveVersionPrefix = args[++i]; continue; }
    if (args[i] == "--auto-provision") { autoProvision = true; continue; }
    if (args[i] == "--no-auto-provision") { autoProvision = false; continue; }
    if (args[i] == "--bc-version" && i + 1 < args.Length) { bcVersionArg = args[++i]; continue; }
    if (args[i] == "--artifact-path" && i + 1 < args.Length) { artifactPathArg = args[++i]; continue; }
    if (args[i] == "--out" && i + 1 < args.Length) { outPath = args[++i]; printClassification = true; continue; }
    if (args[i] == "--classify") { printClassification = true; continue; }
    if (args[i] == "--output-json") { outputJson = true; continue; }
    if (args[i] == "--output-junit" && i + 1 < args.Length) { outputJunitPath = args[++i]; continue; }
    if (args[i] == "--coverage")
    {
        coverageEnabled = true;
        AlRunner.Infrastructure.AlCoverageTracker.Enabled = true;
        continue;
    }
    if (args[i] == "--coverage-out" && i + 1 < args.Length) { coverageOutputPath = args[++i]; continue; }
    if (args[i] == "--package-cache" && i + 1 < args.Length) { packageCacheArgs.Add(args[++i]); continue; }
    if (args[i] == "--per-suite") { bundledMode = false; continue; }
    if (args[i] == "--bundled") { bundledMode = true; continue; }
    if (args[i] == "--expectations" && i + 1 < args.Length) { expectationsDirArg = args[++i]; continue; }
    if (args[i] == "--count-baseline" && i + 1 < args.Length) { countBaselinePath = args[++i]; continue; }
    // #1821: the SAME --cache value also becomes the isolation root for the four
    // caches (compiled-deps/workspace-deps/ncl-cecil/bc-symbols) that used to ignore
    // it — see AlRunner.Infrastructure.CacheRoots for why al-out itself is unaffected.
    // --no-cache intentionally does NOT touch cacheRootOverride: it has only ever
    // disabled the AL-output cache, and this issue doesn't expand that scope.
    if (args[i] == "--cache" && i + 1 < args.Length) { alCacheDir = args[++i]; cacheRootOverride = alCacheDir; continue; }
    if (args[i] == "--no-cache") { alCacheDir = null; continue; }
    if (args[i] == "--print-cache-key") { printCacheKeyOnly = true; continue; }
    if (args[i] == "--watch") { watchMode = true; continue; }
    if (args[i] == "--tdd") { tddMode = true; continue; }
    if (args[i] == "--server") { continue; }  // handled above (serverMode); consume so it isn't "unknown"
    if (args[i] == "--dap")  // handled above (dapMode/dapPort/dapStdioMode); consume the flag and its optional value (numeric port, or "stdio")
    {
        if (i + 1 < args.Length && (int.TryParse(args[i + 1], out _) || string.Equals(args[i + 1], "stdio", StringComparison.OrdinalIgnoreCase))) i++;
        continue;
    }
    if (args[i] == "--verbose") { AlRunner.Log.Verbose = true; continue; }
    if (args[i] == "--show-pass") { showPass = true; continue; }   // no-op (default in v2); kept for v1 back-compat
    if (args[i] == "--failures-only" || args[i] == "--quiet") { showPass = false; continue; }
    if (args[i] == "--strict") { strictExitCode = true; continue; }  // no-op: default since the v2 cut
    if (args[i] == "--no-strict-exit") { strictExitCode = false; continue; }
    if ((args[i] == "--test" || args[i] == "--filter") && i + 1 < args.Length) { testFilter = args[++i]; continue; }
    if (args[i] == "--test-timeout" && i + 1 < args.Length)
    {
        var rawTimeout = args[++i];
        if (!int.TryParse(rawTimeout, out var parsedTimeout) || parsedTimeout <= 0)
        {
            Console.Error.WriteLine($"--test-timeout: '{rawTimeout}' is not a positive integer number of seconds.");
            return 2;
        }
        testTimeoutSeconds = parsedTimeout;
        continue;
    }
    if (args[i] == "--preprocessor-symbols" && i + 1 < args.Length)
    {
        foreach (var raw in args[++i].Split(','))
        {
            var sym = raw.Trim();
            if (sym.Length == 0) continue;
            if (!BcCompiler.IsValidPreprocessorSymbol(sym))
            {
                Console.Error.WriteLine($"--preprocessor-symbols: '{sym}' is not a valid AL preprocessor symbol (letters/digits/underscores, must not start with a digit).");
                return 2;
            }
            extraPreprocessorSymbols.Add(sym);
        }
        continue;
    }
    if (args[i] == "--define" && i + 1 < args.Length)
    {
        var sym = args[++i].Trim();
        if (!BcCompiler.IsValidPreprocessorSymbol(sym))
        {
            Console.Error.WriteLine($"--define: '{sym}' is not a valid AL preprocessor symbol (letters/digits/underscores, must not start with a digit).");
            return 2;
        }
        extraPreprocessorSymbols.Add(sym);
        continue;
    }
    if (args[i] == "--dump-csharp" && i + 1 < args.Length)
    {
        dumpCsharpDir = args[++i];
        Directory.CreateDirectory(dumpCsharpDir);
        continue;
    }
    // --test-isolation and --isolation are aliases (v1 used the former, v2 introduced the shorter form).
    if ((args[i] == "--isolation" || args[i] == "--test-isolation") && i + 1 < args.Length)
    {
        var mode = args[++i];
        try { isolation = AlRunner.TestIsolationParser.Parse(mode); }
        catch (ArgumentException ex) { throw new ArgumentException($"--isolation: {ex.Message}"); }
        continue;
    }
    if (args[i].StartsWith("--"))
    {
        Console.Error.WriteLine($"Unknown option '{args[i]}'. Run with --help for the supported flags.");
        return 2;
    }
    bundles.Add(args[i]);
}
if (serverMode && watchMode)
{
    Console.Error.WriteLine("--server and --watch are mutually exclusive (both stay warm in-process; pick one).");
    return 2;
}
// Issue #2085: --platform-apps/--test-apps/--service-tier/--resolve-version only make
// sense under the `provision` subcommand (they force/bypass a specific artifact-set
// download; a normal test run has no use for them). Reject early rather than silently
// accepting-and-ignoring, which would look like support that isn't there.
if (!provisionSubcommand && (provisionPlatformApps || provisionTestApps || provisionServiceTier
    || provisionResolveVersionPrefix != null))
{
    var badFlag = provisionPlatformApps ? "--platform-apps"
        : provisionTestApps ? "--test-apps"
        : provisionServiceTier ? "--service-tier"
        : "--resolve-version";
    Console.Error.WriteLine($"{badFlag} is only valid with the `provision` subcommand (e.g. `al-runner provision {badFlag}`).");
    return 2;
}
if (!provisionSubcommand && provisionForce)
{
    Console.Error.WriteLine("--force is only valid with `provision --platform-apps` / `--test-apps` / `--service-tier`.");
    return 2;
}
// `al-runner provision --help`: subcommands must accept --help like everything else —
// previously this fell through to the generic arg-parser and answered "Unknown option
// '--help'. Run with --help for the supported flags.", which tells the caller to run the
// exact command it just ran. Handled before any BC type loads, same as the top-level
// --help/--guide fast paths.
if (provisionHelp)
{
    PrintProvisionHelp(Console.Out);
    return 0;
}
// `provision --resolve-version PREFIX` / `--platform-apps` / `--test-apps` / `--service-tier`:
// force a specific artifact set, bypassing need-detection, and exit — never reaches the
// bundle/version-auto-select machinery below (none of it applies: there's no run to size a
// BC selection for). Handled here, before the shadow-re-exec / BcArtifacts.SelectVersion
// machinery further down, so it works even with a completely empty artifacts cache.
if (provisionSubcommand && (provisionPlatformApps || provisionTestApps || provisionServiceTier
    || provisionResolveVersionPrefix != null))
{
    return RunExplicitProvisionModes(bcVersionArg, bundles, provisionPlatformApps, provisionTestApps,
        provisionServiceTier, provisionForce, provisionResolveVersionPrefix);
}
// --tdd (issue #1997) only changes the bundled-mode CLI run loop's EMIT-EXCLUDED
// handling (Program.cs, below). --server has its own, separate EMIT-EXCLUDED guard
// (a different Emit() call site) that this issue's reduced scope does not touch, so
// --tdd + --server stays rejected. Rejecting explicitly beats silently ignoring the
// flag — a --tdd run that quietly behaved like a normal run under --server would be
// far more confusing than an upfront error naming the gap.
if (tddMode && serverMode)
{
    Console.Error.WriteLine("--tdd is not supported together with --server yet (local-development flag; --server's EMIT-EXCLUDED handling is a separate code path this hasn't reached). Run --tdd from the CLI directly.");
    return 2;
}
// --tdd + --watch (issue #2002, follow-up to #1997): NOT rejected. --watch's
// incremental (RAD) recompile path (BcCompiler.TryEmitIncremental) genuinely does not
// carry per-excluded-object diagnostics through to a synthetic TestResult — but it
// does not need to, because of how BcCompiler.Emit's own baseline bookkeeping already
// behaves: RecordIncrementalBaseline (the thing that lets TryEmitIncremental take the
// fast path AT ALL) is only ever called when a cycle's Emit was a CLEAN success —
// caught == null && excludedObjects.Count == 0 — see BcCompiler.cs. Under --tdd, a
// cycle that excludes an object for referencing a missing symbol therefore NEVER
// records a baseline, which forces every cycle downstream of it (including the one
// where the missing symbol finally gets implemented) through TryEmitIncremental's own
// "no incremental baseline yet" fallback into the ordinary full Emit() — the SAME
// full-emit-retry loop that already builds TddExcludedObjectDetail and reports
// synthetic FAILED tests regardless of watchMode. So the option 2 the issue's own
// text proposes ("fall back to a full re-emit whenever a symbol is still missing,
// revert to incremental once resolved") falls out of the EXISTING guard for free —
// nothing here had to learn to carry TDD diagnostics through CreateForRad. See
// TryEmitIncremental's own tdd-specific fallback-reason text for what a developer
// actually sees on the console when this happens (issue #1994 precedent).

// --tdd forces the AL-output cache off (same effect as --no-cache), on top of the
// tdd:<0|1> cache-key line added above. The line alone stops a --tdd run from ever
// SERVING a normal-mode DLL or vice versa (criterion 11) — but it does not make a
// --tdd HIT correct on its own: the synthetic FAILED TestResults for excluded
// objects are derived fresh from source every Emit() call (TddSupport.BuildFailedTests
// re-parses the excluded .al files), and nothing about them is baked into the cached
// DLL. A --tdd cache HIT would skip Emit() entirely and silently drop back to
// reporting only the objects that DID compile — the exact "tests vanished, run looks
// green" failure mode this whole issue exists to fix, just moved one level down. Until
// the excluded-object detail has its own cache sidecar (a --tdd cache HIT is a
// reasonable follow-up), disabling the cache is what keeps every --tdd run correct.
//
// #2097 considered — but rejected — deferring this notice: unlike the trio (#2066) and
// the "already cached, proceeds normally" BC-selection lines below, this print sits
// upstream of several unrelated failure returns still to come in THIS SAME generation
// (bad bundle root, malformed --expectations/--count-baseline manifest, BC version
// selection failure, no matching engine variant, an incomplete artifact closure) — any
// of which would silently discard this notice along with it if it were queued instead
// of printed immediately. It duplicates on a stacked re-exec exactly like the lines
// below do, but staying immediate here is the smaller cost versus losing it on error.
if (tddMode && alCacheDir != null)
{
    Console.Error.WriteLine(
        "--tdd disables the AL-output cache for this run — its synthetic FAILED tests " +
        "for excluded objects are derived fresh from source on every Emit() call and " +
        "are not part of the cached DLL, so a cache HIT would silently drop them.");
    alCacheDir = null;
}
// ── Positional bundle roots must exist (#1713) ────────────────────────────────
// Checked HERE — at argument-parse time, before the BC artifact selection, the Cecil
// re-exec and the ~6s patch pass — so a mistyped path costs milliseconds. Before this,
// a nonexistent path travelled all the way into EnumerateSuitesBelow and threw a raw
// DirectoryNotFoundException out of Main: exit 134, the code the CI matrix documents as
// "crash", for the most ordinary user error there is. Exit 2 is the existing ladder
// entry for "could not execute (process-level error)" and is what every other CLI usage
// error above already returns — no new code introduced.
{
    var rootProblem = AlRunner.Infrastructure.BundleRootValidation.Validate(bundles);
    if (rootProblem != null)
    {
        Console.Error.WriteLine(rootProblem);
        return 2;
    }
}
// ── The same directory named twice runs ONCE (#2136) ──────────────────────────
// Immediately after the existence check above, and deliberately not before it: a
// mistyped path must still be reported as a mistyped path, never quietly folded into a
// similar-looking sibling. Dedup is by RESOLVED REAL path (symlinks followed, '..'
// collapsed, trailing separators trimmed), not by argument string — see
// BundleRootDeduplication for why identity is not the key and why raw-string Distinct()
// would fix only the least interesting case.
//
// Doing it here rather than inside the bundle loop fixes every downstream consumer at
// once: PhaseLog.SetBundles, the expectations-directory probe, TryDeriveBcMajorFromProject,
// CollectBundleAlpackagesDirs, the `bundles.Count > 1` layered pre-pass (which a
// duplicated single bundle was needlessly triggering), the run banner's bundle count,
// --watch's bundle name, and --dap's "exactly one bundle path" guard.
//
// Printed immediately rather than queued into startupNotices for the same reason the
// --tdd cache notice above is: several unrelated failure returns still lie between here
// and the flush, and any of them would discard a queued notice. It duplicates on a
// stacked re-exec exactly as those lines do; losing it entirely is the worse trade.
{
    var deduped = AlRunner.Infrastructure.BundleRootDeduplication.Deduplicate(bundles);
    var duplicateNotice = AlRunner.Infrastructure.BundleRootDeduplication.DescribeDropped(deduped.Dropped);
    if (duplicateNotice != null)
    {
        Console.Error.WriteLine(duplicateNotice);
        bundles = deduped.Roots.ToList();
    }
}
// #2041/#2066/#2097: rather than PREDICTING whether this generation will need to
// re-exec (the #2041 approach — a flag computed from NeedsShadow alone, before either
// the per-BC-minor variant swap or the Cecil-rewrite cache state is knowable), the
// success-path startup lines below are DEFERRED into this list and only flushed once
// this generation has cleared every re-exec decision point in the function — the
// shadow-hop check AND the Cecil-fresh-rewrite check, in that order, however many of
// them fire.
//
// #2041's predict-then-suppress design covered exactly one re-exec (the shadow hop) and
// silently broke the moment a SECOND one stacked on top: a per-BC-minor engine-variant
// swap forces its own shadow-hop generation to also perform its first-ever Cecil rewrite
// of that variant's Ncl.dll (a cache MISS, since the shadow-dir builder skips the
// pre-rewrite for a variant swap — see EnsureShadowDir's doc comment), which is a SECOND
// re-exec `reexecPending` had no way to see coming. That intermediate generation printed
// the trio believing itself final, then re-exec'd anyway, and the real final generation
// printed it again — three generations, two prints. See #2066.
//
// #2097: #2066 only fixed the trio. The "[expectations] loaded/not found" lines just
// below, and the "cached-exact"/"cached-minor" branches of the BC auto-selection switch
// further down, had the identical shape and duplicated the identical way, because they
// all print BEFORE either re-exec decision point below and this list did not exist yet
// at the point they ran. Declared here — ahead of all of them, instead of just ahead of
// the trio — so none of those prints can slip past deferral.
//
// NOT every candidate found by #2097's own audit of this startup path got moved into
// this list, even though every one of them duplicates the same way on a stacked re-exec.
// The --tdd cache-disable notice, the "cdn-exact"/"cdn-minor"/KNOWN-DEGRADED branches of
// the switch below, and the per-BC-minor-variants-shipped branch's own auto-select line
// all sit upstream of a LOUD FAILURE that can return from THIS SAME generation before
// ever reaching the flush point — deferring them risks silently discarding the one
// piece of output that explains why that failure happened, or (for "cdn-exact"/"cdn-
// minor" specifically) delays the caller's only signal that a real, possibly
// multi-minute download is about to start until AFTER that download finishes. See each
// site's own comment for why it was left immediate instead. Confirmed necessary by
// DefaultProvisionTargetMessagingTests, which failed against an earlier draft of this
// fix that deferred all of them uniformly.
//
// A generation that re-execs further always `return`s from inside one of the two
// decision blocks below, before ever reaching the flush point — so its accumulated
// entries are silently discarded, exactly as #2041 intended for the single-re-exec case,
// but now correctly for however many stack. LOUD FAILURES on the lines that ARE deferred
// here are still fine to lose this way: every error path in this function returns its
// own specific message immediately regardless, and the `[reexec]` explanation lines
// (#2034/#2038) are a different print entirely and stay unconditional, printed from
// whichever generation actually decides to hand off.
var deferredStartupLines = new List<Action>();
// ── Test-expectations manifest (issue #1734; docs/expectations.md) ────────────────
// Loaded HERE — at parse time, before BC init — so a malformed manifest aborts the
// invocation (exit 2, the "bad invocation" ladder entry) without running a single
// test. An explicit --expectations dir must exist; without the flag, the auto-probe
// walks up from each bundle path (and, secondarily, cwd) looking for a
// `tests/expectations` sibling — see ExpectationsDirectoryResolution for why cwd
// alone silently missed it (#1984) — activating classification only when found,
// leaving every invocation with no reachable manifest exactly as before.
AlRunner.Infrastructure.ExpectationManifest? expectations = null;
{
    var expectationsDir = expectationsDirArg;
    if (expectationsDir != null && !Directory.Exists(expectationsDir))
    {
        Console.Error.WriteLine($"--expectations: directory not found: {expectationsDir}");
        return 2;
    }
    if (expectationsDir == null)
    {
        expectationsDir = AlRunner.Infrastructure.ExpectationsDirectoryResolution.Resolve(bundles, Environment.CurrentDirectory);
        if (expectationsDir == null)
        {
            // #1984: this used to be silent — an explicit --expectations miss exits 2
            // loudly, but the auto-probed default just left `expectations` null and
            // every expect-oos/expect-divergence test in the run flipped to a plain
            // FAIL with nothing in the output to say why. Diagnosable, not inferred.
            var cwdCandidate = Path.Combine(Path.GetFullPath(Environment.CurrentDirectory), "tests", "expectations");
            // #2097: deferred — see `deferredStartupLines`'s declaration above. Captured
            // into a local now: `bundles` itself is never mutated again after arg
            // parsing, but capturing its count here (rather than reading `bundles.Count`
            // fresh inside the closure) keeps this consistent with every other deferred
            // line's rule of freezing values at queue time, not at flush time.
            var bundleCountForPrint = bundles.Count;
            deferredStartupLines.Add(() => Console.Error.WriteLine(
                $"[expectations] no tests/expectations manifest found (probed {cwdCandidate}" +
                (bundleCountForPrint > 0 ? $" and the ancestor tree of {bundleCountForPrint} bundle path(s)" : "") +
                ") — expect-oos / expect-fail-known-gap / expect-divergence classification is OFF " +
                "this run. Pass --expectations DIR to set it explicitly."));
        }
    }
    if (expectationsDir != null)
    {
        try
        {
            expectations = AlRunner.Infrastructure.ExpectationManifest.LoadFromDirectory(expectationsDir);
            // #2097: deferred — see `deferredStartupLines`'s declaration above. Captured
            // into locals now (LoadFromDirectory has already returned, so these values
            // are fixed) so the closure below reads exactly what THIS generation loaded,
            // not `expectations`/`expectationsDir` as they stand whenever the list is
            // eventually flushed.
            var expectationsEntryCountForPrint = expectations.Entries.Count;
            var expectationsDirForPrint = expectationsDir;
            deferredStartupLines.Add(() => Console.Error.WriteLine(
                $"[expectations] loaded {expectationsEntryCountForPrint} " +
                (expectationsEntryCountForPrint == 1 ? "entry" : "entries") +
                $" from {expectationsDirForPrint}"));
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"expectations manifest ({expectationsDir}): {ex.Message}");
            return 2;
        }
    }
}
// ── Count-baseline manifest (issue #1880; AlRunner/Infrastructure/CountBaseline.cs) ──
// Loaded HERE too — same reasoning as --expectations above: a malformed baseline
// aborts before any test runs (exit 2), not after paying for a full corpus run.
// Deliberately explicit-only (no auto-probed default) — see CountBaselinePath's
// declaration comment for why.
AlRunner.Infrastructure.CountBaselineManifest? countBaseline = null;
if (countBaselinePath != null)
{
    try
    {
        countBaseline = AlRunner.Infrastructure.CountBaselineManifest.Load(countBaselinePath);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}
// --output-json: stdout must be JSON-only, matching the documented contract ("Replace
// the normal text output with per-test JSON on stdout") and the convention --server
// already follows. Redirect ALL human-readable progress (bundle/suite banners, [layered]
// cache lines, [bc] selection notices, etc.) to stderr from here on; capture the real
// stdout so the single final JSON write below can go straight to it, un-interleaved.
System.IO.TextWriter? outputJsonStdout = null;
if (outputJson && !serverMode)
{
    outputJsonStdout = Console.Out;
    Console.SetOut(Console.Error);
}
// ── BC artifact/version selection (must run BEFORE the Cecil block below, which
// reads BcArtifacts.ServiceTierDir, and before any dependency/symbol resolver). Sets
// the process-global selection that resolvers A (engine), B (deps), C (symbols) all
// read, so a single chosen version drives the whole run. No auto-download: a missing /
// empty artifact root or an unmatched version throws loud (named download command).
// (Mutual exclusion is validated early — before the R2R re-exec — at the top of the file.)
// When --artifact-path points at a version-named child of the standard artifacts
// cache (the common case), translate it to the equivalent --bc-version selection so it
// takes the byte-identical code path as --bc-version. The explicit-root branch is then
// reserved for roots OUTSIDE the standard cache. (Empirically the bare existence of the
// explicit-root selection branch perturbs BC's R2R-precompiled startup bind enough to
// trigger a teardown AV — MEMORY.md "R2R-layout-perturbation native AV"; this keeps the
// in-cache case on the proven path.)
if (artifactPathArg != null)
{
    try
    {
        var translated = AlRunner.Infrastructure.BcArtifacts.TryTranslateArtifactPathToVersion(artifactPathArg);
        if (translated != null) { bcVersionArg = translated; artifactPathArg = null; }
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"BC version selection failed: {ex.Message}");
        return 2;
    }
}
// When the user pinned neither --bc-version nor --artifact-path, default the artifact
// selection to the ENGINE's built MAJOR rather than blindly latest-in-cache: this binary
// can only faithfully run its own major (cross-major needs a matching engine build), so a
// stray download of another major must never become the default. Within the major, any
// cached minor is interchangeable (verified 28.1<->28.2), so latest-in-major is picked.
// The target project's app.json (application/platform) is read purely as a cross-check —
// a mismatch means the project targets a BC major this runner build can't run, surfaced
// as a clear message instead of a deep failure. All of this stays overridable.
// Tracks whether bcVersionArg/artifactPathArg came from the auto-select default
// below, so the explicit-selection engine-minor-mismatch warning further down (see
// BcArtifacts.WarnIfExplicitEngineMinorMismatch) does not double-warn a case the
// auto-select branch already covers with its own, richer message.
bool bcVersionAutoSelected = false;
if (bcVersionArg == null && artifactPathArg == null)
{
    // #2027 BEHAVIOUR CHANGE: when this install ships per-BC-minor engine variants
    // (variants/ present — see EngineVariants), the no-flags default INVERTS from
    // engine-first to artifact-first. Below, ENGINE-first means "prefer whichever
    // minor THIS compiled binary happens to be" — that bias existed because there was
    // only ever one engine that could run at all, so a mismatched artifact was a real
    // problem to steer away from (see the -45/+42/+3 Pageworks regression in the
    // comment inside the else branch). With N correctly-matched variants shipped and
    // auto-swapped-to below, that bias no longer protects anything — ANY of the N
    // shipped minors is equally "this install's own engine" now, so picking the
    // LATEST CACHED artifact (this was the runner's ORIGINAL default, before the
    // engine-first change) is the more useful behaviour: a user who has since
    // downloaded a newer BC artifact gets it by default, rather than being pinned to
    // whichever minor happened to be copied into the package's top-level slot at pack
    // time. TryDeriveBcMajorFromProject(bundles) is still the cross-check either way.
    var shippedVariantsForDefault = AlRunner.Infrastructure.EngineVariants.Discover(AppContext.BaseDirectory);
    if (shippedVariantsForDefault.Count > 0)
    {
        bcVersionAutoSelected = true;
        // Prefer the engine's OWN major.minor. Latest-in-major used to win here, which
        // silently selected a minor the engine was not built for — measured at -45 passing
        // / +42 failing / +3 errors on Pageworks. See BcArtifacts.DefaultVersionPrefix.
        //
        // #2027: with per-BC-minor engine variants shipped, this branch (variants present)
        // goes artifact-first instead of engine-first — see the outer if/else below for why.
        try
        {
            var latestDir = AlRunner.Infrastructure.BcArtifacts.SelectArtifactVersionDir(
                AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, null);
            bcVersionArg = Path.GetFileName(latestDir);
            // #2097 considered — but rejected — deferring this line and the mismatch
            // warning just below: unlike the "cached-exact"/"cached-minor" branches of
            // the OTHER (no-variants-shipped) half of this if/else, this branch's own
            // "latest cached artifact" can still fail to have a matching engine variant
            // a few dozen lines further down (EngineVariants.SelectBestMatch returning
            // null is a loud, immediate `return 2`) — the exact silent-discard-on-error
            // shape proven real by DefaultProvisionTargetMessagingTests below, one
            // if/else branch over. Staying immediate accepts the same "duplicates 3x on
            // a stacked re-exec" cost the no-variants-shipped switch's KNOWN-DEGRADED
            // branches also still pay, for the same reason.
            Console.Error.WriteLine($"[bc] no --bc-version given — selecting BC {bcVersionArg}, the latest " +
                $"cached artifact ({shippedVariantsForDefault.Count} engine variant(s) shipped; the matching " +
                $"one is selected automatically below). Override with --bc-version.");
        }
        catch (InvalidOperationException)
        {
            // No artifacts cached at all — leave bcVersionArg null. SelectVersion below
            // throws the loud, path-naming "no artifacts" error users already see today.
        }

        var projMajorV = TryDeriveBcMajorFromProject(bundles);
        if (projMajorV != null && bcVersionArg != null
            && Version.TryParse(bcVersionArg, out var selV) && selV.Major.ToString() != projMajorV)
            Console.Error.WriteLine($"[bc] warning: project app.json targets BC major {projMajorV} but the " +
                $"latest cached artifact is {bcVersionArg} (major {selV.Major}).");
    }
    else
    {
        // The BUILT version (4-part, baked in at compile time) — not Ncl.dll's assembly
        // version, whose minor is always 0. Falls back to the Ncl major if the attribute is
        // missing (e.g. an older build), which restores the previous major-only behaviour.
        var engineVersion = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion()
            ?? AlRunner.Infrastructure.BcArtifacts.EngineVersion(AppContext.BaseDirectory);
        var engineMajor = engineVersion?.Major;
        // #2114: ArtifactsRootDir (used twice inside this block) throws loudly when $HOME
        // cannot be resolved to an absolute path. Probing it here — instead of letting the
        // two calls below throw UNCAUGHT (nothing wraps this block, unlike the sibling
        // "shippedVariantsForDefault" branch above, which already swallows the same
        // exception the same way) — lets a broken $HOME fall through to the
        // unconditionally-reached SelectVersion call further down, which IS wrapped in a
        // try/catch that turns this into the correct "BC version selection failed: ..."
        // exit-2 diagnostic, instead of crashing here unhandled.
        bool artifactsRootResolvable;
        try { _ = AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir; artifactsRootResolvable = true; }
        catch (InvalidOperationException) { artifactsRootResolvable = false; }
        if (engineVersion != null && engineMajor != null && artifactsRootResolvable)
        {
            bcVersionAutoSelected = true;
            // Prefer the engine's OWN major.minor. Latest-in-major used to win here, which
            // silently selected a minor the engine was not built for — measured at -45 passing
            // / +42 failing / +3 errors on Pageworks. See BcArtifacts.DefaultVersionPrefix.
            //
            // Issue #2033: when auto-provisioning is about to run anyway (the default since
            // #2024/#2028), ask what it can FETCH — cache, then the CDN, at each tier — not
            // just what's already cached. Otherwise a genuinely empty cache collapses this
            // straight to "major only" before a single byte is downloaded, and provisioning
            // then fetches "latest in major" (e.g. 28.4) while the engine was built for 28.1,
            // landing a first run in the exact KNOWN-DEGRADED skew #2020 describes. Without
            // --auto-provision there is no network step coming, so stay cache-only exactly as
            // before — that path has nothing to gain from probing a CDN it will never use.
            string tier;
            if (provisionSubcommand || autoProvision)
                bcVersionArg = AlRunner.Infrastructure.BcArtifacts.DefaultProvisionTarget(
                    engineVersion, AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, out tier);
            else
            {
                bcVersionArg = AlRunner.Infrastructure.BcArtifacts.DefaultVersionPrefix(
                    engineVersion, AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir);
                var engineMinorPfx = $"{engineVersion.Major}.{engineVersion.Minor}";
                tier = bcVersionArg == engineVersion.ToString() ? "cached-exact"
                    : bcVersionArg == engineMinorPfx ? "cached-minor"
                    : "major-fallback-offline"; // distinct from "major-fallback": no CDN was consulted
            }

            var engineMajorMinor = $"{engineVersion.Major}.{engineVersion.Minor}";
            // #2097: only "cached-exact" and "cached-minor" below are deferred — see
            // `deferredStartupLines`'s declaration above. Both describe an artifact
            // that is ALREADY on disk, so SelectVersion just below reliably succeeds
            // against it and this generation proceeds normally to the flush point,
            // same shape as the reported "cached-exact" duplication. The other three
            // branches are deliberately left printing immediately, for two DIFFERENT
            // reasons:
            //   - "cdn-exact"/"cdn-minor": ResolveProvisionTargetCore checks the cache
            //     BEFORE the CDN at every tier (see its own doc comment), so once
            //     RunProvisioning below successfully downloads what these branches
            //     describe, EVERY later generation's tier recomputation finds it
            //     already cached and takes the "cached-exact"/"cached-minor" branch
            //     instead — a "cdn-*" branch can only ever fire in the one generation
            //     that is about to perform the download, so it cannot itself
            //     duplicate across re-execs the way the cached branches do. Deferring
            //     it anyway would be a straight regression: the download that follows
            //     can take minutes, and this line is the ONLY signal a caller gets
            //     that a download is about to start at all — see
            //     DefaultProvisionTargetMessagingTests.
            //     AutoProvisionDefault_EmptyCache_TargetsEngineExactBuild_NeverDegradedWarning,
            //     which kills the process the instant this line appears specifically
            //     so it never has to wait out the real download, and failed hard
            //     (30s timeout) the one time this was deferred here.
            //   - "major-fallback-offline"/default ("major-fallback"): both are a
            //     KNOWN-DEGRADED warning that commonly precedes an immediate failure
            //     in THIS SAME generation (SelectVersion below has nothing durable to
            //     select if nothing at all could be resolved) — deferring would risk
            //     silently discarding the one piece of output that explains WHY the
            //     following generic "BC version selection failed" error happened.
            //     Confirmed by DefaultProvisionTargetMessagingTests.
            //     NoAutoProvision_EmptyCache_MajorFallbackWarning_NeverClaimsCdnWasChecked,
            //     which asserts on this exact text and failed the one time it was
            //     deferred here (the process exits 2 before ever reaching the flush
            //     point, so the deferred entry was silently dropped).
            switch (tier)
            {
                case "cached-exact":
                    deferredStartupLines.Add(() => Console.Error.WriteLine(
                        $"[bc] no --bc-version given — selecting BC {engineVersion}, the exact " +
                        $"build this binary was compiled against. Override with --bc-version."));
                    break;
                case "cdn-exact":
                    Console.Error.WriteLine($"[bc] no --bc-version given — provisioning BC {engineVersion}, the exact " +
                        $"build this binary was compiled against. Override with --bc-version.");
                    break;
                case "cached-minor":
                    // Degraded but usually survivable: right minor, different build. The CodeAnalysis
                    // assembly version can still differ between builds of one minor, which fails loud
                    // at startup rather than silently — see BcArtifacts.DefaultVersionPrefix.
                    deferredStartupLines.Add(() => Console.Error.WriteLine(
                        $"[bc] warning: no cached BC {engineVersion} — selecting the latest " +
                        $"{engineMajorMinor}.x instead. Build-level skew within a minor can still fail to load " +
                        $"Microsoft.Dynamics.Nav.CodeAnalysis. Fix with: al-runner provision --bc-version {engineVersion}"));
                    break;
                case "cdn-minor":
                    Console.Error.WriteLine($"[bc] no --bc-version given and BC {engineVersion} is not published on " +
                        $"the CDN — provisioning the latest {engineMajorMinor}.x instead (still this binary's own " +
                        $"engine minor). Build-level skew within a minor can still fail to load " +
                        $"Microsoft.Dynamics.Nav.CodeAnalysis. Fix with: al-runner provision --bc-version {engineVersion}");
                    break;
                case "major-fallback-offline":
                    // No network step is coming (--no-auto-provision, or the rare case where
                    // engineVersion resolved but auto-provisioning is off) — this can only speak
                    // to what's CACHED, never to CDN availability. Original pre-#2033 wording.
                    Console.Error.WriteLine($"[bc] warning: no cached BC {engineMajorMinor}.x — this binary's engine " +
                        $"was built for {engineVersion}, so a different minor is a KNOWN-DEGRADED configuration " +
                        $"(measured: dozens of extra failures from engine/artifact minor skew). Falling back to the " +
                        $"latest cached {engineMajor}.x. Fix with: al-runner provision --bc-version {engineMajorMinor}");
                    break;
                default: // major-fallback: neither the exact build nor the engine's own minor is
                         // available from cache or the CDN — a genuine degradation (e.g. #2010,
                         // Microsoft withdrew the build), not the default-path norm.
                    Console.Error.WriteLine($"[bc] warning: BC {engineMajorMinor}.x is not cached and not available " +
                        $"from the CDN — this binary's engine was built for {engineVersion}, so a different minor is " +
                        $"a KNOWN-DEGRADED configuration (measured: dozens of extra failures from engine/artifact " +
                        $"minor skew). Falling back to the latest {engineMajor}.x. Fix with: al-runner provision " +
                        $"--bc-version {engineMajorMinor}");
                    break;
            }

            // #2097: NOT deferred — deliberately kept immediate, unlike the cached-tier
            // branches above. This fires regardless of which tier won, including the two
            // KNOWN-DEGRADED branches that can precede an immediate failure return in
            // this same generation — deferring it would risk the same silent-discard-on-
            // error trap documented on the switch above.
            var projMajor = TryDeriveBcMajorFromProject(bundles);
            if (projMajor != null && projMajor != engineMajor.Value.ToString())
                Console.Error.WriteLine($"[bc] warning: project app.json targets BC major {projMajor} but this " +
                    $"runner build supports major {engineMajor} (cross-major needs a matching runner build).");
        }
    }
}
// ── Provisioning (on by default since issue #2024; opt out with --no-auto-provision):
// `provision` subcommand or autoProvision (default true). Resolves the target version,
// downloads the engine service-tier closure if it's missing/incomplete, then (subcommand)
// exits or (flag/default) continues the run against what was provisioned. This is the
// ONLY path that downloads — a run with --no-auto-provision never does.
if (provisionSubcommand || autoProvision)
{
    // Issue #1996 (AC #6): manifest-app (platform-apps/test-apps) provisioning must have
    // exactly ONE owner per invocation. For a plain --auto-provision run that continues
    // (not the `provision` subcommand), that owner is the post-SelectVersion gate further
    // down — it alone knows the actual selected BC version, the actual --package-cache set,
    // and the manifest need, and it runs exactly once even across a Cecil-rewrite re-exec
    // (the re-exec always returns before reaching it). RunProvisioning here therefore only
    // handles the ENGINE service-tier closure in that case; only the `provision` subcommand
    // (which never reaches the post-selection gate — it returns immediately below) also
    // provisions manifest apps itself.
    //
    // #2041/#2066: the `provision` subcommand itself always prints immediately (passes
    // `deferredLines: null`) — it returns right below and never re-execs, so its own
    // success line would otherwise be silently lost with no later generation to reprint
    // it. The continuing (non-subcommand) path defers into `deferredStartupLines` instead
    // of printing here — see that list's declaration above for why.
    var prc = RunProvisioning(bcVersionArg, artifactPathArg, bundles, provisionManifestApps: provisionSubcommand,
        deferredLines: provisionSubcommand ? null : deferredStartupLines, out var provisionedVersion);
    if (provisionSubcommand)
        return prc; // the subcommand always exits after provisioning, never runs tests
    if (prc == 0 && provisionedVersion != null)
        bcVersionArg = provisionedVersion; // run against the version we just ensured
    // On failure with --auto-provision we fall through; SelectVersion below emits the
    // loud, path-naming error (and the detailed ProvisioningCheck report if partial).
}
// #2037: discovered here, OUTSIDE and BEFORE the try block below, so both the warn-gate
// (inside the try) and the variant-swap block (after it) share one discovery — see the
// comments at each use site.
var shippedVariants = AlRunner.Infrastructure.EngineVariants.Discover(AppContext.BaseDirectory);
try
{
    AlRunner.Infrastructure.BcArtifacts.SelectVersion(bcVersionArg, artifactPathArg);
    // Consistency guard: the engine DLLs baked into bin/ are built for a fixed BC
    // major.minor; if the selected version's major.minor differs, dependency symbols
    // and the engine can disagree — fail loud rather than crash deep in BC. Patch-level
    // skew (28.1.x build vs 28.1.y cache) is tolerated.
    AlRunner.Infrastructure.BcArtifacts.VerifyEngineConsistency(AppContext.BaseDirectory);
    // #2008's root cause: VerifyEngineConsistency only catches a MAJOR mismatch (Ncl.dll's
    // own AssemblyVersion is always major.0.0.0, so it cannot see a same-major
    // different-minor selection). The auto-select default path above already warns about
    // minor skew; an EXPLICIT --bc-version/--artifact-path bypassed that warning entirely
    // and ran a mismatched engine silently. Only warn here for the explicit path.
    //
    // #2037: also only warn when this install ships NO per-BC-minor engine variants at
    // all (see ShouldWarnExplicitEngineMinorMismatch) — once any variant is shipped, the
    // variant-swap block below is the sole authority on whether the selection is
    // degraded, not this generic same-process-engine comparison.
    if (AlRunner.Infrastructure.BcArtifacts.ShouldWarnExplicitEngineMinorMismatch(
            bcVersionAutoSelected, shippedVariants.Count))
        AlRunner.Infrastructure.BcArtifacts.WarnIfExplicitEngineMinorMismatch();
    // #2041/#2066: deferred — see `deferredStartupLines`' declaration above. Captured into
    // locals now (the values are fixed the instant SelectVersion above returns) so the
    // closure below reads exactly what THIS generation selected, not whatever the static
    // BcArtifacts state happens to hold whenever the list is eventually flushed.
    var selectedVersionForPrint = AlRunner.Infrastructure.BcArtifacts.SelectedVersion;
    var serviceTierDirForPrint = AlRunner.Infrastructure.BcArtifacts.ServiceTierDir;
    deferredStartupLines.Add(() => Console.Error.WriteLine(
        $"[bc] selected BC {selectedVersionForPrint} ({serviceTierDirForPrint})"));
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"BC version selection failed: {ex.Message}");
    return 2;
}

// ── Per-BC-minor engine variant selection (#2024 item 3 / #2027). A packaged install
// ships one thin engine variant per .github/bc-versions.txt entry under
// variants/<full-build-version>/ (see EngineVariants) — this process's own compiled-in
// engine is just ONE of them. A plain dev/test build (`dotnet build`/`dotnet run`) has no
// variants/ directory at all, and this whole block is then a complete no-op: `variants`
// comes back empty, `variantSwapDir` stays null, and every existing single-build code
// path below behaves exactly as it always has.
//
// No match found among the shipped variants is a LOUD failure, never a silent fallback
// to a nearby minor — that silent fallback is the root cause #2020 traced this whole
// mechanism back to (see .claude/rules/loud-failures.md).
//
// `shippedVariants` was already discovered above (see #2037 comment on the warn gate) —
// reused here rather than re-walking the variants/ directory a second time.
string? variantSwapDir = null;
{
    if (shippedVariants.Count > 0)
    {
        var selected = AlRunner.Infrastructure.BcArtifacts.SelectedVersion;
        var match = AlRunner.Infrastructure.EngineVariants.SelectBestMatch(shippedVariants, selected);
        if (match == null)
        {
            Console.Error.WriteLine(
                $"BC version selection failed: no shipped engine variant supports BC {selected} " +
                $"(major {selected.Major}). Available variants: " +
                $"{AlRunner.Infrastructure.EngineVariants.DescribeAvailable(shippedVariants)}. Select a " +
                $"cached BC version this install ships an engine for (--bc-version), or update al-runner.");
            return 2;
        }

        var (variant, degraded) = match.Value;
        if (degraded)
        {
            // #2041/#2066: deferred — see `deferredStartupLines`' declaration above. This
            // block runs in EVERY generation that reaches it (it is not itself gated on a
            // re-exec prediction), so without deferring it this warning reprints once per
            // generation — the specific "[bc] warning: ... built against ..." duplication
            // (×3 on a stacked variant-swap-then-fresh-rewrite run) the issue measured.
            var degradedVariantBuild = variant.BuildVersion;
            var degradedSelected = selected;
            deferredStartupLines.Add(() => Console.Error.WriteLine(
                $"[bc] warning: the shipped {degradedVariantBuild.Major}.{degradedVariantBuild.Minor} engine " +
                $"variant was built against {degradedVariantBuild}, not the selected {degradedSelected} — " +
                $"different BUILDS of the same minor can still fail to load " +
                $"Microsoft.Dynamics.Nav.CodeAnalysis (it's strong-named per build, not per minor). Expected: " +
                $"variants pin the newest build of a minor AT PACK TIME, so any user on a different build of " +
                $"that same minor hits this. See docs/limitations.md."));
        }

        var runningBuild = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion();
        if (runningBuild != variant.BuildVersion)
        {
            variantSwapDir = variant.Dir;
            Console.Error.WriteLine(
                $"[bc] selecting engine variant {variant.BuildVersion} for BC {selected} (this process is " +
                $"currently running the {(runningBuild?.ToString() ?? "unknown")} variant) — re-execing.");
        }
    }
}

// Completeness gate: the selected version's dir exists, but is its engine closure whole?
// A partial /service/ closure would otherwise fail deep in a FileLoadException at runtime
// (the version-agnostic engine serves the BC-app closure from this dir). On a normal run
// we do NOT download — we print ONE loud, path-naming report + the one-command fix and
// stop. (--auto-provision already completed it above, so this only trips on a real gap.)
{
    var provReport = AlRunner.Infrastructure.ProvisioningCheck.Check(
        AlRunner.Infrastructure.BcArtifacts.SelectedVersion.ToString(),
        AlRunner.Infrastructure.BcArtifacts.ServiceTierDir);
    if (!provReport.Ok)
    {
        Console.Error.WriteLine(provReport.ToDetailedMessage(bundles.Count > 0 ? bundles[0] : null));
        return 2;
    }
}

if (alCacheDir != null) Directory.CreateDirectory(alCacheDir);
// #1821: must run before the Cecil rewrite below (first ncl-cecil consumer) and well
// before any DependencyLoader/BcAppSymbolCache/workspace-deps call — all four read
// CacheRoots.Resolve for their cache directory.
AlRunner.Infrastructure.CacheRoots.SetOverride(cacheRootOverride);
// #2041/#2066: deferred — see `deferredStartupLines`' declaration above. This generation
// may still hand off via either re-exec decision below, and touches no bundle work at all
// before doing so — the flush after both decisions is what makes this print exactly once,
// from whichever generation is actually terminal.
deferredStartupLines.Add(() => Console.WriteLine(serverMode
    ? "al-runner — server mode (JSON-RPC over stdin/stdout)"
    : watchMode
        ? $"al-runner — watch mode, {bundles.Count} bundle(s) (Ctrl+C to quit)"
        : $"al-runner — running {bundles.Count} bundle(s)"));

// The packaged tool no longer ships Microsoft.Dynamics.Nav.Ncl.dll (see
// check-nupkg-contents.sh) — it must be resolved from the user's own BC artifact
// cache at runtime, like every other BC/Aspose/Graph DLL already stripped from the
// package. CoreCLR's TPA list is computed once, by the native host, before any of
// our code runs, so a THIS-process fix is impossible once we're past that point:
// re-exec into a shadow runtime dir (see NclShadowRuntime) that legitimately has the
// file on disk before ITS TPA is computed. A shadow child's own base directory
// always has the real file, so this naturally does not re-fire there.
// variantSwapDir != null (set above) ALSO routes through this same shadow-dir
// mechanism: NclShadowRuntime.EnsureShadowDir's entrySourceDir parameter copies the
// entry-assembly manifest set from the SELECTED VARIANT's own directory instead of this
// process's, so one re-exec covers both "Ncl.dll isn't shipped" and "a different BC-minor
// engine variant is needed" — see the doc comment on EnsureShadowDir.
{
    var shadowChildExit = TryShadowReexec(variantSwapDir);
    if (shadowChildExit.HasValue) return shadowChildExit.Value;
}

// Cecil-rewrite Ncl.dll IN-PLACE on the bin path BEFORE CoreCLR's TPA probe
// resolves it. Must run BEFORE any reference to BcRuntime (whose field metadata
// triggers Ncl load on class init). Allowed surface per
// .claude/rules/precompiled-dll-respect.md — Ncl is runtime engine, not BaseApp.
{
    var srcDir = AlRunner.Infrastructure.BcArtifacts.ServiceTierDir;
    var binNcl = Path.Combine(AppContext.BaseDirectory, "Microsoft.Dynamics.Nav.Ncl.dll");
    var didFreshRewrite = AlRunner.Infrastructure.NclCecilRewrite.RewriteInPlace(srcDir, binNcl);

    // A process that performs the Cecil rewrite and then loads the byte-identical
    // rewritten Ncl in-process intermittently dies with BadImageFormatException
    // 0x80131124 ("Index not found"). A fresh process loading the same bytes via
    // cache HIT always succeeds. So on a fresh rewrite (cold run / CACHE_VERSION
    // bump), re-exec ourselves once: the child hits the now-populated cache and
    // loads cleanly. The AL_RUNNER_REEXECED guard prevents an infinite loop.
    if (didFreshRewrite && Environment.GetEnvironmentVariable("AL_RUNNER_REEXECED") != "1")
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
        };
        var argv = RewriteArtifactPathArg(Environment.GetCommandLineArgs());
        // Under the `dotnet` muxer, ProcessPath is dotnet and argv[0] (the managed
        // dll) must be forwarded as its first arg. Under the native apphost,
        // ProcessPath is the app itself and argv[0] must NOT be forwarded (the
        // apphost would treat the dll path as a bundle directory → DirectoryNotFoundException).
        var underDotnet = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath!)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var userArgs = underDotnet ? argv : argv.Skip(1);
        foreach (var a in userArgs)
            psi.ArgumentList.Add(a);
        psi.Environment["AL_RUNNER_REEXECED"] = "1";
        // #2034 audit: this is the SAME class of silently-swallowed re-exec explanation
        // (a fresh Cecil IL rewrite forces one more relaunch so the child loads the
        // now-cached bytes cleanly) — also retagged so it survives the default filter.
        Console.Error.WriteLine("[reexec] Fresh rewrite done — re-execing for a clean Ncl load");
        // This process waits for the child below, so its wall clock CONTAINS the
        // child's entire run. Re-label the row so aggregates that sum `kind=="process"`
        // do not double-count it.
        AlRunner.Infrastructure.PhaseLog.MarkReexecParent();
        using var child = System.Diagnostics.Process.Start(psi)!;
        child.WaitForExit();
        return child.ExitCode;
    }
}

// #2041/#2066: this generation has now cleared BOTH re-exec decision points above (the
// shadow hop and the Cecil-fresh-rewrite hop) without returning — it is the terminal
// generation for this invocation, so this is the one and only point that flushes the
// startup lines queued in `deferredStartupLines`, in the order they were queued
// (provisioning result, selected BC version, any degraded-variant warning, then the
// running/watch/server-mode banner). Any earlier generation that instead re-exec'd
// returned from inside one of those blocks and never reaches this line, so its own queued
// entries are simply discarded — however many generations preceded this one.
foreach (var deferredLine in deferredStartupLines) deferredLine();

var packageCacheDirs = packageCacheArgs.Count > 0
    ? ExpandPackageCacheDirs(packageCacheArgs).ToList()
    : DefaultPackageCacheDirs().ToList();
// #2107: labeled "(requested)" — this is the explicit/default set ONLY, before
// packageCacheDirs gains the fold-ins below (extraProvisionSearchDirs, then
// platformAppsOut/testAppsOut inside the provisioning block). A generic "package
// caches: N dir(s)" label read as the whole story here, so a reader who only saw this
// line (the provisioning block between it and the final count can print a multi-minute
// download) could reasonably conclude nothing was searched — exactly backwards from
// what #2067 needed. SourceDepSymbolsWithoutPackageCacheTests/SourceDepCacheEnumMetadataTests
// pin this exact label + count as a precondition on the explicit-arg branch.
Console.WriteLine($"  package caches (requested): {packageCacheDirs.Count} dir(s)");
AlRunner.Infrastructure.PhaseLog.SetBundles(bundles);

// Issue #1678: the platform-app R2R gate below used to scan ONLY packageCacheDirs
// (the home-rooted default caches / explicit --package-cache dirs) — never the target
// bundles' own `.alpackages`, which is exactly where every standard AL project's symbol
// download lives. For that ordinary shape the gate saw an empty set, reported "Ok"
// vacuously, and neither the loud failure nor --auto-provision's download ever fired —
// the run limped all the way to a cryptic NavNCLMissingMethodException deep in dispatch
// instead of hitting either remediation the "[provision-gap]" message promises. Fold the
// bundles' own .alpackages into the dirs the gate scans (recomputed via PlatformCheckDirs
// below so it picks up anything --auto-provision adds to packageCacheDirs afterward).
var bundleAlpackagesDirs = AlRunner.Infrastructure.ProvisioningCheck.CollectBundleAlpackagesDirs(bundles);

// Issue #1996 (AC #3/#4): the runner-owned versioned destination(s) from a PRIOR
// --auto-provision / `provision` run — checked BEFORE any network attempt, and BEFORE any
// --package-cache dir even needs to exist, so a warm re-run (even one still passing an
// empty/nonexistent --package-cache, as issue #1996's own repro does) never re-hits the
// CDN. Populated once the selected BC version is known (already true at this point).
var selectedVersionForProvisioning = AlRunner.Infrastructure.BcArtifacts.SelectedVersion.ToString();
var runnerOwnedPlatformAppsDir = AlRunner.Infrastructure.ProvisioningCheck.PlatformAppsDirFor(
    AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, selectedVersionForProvisioning);
var runnerOwnedTestAppsDir = AlRunner.Infrastructure.ProvisioningCheck.TestAppsDirFor(
    AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, selectedVersionForProvisioning);
var extraProvisionSearchDirs = new List<string>();
if (Directory.Exists(runnerOwnedPlatformAppsDir)) extraProvisionSearchDirs.Add(runnerOwnedPlatformAppsDir);
if (Directory.Exists(runnerOwnedTestAppsDir)) extraProvisionSearchDirs.Add(runnerOwnedTestAppsDir);
List<string> PlatformCheckDirs() =>
    packageCacheDirs.Concat(bundleAlpackagesDirs).Concat(extraProvisionSearchDirs).Distinct().ToList();
// These are read-only, already-on-disk runner-owned dirs for the SELECTED version — fold
// them into the set dependency resolution actually uses too (mirrors what
// DefaultPackageCacheDirs already does automatically when no explicit --package-cache is
// given; this closes the same gap for an EXPLICIT --package-cache, e.g. this exact
// invocation's own CLI flags). Without this, a prior run's warm test-apps/platform-apps
// stay invisible to compilation even though the provisioning DECISION already sees them.
foreach (var d in extraProvisionSearchDirs)
    if (!packageCacheDirs.Contains(d))
        packageCacheDirs.Add(d);

// Platform-app R2R check: scan the package cache for known Microsoft platform runtime apps
// (System Application, Base Application, Business Foundation). If any are present as
// symbol-only (non-R2R) packages, the runner CANNOT execute their codeunits at runtime —
// the EMIT-ZERO crash is a provisioning gap, not a user-code error. Fail loud here before
// any bundle compile, naming the fix, instead of deep inside the dep-load pipeline.
// (--auto-provision downloads the R2R apps and clears the check.)
//
// Issue #1996: this used to be gated on `packageCacheDirs.Count > 0 || bundleAlpackagesDirs
// .Count > 0` — an EMPTY cache (no .alpackages at all, or a --package-cache dir that simply
// doesn't exist yet) skipped the whole gate, so a bundle whose app.json genuinely needs a
// Microsoft app with no service-tier DLL fallback (Application Test Library) got neither
// the loud failure nor the auto-provision download; it limped to a cryptic "Missing:" error
// deep in dependency resolution instead. The gate now ALWAYS runs (dropping that count
// check) and consults the bundle's own manifests — an independent source of truth for what
// is actually needed — instead of only what happens to already be on disk.
if (!provisionSubcommand)
{
    var version = selectedVersionForProvisioning;
    var platformReport = AlRunner.Infrastructure.ProvisioningCheck.CheckPlatformApps(
        version, PlatformCheckDirs());
    // Manifest-driven need (issue #1996): independent of what CheckPlatformApps/
    // TestToolkitPresent can see on disk. See ProvisioningCheck.DecideManifestProvisioning.
    var manifestDependencyRoots = ScanManifestDependencyRoots(bundles);
    var decision = AlRunner.Infrastructure.ProvisioningCheck.DecideManifestProvisioning(
        manifestDependencyRoots, platformReport, PlatformCheckDirs());
    // Test-toolkit apps (Business Foundation Test Libraries, Application Test Library, …)
    // are a SEPARATE artifact set from the w1 platform apps (they live under the
    // `platform` artifact, not `w1`) — a cache can have complete R2R platform apps and
    // still be missing the whole test toolkit, which fails compiling any test bundle.
    var toolkitPresent = decision.TestComplete;

    if (decision.ShouldDownloadAny && !autoProvision)
    {
        // Issue #1996 acceptance criterion #10 / issue #2024: no download when the caller
        // has explicitly refused it with --no-auto-provision, on EITHER path.
        Console.Error.WriteLine(!platformReport.Ok
            ? platformReport.ToDetailedMessage()
            : AlRunner.Infrastructure.ProvisioningCheck.BuildManifestNeedsMissingMessage(
                decision.ShouldDownloadPlatform, decision.ShouldDownloadTest, PlatformCheckDirs()));
        return 2;
    }

    if (autoProvision && decision.ShouldDownloadAny)
    {
        // Issue #2077: always target the SELECTED BC version's own major.minor — never one
        // derived from cache contents (a symbol-only app, or a project-vendored
        // `.alpackages` closure) as this used to. That derivation silently redirected the
        // whole provisioning pass to whatever minor happened to already be on disk (e.g.
        // `--bc-version 28.4` provisioning 28.1 platform apps because the bundle's own
        // committed `.alpackages` vendors 28.1 symbols) — the engine ended up running R2R
        // apps from a build nobody asked for, with the mismatch never stated.
        var mm = AlRunner.Infrastructure.ProvisioningCheck.ResolveProvisionMajorMinor(version);
        {
            // Loud mismatch note (acceptance criterion): tell the user when the cache would
            // have suggested a DIFFERENT minor than the one actually being provisioned, even
            // though we no longer act on that suggestion.
            var cacheMm = !platformReport.Ok
                ? AlRunner.Infrastructure.ProvisioningCheck.DeriveProvisionMajorMinor(platformReport, version)
                : AlRunner.Infrastructure.ProvisioningCheck.DerivePresentPlatformMajorMinor(PlatformCheckDirs(), version);
            var skewNote = AlRunner.Infrastructure.ProvisioningCheck.BuildProvisionVersionSkewNote(
                mm, cacheMm,
                !platformReport.Ok
                    ? "a symbol-only platform app already in the package cache"
                    : "platform apps already in the package cache");
            if (skewNote != null)
                Console.Error.WriteLine(skewNote);
        }
        // AC #4/#5: prefer a version ALREADY cached (any patch of this major.minor) whose
        // needed set(s) are complete, before ever asking the CDN for "latest" — otherwise a
        // warm machine re-resolves and re-downloads a NEWER patch on every single run.
        // Skipped when a LEGACY symbol-only issue exists (!platformReport.Ok): that's a
        // known-bad app already sitting in the cache, distinct from "nothing found yet",
        // and NoFallbackPlatformAppsPresent's app (Application Test Library) doesn't even
        // exist for every BC version (e.g. 27.x) — using it as the completeness signal for
        // that case would never find a match and would incorrectly gate warm reuse on an
        // app the legacy issue has nothing to do with. Falling through to CDN resolution
        // here matches this path's pre-existing (issue #1678) behavior.
        //
        // Issue #2003: a warm candidate must also meet the version floor the bundle's own
        // app.json manifests declare, not just be present — versionFloors carries that
        // (derived from the SAME manifestDependencyRoots DetermineManifestNeeds already
        // read above), so a stale warm set is skipped rather than silently reused.
        var versionFloors = AlRunner.Infrastructure.ProvisioningCheck.DetermineVersionFloors(manifestDependencyRoots);
        var full = (platformReport.Ok
                ? FindWarmProvisionedVersion(
                    AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, mm,
                    decision.NeedsPlatformApps, decision.ShouldDownloadTest,
                    versionFloors, m => Console.Error.WriteLine(m))
                : null)
            ?? AlRunner.Provisioning.ArtifactDownloader.ResolveVersion(
                mm, m => Console.Error.WriteLine($"[provision] {m}"));
        if (full == null)
        {
            Console.Error.WriteLine($"[provision] could not resolve a full BC artifact version for '{mm}'; cannot continue.");
            return 2;
        }
        // Runner-owned artifact-cache destinations — NEVER a caller-supplied --package-cache
        // dir (issue #1653: this used to pick packageCacheDirs[0], writing ~135 MB of
        // downloaded apps straight into the project's .alpackages). Mirrors the destination
        // the standalone `al-runner provision` step already uses for the test toolkit.
        var platformAppsOut = AlRunner.Infrastructure.ProvisioningCheck.PlatformAppsDirFor(
            AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, full);
        var testAppsOut = AlRunner.Infrastructure.ProvisioningCheck.TestAppsDirFor(
            AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, full);

        if (decision.ShouldDownloadPlatform)
        {
            // Reuse-first (AC #4/#5): the resolved `full` version can be a warm same-
            // major/minor destination that a PRIOR run already completed (e.g. a
            // different patch of the same minor, or this exact invocation retried after
            // a transient failure) — check before ever touching the network. Folding
            // platformAppsOut into the search set FIRST and re-deciding against it (rather
            // than a standalone ATL-only presence check) correctly covers BOTH triggers:
            // the legacy symbol-only gap (some BC versions — e.g. 27.x — never ship
            // Application Test Library at all, so an ATL-only check would never be
            // satisfiable for them) and the manifest-driven ATL need.
            if (!packageCacheDirs.Contains(platformAppsOut))
                packageCacheDirs.Add(platformAppsOut);
            var reuseReport = AlRunner.Infrastructure.ProvisioningCheck.CheckPlatformApps(
                version, PlatformCheckDirs());
            var reuseDecision = AlRunner.Infrastructure.ProvisioningCheck.DecideManifestProvisioning(
                manifestDependencyRoots, reuseReport, PlatformCheckDirs());
            if (!reuseDecision.ShouldDownloadPlatform)
            {
                Console.Error.WriteLine($"[provision] platform apps already complete at {platformAppsOut}.");
            }
            else
            {
                Console.Error.WriteLine("[provision] platform R2R apps missing — downloading...");
                var rc = AlRunner.Provisioning.ArtifactDownloader.PlatformApps(
                    full, platformAppsOut, m => Console.Error.WriteLine($"[provision] {m}"));
                if (rc != 0)
                {
                    Console.Error.WriteLine("[provision] platform-apps download failed; cannot continue.");
                    return 2;
                }
            }
            // Re-check: never silently continue on a partial/failed provision.
            platformReport = AlRunner.Infrastructure.ProvisioningCheck.CheckPlatformApps(
                version, PlatformCheckDirs());
            if (!platformReport.Ok)
            {
                var stillMissing = string.Join(", ", platformReport.Issues.Select(i => i.Name));
                Console.Error.WriteLine($"[provision] platform apps still symbol-only after download: {stillMissing}");
                return 2;
            }
            // Only demand literal Application Test Library presence when the MANIFEST
            // actually declared a need for it — some BC versions never ship it at all, so
            // requiring it unconditionally would fail every platform-apps download
            // triggered solely by the legacy symbol-only gap (e.g. corpus bundles on BC 27.x).
            if (decision.NeedsPlatformApps
                && !AlRunner.Infrastructure.ProvisioningCheck.NoFallbackPlatformAppsPresent(PlatformCheckDirs()))
            {
                Console.Error.WriteLine("[provision] platform apps (Application Test Library) still missing after download.");
                return 2;
            }
        }

        if (decision.ShouldDownloadTest)
        {
            if (AlRunner.Infrastructure.ProvisioningCheck.TestToolkitPresent(new[] { testAppsOut }))
            {
                Console.Error.WriteLine($"[provision] test toolkit already complete at {testAppsOut}.");
            }
            else
            {
                Console.Error.WriteLine("[provision] test-toolkit apps missing — downloading...");
                var rc = AlRunner.Provisioning.ArtifactDownloader.TestApps(
                    full, testAppsOut, m => Console.Error.WriteLine($"[provision] {m}"));
                if (rc != 0)
                {
                    Console.Error.WriteLine("[provision] test-toolkit download failed; cannot continue.");
                    return 2;
                }
            }
            // Make the downloaded apps visible to resolution: add the artifact-cache dir as
            // an additional search root rather than copying its contents into the project.
            if (!packageCacheDirs.Contains(testAppsOut))
                packageCacheDirs.Add(testAppsOut);
            // Re-check: never silently continue on a partial/failed provision.
            toolkitPresent = AlRunner.Infrastructure.ProvisioningCheck.TestToolkitPresent(PlatformCheckDirs());
            if (!toolkitPresent)
            {
                Console.Error.WriteLine("[provision] test-toolkit apps still missing after download.");
                return 2;
            }
        }
    }
}

// #2107: packageCacheDirs is now complete — every fold-in above (extraProvisionSearchDirs,
// then platformAppsOut/testAppsOut inside the provisioning block just closed) has already
// run, whichever branch of `if (!provisionSubcommand)` was taken. This is the number
// dependency resolution (PlatformCheckDirs, DependencyResolver's resolverDirs) actually
// searches — the "(requested)" line above is scoped to before these folds by its label.
AlRunner.Infrastructure.PhaseLog.SetPackageCacheDirs(packageCacheDirs.Count);
Console.WriteLine($"  package caches (final search set): {packageCacheDirs.Count} dir(s)");
// --verbose: name the directories themselves, not just the count. The count alone was
// exactly what made #2067 hard to read — "0" on a machine that went on to search several
// dirs — so the natural companion to the --verbose "[dep] Publisher/Name" line below (which
// names which package WON each dependency slot) is naming what got SEARCHED to produce that
// winner in the first place.
if (AlRunner.Log.Verbose)
    foreach (var d in packageCacheDirs)
        Console.WriteLine($"    [pkg-cache] {d}");

// One-time runtime setup. Must happen BEFORE any BC type is touched.
// Install the assembly Resolving handler FIRST so patch reflection or generic
// instantiation in BC code can resolve transitively-referenced service-tier DLLs
// (Microsoft.Dynamics.Nav.Core, .AL.Common, .Apps, .TableProxyBuilder, etc. — 19
// of the 24 BC DLLs Ncl.dll references aren't project-referenced).
DependencyLoader.EnsureResolverInstalled_Public();
if (extraPreprocessorSymbols.Count > 0)
    BcCompiler.SetExtraPreprocessorSymbols(extraPreprocessorSymbols.Distinct().ToList());
BcCompiler.SetTddMode(tddMode);
if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_FCE") is "1" or "2")
{
    var fceFull = Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_FCE") == "2";
    AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
    {
        var ex = e.Exception;
        var n = ex.GetType().Name;
        // Every Nav* type, not a hand-picked list of families. The list used to name only
        // NavNCL* / *Report* / NullReference / InvalidOperation, which silently hid whole
        // exception families — NavTestFieldException, NavControlException, NavCSide* — and
        // those are exactly the ones BC swallows internally (Report.SaveAs catches
        // NavBaseException and returns false). A trace that cannot see the exception the
        // caller is trying to explain is worse than no trace: it reads as "nothing threw".
        if (n.StartsWith("Nav") || n.Contains("Report") || n.Contains("NullReference") || n.Contains("InvalidOperation"))
        {
            var st = ex.StackTrace ?? "";
            if (fceFull)
            {
                var frames = st.Split('\n').Where(l => l.Contains("Nav.")).Take(8);
                Console.Error.WriteLine($"[FCE] {ex.GetType().FullName}: {ex.Message}\n{string.Join("\n", frames)}");
            }
            else
            {
                var frame = st.Split('\n').FirstOrDefault(l => l.Contains("Nav.Runtime") || l.Contains("NavReport") || l.Contains("Report")) ?? st.Split('\n').FirstOrDefault() ?? "";
                Console.Error.WriteLine($"[FCE] {ex.GetType().FullName}: {ex.Message} @ {frame.Trim()}");
            }
        }
    };
}
var t0 = System.Diagnostics.Stopwatch.StartNew();
BcRuntime.EnsureApplied();
Console.WriteLine($"BC runtime patches applied ({t0.ElapsedMilliseconds}ms)");
AlRunner.Infrastructure.PhaseLog.SetPatchesMs(t0.ElapsedMilliseconds);
AlRunner.PerfTrace.Log($"BcRuntime.EnsureApplied {t0.ElapsedMilliseconds}ms");

var emitter = new BcCompiler();
var assembler = new BcAssembler();
var executor = new TestExecutor { Isolation = isolation, TestFilter = testFilter, TimeoutSeconds = testTimeoutSeconds, Expectations = expectations };
var depLoader = new DependencyLoader(emitter, assembler);
var results = new List<BucketResult>();
// --tdd (issue #2001) acceptance criterion 8: every member generated across the WHOLE run
// (every bundle's Emit call), printed as one list at the end — see the print site below.
var allTddGeneratedMembers = new List<TddGeneratedMember>();

// #1905 (defect 4): the reason a --watch cycle fell back to a full rebuild (instead
// of the proportional-cost incremental path), one entry per module that fell back
// THIS cycle. Declared once, outside the loop, and cleared+repopulated at the top
// of each cycle — same pattern as `results` above — so the local functions below
// (closures declared once, before the loop) always see the current cycle's data
// when they render. Left empty on a proportional cycle, so its emptiness alone is
// "nothing to explain" for both the dashboard banner and the plain-line fallback.
var watchFullRebuildReasons = new List<(string Module, string Reason)>();
// The very first --watch cycle for a bundle ALWAYS falls back (there is no baseline
// yet to diff against) — that is not a fallback in any meaningful sense, just the
// starting state, so it is excluded from watchFullRebuildReasons below rather than
// trained into looking like every other startup's alarm. See the incremental-path
// call site for the full reasoning.
int watchCycleIndex = 0;

// ── Layered source build pre-pass ─────────────────────────────────────────
// When multiple bundles are passed and one depends on another (by AppId or
// Name+Publisher), emit each "impl" bundle (one that another depends on) as
// a real in-process .app and place it in a fresh per-run workspace cache dir.
// This lets the dependent bundle's DependencyResolver find the impl .app
// exactly like any other package-cache .app.
// Inert when only one bundle is passed or no inter-bundle dep edges exist.
// Synthetic-workspace dirs created by the pre-passes below. These hold
// source-only .app packages (NO SymbolReference.json) plus their *.symbols.json
// sidecars. They MUST feed the runtime resolver (DependencyLoader extracts the
// .app's src and compiles real dep code from it) but MUST NOT feed BC's
// compile-time .app scanner (CreateReferenceLoader): a synthetic .app with no
// SymbolReference.json makes that scanner throw AL1023 "package not valid" —
// observed for RS, where a real symbol-only Customizations.app with the same
// AppId also sits in .alpackages. So we register them via SetExtraSymbolDirs
// (symbols.json-only scan) instead of _packageCacheDirs. See BcCompiler
// GetSharedReferences for the _extraSymbolDirs contract.
var layeredWorkspaceDirs = new List<string>();
// #1898: RunLayeredPrePass/BuildSiblingSourceDeps run BEFORE a single object of ANY
// bundle compiles or a single test runs — a genuine dependency-compile failure inside
// either (e.g. an impl app whose app.json really omits a manifest property its AL
// needs, so AL0543 legitimately fires) throws InvalidOperationException, and this call
// site sat outside every try/catch in Main. That let the exception reach the CLR's
// default unhandled-exception handler, which prints a raw .NET stack trace and aborts
// the process with SIGABRT (exit 134) — no al-runner-formatted diagnostic, no
// documented exit code, and EVERY bundle in the invocation lost, not just the one
// whose dependency is broken. Catch here and report it the same way every other
// compile-time failure in this file does: a "<layered-deps>: COMPILE-FAIL" line on
// stderr and the documented exit code 3 (docs/server-mode.md's "3 compilation error"
// ladder — same code EMIT-ZERO/COMPILE-FAIL already return elsewhere in Main).
//
// #2095: a MissingDependencyException / DependencyVersionMismatchException reaching
// either catch below is NOT a compile failure — it is a provisioning/version gap
// discovered while resolving THIS pre-pass's OWN dependency closure (e.g. a sibling
// source app's declared dep that no cache dir has, or has only too-old builds of).
// Folding it into the generic "COMPILE-FAIL — {ex.Message}" line prints the short
// one-liner (ex.Message) instead of the detailed, actionable ToDetailedMessage() the
// exception already carries, and mislabels a missing/too-old package as "your AL code
// did not compile". Special-case both (via the shared IDependencyProvisioningDiagnostic
// marker) ahead of the generic path; every other exception keeps today's COMPILE-FAIL /
// exit 3 behavior unchanged. Exit code 2 ("execution error" in docs/server-mode.md's
// ladder) matches the ProvisioningCheck gap report a few hundred lines up (Program.cs,
// the "Completeness gate" block) — same shape (bare ToDetailedMessage, no compile even
// attempted yet) and the same exit code.
if (bundles.Count > 1)
{
    try
    {
        packageCacheDirs = RunLayeredPrePass(bundles, packageCacheDirs, layeredWorkspaceDirs);
    }
    catch (Exception ex) when (ex is AlRunner.Infrastructure.IDependencyProvisioningDiagnostic diag)
    {
        var bcVer = AlRunner.Infrastructure.BcArtifacts.SelectedVersion.ToString();
        Console.Error.WriteLine();
        Console.Error.WriteLine(diag.ToDetailedMessage(bcVer));
        Console.Error.WriteLine();
        return 2;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"<layered-deps>: COMPILE-FAIL — {ex.Message}");
        return 3;
    }
}
// Discover + compile sibling source-only dependency apps. Some apps declare a
// dependency that ships ONLY as AL source in a sibling directory (not a compiled
// .app in any cache) — e.g. the corpus internalsVisibleTo fixture next to the
// main test app. Inert when no declared dep matches a sibling source app.
// Same unhandled-exception exposure as RunLayeredPrePass above (#1898) — same fix.
// Same #2095 provisioning/version-gap special-case as RunLayeredPrePass above.
try
{
    packageCacheDirs = BuildSiblingSourceDeps(bundles, packageCacheDirs, layeredWorkspaceDirs);
}
catch (Exception ex) when (ex is AlRunner.Infrastructure.IDependencyProvisioningDiagnostic diag)
{
    var bcVer = AlRunner.Infrastructure.BcArtifacts.SelectedVersion.ToString();
    Console.Error.WriteLine();
    Console.Error.WriteLine(diag.ToDetailedMessage(bcVer));
    Console.Error.WriteLine();
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"<sibling-source-deps>: COMPILE-FAIL — {ex.Message}");
    return 3;
}

// Dirs the COMPILE-time .app scanner may safely enumerate: everything except the
// synthetic workspace dirs (whose source-only .apps would trip AL1023).
var compilerPackageDirs = packageCacheDirs
    .Where(d => !layeredWorkspaceDirs.Contains(d, StringComparer.OrdinalIgnoreCase))
    .ToList();

// ── --server: stay resident. Warm state (BC patches + the dep symbol loader) is
// now established; each request re-emits the requested bundle (warm) and runs it
// in-process, resetting bundle-derived caches between requests so an edited
// same-identity bundle is picked up. Never returns to the bundle loop below.
if (serverMode)
    return RunServerLoop(serverStdin!, serverStdout!);

// ── --dap: start a Debug Adapter Protocol session and stay resident until the
// client disconnects or the debuggee run finishes (issue #1642). Never returns to
// the bundle loop below — same "stay resident" shape as --server above, minus the
// warm-reload contract (a debug session runs the bundle exactly once).
if (dapMode)
{
    if (bundles.Count != 1)
    {
        Console.Error.WriteLine(
            $"--dap currently supports exactly one bundle path (got {bundles.Count}) — " +
            "multi-bundle debugging is tracked as follow-up work, see issue #1642's PR.");
        return 2;
    }
    return RunDapLoop(bundles[0], dapPort, dapStdioMode, dapStdioInput, dapStdioOutput);
}

// Watch loop: normal mode runs exactly one pass then breaks to the summary below.
// Watch mode loops forever — each pass re-emits (warm) and re-runs in-process.
//
// On an interactive TTY the watch loop renders a live, in-place Spectre.Console
// dashboard (WatchDashboard) that repaints each cycle. On a non-interactive stdout
// (CI, a pipe, VS Code, the WatchTests harness) it MUST fall back to the plain
// line output (Reporter.PrintPerTest/PrintSummary + the "[watch] waiting…" marker)
// so existing consumers and the integration test keep working — never emit ANSI to
// a redirected stream. Detect via Console.IsOutputRedirected AND Spectre's own
// interactivity probe (which also returns false for dumb/no-color terminals).
bool watchUi = watchMode
    && !Console.IsOutputRedirected
    && Spectre.Console.AnsiConsole.Profile.Capabilities.Interactive
    && Spectre.Console.AnsiConsole.Profile.Capabilities.Ansi;
string watchBundleName = bundles.Count == 1
    ? Path.GetFileName(Path.GetFullPath(bundles[0]).TrimEnd(Path.DirectorySeparatorChar))
    : $"{bundles.Count} bundles";

// Scroll offset (in lines from the top) for the idle dashboard viewport. The
// rendered dashboard frequently exceeds the terminal height (long failure stacks),
// so the idle branch paints only the window that fits and the user scrolls with
// the arrow/page/home/end keys. Reset to 0 on each fresh cycle paint.
int watchScroll = 0;

// Render the dashboard to a flat list of (already-ANSI-markup) lines at the current
// console width, so the idle branch can window it into the visible viewport.
List<string> RenderDashboardLines(WatchStatus status, DateTime ts, TimeSpan dur)
{
    int width = Math.Max(40, Console.WindowWidth);
    var sw = new StringWriter();
    var rec = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
    {
        Ansi = Spectre.Console.AnsiSupport.Yes,
        ColorSystem = Spectre.Console.ColorSystemSupport.TrueColor,
        Out = new Spectre.Console.AnsiConsoleOutput(sw),
    });
    rec.Profile.Width = width;
    // #1905 (defect 4): watchFullRebuildReasons reflects the cycle that JUST finished
    // (populated during this iteration's bundle loop, cleared at the top of the NEXT
    // one) — exactly what an idle/post-cycle repaint should explain. PaintWatchRunning
    // below deliberately does NOT pass it: while "⟳ running…" is showing, the reason
    // in this list (if any) is stale — it belongs to the PREVIOUS cycle, and the cycle
    // in flight hasn't decided whether it needs a full rebuild yet.
    rec.Write(WatchDashboard.Build(results, watchBundleName, status, ts, dur, watchFullRebuildReasons));
    return sw.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
}

// Console.KeyAvailable can still throw on some terminals even when stdin isn't
// flagged redirected; treat any failure as "no key" so the watch loop never crashes.
static bool SafeKeyAvailable()
{
    try { return Console.KeyAvailable; }
    catch { return false; }
}

// Paint a window of pre-rendered lines starting at `offset`, clamped to the screen.
// Returns the clamped offset actually used (so the caller's scroll state stays valid).
int PaintWatchViewport(List<string> lines, int offset)
{
    int height = Math.Max(5, Console.WindowHeight);
    // Last line is a sticky footer hint; reserve one row for it.
    int viewport = Math.Max(1, height - 1);
    int maxOffset = Math.Max(0, lines.Count - viewport);
    if (offset > maxOffset) offset = maxOffset;
    if (offset < 0) offset = 0;

    Spectre.Console.AnsiConsole.Clear();
    var window = lines.Skip(offset).Take(viewport);
    foreach (var l in window)
        Console.Out.WriteLine(l);

    bool more = lines.Count > viewport;
    var hint = more
        ? $"[grey]↑↓ scroll · PgUp/PgDn · Home/End · q quit   ({offset + 1}-{Math.Min(offset + viewport, lines.Count)}/{lines.Count})[/]"
        : "[grey]↑↓ scroll · q quit[/]";
    Spectre.Console.AnsiConsole.Markup(hint);
    return offset;
}

// Paint the busy "running…" frame so the cold first cycle (~70-90s) doesn't look
// frozen. No-op unless the interactive dashboard is active.
void PaintWatchRunning()
{
    if (!watchUi) return;
    watchScroll = 0;
    Spectre.Console.AnsiConsole.Clear();
    Spectre.Console.AnsiConsole.Write(
        WatchDashboard.Build(results, watchBundleName, WatchStatus.Running,
            DateTime.Now, TimeSpan.Zero));
}

if (watchUi) PaintWatchRunning();

while (true)
{
// A watch rerun is a new execution even though it reuses the process. NumberSequence
// values deliberately survive bundle and test boundaries within this cycle.
AlRunner.Patches.NumberSequencePatches.ResetForNewExecution();
results.Clear();
watchFullRebuildReasons.Clear();
// Clean loading (#5): the interactive dashboard owns the whole screen, but the
// run-cycle body emits diagnostic Console.WriteLine noise ("[bundle] resolved N
// dep(s)", "loaded N assembl(ies)", "[i/N] … suites", …) that would scroll over
// the painted "⟳ running…" frame. Silence stdout for the duration of the cycle
// body when the dashboard is active (AnsiConsole binds its own writer at startup,
// so the spinner repaint is unaffected). Under --verbose, keep the logs. Restored
// right after the bundle loop, before any dashboard repaint that uses Console.Out.
var savedOut = Console.Out;
var savedErr = Console.Error;
bool stdoutSilenced = false;
if (watchUi && !AlRunner.Log.Verbose)
{
    // Silence BOTH streams: the diagnostic noise is split across stdout
    // (dep-resolve / suite-count lines) and stderr ([cache] MISS/WROTE lines),
    // and either would scroll over the painted frame. Per-bundle compile/exec
    // failures are still surfaced — they're collected into bundleErrors and
    // rendered as COMPILE/EXEC FAILED nodes in the dashboard tree. A truly fatal
    // dep-load aborts with return 1 (process exit), so nothing important is hidden.
    Console.SetOut(TextWriter.Null);
    Console.SetError(TextWriter.Null);
    stdoutSilenced = true;
}
int i2 = 0;
foreach (var bundle in bundles)
{
    i2++;
    var bundleAbs = Path.GetFullPath(bundle);
    var rel = Path.GetRelativePath(Environment.CurrentDirectory, bundleAbs);
    AlRunner.Infrastructure.PhaseLog.BeginBundle(rel, i2);

    // Watch mode re-runs the SAME process across edits, so drop the previous
    // iteration's bundle-derived caches (record/codeunit types, parsed schemas,
    // in-memory rows, enum registry) before re-resolving + re-emitting. The
    // expensive dependency symbol loader is keyed on the dep set (not the bundle
    // source), so it stays warm — that is what makes a watch re-run fast. No-op
    // on the first iteration (caches already empty). Normal one-shot mode never
    // calls this, so its behaviour is unchanged.
    if (watchMode)
        BcRuntime.ResetForNewBundleReload();

    // Forget the previous bundle's install-trigger registrations so a bundle
    // without deps doesn't inherit a sibling bundle's Install codeunits.
    AlRunner.InstallTriggerRunner.ResetForNewBundle();

    // ── per-bucket dep resolution ──────────────────────────────────────────
    // Hoisted out of the try block below so EmitSiblingSymbols (called later, once
    // per bundle) can pass this bundle's resolved Microsoft-platform closure into
    // each in-bundle sibling's *.symbols.deps.json sidecar — see #1686 follow-up:
    // without it, a sibling app that extends a PLATFORM table (not one of its own)
    // gets an empty dependency sidecar, and BC's ReferenceManager cannot attach its
    // tableextension to the platform table because the declaring module has no
    // recorded path to the module that owns the base table.
    IReadOnlyList<(AlRunner.AppManifest Manifest, string AppPath)> bundleResolvedDeps =
        Array.Empty<(AlRunner.AppManifest, string)>();
    var bucketRoot = FindBucketRoot(bundleAbs);
    // The dependency closure comes from the bucket root's app.json when it has one, and
    // otherwise from the union of the child apps' manifests — see CollectBundleManifests.
    // Stage-timed from here on: everything between BeginBundle and the app loop is the
    // block #1828 is attributing. See PhaseLog.Stage for the no-nesting/no-overlap rules.
    List<string> bundleManifests;
    using (AlRunner.Infrastructure.PhaseLog.Stage("bundle-manifests"))
        bundleManifests = CollectBundleManifests(bucketRoot, bundleAbs);
    // Everything below resolves package dirs and loads deps relative to a directory; when
    // the bundle is a parent of many apps there is no bucket root, so the bundle dir is it.
    var depRootDir = bucketRoot ?? bundleAbs;
    {
        var appJsonPath = Path.Combine(depRootDir, "app.json");
        if (bundleManifests.Count > 0)
        {
            try
            {
                List<DependencyRef> roots;
                using (AlRunner.Infrastructure.PhaseLog.Stage("dep-roots"))
                    roots = ReadBundleDependencyRoots(bundleManifests);
                // Include the bundle's own .alpackages in the resolver search dirs. They
                // carry the committed Microsoft platform symbol closure (Base Application /
                // System Application / …) as real .app files. On CI, packageCacheDirs is
                // empty (artifacts live elsewhere), so a Base App table that the app (or a
                // tableextension it ships) references is ONLY resolvable from here. Resolving
                // it produces the COMPILE spec; LoadAll skips Microsoft platform apps so their
                // runtime still comes from the service-tier DLLs, not a .app source-compile.
                List<string> bundlePkgDirs;
                using (AlRunner.Infrastructure.PhaseLog.Stage("alpackages-scan"))
                    bundlePkgDirs = Directory
                        .EnumerateDirectories(depRootDir, ".alpackages", SearchOption.AllDirectories)
                        .ToList();
                var resolverDirs = bundlePkgDirs.Concat(packageCacheDirs).Distinct().ToList();
                var resolver = new DependencyResolver(resolverDirs);
                IReadOnlyList<(AlRunner.AppManifest Manifest, string AppPath)> ordered;
                using (AlRunner.Infrastructure.PhaseLog.Stage("dep-resolve"))
                    ordered = resolver.Resolve(roots);
                bundleResolvedDeps = ordered;
                Console.WriteLine($"  [{rel}] resolved {ordered.Count} dep(s)");
                AlRunner.Infrastructure.PhaseLog.NoteDepsResolved(ordered.Count);
                // Under --verbose, name the package that actually WON for each
                // dependency, with the file it came from. Resolution picks by highest
                // version across every scanned dir, so a symbols-only .app can outrank
                // the code-bearing copy of a *different* package in the same family and
                // the run then dies at execution with "object with ID 0". A count alone
                // cannot show that; the winning path can. See --guide (DEPENDENCIES).
                if (AlRunner.Log.Verbose)
                    foreach (var (m, appPath) in ordered)
                        Console.WriteLine($"    [dep] {m.Publisher}/{m.Name} {m.Version}  <- {appPath}");
                // Verbose-only, deliberately. MEASURED 2026-07-29: on the known-good
                // Pageworks configuration this fires for 7 MS test-toolkit packages
                // (Library Assert, Test Runner, Any, …) whose symbols-only 28.2 copies in
                // the test bundle's .alpackages outrank the code-bearing 28.1 ones — and
                // that run scores 1041P/35F/0E. So "symbols-only won" is NOT on its own
                // evidence of a broken set, and promoting it to an always-on warning would
                // put 12 lines of noise on every healthy run.
                //
                // It is still the right thing to look at when execution dies with an
                // object-ID-0 MissingMethod, which is why it is retained and printed under
                // --verbose.
                //
                // The open question this used to carry — why does the healthy run tolerate a
                // symbols-only winner? — is answered (#1689): because "symbols-only" here means
                // "no publishedartifacts DLL", and Microsoft's test toolkit ships no DLL but DOES
                // ship src/*.al, so the loader's Tier-3 source compile implements it. Verified
                // against the real 28.1.49838.53479 artifact: `Microsoft_Library Assert.app` is
                // 22 KB, IsR2R=false, one src/*.al. That is exactly the 7 packages measured above.
                //
                // So this list stays evidence rather than a verdict, and the verdict moved to
                // UnservableDependencies below, which applies the discriminator that actually
                // separates the two: neither a DLL nor AL source.
                if (AlRunner.Log.Verbose)
                    foreach (var d in resolver.Diagnostics)
                        Console.Error.WriteLine(d);
                // Always-on, unlike the above: a dependency no loader tier can implement is a
                // certain object-ID-0 failure later, and #1689 is precisely the report that
                // nothing named it. One line per app, and only for a shape that cannot work.
                foreach (var u in resolver.UnservableDependencies)
                    Console.Error.WriteLine(u);
                // Compiler sees only non-workspace dirs in its .app scanner; the
                // synthetic workspace dirs are registered as symbols.json-only
                // sources via SetExtraSymbolDirs (called AFTER SetResolvedDeps,
                // which resets _extraSymbolDirs). Runtime resolution above used the
                // full packageCacheDirs (incl. workspace) so dep code still loads.
                // Include the bundle .alpackages (real .apps w/ SymbolReference.json — safe
                // for the .app scanner) so the loader can resolve the Microsoft platform
                // specs (Base App etc.) on CI, where compilerPackageDirs is otherwise empty.
                var compilerDirs = bundlePkgDirs.Concat(compilerPackageDirs).Distinct().ToList();
                using (AlRunner.Infrastructure.PhaseLog.Stage("dep-symbols"))
                {
                    BcCompiler.SetResolvedDeps(ordered, compilerDirs);
                    if (layeredWorkspaceDirs.Count > 0)
                        BcCompiler.SetExtraSymbolDirs(layeredWorkspaceDirs);
                }
                // Not stage-timed as one block: LoadAll times each dependency separately
                // as `dep-load:<Name>` (see DependencyLoader.LoadAll). Wrapping it here too
                // would nest, and nested stages double-count — see PhaseLog.Stage.
                var loaded = depLoader.LoadAll(ordered, depRootDir);
                Console.WriteLine($"  [{rel}] loaded {loaded.Count} dep assembl(ies)");
                AlRunner.Infrastructure.PhaseLog.NoteDepAssembliesLoaded(loaded.Count);
                // Register dep assemblies (dependency order) so their Subtype=Install
                // codeunit lifecycle triggers fire before this bundle's tests run.
                using (AlRunner.Infrastructure.PhaseLog.Stage("dep-register"))
                {
                    AlRunner.InstallTriggerRunner.SetDependencyAssemblies(loaded);
                    // Source-only dependency loading compiles those dependencies through
                    // BcCompiler too, which updates the process-wide reference state. Restore
                    // this bundle's dependency symbols before emitting the bundle itself.
                    BcCompiler.SetResolvedDeps(ordered, compilerDirs);
                    if (layeredWorkspaceDirs.Count > 0)
                        BcCompiler.SetExtraSymbolDirs(layeredWorkspaceDirs);
                    // Register dep .app paths with RecordPatches so the NCLMetaTable
                    // populator can fall back to the AL source shipped inside the .app
                    // (NAVX zip) for tables defined in compiled BC dependencies — the
                    // case spike-a-baseapp's Currency-init scenario depends on.
                    foreach (var (_, appPath) in ordered)
                        AlRunner.Patches.RecordPatches.AddBcAppPath(appPath);
                    // Register any prebuilt bundle-root .app (with SymbolReference.json) so the
                    // generic NCLMetaQuery builder can read this bundle's own query column ids.
                    AlRunner.Patches.RecordPatches.RegisterBundleSymbolApps(depRootDir);
                    // Populate BcRuntime with this bundle's identity for the
                    // NavApp.GetCurrentModuleInfo polyfill shim. A parent-of-many-apps bundle
                    // has no identity of its own; each AppGroup sets its own below.
                    if (File.Exists(appJsonPath)) SetBundleInfoFromAppJson(appJsonPath);
                    // Compile this bundle under its REAL app.json identity so a dependency's
                    // internalsVisibleTo grant (which names this app) matches — otherwise the
                    // synthetic compile identity fails the grant check (AL0161).
                    var bundleId = File.Exists(appJsonPath)
                        ? AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(appJsonPath)
                        : null;
                    if (bundleId != null)
                        BcCompiler.SetCurrentAppIdentity(bundleId.AppId, bundleId.Publisher, bundleId.Version);
                    else
                        BcCompiler.SetCurrentAppIdentity(null, null, null);
                }
            }
            catch (AlRunner.Infrastructure.DependencyLoadException ex)
            {
                // DependencyLoadException already printed a [dep-load-fail] line.
                // Abort immediately with exit 1: running with a broken dependency
                // produces cryptic NavNCLMissingMethodException with object ID 0,
                // which is far harder to diagnose than this immediate loud failure.
                // Restore the real streams first (we may have silenced them for the
                // clean-loading frame) so this fatal reason isn't swallowed.
                if (stdoutSilenced) { Console.SetOut(savedOut); Console.SetError(savedErr); }
                Console.Error.WriteLine(
                    $"FATAL: dependency compile failed — cannot continue. {ex.Message}");
                return 1;
            }
            catch (AlRunner.Infrastructure.MissingDependencyException ex)
            {
                // A declared dependency is completely absent from every package-cache directory.
                // Continuing to compile would produce thousands of misleading AL0185 "X is missing"
                // errors that blame the user's own code. Instead: restore streams, print ONE loud
                // provisioning-gap message naming the dep + fix commands, and abort.
                if (stdoutSilenced) { Console.SetOut(savedOut); Console.SetError(savedErr); }
                var bcVer = AlRunner.Infrastructure.BcArtifacts.SelectedVersion.ToString();
                Console.Error.WriteLine();
                Console.Error.WriteLine(ex.ToDetailedMessage(bcVer));
                Console.Error.WriteLine();
                return 1;
            }
            catch (AlRunner.Infrastructure.AppIdCollisionException ex)
            {
                // Two different apps declare the same app.json id (#1850), discovered while
                // resolving THIS bundle's dependencies. Must abort, not just log: the generic
                // catch below only prints and continues, which would leave the run reporting
                // "green" with a dependency silently missing — exactly the bug this exception
                // exists to prevent. See loud-failures.md.
                if (stdoutSilenced) { Console.SetOut(savedOut); Console.SetError(savedErr); }
                Console.Error.WriteLine();
                Console.Error.WriteLine($"FATAL: {ex.Message}");
                Console.Error.WriteLine();
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [{rel}] DEP-RESOLVE-FAIL: {ex.Message}");
            }
        }
        else
        {
            Console.Error.WriteLine($"  [{rel}] WARN: no app.json under {depRootDir} — skipping dep loading");
        }
    }

    List<string> suites;
    using (AlRunner.Infrastructure.PhaseLog.Stage("enumerate-suites"))
        suites = EnumerateSuites(bundleAbs).ToList();
    if (suites.Count == 0) { Console.WriteLine($"[{i2}/{bundles.Count}] {rel} ... SKIP (no suites)"); continue; }
    Console.WriteLine($"[{i2}/{bundles.Count}] {rel} — {suites.Count} suites");

    // Pre-register every src dir for RecordPatches at the bundle level. Batched via
    // AddSourceDirs (#1833) so the NCLMetadata cache pass runs ONCE for the whole suite
    // set instead of once per suite — AddSourceDir's per-call populate is O(total ids
    // known so far), so calling it once per suite in this loop was O(N) calls each doing
    // O(total) work: quadratic in suite count (measured 16.33s on the 38-suite
    // tests/runner-extras bundle).
    using (AlRunner.Infrastructure.PhaseLog.Stage("register-source-dirs"))
    {
        var dirsToRegister = new List<string>();
        foreach (var suite in suites)
        {
            var s = Path.Combine(suite, "src");
            if (Directory.Exists(s))
                dirsToRegister.Add(s);
            else if (!Directory.Exists(Path.Combine(suite, "test")))
                // Flat bundle: register the suite root so table parsers can find .al files.
                dirsToRegister.Add(suite);
        }
        AlRunner.Patches.RecordPatches.AddSourceDirs(dirsToRegister);
    }

    var bundleEmit = TimeSpan.Zero;
    var bundleComp = TimeSpan.Zero;
    var bundleRun = TimeSpan.Zero;
    var bundleTests = new List<TestResult>();
    var bundleErrors = new List<string>();
    var bundleStage = BucketStage.Ran;
    int sP = 0, sF = 0, sE = 0;
    // --tdd (orchestrator review on #2005): "ObjectDisplayName.MethodName" -> every member
    // that test's compile depended on --tdd generating. Populated below wherever this
    // bundle's emitOutput.TddGeneratedMembers is collected; consumed by
    // OverrideTddDependentResults right before either execution loop's real TestResult set
    // is counted/added, so a test that only ran against scaffolding can never report pass —
    // see TddGeneratedMember.DependentTests' doc comment for why a generated field is a fully
    // functional fake, not a default return, and must be treated as strictly WORSE.
    var bundleTddDependents = new Dictionary<string, List<TddGeneratedMember>>();
    // --tdd (orchestrator review on #2005): forces every TestResult whose compile depended on
    // a --tdd-generated member to report FAIL, regardless of what actually happened when it
    // ran. The test still executes in full — "keep running the test... only the reported
    // outcome changes" — a generated PROCEDURE stub already fails on its own (it raises
    // Error()), but a generated FIELD or enum value has nothing to fail on: it is real,
    // functioning storage, so a test that only writes and reads it back legitimately passes,
    // and a green result there would be the exact lie loud-failures.md's first paragraph
    // describes — worse than a default return, because it's a fully working fake. Message is
    // rewritten uniformly for BOTH cases (not just the field/enum one) so the failure always
    // names the generated member(s) and their inferred type(s) explicitly, per the review.
    List<TestResult> OverrideTddDependentResults(IReadOnlyList<TestResult> raw)
    {
        if (bundleTddDependents.Count == 0) return raw as List<TestResult> ?? raw.ToList();
        var overridden = new List<TestResult>(raw.Count);
        foreach (var t in raw)
        {
            var label = string.IsNullOrEmpty(t.CodeunitDisplayName) ? t.Codeunit : t.CodeunitDisplayName!;
            if (bundleTddDependents.TryGetValue($"{label}.{t.Method}", out var deps) && deps.Count > 0)
            {
                var depList = string.Join("; ", deps.Select(d => $"{d.ObjectDisplayName}: {d.MemberKind} {d.Signature}"));
                var msg = $"--tdd: this test depends on {deps.Count} generated member(s) the " +
                    $"implementing app has not defined yet: {depList}";
                if (!string.IsNullOrEmpty(t.Message)) msg += $" (underlying result: {t.Message})";
                overridden.Add(t with { Outcome = TestOutcome.Fail, Message = msg });
            }
            else
            {
                overridden.Add(t);
            }
        }
        return overridden;
    }
    // #1880: counts app groups (bundled mode) / suites (--per-suite) that actually
    // reached test execution and contributed to bundleTests — incremented at the
    // SAME point as bundleTests.AddRange below, in both loops, so a group that threw
    // before that point (compile/exec fail, `continue`d away) is correctly NOT counted.
    int ranGroupCount = 0;

    if (bundledMode)
    {
        // ── Bundled mode (default): ONE process, ONE runtime init, ONE test run
        // across all suites — but ONE EMITTED MODULE PER app.json.
        //
        // This used to be one Emit + one Compile over every suite's .al files
        // merged together. That is 5-7× faster than isolating each suite in its
        // own process (measured 23s vs 180s over 68 suites), and the speed is why
        // it stays the default — but merging also collapsed every app into a
        // single synthetic identity, so any suite asserting its OWN identity saw
        // the wrong one. Emitting per app.json keeps the single-process speed and
        // restores per-app identity, resources and install-trigger seeding.
        //
        // Suites whose AL hits BC emit bugs or bundled-only strictness checks are
        // quarantined via a tests/expectations/ known-gaps-<area>.json entry.
        List<AlRunner.AppGroup> appGroups;
        using (AlRunner.Infrastructure.PhaseLog.Stage("build-app-groups"))
            appGroups = BuildAppGroups(suites, bucketRoot, bundleAbs);

        // ── in-bundle sibling symbols ──────────────────────────────────────
        // BuildAppGroups orders an app after every sibling it depends on, but ordering
        // alone does not make the sibling VISIBLE: references come from the resolved dep
        // set, and a sibling app has no .app in any package cache. So `*-main` compiled
        // without `*-dep` and hit AL0185 ("Codeunit 'XMI Dep Api' is missing"), which the
        // emit-retry treats as a broken object — the whole test codeunit was dropped and
        // its tests silently vanished from the run.
        //
        // Emit a *.symbols.json for each app some OTHER app in this bundle depends on, in
        // topological order (a dep-of-a-dep is written before the app that needs it), and
        // chain them into the compiler. Only sibling-dependency TARGETS are compiled here —
        // this is an extra compile per app, and most bundles (the corpus: one app) have none.
        using (AlRunner.Infrastructure.PhaseLog.Stage("sibling-symbols"))
            EmitSiblingSymbols(appGroups, bundleAbs, bundleResolvedDeps);

        var loadedAssemblies = new List<Assembly>();
        // SetTestAssembly re-runs its full body (incl. NavAppResourcePatches.RegisterTestAssembly)
        // on every call whose asm differs from whatever _currentTestAssembly currently holds —
        // which is true for EVERY app the first time the run loop below reaches it, since
        // _currentTestAssembly still holds the LAST app loaded. Without re-pointing
        // SetCurrentBundleDir at that call too, the run loop overwrites every app's resource
        // dir with whichever suite happened to load last. Track it per assembly so both call
        // sites (load loop, run loop) can set the right one immediately before calling
        // SetTestAssembly.
        var suiteDirByAssembly = new Dictionary<Assembly, string>();

        // Ordered dep ids feed every app's cache key but depend only on the bucket root
        // and the package caches — both loop-invariant. Resolving them inside the loop
        // re-scanned the package caches once per app.
        IReadOnlyList<string> orderedDepIds;
        using (AlRunner.Infrastructure.PhaseLog.Stage("ordered-dep-ids"))
            orderedDepIds = GetOrderedDepIds(bucketRoot, packageCacheDirs, bundleAbs);

        int agIdx = 0;
        foreach (var appGroup in appGroups)
        {
        var allPaths = appGroup.Paths;
        var moduleName = appGroup.ModuleName;
        // The app group — one emitted module — is the finest unit of compile+run work
        // and the one #1825 needs counted: CI passes `tests/runner-extras` as a SINGLE
        // bundle holding 38 of these, so a per-bundle row alone would collapse that
        // whole step to one data point. Auto-closes the previous group, so the many
        // `continue` paths below cannot leak a row.
        AlRunner.Infrastructure.PhaseLog.BeginApp(moduleName, ++agIdx, appGroups.Count);

        // Compile THIS app under its own app.json identity, overriding the
        // bundle-level identity set before the suite loop. This is what makes
        // NavApp.GetCurrentModuleInfo, NavApp.GetResource and install-trigger
        // seeding resolve per app instead of per bundle.
        BcCompiler.SetCurrentAppIdentity(appGroup.AppId, appGroup.Publisher, appGroup.Version);

        // ── cross-bundle module identity dedup (issue #1683) ────────────────
        // If this app's identity (AppId) was already compiled and loaded earlier in
        // THIS process — either as an earlier bundle's own AppGroup (this same code
        // path) or as an earlier bundle's resolved dependency (DependencyLoader) —
        // reuse that exact Assembly/Type set instead of emitting+compiling a second,
        // distinct module for the same AL app. Two live modules for one AL identity
        // is what produced the TargetException in #1683: EventSubscriberPatches'
        // registry paired a subscriber MethodInfo discovered from one module's Type
        // with a subscriberInstance BC's dispatcher materialized from the OTHER
        // module's Type at CallEventSubscriberInternalAsync → ValidateInvokeTarget.
        // One AL app identity must resolve to exactly one loaded compilation.
        //
        // Disabled under --watch: watch mode re-runs this SAME per-AppGroup loop on every
        // edit cycle for the SAME bundle set, and its whole point is to pick up the edited
        // source on each iteration. Reusing "the module already loaded for this AppId"
        // there would mean iteration 2 replays iteration 1's stale pre-edit assembly
        // forever — ResetForNewBundleReload() does not (and must not, for the unrelated
        // deps-stay-warm reason documented there) clear DependencyLoader's cross-bundle
        // cache, so this dedup stays scoped to genuinely distinct bundle args in one
        // one-shot invocation, never a same-bundle reload.
        Assembly? reusedAsm;
        try
        {
            // Publisher/Version are non-null whenever AppId is: BuildAppGroups only ever
            // constructs an AppGroup with all three set together (from InProcessAppPackager.
            // ReadIdentity, which defaults an absent app.json field rather than leaving it
            // null) or all three null (the orphan/no-app.json group, which never reaches
            // here — this whole branch is gated on appGroup.AppId being non-null). The `!`
            // asserts that invariant instead of silently masking a violation of it behind a
            // fallback that would disagree with AppLoader's own default (see IdentityMatches'
            // doc comment) — PR #1862 review.
            reusedAsm = (!watchMode && appGroup.AppId is { } reuseCheckId)
                ? DependencyLoader.TryGetByAppId(
                    reuseCheckId, appGroup.ModuleName, appGroup.Publisher!,
                    appGroup.Version!.ToString(), appGroup.SuiteDir)
                : null;
        }
        catch (AlRunner.Infrastructure.AppIdCollisionException ex)
        {
            // Two different apps declare the same app.json id (#1850) — never silently
            // reuse one app's module for the other's tests. See loud-failures.md.
            if (stdoutSilenced) { Console.SetOut(savedOut); Console.SetError(savedErr); }
            Console.Error.WriteLine();
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            Console.Error.WriteLine();
            return 1;
        }
        bool needCompile = reusedAsm == null;
        if (reusedAsm != null)
            Console.Error.WriteLine(
                $"  [{rel}] {moduleName}: AppId {appGroup.AppId} already loaded earlier in this " +
                "process — reusing that module instead of recompiling (see issue #1683).");

        // ── AL-output cache check (Spike B keystone) ───────────────────────
        // Sidecar `<key>.enum-registry.json` carries the AlEnumMetadataRegistry
        // entries that emit would have populated as a side effect — see
        // BcCompiler.CaptureOutputter.AddApplicationObject. On HIT we must
        // replay them BEFORE Assembly.Load so any test executing
        // `Enum::"X".Names()` / `.Ordinals()` finds the registry populated.
        // Cache HIT requires BOTH files to exist; missing sidecar → MISS.
        byte[]? cachedBytes = null;
        string? cacheKey = null;
        string? cachePath = null;
        string? sidecarPath = null;
        string? querySidecarPath = null;
        // A bundle declaring an AL query also needs its query-symbols sidecar: the
        // MetaQuery design is built from the compilation's SymbolReference, which only
        // emit produces. Serving a HIT without it leaves NCLMetaQuery null and every
        // query Find NREs inside BC's NavQuery.ValidateTablesNotVirtual.
        bool bundleDeclaresQuery = BcCompiler.BundleDeclaresQuery(allPaths);
        if (needCompile && alCacheDir != null)
        {
            cacheKey = ComputeAlCacheKey(allPaths, moduleName, ordered: GetOrderedDepIds(bucketRoot, packageCacheDirs, bundleAbs), appRootDir: appGroup.SuiteDir);
            cachePath = Path.Combine(alCacheDir, cacheKey + ".dll");
            sidecarPath = Path.Combine(alCacheDir, cacheKey + AlRunner.Infrastructure.AlCacheSidecars.EnumRegistrySuffix);
            querySidecarPath = Path.Combine(alCacheDir, cacheKey + AlRunner.Infrastructure.AlCacheSidecars.QuerySymbolsSuffix);
            if (AlRunner.Infrastructure.AlCacheSidecars.IsCompleteEntry(
                    File.Exists(cachePath), File.Exists(sidecarPath),
                    bundleDeclaresQuery, File.Exists(querySidecarPath)))
            {
                try
                {
                    cachedBytes = File.ReadAllBytes(cachePath);
                    // A short read of a file another process is still writing is not an I/O
                    // error — ReadAllBytes happily hands back whatever bytes are on disk.
                    // Validate the PE image explicitly so a torn/truncated entry is rejected
                    // here as a MISS instead of reaching Assembly.Load downstream (issue #1810).
                    AlRunner.Infrastructure.AlCacheSidecars.ValidateCachedAssemblyBytes(cachedBytes, cachePath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [cache] read failed for {cachePath}: {ex.Message}");
                    cachedBytes = null;
                }
            }
            else if (File.Exists(cachePath))
            {
                var missing = !File.Exists(sidecarPath) ? sidecarPath : querySidecarPath;
                Console.Error.WriteLine($"  [cache] DLL present but sidecar missing — treating as MISS ({missing})");
            }
        }

        // ── --print-cache-key short-circuit (issue #1851) ──────────────────
        // cacheKey above was computed by the SAME ComputeAlCacheKey call, with the SAME
        // arguments, a real run reaches for this app group — nothing here recomputes it a
        // second way. Print it and exit before touching Emit+Compile at all, whether this
        // would have been a HIT or a MISS on a real run (irrelevant to the key itself).
        // Only handles the first app group of the first bundle — that is exactly the shape
        // every caller of this flag needs (a single-app bundle probing its own key), and a
        // second app group would need its own process anyway to avoid cross-bundle module
        // dedup skewing its key relative to a real cold run.
        if (printCacheKeyOnly)
        {
            if (cacheKey == null)
            {
                Console.Error.WriteLine(
                    "--print-cache-key found no key to print: either the AL-output cache is " +
                    "disabled (--no-cache) or this app group's module was already loaded " +
                    "earlier in this process (cross-bundle dedup, issue #1683) and so never " +
                    "reached the ComputeAlCacheKey call. Re-run without --no-cache, alone.");
                return 2;
            }
            Console.WriteLine($"  [{rel}] {moduleName}: [cache] KEY key={cacheKey}");
            return 0;
        }

        byte[]? assemblyBytes = null;
        if (needCompile && cachedBytes != null)
        {
            // Replay the enum-registry sidecar BEFORE Assembly.Load. Test
            // execution is what reads the registry (via the
            // NCLEnumMetadata_CreateByIdAlAware hook), so as long as replay
            // completes before executor.Run that's sufficient — but doing it
            // pre-Load is cheap insurance against any module-cctor that
            // touches enum metadata.
            int replayed = 0;
            try
            {
                replayed = LoadEnumRegistrySidecar(sidecarPath!);
                // Query symbols: same story, different side effect. Registering the
                // sidecar is what lets RecordPatches build a real NCLMetaQuery.
                if (bundleDeclaresQuery)
                    AlRunner.Patches.RecordPatches.RegisterBundleQuerySymbolsJson(querySidecarPath!);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [cache] sidecar replay failed for {sidecarPath}: {ex.Message} — falling through to MISS");
                cachedBytes = null;
            }
            if (cachedBytes != null)
            {
                Console.Error.WriteLine($"  [cache] HIT  key={cacheKey} path={cachePath} ({cachedBytes.Length} bytes, {replayed} enum entries replayed) — skipping Emit+Compile");
                AlRunner.Infrastructure.PhaseLog.NoteCacheHit();
                assemblyBytes = cachedBytes;
            }
        }
        if (needCompile && assemblyBytes == null)
        {
            if (alCacheDir != null)
            {
                Console.Error.WriteLine($"  [cache] MISS key={cacheKey} — running Emit+Compile");
                AlRunner.Infrastructure.PhaseLog.NoteCacheMiss();
            }
            var et = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<EmittedSource> sources = Array.Empty<EmittedSource>();
            IReadOnlyList<string> alDiagnostics = Array.Empty<string>();
            // --tdd only (issue #1997): count of objects the TDD-EXCLUDED branch below
            // deliberately kept `sources` short by. The PARTIAL-EMIT-DROP guard further
            // down flags any declared-vs-emitted gap as a SILENT drop — under --tdd that
            // gap is not silent (it is exactly the TDD-EXCLUDED objects, already reported
            // above with a synthetic FAILED test each), so the guard must subtract this
            // count before deciding there is an unexplained gap left.
            int tddExcludedCount = 0;
            // Emit-phase timeout: default 120 s, override via AL_RUNNER_EMIT_TIMEOUT_SEC.
            // Note: Task.Run thread continues in background after timeout — acceptable for a CLI tool.
            int emitTimeoutSec = int.TryParse(
                Environment.GetEnvironmentVariable("AL_RUNNER_EMIT_TIMEOUT_SEC"), out var ts) ? ts : 120;
            // Containment: keep a symbol-less .app in ONE suite's .alpackages from failing
            // every OTHER suite in the bundle. BC's native .app scanner reports AL1023
            // ("package file is not valid") for a package with no SymbolReference.json and
            // then AL1022 ("could not be found") for the dep it should have supplied — and
            // because the bundle compiles every module against the UNION of all suites'
            // resolved deps, both land in siblings that never declared that dependency.
            //
            // BC 28's Emit shrugs these off; BC 27's does not — measured on the 27.0 leg,
            // one fixture package took 16 unrelated suites to EMIT-ZERO and cost ~50 tests.
            // Such a package can never serve the compiler's scanner anyway, so dropping it
            // from the COMPILER's dep list loses nothing: its symbols arrive via
            // *.symbols.json (GetSharedReferences) and its code via the runtime's Tier-1
            // .deps-bin path, neither of which this scope touches. Same filter EmitDepSymbols
            // already applies — see BcCompiler.ScopeSymbolBearingDepsOnly.
            using var bundleDepScope = BcCompiler.ScopeSymbolBearingDepsOnly();

            // #1902: in --watch, try the incremental (RAD) path first — one edit costs work
            // proportional to that edit, for every object kind (including the six with no
            // numeric Id) and every file operation (add/edit/rename/delete/touch-with-
            // identical-bytes), instead of the whole module. Falls back to the ordinary full
            // Emit() (still tracking a baseline for the NEXT cycle) for anything it cannot prove
            // safe: the first cycle for this bundle, an app.json/dependency change, more than
            // one object declared in a touched file, a duplicate declaration only the compiler
            // can adjudicate, or any diagnostic the delta compile itself raises — see
            // BcCompiler.Incremental.cs's header comment for the full classification. Every
            // other mode (one-shot, --server) is untouched — normal-mode Emit() never tracks a
            // baseline and never calls TryEmitIncremental, so its cost profile is unchanged.
            BcEmitOutput? incrementalOutput = null;
            if (watchMode)
            {
                incrementalOutput = emitter.TryEmitIncremental(allPaths, moduleName, appGroup.SuiteDir, out var incrementalFallbackReason);
                // #1905 (defect 4): a full rebuild costs whole MINUTES on a large app
                // (761-862s measured on NP Retail, #1905's own numbers) against an
                // incremental cycle's seconds, so which reason forced it is a RESULT
                // the developer needs to see, not an internal diagnostic — it must NOT
                // be gated behind --verbose. That gate was exactly the [bc]/[expectations]
                // mistake Log.cs's header comment warns about: both were silently eaten
                // by the component filter until their cost was measured after the fact.
                // [watch] is already exempt from that filter (see Log.cs), so this reaches
                // the console unconditionally at default verbosity.
                //
                // Cycle 0 is excluded: the very first --watch cycle for a bundle ALWAYS
                // falls back ("no incremental baseline yet") because there is nothing to
                // diff against yet — that is not a fallback in any meaningful sense, it is
                // the starting state every single invocation hits. Printing a scary-sounding
                // "full rebuild" line on every startup would train the reader to ignore it,
                // which is the exact failure this line exists to prevent (see this file's
                // header comment for the identical trap already hit twice with [bc] and
                // [expectations]). From cycle 1 onward the same reason text (e.g. two
                // fallbacks in a row because CreateForRad keeps throwing) IS interesting and
                // is reported.
                if (incrementalOutput == null && watchCycleIndex > 0)
                {
                    watchFullRebuildReasons.Add((moduleName, incrementalFallbackReason));
                    Console.Error.WriteLine(
                        $"[watch] {moduleName}: FULL REBUILD this cycle (whole module — expect the " +
                        $"cold-cycle order of magnitude, not a proportional edit) — {incrementalFallbackReason}");
                }
            }
            var emitTask = incrementalOutput != null
                ? Task.FromResult(incrementalOutput)
                : Task.Run(() => emitter.Emit(allPaths, moduleName, appGroup.SuiteDir, trackIncrementalBaseline: watchMode));
            try
            {
                if (!emitTask.Wait(TimeSpan.FromSeconds(emitTimeoutSec)))
                {
                    Console.Error.WriteLine(
                        $"<bundled>: EMIT-TIMEOUT after {emitTimeoutSec}s on {allPaths.Count} AL paths");
                    Console.Error.WriteLine(
                        "Hint: increase AL_RUNNER_EMIT_TIMEOUT_SEC or quarantine the offending suite via a tests/expectations/ entry.");
                    bundleErrors.Add($"<bundled>: EMIT-TIMEOUT after {emitTimeoutSec}s");
                }
                else
                {
                    var emitOutput = emitTask.Result;
                    sources = emitOutput.Sources;
                    alDiagnostics = emitOutput.Diagnostics;
                    // --tdd (issue #2001): collect regardless of whether anything ended up
                    // excluded afterward — generation can fully resolve an object with NO
                    // exclusion remaining, and that case still belongs in criterion 8's list.
                    if (emitOutput.TddGeneratedMembers != null)
                    {
                        allTddGeneratedMembers.AddRange(emitOutput.TddGeneratedMembers);
                        // Invert DependentTests (member -> tests) into (test -> members), so
                        // OverrideTddDependentResults can look a REAL TestResult up by its own
                        // (CodeunitDisplayName ?? Codeunit, Method) in O(1).
                        foreach (var m in emitOutput.TddGeneratedMembers)
                            foreach (var testLabel in m.DependentTests)
                            {
                                if (!bundleTddDependents.TryGetValue(testLabel, out var list))
                                    bundleTddDependents[testLabel] = list = new List<TddGeneratedMember>();
                                list.Add(m);
                            }
                    }

                    // An emit-retry exclusion means one or more AL objects are NOT in the
                    // compiled module. Any test they declared is now absent from the run —
                    // the total silently shrinks and every remaining test still passes, so
                    // the run looks green. Fail loudly instead (.claude/rules/loud-failures.md).
                    //
                    // Deliberately NOT folded into the PARTIAL-EMIT-DROP guard below: that one
                    // is gated on `alDiagnostics.Count == 0`, and an exclusion always carries
                    // diagnostics (they are what identified the broken object), so it could
                    // never catch this case. Reporting the excluded names directly also beats
                    // inferring a count from a regex over the sources.
                    if (emitOutput.ExcludedObjects.Count > 0)
                    {
                        var names = string.Join(", ", emitOutput.ExcludedObjects);
                        if (tddMode)
                        {
                            // --tdd (issue #1997): the default path above (else branch) is
                            // UNCHANGED — this branch only runs when --tdd was passed. Keep the
                            // recovered `sources` (BcCompiler's emit-retry loop already dropped
                            // ONLY the broken objects and recompiled the survivors) instead of
                            // discarding the whole module, and turn every [Test] procedure the
                            // excluded objects declared into a synthetic FAILED TestResult naming
                            // the AL diagnostic that broke it. bundleErrors MUST stay untouched
                            // here: any entry there forces exit code 3 at the exit-code ladder
                            // below, and the whole point of --tdd is to report a RED TEST (exit
                            // 1), not a compile failure.
                            var synthetic = TddSupport.BuildFailedTests(
                                emitOutput.TddExcludedDetails ?? Array.Empty<TddExcludedObjectDetail>());
                            Console.Error.WriteLine(
                                $"<bundled>: TDD-EXCLUDED — {moduleName}: {emitOutput.ExcludedObjects.Count} " +
                                $"object(s) could not be compiled: [{names}]. {synthetic.Count} [Test] " +
                                $"procedure(s) they declare report as FAILED instead of vanishing from the run. " +
                                $"Re-run with --verbose for the AL diagnostics that identified them.");
                            bundleTests.AddRange(synthetic);
                            tddExcludedCount = emitOutput.ExcludedObjects.Count;
                            // sources stays as BcCompiler returned it (the recovered set) — do
                            // NOT clear it, unlike the non-tdd branch below.
                        }
                        else
                        {
                            // Untagged on purpose: a `[Component]` prefix would be swallowed by
                            // Log's filter at default verbosity, which is the original defect.
                            Console.Error.WriteLine(
                                $"<bundled>: EMIT-EXCLUDED — {moduleName}: {emitOutput.ExcludedObjects.Count} object(s) " +
                                $"could not be compiled and were dropped from the module, so any tests they declare " +
                                $"are MISSING from this run: [{names}]. Re-run with --verbose for the AL diagnostics " +
                                $"that identified them.");
                            bundleErrors.Add(
                                $"<bundled>: EMIT-EXCLUDED for {moduleName}: {emitOutput.ExcludedObjects.Count} " +
                                $"object(s) dropped from the module — tests they declare are missing: [{names}].");
                            sources = Array.Empty<EmittedSource>(); // do not run a module that is missing objects
                        }
                    }

                    // --dump-csharp DIR: write the emitted intermediate C# (BC's
                    // Compilation.Emit produces UTF-8 C# source per AL object before
                    // BcAssembler hands it to Roslyn) so codegen issues can be
                    // inspected with a diff.
                    if (dumpCsharpDir != null)
                        DumpCsharpSources(dumpCsharpDir, moduleName, sources);
                }
            }
            catch (AggregateException aggEx) when (emitTask.IsFaulted)
            {
                var flat = aggEx.Flatten();
                var rootEx = flat.InnerExceptions[0];
                Console.Error.WriteLine($"<bundled>: EMIT-FAIL — {rootEx.GetType().Name}: {rootEx.Message}");
                if (rootEx.StackTrace is { } st) Console.Error.WriteLine(st);
                if (flat.InnerExceptions.Count > 1)
                    foreach (var inner in flat.InnerExceptions.Skip(1))
                        Console.Error.WriteLine($"  → {inner.GetType().Name}: {inner.Message}");
                bundleErrors.Add($"<bundled>: EMIT-FAIL: {rootEx.Message.Split('\n')[0]}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"<bundled>: EMIT-FAIL — {ex.GetType().Name}: {ex.Message}");
                if (ex.StackTrace is { } st) Console.Error.WriteLine(st);
                bundleErrors.Add($"<bundled>: EMIT-FAIL: {ex.Message.Split('\n')[0]}");
            }
            finally
            {
                et.Stop();
                bundleEmit += et.Elapsed;
                AlRunner.Infrastructure.PhaseLog.AddAppEmit(et.Elapsed);
            }

            // Partial silent emit-drop guard. #1620 already catches the ALL-objects-missing
            // case (sources.Count==0 with diagnostics). This catches the SUBSET case: BC's
            // Compilation.Emit can silently drop ONE of several objects with ZERO
            // diagnostics — confirmed reproducible for tests/runner-extras/crypto-hash-instream
            // (2 codeunits in, 1 source out, no error) specifically when compiled as the
            // Nth app in a long-running bundled process; the same 2 files compile correctly
            // every time in isolation. Root cause is inside BC's own Compilation.Emit and is
            // not yet understood — see the tracked runner-gap issue. Per
            // .claude/rules/loud-failures.md this must fail loudly, not vanish a whole
            // suite's tests with no trace.
            if (sources.Count > 0 && alDiagnostics.Count == 0)
            {
                var declaredObjects = allPaths
                    .Where(File.Exists)
                    .Concat(allPaths.Where(Directory.Exists)
                        .SelectMany(d => Directory.EnumerateFiles(d, "*.al", SearchOption.AllDirectories)))
                    .Distinct()
                    .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(
                        File.ReadAllText(f),
                        @"^(table|codeunit|page|report|query|enum|xmlport|tableextension|pageextension|permissionset)\s+\d+\s+""?([^""\r\n]+?)""?\s*$",
                        System.Text.RegularExpressions.RegexOptions.Multiline))
                    .Select(m => m.Groups[2].Value.Trim())
                    .ToList();
                // #1997: the gap is not silent when it exactly matches tddExcludedCount — the
                // TDD-EXCLUDED branch above already reported those objects loudly, with a
                // synthetic FAILED test each. Only a gap BEYOND that is the unexplained,
                // genuinely silent drop this guard exists to catch.
                if (declaredObjects.Count > sources.Count + tddExcludedCount)
                {
                    var emittedNames = sources.Select(s => s.Name).ToList();
                    bundleErrors.Add(
                        $"<bundled>: PARTIAL-EMIT-DROP for {moduleName}: {declaredObjects.Count} object(s) declared, " +
                        $"only {sources.Count} emitted, 0 AL diagnostics explain the gap. Declared: " +
                        $"[{string.Join(", ", declaredObjects)}]. Emitted: [{string.Join(", ", emittedNames)}].");
                    Console.Error.WriteLine(
                        $"<bundled>: PARTIAL-EMIT-DROP — {moduleName}: {declaredObjects.Count} declared vs " +
                        $"{sources.Count} emitted, no diagnostics. Declared: [{string.Join(", ", declaredObjects)}]. " +
                        $"Emitted: [{string.Join(", ", emittedNames)}].");
                    sources = Array.Empty<EmittedSource>(); // do not compile a partial, silently-wrong module
                }
            }
            if (sources.Count == 0 && alDiagnostics.Count > 0)
            {
                // Emit produced zero sources — BC's compiler swallowed exceptions internally.
                // Surface AL diagnostics (parse/declaration errors) so the failure is visible.
                Console.Error.WriteLine($"<bundled>: EMIT-ZERO — 0 sources emitted, {alDiagnostics.Count} AL error(s):");
                foreach (var d in alDiagnostics)
                    Console.Error.WriteLine($"  {d}");
                bundleErrors.Add($"<bundled>: EMIT-ZERO ({alDiagnostics.Count} AL error(s))");
            }
            // AL-diagnostic compile-failure guard (#2150). BC's ContinueBuildOnError keeps
            // compiling an object's SIBLINGS after a declaration-stage error on one object
            // (e.g. a query column declaring both a data source AND `Method = Count`, AL0353)
            // — the broken object's metadata can still emit alongside everything else, so
            // `sources` comes back non-empty even though `alDiagnostics` (built from
            // GetDeclarationDiagnostics()/emitResult.Diagnostics, always Error-severity only,
            // see BcCompiler.Emit) is also non-empty. Neither guard above catches this: it
            // isn't PARTIAL-EMIT-DROP (there ARE diagnostics explaining the gap) and it isn't
            // EMIT-ZERO (sources isn't empty). Real BC never publishes an app with ANY error
            // diagnostic regardless of how many other objects compiled clean, so this must
            // fail the same way — a real service tier would reject this module outright.
            // Skip when EMIT-EXCLUDED already handled it (that branch empties `sources`).
            //
            // #2151 fixed the runner's Tier-3 source compile so it resolves a report's
            // LayoutFile relative to the DECLARING .al file's own directory when the value is
            // explicitly file-relative ("./" / "../"), matching what real BC's compiler does
            // (see ReportLayoutFileSystem) — the six al-language corpus reports that used to
            // need an AL1081 carve-out here now compile clean, so every AL error diagnostic
            // blocks unconditionally, with no per-error-code exception.
            if (sources.Count > 0 && alDiagnostics.Count > 0)
            {
                Console.Error.WriteLine(
                    $"<bundled>: AL-DIAGNOSTIC-FAIL — {moduleName}: {sources.Count} object(s) emitted but " +
                    $"{alDiagnostics.Count} AL error(s) were reported by BC's own compiler; a real " +
                    $"service tier would refuse to publish this module:");
                foreach (var d in alDiagnostics)
                    Console.Error.WriteLine($"  {d}");
                bundleErrors.Add(
                    $"<bundled>: AL-DIAGNOSTIC-FAIL for {moduleName}: {alDiagnostics.Count} AL error(s) " +
                    $"reported even though {sources.Count} object(s) emitted.");
                sources = Array.Empty<EmittedSource>(); // do not run a module BC would refuse to publish
            }
            if (sources.Count > 0)
            {
                var ct = System.Diagnostics.Stopwatch.StartNew();
                var compile = assembler.Compile(moduleName, sources);
                ct.Stop(); bundleComp += ct.Elapsed;
                AlRunner.Infrastructure.PhaseLog.AddAppCompile(ct.Elapsed);
                if (!compile.Success)
                {
                    Console.Error.WriteLine($"<bundled>: COMPILE-FAIL — {compile.Errors.Count} error(s):");
                    foreach (var err in compile.Errors)
                        Console.Error.WriteLine($"  {err}");
                    if (alDiagnostics.Count > 0)
                    {
                        Console.Error.WriteLine($"<bundled>: AL diagnostics from emit ({alDiagnostics.Count}):");
                        foreach (var d in alDiagnostics)
                            Console.Error.WriteLine($"  {d}");
                    }
                    bundleErrors.Add($"<bundled>: COMPILE-FAIL ({compile.Errors.Count}): {compile.Errors.FirstOrDefault()?.Split('\n')[0]}");
                }
                else
                {
                    assemblyBytes = compile.AssemblyBytes;
                    if (cachePath != null && assemblyBytes != null)
                    {
                        try
                        {
                            // Publish atomically, sidecars first and the DLL last (issue
                            // #1810): AlCacheSidecars.IsCompleteEntry gates a HIT on the DLL's
                            // presence, so the DLL becoming visible must be the commit point —
                            // AtomicPublish writes each artifact to a same-directory temp file
                            // and renames it into place, so a concurrent reader observing the
                            // directory at any point sees either the old complete entry, no
                            // entry, or the new complete entry, never a torn file or a DLL
                            // whose sidecar isn't there yet.
                            //
                            // Sidecar: persist the AlEnumMetadataRegistry side-effect that
                            // emit just populated. Without this, cache HIT replays the DLL
                            // but leaves the registry empty → enum tests fail.
                            int written = AlRunner.Infrastructure.AlCacheWriter.AtomicPublish(
                                sidecarPath!, tmp => SaveEnumRegistrySidecar(tmp));
                            // Same for the query symbols emit just serialized — without
                            // this the next HIT has no MetaQuery design (see
                            // AlCacheSidecars).
                            var qsrc = BcCompiler.LastBundleQuerySymbolsPath;
                            if (qsrc != null && File.Exists(qsrc))
                                AlRunner.Infrastructure.AlCacheWriter.AtomicPublish(
                                    querySidecarPath!, tmp => File.Copy(qsrc, tmp, overwrite: true));
                            AlRunner.Infrastructure.AlCacheWriter.AtomicPublish(
                                cachePath, tmp => File.WriteAllBytes(tmp, assemblyBytes));
                            Console.Error.WriteLine($"  [cache] WROTE key={cacheKey} path={cachePath} ({assemblyBytes.Length} bytes, {written} enum entries → sidecar)");
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"  [cache] write failed for {cachePath}: {ex.Message}");
                        }
                    }
                }
            }
        }

        // In watch mode the parent ONLY emits to the cache (above); the actual test run
        // happens in a fresh child process spawned after the loop, which cache-HITS this
        // emit. The parent never loads a bundle assembly, so there is no .NET
        // assembly-coexistence problem across re-emits.
        // Run in-process for BOTH normal and watch mode. The same-bundle reload
        // reset above + the test-assembly-preferring type finders make an
        // in-process re-run of an edited same-id bundle safe (the gap that
        // originally forced watch to spawn a child process is now closed), so
        // watch keeps the deps warm and re-runs in ~seconds instead of re-paying
        // a child process's startup + runtime dep load on every save.
        // Load and register each module as it is built, but do NOT run yet: the
        // test run happens once, after every app in the bundle is loaded, so that
        // an app can call into a sibling it depends on.
        if (reusedAsm != null || assemblyBytes != null)
        {
            Assembly asm;
            if (reusedAsm != null)
            {
                // See the "cross-bundle module identity dedup" comment above: this app's
                // AppId was already loaded earlier in this process, so run with that exact
                // Assembly instead of Assembly.Load-ing a second, distinct module for the
                // same AL identity.
                asm = reusedAsm;
            }
            else
            {
                var loadSw = System.Diagnostics.Stopwatch.StartNew();
                asm = Assembly.Load(assemblyBytes!);
                loadSw.Stop();
                AlRunner.PerfTrace.Log($"test assembly load {rel}/{moduleName} {loadSw.ElapsedMilliseconds}ms");
                // Register this freshly-loaded module by AppId so a LATER bundle that
                // resolves the same app as a dependency (via DependencyLoader) reuses this
                // exact Assembly instead of re-emitting/re-compiling a second module for the
                // same AL identity — see the dedup comment above (issue #1683). Skipped under
                // --watch: TryAdd is first-wins, so iteration 2's freshly-edited asm would
                // never overwrite iteration 1's stale entry, and any sibling bundle that
                // later resolves this AppId as a real dependency would get the stale copy.
                if (!watchMode && appGroup.AppId is { } newlyLoadedId)
                {
                    try
                    {
                        // Publisher/Version are non-null whenever AppId is — see the
                        // BuildAppGroups invariant note above the reusedAsm check (PR #1862
                        // review); the `!` asserts it rather than silently masking a
                        // violation behind a fallback that would disagree with AppLoader's
                        // own default (see IdentityMatches' doc comment).
                        DependencyLoader.RegisterLoaded(
                            newlyLoadedId, asm, appGroup.ModuleName, appGroup.Publisher!,
                            appGroup.Version!.ToString(), appGroup.SuiteDir);
                    }
                    catch (AlRunner.Infrastructure.AppIdCollisionException ex)
                    {
                        // Same defence as the TryGetByAppId check above, for the (in-process,
                        // single-threaded loop) race window between that check and this
                        // registration — see loud-failures.md.
                        if (stdoutSilenced) { Console.SetOut(savedOut); Console.SetError(savedErr); }
                        Console.Error.WriteLine();
                        Console.Error.WriteLine($"FATAL: {ex.Message}");
                        Console.Error.WriteLine();
                        return 1;
                    }
                }
            }
            var registerSw = System.Diagnostics.Stopwatch.StartNew();
            // wireFieldTriggers:false — WireFieldTriggerHandlersAll walks EVERY table
            // registered so far, not just this assembly's. Calling it here, per app,
            // would re-walk the same growing table set on every load AND (worse) mark
            // a later app's tables "wired" before that app's own assembly has loaded,
            // permanently skipping their real wiring — see BcRuntime.SetTestAssembly's
            // doc comment. It runs exactly once below, after every app has loaded.
            // NavApp.GetResource resolves against whatever dir SetCurrentBundleDir last
            // saw, and SetTestAssembly's call to NavAppResourcePatches.RegisterTestAssembly
            // reads it synchronously — so this must be set to THIS app's own suite dir
            // before SetTestAssembly runs, the same requirement as the module-identity
            // fix below. Without it every app in the bundle resolved resources against
            // whichever dir the bundle-level SetBundleInfoFromAppJson last saw (often
            // none, for a multi-app tree with no app.json at its root), and
            // NavApp.GetResource threw "could not be found in app ''" for every app.
            AlRunner.Patches.NavAppResourcePatches.SetCurrentBundleDir(appGroup.SuiteDir);
            BcRuntime.SetTestAssembly(asm, wireFieldTriggers: false);
            // Register THIS app's identity, not the bundle's. RegisterTestAssemblyInfo
            // reads the current bundle info, which stays "Unknown" whenever the bundle
            // root has no app.json of its own (every multi-app tree, tests/runner-extras
            // included) — so point it at the app being loaded first. This feeds both the
            // per-assembly module registry behind NavApp.GetCurrentModuleInfo and the AL
            // call-stack frame decoration.
            if (appGroup.AppId is { } gid)
                // Publisher/Version are non-null whenever AppId is — same BuildAppGroups
                // invariant as the reusedAsm/RegisterLoaded call sites above (PR #1862
                // review).
                BcRuntime.SetCurrentBundleInfo(
                    gid,
                    appGroup.ModuleName,
                    appGroup.Publisher!,
                    appGroup.Version!.ToString());
            BcRuntime.RegisterTestAssemblyInfo(asm);
            registerSw.Stop();
            AlRunner.PerfTrace.Log($"RegisterTestAssemblyInfo {rel}/{moduleName} {registerSw.ElapsedMilliseconds}ms");
            suiteDirByAssembly[asm] = appGroup.SuiteDir;
            loadedAssemblies.Add(asm);
        }
        } // ── end per-app emit/compile/load loop ────────────────────────────────
        // Close the last app's emit/compile turn here, not at the bundle's end: the
        // test run below is a SEPARATE pass, and leaving this row open would bank the
        // whole pass onto it.
        AlRunner.Infrastructure.PhaseLog.EndApp();

        // Every app's assembly is now in the AppDomain, so this single walk resolves
        // every table's Record CLR type in one pass — including tables belonging to
        // apps that loaded LATER than the app that first registered their NCLMetaTable
        // (pre-registration adds every suite's src/ up front, before any app emits).
        using (AlRunner.Infrastructure.PhaseLog.Stage("wire-field-triggers"))
            AlRunner.Patches.RecordPatches.WireFieldTriggerHandlersAll();

        int runIdx = 0;
        foreach (var asm in loadedAssemblies)
        {
            // Reopens THIS app's row (matched by module name) so its test-run time lands
            // on the app that owns it. See PhaseLog.BeginApp for why it is two passes.
            runIdx++;
            AlRunner.Infrastructure.PhaseLog.BeginApp(
                asm.GetName().Name ?? $"<asm {runIdx}>", runIdx, loadedAssemblies.Count);
            var rt = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<TestResult> tests;
            try
            {
                // Re-point the resource dir at THIS app before SetTestAssembly, which
                // re-runs its full body (including the resource-dir registration) here
                // too — see suiteDirByAssembly's declaration for why.
                if (suiteDirByAssembly.TryGetValue(asm, out var suiteDir))
                    AlRunner.Patches.NavAppResourcePatches.SetCurrentBundleDir(suiteDir);
                // #1861: SetTestAssembly is one of the candidates the issue names for the
                // flat ~4.8s-per-app-group tax inside this run turn — mark it explicitly
                // rather than letting it fall into whatever executor.Run's own marks miss.
                using (AlRunner.Infrastructure.PhaseLog.AppStage("set-test-assembly"))
                    BcRuntime.SetTestAssembly(asm, wireFieldTriggers: false);
                BcRuntime.OosHooksActive = true;
                var execSw = System.Diagnostics.Stopwatch.StartNew();
                tests = OverrideTddDependentResults(executor.Run(asm));
                execSw.Stop();
                AlRunner.PerfTrace.Log($"TestExecutor.Run {rel} {execSw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                rt.Stop(); bundleRun += rt.Elapsed;
                AlRunner.Infrastructure.PhaseLog.AddAppRun(rt.Elapsed);
                // A ReflectionTypeLoadException (possibly wrapped) otherwise surfaces only its
                // opaque top line ("Unable to load one or more of the requested types"),
                // hiding WHICH type/dependency could not load. Dig out the concrete
                // LoaderExceptions (per .claude/rules/loud-failures.md) so the developer sees
                // the real cause — almost always a dependency whose runtime DLL was not built.
                var rtle = ex as ReflectionTypeLoadException
                    ?? ex.InnerException as ReflectionTypeLoadException;
                if (rtle != null)
                {
                    var reasons = rtle.LoaderExceptions
                        .Where(e => e != null).Select(e => e!.Message).Distinct().Take(5).ToList();
                    bundleErrors.Add(
                        $"<bundled>: EXEC-FAIL: {ex.Message.Split('\n')[0]} — " +
                        string.Join(" | ", reasons));
                }
                else
                {
                    bundleErrors.Add($"<bundled>: EXEC-FAIL: {ex.Message.Split('\n')[0]}");
                }
                tests = Array.Empty<TestResult>();
            }
            finally
            {
                BcRuntime.OosHooksActive = false;
            }
            rt.Stop(); bundleRun += rt.Elapsed;
            AlRunner.Infrastructure.PhaseLog.AddAppRun(rt.Elapsed);
            bundleTests.AddRange(tests);
            ranGroupCount++;
            sP += tests.Count(t => t.Outcome == TestOutcome.Pass);
            sF += tests.Count(t => t.Outcome == TestOutcome.Fail);
            sE += tests.Count(t => t.Outcome == TestOutcome.Error);
        }
    }
    else
    {
        int si = 0;
        foreach (var suite in suites)
        {
            si++;
            var suiteName = Path.GetRelativePath(bundleAbs, suite);
            // Non-bundled mode emits one module per SUITE, so the suite is the app
            // group here. Same unit, same row kind — the instrument must not go blind
            // just because --isolation moved the compile boundary.
            AlRunner.Infrastructure.PhaseLog.BeginApp($"V2_{Path.GetFileName(suite)}", si, suites.Count);
            var suitePaths = CollectSuitePaths(suite, bucketRoot);
            if (suitePaths.Count == 0) continue;

            var et = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<EmittedSource> sources;
            IReadOnlyList<string> suiteAlDiagnostics = Array.Empty<string>();
            try
            {
                var emitOutput = emitter.Emit(suitePaths, $"V2_{Path.GetFileName(suite)}", suite);
                sources = emitOutput.Sources;
                suiteAlDiagnostics = emitOutput.Diagnostics;
            }
            catch (Exception ex)
            {
                et.Stop(); bundleEmit += et.Elapsed;
                AlRunner.Infrastructure.PhaseLog.AddAppEmit(et.Elapsed);
                Console.Error.WriteLine($"{suiteName}: EMIT-FAIL — {ex.GetType().Name}: {ex.Message}");
                if (ex.StackTrace is { } st) Console.Error.WriteLine(st);
                bundleErrors.Add($"{suiteName}: EMIT-FAIL: {ex.Message.Split('\n')[0]}");
                continue;
            }
            et.Stop(); bundleEmit += et.Elapsed;
            AlRunner.Infrastructure.PhaseLog.AddAppEmit(et.Elapsed);

            // AL-diagnostic compile-failure guard (#2150), extended to --per-suite (#2152).
            // Bundled mode got this gate first because it's the only path CI's corpus/
            // runner-extras legs actually exercise (see #2154) — but --per-suite compiles
            // one module per SUITE instead of per app-group and hits the exact same BC
            // ContinueBuildOnError shape: `sources` can come back non-empty (a broken
            // object's sibling still emitted) at the same time `suiteAlDiagnostics` is also
            // non-empty. Real BC would refuse to publish this suite regardless, so
            // --per-suite must fail here too.
            if (sources.Count > 0 && suiteAlDiagnostics.Count > 0)
            {
                Console.Error.WriteLine(
                    $"{suiteName}: AL-DIAGNOSTIC-FAIL — {sources.Count} object(s) emitted but " +
                    $"{suiteAlDiagnostics.Count} AL error(s) were reported by BC's own compiler; " +
                    $"a real service tier would refuse to publish this module:");
                foreach (var d in suiteAlDiagnostics)
                    Console.Error.WriteLine($"  {d}");
                bundleErrors.Add(
                    $"{suiteName}: AL-DIAGNOSTIC-FAIL ({suiteAlDiagnostics.Count}): " +
                    $"{suiteAlDiagnostics.FirstOrDefault()?.Split('\n')[0]}");
                continue; // do not compile/run a suite BC would refuse to publish
            }

            var ct = System.Diagnostics.Stopwatch.StartNew();
            var compile = assembler.Compile($"V2_{Path.GetFileName(suite)}", sources);
            ct.Stop(); bundleComp += ct.Elapsed;
            AlRunner.Infrastructure.PhaseLog.AddAppCompile(ct.Elapsed);
            if (!compile.Success)
            {
                Console.Error.WriteLine($"{suiteName}: COMPILE-FAIL — {compile.Errors.Count} error(s):");
                foreach (var err in compile.Errors)
                    Console.Error.WriteLine($"  {err}");
                if (suiteAlDiagnostics.Count > 0)
                {
                    Console.Error.WriteLine($"{suiteName}: AL diagnostics ({suiteAlDiagnostics.Count}):");
                    foreach (var d in suiteAlDiagnostics)
                        Console.Error.WriteLine($"  {d}");
                }
                bundleErrors.Add($"{suiteName}: COMPILE-FAIL ({compile.Errors.Count}): {compile.Errors.FirstOrDefault()?.Split('\n')[0]}");
                continue;
            }

            var rt = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<TestResult> tests;
            try
            {
                var asm = Assembly.Load(compile.AssemblyBytes!);
                // #1861: same mark as the bundled-mode run loop, so the app-stage report
                // is consistent whichever compile boundary --isolation chose.
                using (AlRunner.Infrastructure.PhaseLog.AppStage("set-test-assembly"))
                {
                    BcRuntime.SetTestAssembly(asm);
                    BcRuntime.RegisterTestAssemblyInfo(asm);
                }
                BcRuntime.OosHooksActive = true;
                tests = OverrideTddDependentResults(executor.Run(asm));
            }
            catch (Exception ex)
            {
                rt.Stop(); bundleRun += rt.Elapsed;
                AlRunner.Infrastructure.PhaseLog.AddAppRun(rt.Elapsed);
                bundleErrors.Add($"{suiteName}: EXEC-FAIL: {ex.Message.Split('\n')[0]}");
                continue;
            }
            finally
            {
                BcRuntime.OosHooksActive = false;
            }
            rt.Stop(); bundleRun += rt.Elapsed;
            AlRunner.Infrastructure.PhaseLog.AddAppRun(rt.Elapsed);
            bundleTests.AddRange(tests);
            ranGroupCount++;
            sP += tests.Count(t => t.Outcome == TestOutcome.Pass);
            sF += tests.Count(t => t.Outcome == TestOutcome.Fail);
            sE += tests.Count(t => t.Outcome == TestOutcome.Error);
        }
    }

    // The interactive dashboard owns the whole screen and is painted after the
    // cycle, so suppress these per-bundle status lines there (they'd be wiped by
    // the next Clear anyway and corrupt the cleared frame). Piped watch + normal
    // mode keep their existing line output verbatim.
    if (!watchUi)
    {
        if (watchMode)
            Console.WriteLine($"  [watch] re-emitted {rel} ({bundleEmit.TotalSeconds:F1}s) — running…");
        else
            Console.WriteLine($"  → {sP}P/{sF}F/{sE}E across {bundleTests.Count} tests, {bundleErrors.Count} suite errors ({(bundleEmit + bundleComp + bundleRun).TotalSeconds:F1}s)");
    }
    // Deliberately still gated on an EMPTY bundle. CompileFailed suppresses the bucket's
    // per-test reporting (Reporter treats it as "nothing ran"), so widening it to any suite
    // error would hide the tests that DID pass — trading one silent inaccuracy for another.
    // Partial suite loss reaches the exit code via computedExitCode's CompileErrors check
    // instead, which keeps the surviving results in the report and the JSON.
    if (bundleTests.Count == 0 && bundleErrors.Count > 0) bundleStage = BucketStage.CompileFailed;
    results.Add(new BucketResult(bundleAbs, bundleStage,
        bundleErrors, null, bundleTests,
        bundleEmit, bundleComp, bundleRun, ranGroupCount));
    // Appended here, not buffered to process exit: a run that dies mid-way still
    // yields a row for every bundle it did finish. The row's wall clock covers this
    // whole loop turn, so wall − (emit+compile+run) is the per-bundle overhead
    // (dep resolution, symbol/module registration) #1825 is hunting.
    AlRunner.Infrastructure.PhaseLog.EndBundle(bundleEmit, bundleComp, bundleRun);
}

// Restore the streams silenced for the clean-loading frame (#5) before any dashboard
// repaint / summary that writes to Console.Out / Console.Error.
if (stdoutSilenced)
{
    Console.SetOut(savedOut);
    Console.SetError(savedErr);
    stdoutSilenced = false;
}

if (!watchMode)
    break;   // normal mode: one pass, fall through to the summary below

// ── Watch mode: the bundles just ran IN-PROCESS above (deps stayed warm).
// Show this iteration's results, then block until an .al source change and
// loop (reset + re-emit warm + re-run, all in the same warm process). ─────────
var cycleDur = results.Aggregate(TimeSpan.Zero,
    (acc, b) => acc + b.EmitTime + b.CompileTime + b.RunTime);

if (watchUi)
{
    // Interactive: render the idle "● watching" dashboard once, then service
    // keyboard scrolling AND the file-change watcher in one interleaved poll loop.
    // The dashboard frequently exceeds the screen, so we paint only the visible
    // window at the current scroll offset and let arrow/page/home/end keys move it.
    // The paint runs from inside onArmed, which WatchSource invokes only AFTER
    // every FileSystemWatcher is live (#1822) — so "● watching" can never be
    // painted before the watch is actually armed.
    var idleTs = DateTime.Now;
    watchScroll = 0;
    List<string> lines = new();
    var armed = WatchSource.ArmSourceWatch(bundles, onArmed: () =>
        {
            lines = RenderDashboardLines(WatchStatus.Idle, idleTs, cycleDur);
            watchScroll = PaintWatchViewport(lines, watchScroll);
        });
    if (armed == null) return 0;
    var (signal, watchers, watchActivity) = armed.Value;
    bool changed = false;
    // Console.KeyAvailable throws InvalidOperationException when stdin is redirected
    // (a pipe/file rather than a real terminal). We still want the dashboard + file
    // watching in that case (output is a TTY), just without scroll keys. Probe once.
    bool keyboard = !Console.IsInputRedirected;
    try
    {
        while (true)
        {
            // #1904: quiescence, not a fixed sleep after only the first event — a branch
            // switch/bulk rewrite keeps this re-armed until the tree actually stops
            // changing, instead of starting a cycle against a half-applied checkout.
            if (signal.IsSet) { WatchSource.WaitForQuiescence(watchActivity); changed = true; break; }

            if (keyboard && SafeKeyAvailable())
            {
                var key = Console.ReadKey(intercept: true);
                int height = Math.Max(5, Console.WindowHeight);
                int page = Math.Max(1, height - 2);
                bool repaint = true;
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:    watchScroll--; break;
                    case ConsoleKey.DownArrow:  watchScroll++; break;
                    case ConsoleKey.PageUp:     watchScroll -= page; break;
                    case ConsoleKey.PageDown:   watchScroll += page; break;
                    case ConsoleKey.Home:       watchScroll = 0; break;
                    case ConsoleKey.End:        watchScroll = int.MaxValue; break;
                    case ConsoleKey.Q:          return 0; // quit
                    case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                        return 0; // Ctrl+C (intercepted as a key) also quits
                    default: repaint = false; break;
                }
                if (repaint)
                {
                    // Re-render (window may have changed if the terminal was resized).
                    lines = RenderDashboardLines(WatchStatus.Idle, idleTs, cycleDur);
                    watchScroll = PaintWatchViewport(lines, watchScroll);
                }
                continue; // drain remaining buffered keys promptly before sleeping
            }

            System.Threading.Thread.Sleep(40); // don't busy-spin at 100% CPU
        }
    }
    finally
    {
        foreach (var w in watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
        signal.Dispose();
    }
    if (!changed) return 0;
    PaintWatchRunning(); // flip the header to "⟳ running…" while the next cycle compiles
}
else
{
    // Non-interactive fallback: the existing plain line output. The WatchTests
    // integration test asserts on these exact markers — do not change them.
    Reporter.PrintPerTest(results, Console.Out, showPass);
    Reporter.PrintSummary(results, Console.Out);
    // The marker is printed from inside onArmed, which WatchSource invokes only
    // AFTER every FileSystemWatcher is live (#1822) — so it can never be a promise
    // the process has not yet kept. Flush before blocking: when stdout is a
    // pipe/file (a TUI front-end, VS Code, or a test harness) it is block-buffered,
    // so the cycle's results + this marker would otherwise sit unflushed for the
    // entire idle wait. A TTY auto-flushes, but piped consumers must see each cycle
    // as it completes.
    if (!WatchSource.WaitForSourceChange(bundles, onArmed: () =>
        {
            Console.WriteLine("[watch] waiting for AL source changes… (Ctrl+C to quit)");
            Console.Out.Flush();
        }))
        return 0;
    Console.WriteLine("[watch] change detected — re-running…");
    Console.Out.Flush();
}
watchCycleIndex++; // the cycle that just finished is no longer "the first cycle"
} // end while(true) watch loop

// ── Count-baseline check (issue #1880) ──────────────────────────────────────────────
// Runs once, after every bundle has finished, against the FULL `results` list — same
// timing as the exit-code computation right below, which it feeds into. See
// AlRunner/Infrastructure/CountBaseline.cs for the schema/semantics. This is an EXACT
// match, not a floor: a mismatch in EITHER direction fails the run (PR #1882 review —
// a "growth never fails" rule lets the baseline go stale on a passing run, and a
// later real drop can then land above the stale number unnoticed).
bool countBaselineMismatch = false;
if (countBaseline != null)
{
    // A --test/--filter narrows scope ON PURPOSE (e.g. the xmlport-isolation CI leg
    // runs the SAME al-language root with --test "Codeunit6020"), so a baseline sized
    // for the full suite must not fire here. Loud, not silent: anyone who passes both
    // flags together sees exactly why the guard stood down.
    if (testFilter != null)
    {
        Console.Error.WriteLine(
            $"[count-baseline] skipped: --test '{testFilter}' narrows scope intentionally.");
    }
    else
    {
        var actualBySuite = new Dictionary<string, AlRunner.Infrastructure.SuiteCountActual>();
        foreach (var b in results)
        {
            var suiteKey = Path.GetFileName(b.BucketPath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var testCount = b.Tests.Count;
            var groupCount = b.RanGroupCount;
            if (actualBySuite.TryGetValue(suiteKey, out var prior))
                actualBySuite[suiteKey] = new AlRunner.Infrastructure.SuiteCountActual(
                    prior.Tests + testCount, prior.AppGroups + groupCount);
            else
                actualBySuite[suiteKey] = new AlRunner.Infrastructure.SuiteCountActual(testCount, groupCount);
        }

        var selectedVersion = AlRunner.Infrastructure.BcArtifacts.SelectedVersion;
        var bcVersionKey = $"{selectedVersion.Major}.{selectedVersion.Minor}";

        var (drops, growths) = AlRunner.Infrastructure.CountBaselineCheck.Evaluate(
            countBaseline, actualBySuite, bcVersionKey);

        // BucketResult.RanGroupCount means app groups in bundled mode but SUITES under
        // --per-suite (see Reporter.cs), so an `appGroups` baseline recorded against
        // one mode is not a meaningful number in the other. Stand down just that
        // metric — loudly, same shape as the --test stand-down above — rather than
        // silently comparing suite-count-as-if-it-were-app-group-count.
        if (!bundledMode)
        {
            var standDown = drops.Concat(growths).Where(f => f.Metric == "appGroups").ToList();
            if (standDown.Count > 0)
            {
                Console.Error.WriteLine(
                    "[count-baseline] appGroups check skipped: --per-suite changes what "
                    + "RanGroupCount counts (suites, not app groups) — an appGroups baseline "
                    + "is only valid for the mode it was recorded in.");
                drops = drops.Where(f => f.Metric != "appGroups").ToList();
                growths = growths.Where(f => f.Metric != "appGroups").ToList();
            }
        }

        // Growth is also a hard failure, not just a notice — see the header comment
        // above. The message still says "grew" (not "DROP") so the diagnostic tells
        // the reader which direction it needs to bump the baseline.
        if (growths.Count > 0)
        {
            countBaselineMismatch = true;
            foreach (var g in growths)
                Console.Error.WriteLine(
                    $"[count-baseline] GROWTH: {g} — grew past the baseline; "
                    + $"--count-baseline requires an exact match. Bump {countBaselinePath} in this PR.");
        }

        if (drops.Count > 0)
        {
            countBaselineMismatch = true;
            foreach (var d in drops)
                Console.Error.WriteLine(
                    $"[count-baseline] DROP: {d} — a bundle or app group may have silently "
                    + $"stopped being discovered/executed. See {countBaselinePath}.");
        }
    }
}

// Computed once regardless of --no-strict-exit: needed both as the process exit code
// and as the "exitCode" field in --output-json, which reports the real outcome even
// when the process itself exits 0 for JSON-only consumers.
int computedExitCode = 0;
{
    int failed = 0, errored = 0, compileFail = 0, execFail = 0;
    foreach (var b in results)
    {
        if (b.Stage == BucketStage.CompileFailed) { compileFail++; continue; }
        if (b.Stage == BucketStage.ExecuteFailed) { execFail++; continue; }
        // A bundle that RAN but lost whole suites still covers less than it declares, and
        // its surviving tests all pass by construction (the dropped ones contribute nothing).
        // Without this the run exits 0: bucket Stage stays Executed, so suite errors reached
        // neither branch above. Measured on the matrix — BC 27.0 ran 26 of ~76 runner-extras
        // tests with 16 suite errors and reported success; BC 28.0 ran 8 of 76 and exited
        // non-zero only because one survivor happened to fail. See loud-failures.md.
        // No `continue`: the bucket's real results still belong in the totals.
        if (b.CompileErrors.Count > 0) compileFail++;
        foreach (var t in b.Tests)
        {
            if (t.Outcome == TestOutcome.Fail) failed++;
            else if (t.Outcome == TestOutcome.Error) errored++;
        }
    }
    computedExitCode = compileFail > 0 ? 3       // compile errors
        : execFail > 0 ? 2                       // bucket-level execution error
        : (failed + errored > 0 ? 1               // at least one test failed
        : (countBaselineMismatch ? 4 : 0));      // #1880: suite's count didn't exactly match its baseline
}

if (outputJson)
{
    var json = Reporter.SerializeJsonOutput(results, computedExitCode);
    // Restore the real stdout (captured above) so this is the ONLY thing ever
    // written to it — every banner/progress line up to this point went to stderr
    // instead. See the redirect right after arg parsing for why.
    if (outputJsonStdout != null) Console.SetOut(outputJsonStdout);
    Console.WriteLine(json);
}
else
{
    Reporter.PrintPerTest(results, Console.Out, showPass);
    if (printClassification)
        Reporter.PrintFailureClassification(results, Console.Out);
    Reporter.PrintSummary(results, Console.Out);
}
if (tddMode)
{
    // issue #2001 acceptance criterion 8: print the members --tdd actually generated this
    // run — the API the implementing app still has to provide, derived from the tests
    // rather than written by hand. A symbol --tdd could not confidently infer (or that
    // resolved onto a precompiled dependency, out of scope) still falls through to
    // TddSupport's refuse path and shows up as a FAILED test above, never in this list —
    // this list is only what was actually inferred, generated, and recompiled clean.
    var tddOut = outputJson ? Console.Error : Console.Out;
    tddOut.WriteLine();
    if (allTddGeneratedMembers.Count == 0)
    {
        tddOut.WriteLine(
            "--tdd: no members were generated this run — every missing symbol was reported " +
            "as a failed test instead (see the FAILED test messages above for each missing " +
            "symbol).");
    }
    else
    {
        tddOut.WriteLine($"--tdd: generated {allTddGeneratedMembers.Count} member(s) this run:");
        foreach (var m in allTddGeneratedMembers)
            tddOut.WriteLine($"  {m.ObjectDisplayName}: {m.MemberKind} {m.Signature}");
    }
}
if (outPath != null)
{
    Reporter.WriteClassification(results, outPath);
    // In --output-json mode this must not land on stdout (it already printed the
    // JSON above and restored the real stdout writer) — route to stderr there.
    (outputJson ? Console.Error : Console.Out).WriteLine($"Classification → {outPath}");
}
if (outputJunitPath != null)
{
    JUnitReport.WriteJUnit(outputJunitPath, results);
    if (!outputJson) Console.WriteLine($"JUnit XML → {outputJunitPath}");
}
if (coverageEnabled)
{
    // Source map keyed by (AL object label, object id) → file path, scanned from the
    // same bundle roots the run compiled — see AlCoverageSourceMap. relativeTo the
    // working directory so cobertura's <source> (".") lines up with the filename
    // attributes, matching v1's convention.
    var coverageSourceMap = AlRunner.Infrastructure.AlCoverageSourceMap.Build(
        bundles, relativeTo: Directory.GetCurrentDirectory());
    var coverageStatements = AlRunner.Infrastructure.AlCoverageTracker.Collect(coverageSourceMap);
    var coverageFiles = AlRunner.Infrastructure.AlCoverageReport.WriteCobertura(
        coverageOutputPath, coverageStatements);
    var coverageOut = outputJson ? Console.Error : Console.Out;
    coverageOut.WriteLine();
    coverageOut.WriteLine(AlRunner.Infrastructure.AlCoverageReport.FormatConsoleTable(coverageFiles));
    coverageOut.WriteLine($"Cobertura → {coverageOutputPath}");
}

// Exit non-zero if anything failed — the default since the v2 cut, matching main/v1.
// --no-strict-exit restores the old always-0 behaviour for JSON-only consumers.
return strictExitCode ? computedExitCode : 0;
    // Runs every bundle in sourcePaths in order and returns one ServerRunResult per
    // bundle. Restores v1's "honour every sourcePaths entry" behaviour (v1 fed them
    // all into a single compile; v2 keeps one bundle = one compile, so it runs each
    // sequentially instead — the same shape the CLI already uses for multiple
    // <bundle-dir> arguments). See #1658: honouring only sourcePaths[0] silently
    // dropped the rest, returning a green empty result for an app + separate
    // test-app request.
    //
    // When more than one path is given, first wire any inter-bundle dependency (the
    // app + test-app shape --guide recommends) the same way the CLI does before its
    // per-bundle loop: compile whichever bundle a sibling bundle depends on into a
    // package cache the sibling can resolve against.
    //
    // `cancellationToken` (default: none, for the `execute` caller which has no
    // active-run CTS) is checked BETWEEN bundles: a `cancel` landing while bundle 1
    // of a multi-bundle runTests request is still running must stop bundle 2 from
    // ever starting, not just stop mid-bundle-1 (that half is TestExecutor.Run's job).
    List<ServerRunResult> RunAllBundlesForServer(string[] sourcePaths, string[]? requestPackagePaths,
        Func<Assembly, IReadOnlyList<TestResult>> runStep,
        System.Threading.CancellationToken cancellationToken = default)
    {
        // Server requests share a process, so give each request the same fresh
        // NumberSequence lifetime as a standalone CLI/watch execution.
        AlRunner.Patches.NumberSequencePatches.ResetForNewExecution();

        // Drop the previous REQUEST's bundle-derived caches so a reloaded same-named
        // bundle resolves the freshly-emitted Record/Codeunit types and starts with
        // empty in-memory tables. Once per request, NOT per bundle: the CLI bucket
        // loop never resets between bundles, so AddSourceDir accumulates across an
        // app + test-app pair — resetting per bundle wiped the app bundle's parsed
        // table schemas before the test bundle ran, and every Record op on an
        // app-defined table died with "no NCLMetaTable for table N (AL source not
        // parsed)" while the identical CLI invocation passed.
        BcRuntime.ResetForNewBundleReload();

        // #2136, same defect as the CLI's positional arguments one call site over: a
        // `sourcePaths` array naming the same directory twice ran it twice and returned
        // two ServerRunResults, which HandleRunTests then SelectMany'd into a doubled
        // test list — the JSON-RPC shape of the exact count inflation the CLI had. Same
        // resolved-real-path rule, same notice, applied before the sourcePaths.Length > 1
        // branch below so a duplicated single path no longer drags a request through the
        // inter-bundle wiring pre-pass either. Server requests have no stderr contract of
        // their own, so the notice goes to the same place every other server-side
        // diagnostic does.
        {
            var deduped = AlRunner.Infrastructure.BundleRootDeduplication.Deduplicate(sourcePaths);
            var notice = AlRunner.Infrastructure.BundleRootDeduplication.DescribeDropped(deduped.Dropped);
            if (notice != null)
            {
                Console.Error.WriteLine(notice);
                sourcePaths = deduped.Roots.ToArray();
            }
        }

        if (sourcePaths.Length > 1)
        {
            var bundleList = sourcePaths.ToList();
            var workspaceScratch = new List<string>();
            try
            {
                packageCacheDirs = RunLayeredPrePass(bundleList, packageCacheDirs, workspaceScratch);
                packageCacheDirs = BuildSiblingSourceDeps(bundleList, packageCacheDirs, workspaceScratch);
            }
            catch (Exception ex)
            {
                // Loud per-bundle failure below (dep resolution during the per-bundle
                // compile) already covers the "can't resolve" case; a failure in the
                // wiring pre-pass itself must not silently fall back to unwired compiles.
                return new List<ServerRunResult>
                {
                    ServerRunResult.Failure(3, "<inter-bundle-deps>", $"LAYERED-PREPASS-FAIL: {ex.Message}", new())
                };
            }
        }

        var results = new List<ServerRunResult>(sourcePaths.Length);
        // #1888: open/close a phase-log bundle+app row per request bundle, mirroring
        // the CLI loop's BeginBundle/EndBundle. Before this the server path never
        // called into PhaseLog at all, so a --server process produced ZERO bundle/app
        // rows regardless of whether it exited cleanly — the Stage()/AppStage() marks
        // sprinkled through DependencyLoader/TestExecutor were silently no-ops the
        // whole time (AddStageTo/AddApp bail out when _bundle/_app is null). Unlike
        // the once-per-process row (written only from PhaseLog's ProcessExit hook,
        // see WriteProcessRecord), EndBundle appends its row IMMEDIATELY on return —
        // so as long as a request's bundle finishes before the process is later
        // killed (true for every server test: CliServer.DisposeAsync always Kill()s
        // AFTER the runTests round trip completes), this row survives the kill even
        // though the process-level row still does not. bundle_index restarts at 1 per
        // REQUEST (not per process lifetime) — server sessions have no single
        // "argument order" the way a CLI invocation does, and nothing downstream reads
        // it across requests.
        var bundleIndex = 0;
        foreach (var bundleDir in sourcePaths)
        {
            if (cancellationToken.IsCancellationRequested) break;
            bundleIndex++;
            var relBundle = Path.GetRelativePath(
                Environment.CurrentDirectory, Path.GetFullPath(bundleDir));
            AlRunner.Infrastructure.PhaseLog.BeginBundle(relBundle, bundleIndex);
            var result = RunBundleForServer(bundleDir, requestPackagePaths, runStep,
                out var emitElapsed, out var compileElapsed, out var runElapsed);
            AlRunner.Infrastructure.PhaseLog.EndBundle(emitElapsed, compileElapsed, runElapsed);
            results.Add(result);
        }
        return results;
    }

    // Compile + run one bundle, resetting bundle-derived caches first so an edited
    // same-identity bundle is picked up (server reload contract). Mirrors the
    // bundled-mode path of the normal run loop for a single bundle. The run step
    // (executor.Run for runTests, OnRun dispatch for execute) is supplied by the caller.
    ServerRunResult RunBundleForServer(string bundleDir, string[]? requestPackagePaths,
        Func<Assembly, IReadOnlyList<TestResult>> runStep,
        out TimeSpan emitElapsed, out TimeSpan compileElapsed, out TimeSpan runElapsed)
    {
        // #1888: defaulted here so every early-return path below (dep-resolve
        // failure, empty bundle, …) satisfies definite assignment without needing
        // its own assignment — those paths never opened an app row, so zero is the
        // honest answer for them, not a stand-in for a real measurement.
        emitElapsed = TimeSpan.Zero;
        compileElapsed = TimeSpan.Zero;
        runElapsed = TimeSpan.Zero;

        // Cache reset happens once per request in RunAllBundlesForServer, not here —
        // see the comment there for why per-bundle resetting breaks sibling bundles.

        var bundleAbs = Path.GetFullPath(bundleDir);
        var bucketRoot = FindBucketRoot(bundleAbs) ?? bundleAbs;

        // Request package paths augment the server's default caches.
        var effectivePkgDirs = (requestPackagePaths ?? Array.Empty<string>())
            .Where(Directory.Exists)
            .Concat(packageCacheDirs)
            .Distinct()
            .ToList();

        IReadOnlyList<(AlRunner.AppManifest Manifest, string AppPath)> ordered =
            Array.Empty<(AlRunner.AppManifest, string)>();
        var appJsonPath = Path.Combine(bucketRoot, "app.json");
        // Hoisted out of the `if (File.Exists(appJsonPath))` block below so the
        // cross-bundle module identity dedup (#1892) can read it after this block:
        // this bundle's OWN identity, used both to check whether an earlier bundle
        // in this request/session already loaded the same AppId, and to register
        // THIS bundle's freshly-compiled module under its AppId once loaded.
        AlRunner.Infrastructure.BundleIdentity? bundleId = null;
        if (File.Exists(appJsonPath))
        {
            try
            {
                var roots = ReadDependencies(appJsonPath);
                var bundlePkgDirs = Directory
                    .EnumerateDirectories(bucketRoot, ".alpackages", SearchOption.AllDirectories)
                    .ToList();
                var resolverDirs = bundlePkgDirs.Concat(effectivePkgDirs).Distinct().ToList();
                var resolver = new DependencyResolver(resolverDirs);
                ordered = resolver.Resolve(roots);
                AlRunner.Infrastructure.PhaseLog.NoteDepsResolved(ordered.Count);
                BcCompiler.SetResolvedDeps(ordered, resolverDirs);
                var loaded = depLoader.LoadAll(ordered, bucketRoot);
                AlRunner.Infrastructure.PhaseLog.NoteDepAssembliesLoaded(loaded.Count);
                // New bundle in the server session: replace (not inherit) the
                // install-trigger registrations, then register this bundle's deps.
                AlRunner.InstallTriggerRunner.ResetForNewBundle();
                AlRunner.InstallTriggerRunner.SetDependencyAssemblies(loaded);
                BcCompiler.SetResolvedDeps(ordered, resolverDirs);
                foreach (var (_, appPath) in ordered)
                    AlRunner.Patches.RecordPatches.AddBcAppPath(appPath);
                AlRunner.Patches.RecordPatches.RegisterBundleSymbolApps(bucketRoot);
                SetBundleInfoFromAppJson(appJsonPath);
                bundleId = AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(appJsonPath);
                if (bundleId != null)
                    BcCompiler.SetCurrentAppIdentity(bundleId.AppId, bundleId.Publisher, bundleId.Version);
                else
                    BcCompiler.SetCurrentAppIdentity(null, null, null);
            }
            catch (AlRunner.Infrastructure.DependencyLoadException ex)
            {
                return ServerRunResult.Failure(3, "<deps>", ex.Message, new());
            }
            catch (Exception ex)
            {
                return ServerRunResult.Failure(3, "<deps>", $"DEP-RESOLVE-FAIL: {ex.Message}", new());
            }
        }

        var suites = EnumerateSuites(bundleAbs).ToList();
        var allPaths = new List<string>();
        // Batched via AddSourceDirs (#1833) — see the register-source-dirs comment in the
        // non-server run loop above for why per-suite AddSourceDir calls were quadratic.
        var dirsToRegister = new List<string>();
        foreach (var suite in suites)
        {
            var s = Path.Combine(suite, "src");
            if (Directory.Exists(s)) dirsToRegister.Add(s);
            else if (!Directory.Exists(Path.Combine(suite, "test")))
                dirsToRegister.Add(suite);
            allPaths.AddRange(CollectSuitePaths(suite, bucketRoot));
        }
        AlRunner.Patches.RecordPatches.AddSourceDirs(dirsToRegister);
        allPaths = allPaths.Distinct().ToList();
        var fileHashes = ComputeServerFileHashes(allPaths);

        if (allPaths.Count == 0)
            return new ServerRunResult(Array.Empty<TestResult>(), 1, false, null, fileHashes);

        var moduleName = $"V2_{Path.GetFileName(bundleAbs)}";

        // ── cross-bundle module identity dedup (#1892, mirrors the CLI bundle
        // loop's own #1683 fix) ──────────────────────────────────────────────
        // RunAllBundlesForServer runs every sourcePaths entry through THIS method
        // in order, in the SAME process, sharing the SAME DependencyLoader. If an
        // earlier bundle in this request already compiled+loaded this bundle's
        // AppId — either as ITS OWN bundle (this same method, an earlier
        // iteration) or as a resolved dependency (DependencyLoader.LoadAll) —
        // reuse that exact Assembly instead of emitting+compiling a second,
        // distinct module for the same AL app identity. Without this, a sibling
        // bundle that declares a dependency on THIS bundle's app resolves it via
        // DependencyLoader's Tier-3 source-compile (a "Dep_..." module) BEFORE
        // this bundle's own iteration ever runs, or vice versa — either order
        // ends with two live modules for one AL identity, which is exactly the
        // TargetException at NavEventSubscription's ValidateInvokeTarget #1683
        // fixed for the CLI loop: a subscriber MethodInfo discovered from one
        // module's Type paired with a subscriberInstance BC's dispatcher
        // materialized from the OTHER module's Type.
        Assembly? reusedAsm = null;
        if (bundleId != null)
        {
            try
            {
                reusedAsm = DependencyLoader.TryGetByAppId(
                    bundleId.AppId, bundleId.Name, bundleId.Publisher,
                    bundleId.Version.ToString(), bundleAbs);
            }
            catch (AlRunner.Infrastructure.AppIdCollisionException ex)
            {
                // Two different apps declare the same app.json id (#1850) — never
                // silently reuse one app's module for the other's tests.
                return ServerRunResult.Failure(3, moduleName, $"FATAL: {ex.Message}", fileHashes);
            }
            if (reusedAsm != null)
                Console.Error.WriteLine(
                    $"  [server] {moduleName}: AppId {bundleId.AppId} already loaded earlier in " +
                    "this request/session — reusing that module instead of recompiling " +
                    "(see issue #1683/#1892).");
        }

        // #1888: one app row per server-mode module (this mode never groups several
        // AL apps into one bundled compile the way the CLI's bundled mode does, so
        // "1 of 1" is always correct here). EndApp in the finally below closes it on
        // EVERY exit path, including the many early `return`s below — matching
        // TestExecutor.EndApp's own idempotent-close contract.
        AlRunner.Infrastructure.PhaseLog.BeginApp(moduleName, 1, 1);
        try
        {
            // AL-output cache: HIT short-circuits Emit+Compile, like the normal loop.
            // Skipped entirely when reusedAsm is already set (see the cross-bundle
            // dedup check above) — nothing to cache-check, emit, or compile.
            byte[]? assemblyBytes = null;
            // A cross-bundle reuse (reusedAsm != null) is "cached" in the sense the
            // caller cares about — nothing changed for THIS bundle's contribution to
            // the request, exactly like an AL-output cache hit.
            bool cached = reusedAsm != null;
            string? cacheKey = null, cachePath = null, sidecarPath = null, querySidecarPath = null;
            // See AlCacheSidecars: a query bundle without its query-symbols sidecar must MISS.
            bool bundleDeclaresQuery = BcCompiler.BundleDeclaresQuery(allPaths);
            if (reusedAsm == null && alCacheDir != null)
            {
                cacheKey = ComputeAlCacheKey(allPaths, moduleName,
                    ordered: GetOrderedDepIds(bucketRoot, effectivePkgDirs), appRootDir: bucketRoot);
                cachePath = Path.Combine(alCacheDir, cacheKey + ".dll");
                sidecarPath = Path.Combine(alCacheDir, cacheKey + AlRunner.Infrastructure.AlCacheSidecars.EnumRegistrySuffix);
                querySidecarPath = Path.Combine(alCacheDir, cacheKey + AlRunner.Infrastructure.AlCacheSidecars.QuerySymbolsSuffix);
                if (AlRunner.Infrastructure.AlCacheSidecars.IsCompleteEntry(
                        File.Exists(cachePath), File.Exists(sidecarPath),
                        bundleDeclaresQuery, File.Exists(querySidecarPath)))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(cachePath);
                        // Same short-read defence as the CLI path above (issue #1810): a torn
                        // DLL is not a read error, so validate the PE image explicitly before
                        // trusting it.
                        AlRunner.Infrastructure.AlCacheSidecars.ValidateCachedAssemblyBytes(bytes, cachePath);
                        LoadEnumRegistrySidecar(sidecarPath);
                        if (bundleDeclaresQuery)
                            AlRunner.Patches.RecordPatches.RegisterBundleQuerySymbolsJson(querySidecarPath);
                        assemblyBytes = bytes;
                        cached = true;
                        AlRunner.Infrastructure.PhaseLog.NoteCacheHit();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  [cache] hit replay failed: {ex.Message} — rebuilding");
                        assemblyBytes = null;
                        cached = false;
                    }
                }
            }

            var compileErrors = new List<string>();
            if (reusedAsm == null && assemblyBytes == null)
            {
                if (alCacheDir != null)
                    AlRunner.Infrastructure.PhaseLog.NoteCacheMiss();
                IReadOnlyList<EmittedSource> sources;
                IReadOnlyList<string> alDiagnostics;
                IReadOnlyList<string> excludedObjects;
                var et = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var emitOutput = emitter.Emit(allPaths, moduleName, bucketRoot);
                    sources = emitOutput.Sources;
                    alDiagnostics = emitOutput.Diagnostics;
                    excludedObjects = emitOutput.ExcludedObjects;
                }
                catch (Exception ex)
                {
                    return ServerRunResult.Failure(3, moduleName, $"EMIT-FAIL: {ex.Message.Split('\n')[0]}", fileHashes);
                }
                finally
                {
                    et.Stop();
                    emitElapsed = et.Elapsed;
                    AlRunner.Infrastructure.PhaseLog.AddAppEmit(et.Elapsed);
                }
                // An emit-retry exclusion means one or more AL objects are NOT in the
                // compiled module, so any tests they declare silently vanish and the
                // request looks green. Fail loudly with the same classification the CLI's
                // bundled-mode EMIT-EXCLUDED guard uses (.claude/rules/loud-failures.md);
                // without this the server path ran the surviving objects and reported
                // exitCode 0 while e.g. a whole test codeunit was missing from the run.
                if (excludedObjects.Count > 0)
                {
                    var names = string.Join(", ", excludedObjects);
                    compileErrors.Add(
                        $"EMIT-EXCLUDED: {excludedObjects.Count} object(s) dropped from the module — " +
                        $"tests they declare are missing: [{names}]." +
                        (alDiagnostics.Count > 0
                            ? " The AL diagnostics that identified them follow."
                            : " Re-run with --verbose for the AL diagnostics that identified them."));
                    foreach (var d in alDiagnostics) compileErrors.Add(d);
                    return new ServerRunResult(Array.Empty<TestResult>(), 3, false,
                        new List<CompilationErrorGroup> { new(moduleName, compileErrors) }, fileHashes);
                }
                if (sources.Count == 0)
                {
                    foreach (var d in alDiagnostics) compileErrors.Add(d);
                    if (compileErrors.Count == 0) compileErrors.Add("EMIT-ZERO: 0 sources emitted");
                    return new ServerRunResult(Array.Empty<TestResult>(), 3, false,
                        new List<CompilationErrorGroup> { new(moduleName, compileErrors) }, fileHashes);
                }
                // AL-diagnostic compile-failure guard (#2150), extended to --server (#2152).
                // The most important of the three follow-up paths: this is what an editor
                // integration drives on every save, so a false green here reaches a
                // developer's inner loop with nothing telling them their AL would not build
                // against real BC. Same BC ContinueBuildOnError shape as bundled mode —
                // `sources` non-empty at the same time `alDiagnostics` is non-empty means a
                // real service tier would still refuse to publish this module. Surfaced over
                // the wire via the SAME compilationErrors/exitCode:3 convention every other
                // compile failure in this method already uses (EMIT-EXCLUDED, EMIT-ZERO,
                // COMPILE-FAIL just below) — there is no separate protocol shape to invent,
                // and the client already has to handle non-empty compilationErrors.
                if (alDiagnostics.Count > 0)
                {
                    Console.Error.WriteLine(
                        $"[server] {moduleName}: AL-DIAGNOSTIC-FAIL — {sources.Count} object(s) emitted but " +
                        $"{alDiagnostics.Count} AL error(s) were reported by BC's own compiler; a real " +
                        $"service tier would refuse to publish this module:");
                    foreach (var d in alDiagnostics)
                        Console.Error.WriteLine($"  {d}");
                    compileErrors.AddRange(alDiagnostics);
                    return new ServerRunResult(Array.Empty<TestResult>(), 3, false,
                        new List<CompilationErrorGroup> { new(moduleName, compileErrors) }, fileHashes);
                }
                var ct = System.Diagnostics.Stopwatch.StartNew();
                var compile = assembler.Compile(moduleName, sources);
                ct.Stop();
                compileElapsed = ct.Elapsed;
                AlRunner.Infrastructure.PhaseLog.AddAppCompile(ct.Elapsed);
                if (!compile.Success)
                {
                    compileErrors.AddRange(compile.Errors);
                    compileErrors.AddRange(alDiagnostics);
                    return new ServerRunResult(Array.Empty<TestResult>(), 3, false,
                        new List<CompilationErrorGroup> { new(moduleName, compileErrors) }, fileHashes);
                }
                assemblyBytes = compile.AssemblyBytes;
                if (cachePath != null && assemblyBytes != null)
                {
                    try
                    {
                        // Same atomic, sidecars-first-DLL-last publish as the CLI path above
                        // (issue #1810) — see the comment there for why the ordering matters.
                        AlRunner.Infrastructure.AlCacheWriter.AtomicPublish(
                            sidecarPath!, tmp => SaveEnumRegistrySidecar(tmp));
                        var qsrc = BcCompiler.LastBundleQuerySymbolsPath;
                        if (qsrc != null && File.Exists(qsrc))
                            AlRunner.Infrastructure.AlCacheWriter.AtomicPublish(
                                querySidecarPath!, tmp => File.Copy(qsrc, tmp, overwrite: true));
                        AlRunner.Infrastructure.AlCacheWriter.AtomicPublish(
                            cachePath, tmp => File.WriteAllBytes(tmp, assemblyBytes));
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"  [cache] write failed: {ex.Message}"); }
                }
            }

            Assembly asm;
            if (reusedAsm != null)
            {
                // See the cross-bundle module identity dedup comment above: this
                // bundle's AppId was already loaded earlier in this request/session,
                // so run with that exact Assembly instead of Assembly.Load-ing a
                // second, distinct module for the same AL identity.
                asm = reusedAsm;
            }
            else
            {
                if (assemblyBytes == null)
                    return ServerRunResult.Failure(2, moduleName, "no assembly produced", fileHashes);
                asm = Assembly.Load(assemblyBytes);
                // Register this freshly-loaded module under its AppId so a LATER
                // bundle in this request/session that resolves the same AppId —
                // either as its own bundle (this same method, a later iteration) or
                // as a dependency (DependencyLoader.LoadAll) — reuses this exact
                // Assembly instead of re-emitting/re-compiling a second module for
                // the same AL identity (#1892, mirrors the CLI loop's #1683 fix).
                if (bundleId != null)
                {
                    try
                    {
                        DependencyLoader.RegisterLoaded(
                            bundleId.AppId, asm, bundleId.Name, bundleId.Publisher,
                            bundleId.Version.ToString(), bundleAbs);
                    }
                    catch (AlRunner.Infrastructure.AppIdCollisionException ex)
                    {
                        // Same defence as the TryGetByAppId check above, for the (in
                        // this process, one request at a time) race window between
                        // that check and this registration — see loud-failures.md.
                        return ServerRunResult.Failure(3, moduleName, $"FATAL: {ex.Message}", fileHashes);
                    }
                }
            }

            IReadOnlyList<TestResult> tests;
            var rt = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                BcRuntime.SetTestAssembly(asm);
                BcRuntime.RegisterTestAssemblyInfo(asm);
                BcRuntime.OosHooksActive = true;
                tests = runStep(asm);
            }
            catch (Exception ex)
            {
                return ServerRunResult.Failure(2, moduleName, $"EXEC-FAIL: {ex.Message.Split('\n')[0]}", fileHashes);
            }
            finally
            {
                BcRuntime.OosHooksActive = false;
                rt.Stop();
                runElapsed = rt.Elapsed;
                AlRunner.Infrastructure.PhaseLog.AddAppRun(rt.Elapsed);
            }

            int exit = 0;
            if (tests.Any(t => t.Outcome == TestOutcome.Fail || t.Outcome == TestOutcome.Error)) exit = 1;
            return new ServerRunResult(tests, exit, cached, null, fileHashes);
        }
        finally
        {
            AlRunner.Infrastructure.PhaseLog.EndApp();
        }
    }


// ── --dap loop (issue #1642; stdio transport added for #2058) ────────────────────
// Non-static so it captures the warm pipeline objects (executor et al.) and
// RunAllBundlesForServer, same reasons as RunServerLoop below. Unlike --server this
// is not a warm-reload daemon: one client, one bundle, one run, then exit — a
// debug session is inherently single-shot (VS Code starts al-runner, debugs,
// disconnects, the process goes away).
//
// `stdioMode` selects the transport: stdio (stdioInput/stdioOutput, captured as raw
// OS handles before Log.Install — see the argument-parsing block above) or the
// original TCP accept loop. Everything from AlDapSession.Reset() onward is
// transport-agnostic and identical either way, matching DapTransport's own
// Stream-based design (its header comment: proven against a non-socket stream by
// AlRunner.Tests' in-memory-pipe harness well before this issue existed).
//
// Session shape (see docs/archive/dap.md for the mechanism this restores, and
// AlDapSession's file header for why pausing at StmtHit(N) — unlike
// --capture-values, #1640 — needs no Exit()-style redesign):
//   initialize     -> capabilities, then an `initialized` event
//   launch/attach  -> compiles the bundle SYNCHRONOUSLY (blocks the response until
//                     compiledTcs resolves or the whole run finishes without ever
//                     reaching runStep, i.e. a compile failure) so setBreakpoints
//                     right after has real statement indices to resolve against
//   setBreakpoints -> DapBreakpointResolver against the now-loaded scope types;
//                     REPLACES this source's previous set (DAP contract)
//   configurationDone -> releases the run-start gate; AL execution begins
//   (AlDapSession.Stopped fires on the AL thread when a breakpoint hits; this loop
//    pushes the "stopped" event the moment it fires — see the subscription below)
//   threads/stackTrace/scopes/variables -> read AlDapSession.PausedScope while paused
//   continue -> AlDapSession.Continue(); next/stepIn/stepOut -> AlDapSession.StepOver()/
//    StepIn()/StepOut() (issue #2045 — real step granularity, arms a depth-based
//    qualifying condition instead of releasing unconditionally; see AlDapSession's file
//    header for exactly what "qualifies" means for each)
//   disconnect/terminate -> AlDapSession.Detach() (never leaves the AL thread stuck)
int RunDapLoop(string bundleDir, int port, bool stdioMode, System.IO.Stream? stdioInput, System.IO.Stream? stdioOutput)
{
    System.Net.Sockets.TcpListener? listener = null;
    System.Net.Sockets.TcpClient? tcpClient = null;
    AlRunner.Infrastructure.DapTransport transport;
    if (stdioMode)
    {
        // Readiness signal for a stdio client: unlike the TCP branch below, there is
        // no "listening" state to report (stdin/stdout are already connected the
        // moment this process exists) — this just tells a human/log watcher the
        // session loop is about to start reading. Console.Error directly, not
        // Console.WriteLine: Console.Out is redirected to Console.Error already (see
        // the argument-parsing block above) so it would land in the same place
        // either way, but writing to Console.Error here documents the intent at the
        // call site rather than relying on the earlier redirect being remembered.
        Console.Error.WriteLine("[dap] stdio transport ready — waiting for a debug client to send 'initialize'...");
        transport = new AlRunner.Infrastructure.DapTransport(stdioInput!, stdioOutput!);
    }
    else
    {
        listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"[dap] listening on 127.0.0.1:{port} — waiting for a debug client to connect...");
        tcpClient = listener.AcceptTcpClient();
        listener.Stop();
        Console.WriteLine("[dap] client connected.");
        transport = new AlRunner.Infrastructure.DapTransport(tcpClient.GetStream(), tcpClient.GetStream());
    }
    using var transportDisposable = transport;
    using var tcpClientDisposable = tcpClient;
    AlRunner.Infrastructure.AlDapSession.Reset();

    Dictionary<(string Label, int Id), string> sourceMap = new();
    var lastFrames = new List<AlRunner.Infrastructure.AlDapFrame>();

    var compiledTcs = new System.Threading.Tasks.TaskCompletionSource<Assembly>(
        System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
    var configurationDoneGate = new System.Threading.SemaphoreSlim(0, 1);
    var cts = new System.Threading.CancellationTokenSource();

    Func<Assembly, IReadOnlyList<TestResult>> dapRunStep = asm =>
    {
        compiledTcs.TrySetResult(asm);
        configurationDoneGate.Wait(cts.Token);
        return executor.Run(asm, t =>
        {
            Console.WriteLine($"[dap] {t.Codeunit}.{t.Method}: {t.Outcome}");
            // issue #2045: a step armed for THIS test but never consumed (it ran to
            // completion without another qualifying StmtHit) must not leak into the
            // NEXT test — see AlDapSession.OnTestBoundary's doc comment.
            AlRunner.Infrastructure.AlDapSession.OnTestBoundary();
        }, cts.Token);
    };

    var bundleRunTask = System.Threading.Tasks.Task.Run(
        () => RunAllBundlesForServer(new[] { bundleDir }, null, dapRunStep, cts.Token));

    int exitCode = 0;
    bool terminatedSent = false;
    void SendTerminatedOnce()
    {
        if (terminatedSent) return;
        terminatedSent = true;
        transport.WriteEvent("terminated");
        transport.WriteEvent("exited", new { exitCode });
    }
    // Reports the run's outcome the moment it finishes, on WHATEVER thread that is —
    // Stopped's handler below writes to `transport` from the AL execution thread
    // too, and DapTransport's write lock is what keeps those from interleaving.
    _ = bundleRunTask.ContinueWith(t =>
    {
        if (t.IsFaulted)
        {
            exitCode = 2;
        }
        else
        {
            var runs = t.Result;
            exitCode = runs.Count > 0 ? runs.Max(r => r.ExitCode) : 0;
        }
        SendTerminatedOnce();
    }, System.Threading.Tasks.TaskScheduler.Default);

    // Pushed synchronously on the AL EXECUTION thread by AlDapSession.OnStmtHit,
    // right before it blocks — see that method's doc comment. Must not throw. `reason`
    // is "breakpoint" or "step" (issue #2045), whichever condition actually caused
    // this particular pause.
    //
    // Issue #2070 root cause (found chasing a CI hang that survived the watchdog fix
    // AND ruled out client-side starvation via socket.Available/ThreadPool evidence —
    // see the PR discussion): this used to be `try { Walk(...); WriteEvent(...); }
    // catch { Console.Error.WriteLine(...); }` — if Walk threw, WriteEvent was never
    // reached, the catch swallowed the exception into a bare stderr line (invisible
    // whenever DapClient's now-fixed two-reader bug happened to steal it), and the
    // handler returned NORMALLY. OnStmtHit reads "the handler returned" as "the stop
    // was reported" and proceeds straight into gate.Wait() — a real AL execution
    // thread parked forever with NO "stopped" event ever sent, which is
    // indistinguishable from the outside (and from every trace this issue built before
    // this one) from "the step never fired" or "the client was never scheduled to
    // read it". Per .claude/rules/loud-failures.md: a handler that cannot report a
    // stop must never leave the client waiting with nothing sent. Walk failing now
    // degrades (empty frame list, line 0) rather than aborting the whole report, and
    // the client is told WHY via a DAP `output` event instead of silently getting
    // nothing — the session stays alive and the developer sees the cause instead of
    // an unexplained hang.
    AlRunner.Infrastructure.AlDapSession.Stopped += (scope, stmt, reason) =>
    {
        AlRunner.Infrastructure.AlDapSession.Trace(
            $"STOPPED-HANDLER enter scope={scope.GetType().Name} stmt={stmt} reason={reason}");
        var line = 0;
        Exception? walkError = null;
        try
        {
            lastFrames = AlRunner.Infrastructure.AlDapStackWalker.Walk(scope, stmt, sourceMap);
            line = lastFrames.Count > 0 ? lastFrames[0].Line : 0;
            AlRunner.Infrastructure.AlDapSession.Trace(
                $"STOPPED-HANDLER walk ok frames={lastFrames.Count} line={line}");
        }
        catch (Exception ex)
        {
            walkError = ex;
            lastFrames = new List<AlRunner.Infrastructure.AlDapFrame>();
            AlRunner.Infrastructure.AlDapSession.Trace(
                $"STOPPED-HANDLER walk THREW {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            transport.WriteEvent("stopped", new
            {
                reason,
                threadId = 1,
                allThreadsStopped = true,
                line,
            });
            AlRunner.Infrastructure.AlDapSession.Trace("STOPPED-HANDLER write-event(stopped) ok");
            if (walkError != null)
            {
                transport.WriteEvent("output", new
                {
                    category = "stderr",
                    output = $"[dap] failed to compute the stack frame for this stop " +
                             $"(reason={reason}, stmt={stmt}): {walkError.GetType().Name}: " +
                             $"{walkError.Message}\n",
                });
            }
        }
        catch (Exception ex)
        {
            AlRunner.Infrastructure.AlDapSession.Trace(
                $"STOPPED-HANDLER write-event THREW {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine($"[dap] failed to report a stop: {ex.Message}");
        }
    };

    try
    {
        while (true)
        {
            AlRunner.Infrastructure.DapIncomingMessage? msg;
            try { msg = transport.ReadMessageAsync().GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[dap] transport error: {ex.Message}");
                break;
            }
            if (msg == null) break; // client closed the connection

            var command = msg.Command ?? "";
            var args = msg.Arguments;
            try
            {
                switch (command)
                {
                    case "initialize":
                        transport.WriteResponse(msg.Seq, command, true, new
                        {
                            supportsConfigurationDoneRequest = true,
                        });
                        transport.WriteEvent("initialized");
                        break;

                    case "launch":
                    case "attach":
                    {
                        var winner = System.Threading.Tasks.Task.WhenAny(compiledTcs.Task, bundleRunTask)
                            .GetAwaiter().GetResult();
                        if (!ReferenceEquals(winner, compiledTcs.Task))
                        {
                            // The run finished (or failed) before ever reaching dapRunStep —
                            // a compile failure. Report it on the launch response rather than
                            // silently proceeding into a session that will never run anything.
                            var runs = bundleRunTask.Result;
                            var errMsg = runs
                                .SelectMany(r => r.CompileErrors ?? Array.Empty<CompilationErrorGroup>())
                                .SelectMany(g => g.Errors)
                                .FirstOrDefault() ?? "compile failed (no diagnostic captured)";
                            transport.WriteResponse(msg.Seq, command, false, message: errMsg);
                            break;
                        }
                        sourceMap = AlRunner.Infrastructure.AlCoverageSourceMap.Build(
                            new[] { bundleDir }, relativeTo: null);
                        transport.WriteResponse(msg.Seq, command, true);
                        break;
                    }

                    case "setBreakpoints":
                    {
                        if (args == null || !args.Value.TryGetProperty("source", out var srcEl) ||
                            !srcEl.TryGetProperty("path", out var pathEl) || pathEl.GetString() is not string srcPath)
                        {
                            transport.WriteResponse(msg.Seq, command, false, message: "setBreakpoints: missing source.path");
                            break;
                        }
                        var lines = new List<int>();
                        if (args.Value.TryGetProperty("breakpoints", out var bpsEl) && bpsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var bp in bpsEl.EnumerateArray())
                                if (bp.TryGetProperty("line", out var lineEl)) lines.Add(lineEl.GetInt32());
                        else if (args.Value.TryGetProperty("lines", out var legacyLinesEl) && legacyLinesEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var l in legacyLinesEl.EnumerateArray()) lines.Add(l.GetInt32());

                        var requests = lines.Select(l => new AlRunner.Infrastructure.DapBreakpointRequest(srcPath, l)).ToList();
                        var resolved = AlRunner.Infrastructure.DapBreakpointResolver.Resolve(requests, sourceMap);

                        var fullSrcPath = Path.GetFullPath(srcPath);
                        // Replace (not accumulate) — DAP's setBreakpoints contract: this
                        // request is the COMPLETE set for `source` from now on.
                        foreach (var rb in resolved)
                            if (rb.ScopeType != null) AlRunner.Infrastructure.AlDapSession.ClearBreakpoints(rb.ScopeType);
                        foreach (var rb in resolved)
                            if (rb.Verified && rb.ScopeType != null)
                                AlRunner.Infrastructure.AlDapSession.SetBreakpoint(rb.ScopeType, rb.StatementIndex);

                        transport.WriteResponse(msg.Seq, command, true, new
                        {
                            breakpoints = resolved.Select((rb, idx) => new
                            {
                                id = idx,
                                verified = rb.Verified,
                                line = rb.Verified ? rb.ActualLine : rb.RequestedLine,
                            }),
                        });
                        break;
                    }

                    case "configurationDone":
                        transport.WriteResponse(msg.Seq, command, true);
                        AlRunner.Infrastructure.AlDapSession.Enabled = true;
                        configurationDoneGate.Release();
                        break;

                    case "threads":
                        transport.WriteResponse(msg.Seq, command, true, new
                        {
                            threads = new[] { new { id = 1, name = "AL Test Thread" } },
                        });
                        break;

                    case "stackTrace":
                        if (!AlRunner.Infrastructure.AlDapSession.IsPaused)
                        {
                            transport.WriteResponse(msg.Seq, command, false, message: "not stopped");
                            break;
                        }
                        transport.WriteResponse(msg.Seq, command, true, new
                        {
                            stackFrames = lastFrames.Select(f => new
                            {
                                id = f.Id,
                                name = f.ScopeName,
                                source = f.SourcePath != null ? new { path = f.SourcePath, name = Path.GetFileName(f.SourcePath) } : null,
                                line = f.Line,
                                column = 1,
                            }),
                            totalFrames = lastFrames.Count,
                        });
                        break;

                    case "scopes":
                    {
                        var frameId = args?.GetProperty("frameId").GetInt32() ?? -1;
                        transport.WriteResponse(msg.Seq, command, true, new
                        {
                            scopes = new[] { new { name = "Locals", variablesReference = frameId, expensive = false } },
                        });
                        break;
                    }

                    case "variables":
                    {
                        var varsRef = args?.GetProperty("variablesReference").GetInt32() ?? -1;
                        var frame = lastFrames.FirstOrDefault(f => f.Id == varsRef);
                        if (frame.Scope == null)
                        {
                            transport.WriteResponse(msg.Seq, command, false, message: $"unknown variablesReference {varsRef}");
                            break;
                        }
                        var locals = AlRunner.Infrastructure.AlScopeInspector.ReadLocals(frame.Scope);
                        transport.WriteResponse(msg.Seq, command, true, new
                        {
                            variables = locals.Select(v => new
                            {
                                name = v.Name,
                                value = v.Readable ? System.Text.Json.JsonSerializer.Serialize(v.Value) : (string)v.Value!,
                                variablesReference = 0,
                            }),
                        });
                        break;
                    }

                    case "continue":
                    case "pause":
                        AlRunner.Infrastructure.AlDapSession.Continue();
                        transport.WriteResponse(msg.Seq, command, true,
                            command == "continue" ? new { allThreadsContinued = true } : null);
                        break;

                    // issue #2045: real step granularity — each arms a depth-based
                    // qualifying condition (see AlDapSession's file header) instead of
                    // releasing unconditionally like "continue" above.
                    case "next":
                        AlRunner.Infrastructure.AlDapSession.StepOver();
                        transport.WriteResponse(msg.Seq, command, true);
                        break;

                    case "stepIn":
                        AlRunner.Infrastructure.AlDapSession.StepIn();
                        transport.WriteResponse(msg.Seq, command, true);
                        break;

                    case "stepOut":
                        AlRunner.Infrastructure.AlDapSession.StepOut();
                        transport.WriteResponse(msg.Seq, command, true);
                        break;

                    case "disconnect":
                    case "terminate":
                        AlRunner.Infrastructure.AlDapSession.Enabled = false;
                        AlRunner.Infrastructure.AlDapSession.Detach();
                        cts.Cancel();
                        transport.WriteResponse(msg.Seq, command, true);
                        SendTerminatedOnce();
                        return exitCode;

                    default:
                        transport.WriteResponse(msg.Seq, command, false, message: $"unsupported command: {command}");
                        break;
                }
            }
            catch (Exception ex)
            {
                transport.WriteResponse(msg.Seq, command, false, message: ex.Message);
            }
        }
    }
    finally
    {
        AlRunner.Infrastructure.AlDapSession.Enabled = false;
        AlRunner.Infrastructure.AlDapSession.Detach();
        cts.Cancel();
    }
    return exitCode;
}

// ── --server loop ──────────────────────────────────────────────────────────────
// Non-static so it captures the warm pipeline objects (emitter/assembler/executor/
// depLoader) and the resolved cache dirs established above. Reads newline-delimited
// JSON requests from stdin, writes one JSON response line per request to stdout.
// Protocol shape matches v1 (see ServerProtocol). Returns the process exit code.
//
// `cancel` (#1641/v1 #1613-#1614) needs a stdin-reader thread: without one, this
// loop is fully synchronous — it blocks in ReadLine() while a `runtests` request
// streams, so a `cancel` sitting on stdin is not even READ until the run finishes,
// let alone acted on. A dedicated background thread keeps reading stdin the whole
// time; it recognises `cancel` itself and answers it immediately as a side channel
// (bypassing the normal one-line-processed-at-a-time queue entirely), while every
// other command still goes through `mainQueue` and is processed sequentially by
// this method exactly as before. See `outputLock`/`activeRunCts` below.
int RunServerLoop(System.IO.TextReader input, System.IO.TextWriter output)
{
    // Per-session memory of the last served request's .al file hashes, so a cache
    // miss can report which files changed (v1 `changedFiles`).
    Dictionary<string, string>? lastFileHashes = null;

    // Guards every write to `output`: the reader thread's cancel-ack and this
    // method's normal command responses / streaming runtests output are now genuine
    // concurrent writers to the same stream once a runtests request is streaming.
    var outputLock = new object();

    // CancellationTokenSource for the currently-active `runtests` request, or null
    // when none is running. Written (via Interlocked) at the start/end of
    // HandleServerRunTests on THIS (main dispatch) thread; read (via Interlocked,
    // for an atomic reference snapshot) from the READER thread when a `cancel`
    // command arrives. No `lock` needed — CancellationTokenSource.Cancel() is
    // itself thread-safe, and Interlocked.CompareExchange gives an atomic
    // snapshot-or-null read/write of the reference without one.
    System.Threading.CancellationTokenSource? activeRunCts = null;

    // The side-channel command set: recognised and answered by the reader thread
    // itself, never enqueued onto mainQueue. Currently only `cancel`.
    string? HandleSideChannelCommand(AlRunner.ServerRequest? req)
    {
        if (!string.Equals(req?.Command, "cancel", StringComparison.OrdinalIgnoreCase))
            return null;

        // Atomic snapshot read of the reference (see activeRunCts doc comment).
        var cts = System.Threading.Interlocked.CompareExchange(ref activeRunCts, null, null);
        if (cts == null || cts.IsCancellationRequested)
            return AlRunner.ServerProtocol.Ack("cancel", noop: true);
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Race: HandleServerRunTests's finally already disposed the CTS between
            // our snapshot and Cancel() — the request had already completed.
            return AlRunner.ServerProtocol.Ack("cancel", noop: true);
        }
        return AlRunner.ServerProtocol.Ack("cancel", noop: false);
    }

    // Producer: reads stdin continuously on a dedicated background thread so the
    // main dispatch loop below is never blocked from seeing a `cancel` by a
    // synchronous `runtests`/`execute` handler. Side-channel commands are answered
    // here directly; everything else is handed to `mainQueue` for the sequential
    // dispatch loop, unchanged from before this command existed.
    var mainQueue = new System.Collections.Concurrent.BlockingCollection<string>();
    var readerThread = new System.Threading.Thread(() =>
    {
        string? readerLine;
        while ((readerLine = input.ReadLine()) != null)
        {
            if (readerLine.Length == 0) continue;
            AlRunner.ServerRequest? parsed = null;
            try { parsed = AlRunner.ServerProtocol.Parse(readerLine); }
            catch { /* malformed JSON — let the main loop's existing catch report it */ }

            var sideChannelResponse = HandleSideChannelCommand(parsed);
            if (sideChannelResponse != null)
            {
                lock (outputLock)
                {
                    output.WriteLine(sideChannelResponse);
                    output.Flush();
                }
                continue;
            }
            mainQueue.Add(readerLine);
        }
        mainQueue.CompleteAdding();
    })
    { IsBackground = true, Name = "al-runner-server-stdin" };
    readerThread.Start();

    // The isolation mode in effect when the server started (CLI --isolation, or
    // TestIsolation.Codeunit if not given) — the fallback for any request that
    // doesn't carry its own `testIsolation` field. Captured once so a request that
    // DOES set testIsolation never leaks its mode onto a later request that
    // doesn't (see #1616 — the whole point is per-request control, not a sticky
    // session-wide override).
    var defaultServerIsolation = executor.Isolation;

    // Readiness handshake — MUST be the first line on stdout.
    lock (outputLock)
    {
        output.WriteLine("{\"ready\":true}");
        output.Flush();
    }

    // Sequential dispatch loop, unchanged in shape from before `cancel` existed —
    // it now consumes from `mainQueue` (fed by the reader thread above) instead of
    // calling input.ReadLine() itself, so a `cancel` sitting ahead of a `runtests`
    // line in the OS pipe buffer never gets stuck behind it.
    foreach (var line in mainQueue.GetConsumingEnumerable())
    {
        if (line.Length == 0) continue;
        // Null means "already fully written to output" — currently only the
        // streaming runTests path (see HandleServerRunTests below), which emits
        // its own {"type":"test"}* + {"type":"summary"} lines directly instead of
        // going through the single-response write below.
        string? response;
        bool shuttingDown = false;
        try
        {
            var req = AlRunner.ServerProtocol.Parse(line);
            switch (req?.Command?.ToLowerInvariant())
            {
                case null:
                    response = AlRunner.ServerProtocol.Error("Invalid request (missing 'command')");
                    break;
                case "runtests":
                    HandleServerRunTests(req, output);
                    response = null;
                    break;
                case "execute":
                    response = HandleServerExecute(req);
                    break;
                case "shutdown":
                    response = AlRunner.ServerProtocol.Shutdown();
                    shuttingDown = true;
                    break;
                default:
                    response = AlRunner.ServerProtocol.Error($"Unknown command: {req.Command}");
                    break;
            }
        }
        catch (Exception ex)
        {
            response = AlRunner.ServerProtocol.Error(ex.Message);
        }

        if (response != null)
        {
            lock (outputLock)
            {
                output.WriteLine(response);
                output.Flush();
            }
        }
        if (shuttingDown) return 0;
    }
    // EOF — client disconnected.
    return 0;

    // Sets executor.Isolation from req.TestIsolation (see #1616), falling back to
    // defaultServerIsolation when the request doesn't specify one. Returns an
    // error response string on an unrecognised mode, else null.
    string? ApplyRequestIsolation(AlRunner.ServerRequest req)
    {
        if (req.TestIsolation == null)
        {
            executor.Isolation = defaultServerIsolation;
            return null;
        }
        try
        {
            executor.Isolation = AlRunner.TestIsolationParser.Parse(req.TestIsolation);
            return null;
        }
        catch (ArgumentException ex)
        {
            return AlRunner.ServerProtocol.Error($"testIsolation: {ex.Message}");
        }
    }

    // ── runTests: re-emit + run every requested bundle in-process, STREAMING one
    // {"type":"test"} NDJSON line per completed test (via TestExecutor.Run's
    // onTestComplete hook) as it finishes, then exactly one terminal
    // {"type":"summary"} line once every bundle has run — protocol-v2
    // (protocol-v2.schema.json), see #1641. Writes directly to `output` rather
    // than returning a single response string, unlike every other command.
    //
    // Owns the CancellationTokenSource a concurrent `cancel` side-channel command
    // signals (see HandleSideChannelCommand above `activeRunCts`). Published to
    // `activeRunCts` for the WHOLE multi-bundle request, not per-bundle, so a
    // cancel arriving between two sourcePaths entries still takes effect on the
    // remaining bundles. Cooperative only (TestExecutor.Run's doc comment): a test
    // already in flight always finishes; cancellation stops the NEXT one.
    // ─────────────────────────────────────────────────────────────────────────
    void HandleServerRunTests(AlRunner.ServerRequest req, System.IO.TextWriter output)
    {
        // #1936: real wall-clock duration of THIS request (received → summary
        // written), for the `wallSeconds` field on the terminal summary line. Not
        // the process's total uptime — a warm server serves many requests, so
        // "since process start" is only meaningful for the very first one. Started
        // here (before the sourcePaths/isolation validation below) so it also
        // captures those cheap up-front checks, not just the run itself.
        var reqSw = System.Diagnostics.Stopwatch.StartNew();
        if (req.SourcePaths == null || req.SourcePaths.Length == 0)
        {
            lock (outputLock)
            {
                output.WriteLine(AlRunner.ServerProtocol.Error("sourcePaths is required"));
                output.Flush();
            }
            return;
        }

        foreach (var p in req.SourcePaths)
            if (!Directory.Exists(p))
            {
                lock (outputLock)
                {
                    output.WriteLine(AlRunner.ServerProtocol.Error($"bundle directory not found: {p}"));
                    output.Flush();
                }
                return;
            }

        var isolationError = ApplyRequestIsolation(req);
        if (isolationError != null)
        {
            lock (outputLock)
            {
                output.WriteLine(isolationError);
                output.Flush();
            }
            return;
        }

        // #2042: 'coverage:true' opts into per-statement hit counts + a position table
        // on the terminal summary line — reuses AlCoverageTracker's existing StmtHit
        // hook (#1922), same process-global-flag pattern as AlValueCapture.Enabled in
        // HandleServerExecute below. Reset() (not just Enabled=true) so a warm
        // server's hit counts from a PRIOR request never leak into this one — the
        // dictionary is process-global and this process outlives many requests.
        AlRunner.Infrastructure.AlCoverageTracker.Enabled = req.Coverage == true;
        if (req.Coverage == true) AlRunner.Infrastructure.AlCoverageTracker.Reset();
        // #2135: 'perTestCoverage:true' opts into the SAME per-statement hit counts,
        // grouped by the test whose window recorded them instead of summed over the
        // whole run — a SEPARATE flag/dictionary from 'coverage' above (see
        // AlCoverageTracker.PerTestEnabled's doc comment), so the two opt-ins are
        // priced independently and a caller can ask for either, both, or neither.
        AlRunner.Infrastructure.AlCoverageTracker.PerTestEnabled = req.PerTestCoverage == true;
        if (req.PerTestCoverage == true) AlRunner.Infrastructure.AlCoverageTracker.ResetPerTest();

        var cts = new System.Threading.CancellationTokenSource();
        System.Threading.Interlocked.Exchange(ref activeRunCts, cts);
        try
        {
            // Flushed after every line so a client watching stdout sees each test the
            // instant it finishes, not batched behind the whole bundle (or worse, every
            // bundle in a multi-sourcePaths request).
            void OnTestComplete(TestResult t)
            {
                lock (outputLock)
                {
                    output.WriteLine(AlRunner.ServerProtocol.TestEvent(t));
                    output.Flush();
                }
                // #1845: test-only barrier, no-op unless AL_RUNNER_TEST_BARRIER_DIR is
                // set on THIS process — see AlRunner.Infrastructure.TestBarrier's doc
                // comment. Called AFTER the write+flush above so a client observing the
                // `test` line is guaranteed the server has not yet started the next test.
                AlRunner.Infrastructure.TestBarrier.WaitForRelease();
            }

            var runs = RunAllBundlesForServer(req.SourcePaths, req.PackagePaths,
                asm => executor.Run(asm, OnTestComplete, cts.Token), cts.Token);

            var allTests = runs.SelectMany(r => r.Tests).ToList();
            var allCompileErrors = runs.SelectMany(r => r.CompileErrors ?? Array.Empty<CompilationErrorGroup>()).ToList();
            // Same priority as the CLI's computedExitCode: 3 (compile) > 2 (exec) > 1 (test fail) > 0.
            var exitCode = runs.Count > 0 ? runs.Max(r => r.ExitCode) : 0;
            var cached = runs.Count > 0 && runs.All(r => r.Cached);

            var combinedHashes = new Dictionary<string, string>();
            foreach (var r in runs)
                foreach (var kv in r.FileHashes)
                    combinedHashes[kv.Key] = kv.Value;

            // changedFiles is only meaningful on a cache miss (a hit means nothing changed).
            List<string>? changed = null;
            if (!cached)
                changed = DiffServerFiles(lastFileHashes, combinedHashes);
            lastFileHashes = combinedHashes;

            // Read BEFORE clearing activeRunCts below (still valid — not yet disposed).
            var cancelled = cts.Token.IsCancellationRequested;

            // #1809: clear activeRunCts BEFORE writing+flushing the summary line, not
            // after (the old code cleared it in `finally`, which runs AFTER this write).
            // The reader thread's `cancel` side channel (HandleSideChannelCommand,
            // above) only has something to observe once the client sends a `cancel`
            // request, and a well-behaved client can only do that once it has actually
            // read the summary line this method is about to emit. So clearing first
            // makes "cancel sent right after the summary" ALWAYS see activeRunCts
            // already null — by program order on this one thread, not by winning a race
            // against the reader thread. The old ordering left a real gap: the client
            // could read+flush-observe the summary and fire `cancel` before this
            // thread ever reached its `finally`, during which HandleSideChannelCommand
            // would still see the stale non-null cts and answer noop:false for a run
            // that had already finished — a bug the wider concurrency #1809 introduces
            // (more collections running at once → more scheduler contention → this
            // window widens) makes far more likely to actually land, not merely a
            // theoretical TOCTOU. See ServerCancelTests.Cancel_AfterRunTestsCompletes_IsNoop.
            System.Threading.Interlocked.CompareExchange(ref activeRunCts, null, cts);

            // #2042: built from sourcePaths (the SAME roots the run just compiled),
            // matching the CLI --coverage path's AlCoverageSourceMap.Build call —
            // scopes whose owning object isn't found here (framework/dependency
            // assemblies outside the bundle under test) are silently excluded, same
            // as --coverage. Only built when requested: reflection-scanning every
            // loaded assembly's types on every plain runTests call would be wasted
            // work for callers who never asked for it.
            IReadOnlyList<AlRunner.Infrastructure.AlCoverageTracker.AlStatementRecord>? statementTable = null;
            IReadOnlyDictionary<string, List<AlRunner.Infrastructure.AlCoverageTracker.AlStatementRecord>>? perTestStatementTable = null;
            if (req.Coverage == true || req.PerTestCoverage == true)
            {
                var covSourceMap = AlRunner.Infrastructure.AlCoverageSourceMap.Build(
                    req.SourcePaths, relativeTo: null);
                if (req.Coverage == true)
                    statementTable = AlRunner.Infrastructure.AlCoverageTracker.CollectStatementTable(covSourceMap);
                // #2135: independent of the aggregate table above — see
                // AlCoverageTracker.CollectPerTestStatementTable's doc comment.
                if (req.PerTestCoverage == true)
                    perTestStatementTable = AlRunner.Infrastructure.AlCoverageTracker.CollectPerTestStatementTable(covSourceMap);
            }

            lock (outputLock)
            {
                output.WriteLine(AlRunner.ServerProtocol.Summary(
                    allTests, exitCode, cached, changed,
                    allCompileErrors.Count > 0 ? allCompileErrors : null,
                    cancelled: cancelled, wallSeconds: reqSw.Elapsed.TotalSeconds,
                    statementTable: statementTable,
                    perTestStatementTable: perTestStatementTable));
                output.Flush();
            }
        }
        finally
        {
            // Scoped to THIS request only, same reasoning as HandleServerExecute's
            // AlValueCapture.Enabled reset below — a coverage:true request must never
            // leave hit-count tracking on for a later request that didn't ask for it.
            AlRunner.Infrastructure.AlCoverageTracker.Enabled = false;
            // #2135: same per-request scoping as Enabled above.
            AlRunner.Infrastructure.AlCoverageTracker.PerTestEnabled = false;
            // Belt-and-braces: reaches the same state as the explicit clear above on
            // every path, INCLUDING an exception thrown before that point (e.g. from
            // RunAllBundlesForServer) — a pathological caller must never be left with a
            // permanently-stuck activeRunCts pointing at a cts nothing will ever
            // complete. A no-op on the normal path (already null there).
            System.Threading.Interlocked.CompareExchange(ref activeRunCts, null, cts);
            cts.Dispose();
        }
    }

    // ── execute: run every requested bundle's first OnRun-bearing codeunit
    // (run-mode), aggregating the results. #1917: v1 also accepted an inline
    // `code` string — a temp single-file bundle is synthesised from it (see
    // SynthesizeInlineCodeBundle) and run through the SAME compile pipeline a
    // sourcePaths-based execute already uses (RunAllBundlesForServer →
    // RunBundleForServer → RunFirstCodeunitOnRun), rather than inventing a
    // second execution path. `captureValues` (#1640, second slice — --coverage
    // was the first, #1922) gates AlValueCapture.Enabled for the duration of
    // this call; RunFirstCodeunitOnRun resets+collects it per bundle.
    string HandleServerExecute(AlRunner.ServerRequest req)
    {
        string? scratchDir = null;
        string[] sourcePaths;
        if (!string.IsNullOrWhiteSpace(req.Code))
        {
            if (req.SourcePaths != null && req.SourcePaths.Length > 0)
                return AlRunner.ServerProtocol.Error(
                    "execute: 'code' and 'sourcePaths' are mutually exclusive — pass one or the other.");
            scratchDir = SynthesizeInlineCodeBundle(req.Code!);
            sourcePaths = new[] { scratchDir };
        }
        else
        {
            if (req.SourcePaths == null || req.SourcePaths.Length == 0)
                return AlRunner.ServerProtocol.Error("sourcePaths is required");
            foreach (var p in req.SourcePaths)
                if (!Directory.Exists(p))
                    return AlRunner.ServerProtocol.Error($"bundle directory not found: {p}");
            sourcePaths = req.SourcePaths;
        }

        var isolationError = ApplyRequestIsolation(req);
        if (isolationError != null) return isolationError;

        // Scoped to THIS request only — reset in `finally` below regardless of outcome,
        // so a captureValues:true request never leaves the flag on for a later request
        // that didn't ask for it (the flag is process-global, same as AlCoverageTracker.Enabled).
        AlRunner.Infrastructure.AlValueCapture.Enabled = req.CaptureValues == true;
        // #2042: 'coverage:true' on `execute` — same request/response correlation the
        // issue's acceptance criteria need: THIS single `execute` call can enable BOTH
        // captureValues AND coverage together, so a caller can prove statementId lines
        // up between capturedValues and the statement table from ONE run (see
        // AlStatementTableTests.CapturedValueStatementId_MatchesStatementTableScopeAndId).
        AlRunner.Infrastructure.AlCoverageTracker.Enabled = req.Coverage == true;
        if (req.Coverage == true) AlRunner.Infrastructure.AlCoverageTracker.Reset();
        // #2135: same per-test opt-in as HandleServerRunTests — see
        // AlCoverageTracker.PerTestEnabled's doc comment.
        AlRunner.Infrastructure.AlCoverageTracker.PerTestEnabled = req.PerTestCoverage == true;
        if (req.PerTestCoverage == true) AlRunner.Infrastructure.AlCoverageTracker.ResetPerTest();
        // #2117: Message() output — UNCONDITIONAL, not gated by a request field, matching
        // ServerProtocol's own long-standing doc comment for `execute`'s `messages`
        // (`messages|null` was documented before this field was ever populated). Reset
        // ONCE before the whole (possibly multi-bundle) run so messages from every bundle
        // land in ONE ordered list — see AlMessageCapture.Reset's doc comment for why
        // that differs from AlValueCapture's per-bundle scoping. ClientCallbackOverride
        // is installed on the skeleton session for the SAME reason AlValueCapture.Enabled
        // /AlCoverageTracker.Enabled are process-global flags reset in `finally` below: a
        // later request that isn't `execute` (e.g. `runTests`) must never see it — though
        // in practice nothing on the [Test]-procedure path would ever consult it (see
        // RunnerClientCallback.cs's header).
        AlRunner.Infrastructure.AlMessageCapture.Reset();
        var messageCaptureSession = AlRunner.BcRuntime.SkeletonSession as Microsoft.Dynamics.Nav.Runtime.NavSession;
        if (messageCaptureSession != null)
            messageCaptureSession.ClientCallbackOverride = new AlRunner.Patches.RunnerClientCallback();
        try
        {
            var runs = RunAllBundlesForServer(sourcePaths, req.PackagePaths, RunFirstCodeunitOnRun);

            var allTests = runs.SelectMany(r => r.Tests).ToList();
            var allCompileErrors = runs.SelectMany(r => r.CompileErrors ?? Array.Empty<CompilationErrorGroup>()).ToList();
            var exitCode = runs.Count > 0 ? runs.Max(r => r.ExitCode) : 0;

            var combinedHashes = new Dictionary<string, string>();
            foreach (var r in runs)
                foreach (var kv in r.FileHashes)
                    combinedHashes[kv.Key] = kv.Value;
            lastFileHashes = combinedHashes;

            // Built BEFORE returning (i.e. before `finally` deletes an inline-code
            // scratchDir below) — sourcePaths here is either the caller's real
            // sourcePaths or that same scratchDir, and AlCoverageSourceMap.Build
            // needs the .al files on disk to still exist when it scans them.
            IReadOnlyList<AlRunner.Infrastructure.AlCoverageTracker.AlStatementRecord>? statementTable = null;
            IReadOnlyDictionary<string, List<AlRunner.Infrastructure.AlCoverageTracker.AlStatementRecord>>? perTestStatementTable = null;
            if (req.Coverage == true || req.PerTestCoverage == true)
            {
                var covSourceMap = AlRunner.Infrastructure.AlCoverageSourceMap.Build(sourcePaths, relativeTo: null);
                if (req.Coverage == true)
                    statementTable = AlRunner.Infrastructure.AlCoverageTracker.CollectStatementTable(covSourceMap);
                if (req.PerTestCoverage == true)
                    perTestStatementTable = AlRunner.Infrastructure.AlCoverageTracker.CollectPerTestStatementTable(covSourceMap);
            }

            return AlRunner.ServerProtocol.Execute(allTests, exitCode,
                AlRunner.Infrastructure.AlMessageCapture.Snapshot(),
                allCompileErrors.Count > 0 ? allCompileErrors : null,
                statementTable: statementTable,
                perTestStatementTable: perTestStatementTable);
        }
        finally
        {
            AlRunner.Infrastructure.AlValueCapture.Enabled = false;
            AlRunner.Infrastructure.AlCoverageTracker.Enabled = false;
            AlRunner.Infrastructure.AlCoverageTracker.PerTestEnabled = false;
            if (messageCaptureSession != null) messageCaptureSession.ClientCallbackOverride = null;
            // Best-effort cleanup: the scratch dir's contents are fully consumed
            // once RunBundleForServer has emitted+compiled them into an in-memory
            // assembly (or failed trying) — nothing downstream needs the files on
            // disk after this call returns, and a leaked temp dir per `execute`
            // call would otherwise accumulate for the life of the server process.
            if (scratchDir != null)
            {
                try { Directory.Delete(scratchDir, recursive: true); }
                catch { /* not fatal — OS temp cleanup will catch it eventually */ }
            }
        }
    }

    // #1917: synthesise a temp single-file AL bundle from an inline `code`
    // string so `execute`'s "code" field can go through the same compile
    // pipeline as a sourcePaths-based execute, instead of a separate inline-AL
    // execution path. v1 parity (see git history for e1a22f84, "fixes #12"):
    // `code` that already looks like a full AL object definition is used
    // verbatim; anything else is treated as a bare statement list and wrapped
    // in a scratch codeunit's OnRun trigger body, matching v1's CLI `-e` shape.
    //
    // #1931: "already looks like a full AL object" used to be
    // `trimmed.StartsWith("codeunit"/"table")` — a two-keyword allowlist that
    // misclassified every other object type (page/enum/report/query/xmlport/
    // interface/...) AND any codeunit behind a leading `//` comment (TrimStart
    // leaves the `//` in place, so it never matched). See IsFullAlObjectDeclaration
    // for the fix: ask BC's own parser instead of maintaining a keyword list.
    static string SynthesizeInlineCodeBundle(string code)
    {
        var isFullObject = IsFullAlObjectDeclaration(code);
        var source = isFullObject
            ? code
            : $"codeunit 50100 \"AL Runner Inline Execute\" {{ trigger OnRun() begin {code} end; }}";

        var dir = Path.Combine(Path.GetTempPath(), "al-runner-server-inline", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Scratch.al"), source);
        return dir;
    }

    // #1931: is `code` already a full AL object declaration (should be used
    // verbatim), or a bare statement list (needs wrapping in a scratch OnRun
    // body)? Answered by asking BC's OWN parser rather than maintaining a
    // keyword allowlist that drifts as AL gains object types — the same
    // approach RecordPatches.AlSourceParser.ParseAlObjects already uses for
    // table/tableextension extraction (#1696). SyntaxTree.ParseObjectText needs
    // only a ParseOptions, no Compilation and no reference closure, so this is
    // cheap and side-effect-free.
    //
    // Every top-level AL object syntax type shares one common base,
    // Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.ObjectSyntax — verified via
    // reflection over the shipped CodeAnalysis DLL: table/codeunit/page/report/
    // query/xmlport/enum/(+ extension variants) all derive from
    // ApplicationObjectSyntax : ObjectSyntax, while interface/controladdin/
    // profile/dotnet/entitlement derive from ObjectSyntax directly (they have no
    // object id, so they don't go through ApplicationObjectSyntax) — so "did the
    // compilation-unit root produce at least one ObjectSyntax child" answers
    // "is this a full object declaration" for the whole AL object-keyword set at
    // once, with no list to keep in sync.
    //
    // Leading trivia (a `//` comment, a blank line, a `#pragma`) needs no manual
    // skipping: comments/blank lines are trivia the parser already attaches to
    // the first real token when it scans for the object keyword, so a
    // `//`-prefixed codeunit still yields a CodeunitSyntax child.
    //
    // A malformed-but-recognisable object (e.g. `codeunit 50100 "X" { trigger
    // OnRun() begin Error(` with an unclosed paren) still parses to exactly one
    // ObjectSyntax child — BC's parser recovers past the syntax error and still
    // recognises the object shape — so it is STILL used verbatim. That is
    // deliberate: the caller's real compile error then names the caller's real
    // code (via the normal `compilationErrors` channel `execute` already
    // returns), not a wrapper the caller never wrote. A genuine bare statement
    // list, or text that isn't AL at all, produces zero children (BC's parser
    // reports AL0198 "expected one of the application object keywords" and
    // recovers to an empty compilation unit) and falls through to wrapping.
    //
    // Never throws: this is fed arbitrary text a human may have typed by hand,
    // and a parse ParseObjectText itself cannot make sense of must fall back to
    // "not a full object" (wrap it) rather than blow up the request — the same
    // never-throw contract RecordPatches.AlSourceParser.ParseAlObjects documents
    // for the identical call.
    //
    // Classification is deterministic (yes/no), never ambiguous, so there is no
    // third "couldn't tell" state to surface as a request-level protocol error:
    // whichever branch is chosen, a real problem in the caller's AL still comes
    // back through the existing `compilationErrors` channel that
    // Execute_InlineCode_CompileError_ReturnsCompilationErrors already proves —
    // exactly where every other AL-content problem in this protocol surfaces.
    // The top-level `error` field stays reserved for request-shape problems
    // (unknown command, missing sourcePaths, mutually exclusive fields) that
    // have nothing to do with what the AL says.
    static bool IsFullAlObjectDeclaration(string code)
    {
        try
        {
            var parseOpts = new NavCA.ParseOptions(
                runtimeVersion: null!,
                preprocessorSymbols: Enumerable.Range(1, 25).Select(n => $"CLEANSCHEMA{n}")
                    .Concat(AlRunner.BcCompiler.GetExtraPreprocessorSymbols()),
                documentationMode: NavCA.DocumentationMode.None);
            var tree = NavSyntax.SyntaxTree.ParseObjectText(code, path: "", encoding: null!, parseOpts, default);
            return tree.GetRoot() is NavSyntax.CompilationUnitSyntax root &&
                   root.ChildNodes().Any(n => n is NavSyntax.ObjectSyntax);
        }
        catch
        {
            return false;
        }
    }


    // Run the bundle's OnRun-bearing codeunit (run-mode), mirroring CodeunitPatches'
    // OnRun dispatch. Prefers a non-[Test] codeunit; returns one TestResult named
    // "<Codeunit>.OnRun". An AL Error inside OnRun surfaces as a Fail (exitCode 1).
    IReadOnlyList<TestResult> RunFirstCodeunitOnRun(Assembly asm)
    {
        var navCodeunit = typeof(Microsoft.Dynamics.Nav.Runtime.NavCodeunit);
        Type? target = null;
        foreach (var t in asm.GetTypes())
        {
            if (!t.Name.StartsWith("Codeunit", StringComparison.Ordinal)) continue;
            if (!navCodeunit.IsAssignableFrom(t)) continue;
            var onRun = t.GetMethod("OnRun",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                new[] { typeof(Microsoft.Dynamics.Nav.Runtime.INavRecordHandle) }, null)
                ?? t.GetMethod("OnRun",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                    Type.EmptyTypes, null);
            if (onRun == null) continue;
            // Prefer a non-test codeunit; remember the first match and keep looking
            // for a non-test one.
            bool isTest = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Any(m => m.GetCustomAttributes(false).Any(a => a.GetType().Name is "NavTestAttribute" or "TestAttribute"));
            if (!isTest) { target = t; break; }
            target ??= t;
        }
        if (target == null)
            return new[] { new TestResult("<execute>", "OnRun", TestOutcome.Error,
                "no codeunit with an OnRun trigger found in the bundle", null, TimeSpan.Zero) };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        AlRunner.Infrastructure.AlCallStackCapture.Clear();
        // #1640: only meaningfully non-null when the caller enabled
        // AlValueCapture (HandleServerExecute, gated by req.CaptureValues). Reset
        // BEFORE invoking, mirroring AlCallStackCapture.Clear() above — same
        // process-global, sequential-invocation assumption.
        AlRunner.Infrastructure.AlValueCapture.Reset();
        IReadOnlyList<AlRunner.Infrastructure.AlCapturedValue>? Captured() =>
            AlRunner.Infrastructure.AlValueCapture.Enabled
                ? AlRunner.Infrastructure.AlValueCapture.Collect()
                : null;
        // #2135: same test-window bracket TestExecutor.RunOne uses for [Test]
        // procedures, applied to `execute`'s single OnRun invocation — the key
        // matches the SAME "{Codeunit}.{Method}" shape (target.Name is the .NET type
        // name TestResult's own Codeunit field already carries).
        AlRunner.Infrastructure.AlCoverageTracker.BeginTest($"{target.Name}.OnRun");
        try
        {
            var ctor = target.GetConstructors().FirstOrDefault(c =>
                c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType.Name == "ITreeObject");
            if (ctor == null)
                return new[] { new TestResult(target.Name, "OnRun", TestOutcome.Error,
                    "codeunit has no ITreeObject constructor", null, sw.Elapsed) };
            var instance = ctor.Invoke(new object[] { BcRuntime.RootTreeStub! });
            var onRun = target.GetMethod("OnRun",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                new[] { typeof(Microsoft.Dynamics.Nav.Runtime.INavRecordHandle) }, null);
            if (onRun != null) onRun.Invoke(instance, new object?[] { null });
            else target.GetMethod("OnRun",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                Type.EmptyTypes, null)!.Invoke(instance, null);
            return new[] { new TestResult(target.Name, "OnRun", TestOutcome.Pass, null, null, sw.Elapsed,
                CapturedValues: Captured()) };
        }
        catch (System.Reflection.TargetInvocationException tex)
        {
            var inner = tex.InnerException ?? tex;
            var alStack = AlRunner.Infrastructure.AlCallStackCapture.GetCaptured(inner);
            return new[] { new TestResult(target.Name, "OnRun", TestOutcome.Fail,
                $"{inner.GetType().Name}: {inner.Message}", inner.ToString(), sw.Elapsed, alStack,
                CapturedValues: Captured()) };
        }
        catch (Exception ex)
        {
            return new[] { new TestResult(target.Name, "OnRun", TestOutcome.Error,
                ex.Message, ex.ToString(), sw.Elapsed, CapturedValues: Captured()) };
        }
        finally
        {
            AlRunner.Infrastructure.AlCoverageTracker.EndTest();
        }
    }
}

// ── Helpers ──────────────────────────────────────────────────────────────────

// SHA-256 each .al file reachable from the given folders → path→hash map, for the
// server's changedFiles diff.
static Dictionary<string, string> ComputeServerFileHashes(IReadOnlyList<string> folders)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    using var sha = System.Security.Cryptography.SHA256.Create();
    foreach (var f in folders
        .Where(Directory.Exists)
        .SelectMany(d => Directory.EnumerateFiles(Path.GetFullPath(d), "*.al", SearchOption.AllDirectories))
        .Distinct())
    {
        try
        {
            using var fs = File.OpenRead(f);
            map[f] = Convert.ToHexString(sha.ComputeHash(fs));
        }
        catch { /* unreadable file — omit from the diff */ }
    }
    return map;
}

// Files added/removed/modified between the previously served request and this one.
static List<string> DiffServerFiles(Dictionary<string, string>? prev, Dictionary<string, string> cur)
{
    if (prev == null)
        return cur.Keys.Select(p => Path.GetFileName(p) ?? p).ToList();
    var changed = new List<string>();
    foreach (var kv in cur)
        if (!prev.TryGetValue(kv.Key, out var h) || h != kv.Value)
            changed.Add(Path.GetFileName(kv.Key) ?? kv.Key);
    foreach (var kv in prev)
        if (!cur.ContainsKey(kv.Key))
            changed.Add(Path.GetFileName(kv.Key) ?? kv.Key);
    return changed;
}

// ── --watch helpers ───────────────────────────────────────────────────────────
// WaitForSourceChange / ArmSourceWatch moved to AlRunner.WatchSource (see #1822):
// local functions declared here cannot be unit-tested, and the arm-before-announce
// ordering contract needed a deterministic test.

// #2151 removed the AL1081 carve-out that used to live here (IsKnownLayoutPathResolution-
// Bug / ClassifyBlockingAlDiagnostics): the runner's Tier-3 source compile now resolves a
// report's file-relative LayoutFile against the declaring .al file's own directory (see
// ReportLayoutFileSystem), matching real BC, so the six al-language corpus reports that
// needed tolerating compile clean and every AL-diagnostic compile-failure guard below can
// go back to "any Error-severity AL diagnostic blocks", with no per-error-code exception.

static void DumpCsharpSources(string dir, string moduleName, IReadOnlyList<EmittedSource> sources)
{
    var bundleDir = Path.Combine(dir, SanitiseFilename(moduleName));
    Directory.CreateDirectory(bundleDir);
    int written = 0;
    foreach (var src in sources)
    {
        var name = SanitiseFilename(src.Name) + ".cs";
        File.WriteAllText(Path.Combine(bundleDir, name), src.Code);
        written++;
    }
    Console.WriteLine($"  [--dump-csharp] wrote {written} .cs file(s) to {bundleDir}");
}

static string SanitiseFilename(string name)
{
    var invalid = Path.GetInvalidFileNameChars();
    var sb = new System.Text.StringBuilder(name.Length);
    foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
    return sb.ToString();
}

// ── --guide ───────────────────────────────────────────────────────────────────
// Written for an automated caller that has the binary and nothing else: no repo
// checkout, no handoff notes, no prior session. It answers, in order, the four
// questions such a caller gets wrong: what this runner can actually do, how to
// invoke it against a real app, where dependencies come from, and what the
// common failure signatures mean. Keep it self-contained — the moment an agent
// has to go read docs/ or a HANDOFF file to form a correct invocation, this
// guide has failed at its job.
// Shared by --version/-v/-V/version and --help's first line, so a build's
// self-reported version can never drift between the two surfaces (#2072).
static string VersionString()
{
    var asmVer = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
    return $"al-runner v{asmVer}";
}

static void PrintGuide(TextWriter w)
{
    w.WriteLine("al-runner — AGENT GUIDE");
    w.WriteLine("Operating manual for automated callers. For the flag reference, use --help.");
    w.WriteLine();

    w.WriteLine("CAPABILITY — read this before diagnosing any failure");
    w.WriteLine("  al-runner executes complete AL test suites in-process: it compiles AL from");
    w.WriteLine("  source and/or loads precompiled .app packages, applies its BC runtime patches,");
    w.WriteLine("  and runs every [Test] method. It routinely runs suites in the 1,000+ test");
    w.WriteLine("  range (the al-language corpus and large third-party suites) against BC 28.x.");
    w.WriteLine();
    w.WriteLine("  Therefore: if a run does not produce test results, the overwhelmingly likely");
    w.WriteLine("  cause is a BAD INVOCATION or an INCOMPLETE DEPENDENCY SET — not a missing");
    w.WriteLine("  runner capability. Do NOT report \"al-runner cannot run this suite\" unless the");
    w.WriteLine("  limitation is named in docs/limitations.md. A clean compile does NOT prove the");
    w.WriteLine("  dependency set is runnable (see DEPENDENCIES below); it proves only that");
    w.WriteLine("  symbols resolved.");
    w.WriteLine();

    w.WriteLine("INVOCATION — the shortest correct command lines");
    w.WriteLine("  Run one bundle (a dir containing app.json, or any dir below one):");
    w.WriteLine("    al-runner <bundle-dir>");
    w.WriteLine();
    w.WriteLine("  Run an app together with its separate test app — the usual real-world shape.");
    w.WriteLine("  Pass BOTH dirs; they run sequentially and aggregate into one summary:");
    w.WriteLine("    al-runner --package-cache <deps-dir> MyApp MyApp.Test");
    w.WriteLine();
    w.WriteLine("  Pin the BC version explicitly whenever the machine has more than one:");
    w.WriteLine("    al-runner --bc-version 28.2 --package-cache <deps-dir> MyApp MyApp.Test");
    w.WriteLine();
    w.WriteLine("  Narrow to one test while debugging (substring match on Codeunit.Method):");
    w.WriteLine("    al-runner --test MyFeature_Posts_Correctly MyApp.Test");
    w.WriteLine();
    w.WriteLine("  Machine-readable outcome for a CI gate or a scripted caller:");
    w.WriteLine("    al-runner --quiet --out results.json MyApp.Test");
    w.WriteLine("    exit 0 = all passed | 1 = a test failed | 2 = could not execute | 3 = could not compile");
    w.WriteLine("    Pass --no-strict-exit to always exit 0 and parse the JSON regardless of outcome.");
    w.WriteLine();
    w.WriteLine("  Some apps require AL preprocessor symbols to compile outside their normal");
    w.WriteLine("  environment (key-vault bypasses, local-dev switches). Check the app's own");
    w.WriteLine("  documentation for these — omitting a required one produces real, correct");
    w.WriteLine("  test failures that look like runner bugs:");
    w.WriteLine("    al-runner --define SOME_LOCAL_DEV MyApp MyApp.Test");
    w.WriteLine();

    w.WriteLine("DEPENDENCIES — where packages come from, and the trap");
    w.WriteLine("  Dependencies declared in app.json are resolved, in order, from:");
    w.WriteLine("    1. every --package-cache DIR you pass (repeatable)");
    w.WriteLine("    2. the bundle's own .alpackages/");
    w.WriteLine("    3. ~/.bcartifacts.cache");
    w.WriteLine("    4. ~/.local/share/al-runner/artifacts   (the BC artifact cache)");
    w.WriteLine();
    w.WriteLine("  THE TRAP: a .app package may carry SYMBOLS ONLY (type/method signatures, no");
    w.WriteLine("  compiled bodies) or it may be CODE-BEARING. The AL compiler needs only symbols,");
    w.WriteLine("  so a symbols-only dependency compiles perfectly cleanly and then fails at");
    w.WriteLine("  RUNTIME the moment its code is called. \"0 compile errors\" is therefore NOT");
    w.WriteLine("  evidence that the dependency set is correct.");
    w.WriteLine();
    w.WriteLine("  HOW THE WINNER IS PICKED — and how NOT to check it. Resolution takes the");
    w.WriteLine("  HIGHEST VERSION of each package across ALL scanned directories combined. So a");
    w.WriteLine("  higher-versioned symbols-only copy outranks the code-bearing one, and the loser");
    w.WriteLine("  is never mentioned anywhere in the output.");
    w.WriteLine();
    w.WriteLine("  CALIBRATION: this happens on healthy configurations too — a test bundle's own");
    w.WriteLine("  .alpackages routinely holds small symbols-only copies that outrank code-bearing");
    w.WriteLine("  ones, on runs that pass completely. So \"a symbols-only package won\" is EVIDENCE");
    w.WriteLine("  TO WEIGH, not a verdict. --verbose lists them as [dep] note: lines. Treat them as");
    w.WriteLine("  the first place to look when execution fails, not as proof of the cause.");
    w.WriteLine();
    w.WriteLine("    Do NOT conclude \"shadowing is ruled out\" by hashing or swapping the ONE");
    w.WriteLine("    package named in the error. The package that failed is usually not the");
    w.WriteLine("    package that was shadowed — a test library dies because something it depends");
    w.WriteLine("    on resolved to a symbols-only copy. Checking one file proves nothing about");
    w.WriteLine("    the other hundred.");
    w.WriteLine();
    w.WriteLine("    Instead, make it mechanical — re-run with --verbose and read the [dep] lines:");
    w.WriteLine("      [dep] <Publisher>/<Name> <Version>  <- /path/to/the/winning.app");
    w.WriteLine("    That is the resolved set. Check the path and version of each dependency that");
    w.WriteLine("    is actually on the failing call's path, not just the one that threw.");
    w.WriteLine();
    w.WriteLine("    The [dep] winner comes FROM somewhere: --verbose also lists every directory");
    w.WriteLine("    actually searched to produce it, as [pkg-cache] lines under the \"package");
    w.WriteLine("    caches (final search set): N dir(s)\" count — not the earlier \"(requested)\"");
    w.WriteLine("    count, which is the explicit/default set only. If a package you expect to");
    w.WriteLine("    win is missing, check whether its directory is even in the final list.");
    w.WriteLine();
    w.WriteLine("  VERSION SKEW ACROSS A FAMILY. When a workspace's .alpackages reference two");
    w.WriteLine("  different minors of the same platform family, stage BOTH minors in the");
    w.WriteLine("  dependency directory. Supplying only the higher one lets a symbols-only copy");
    w.WriteLine("  win over the code-bearing package the app actually needs at runtime. Test");
    w.WriteLine("  toolkit libraries and platform apps are frequently on DIFFERENT minors than");
    w.WriteLine("  the app under test — that is normal and must be preserved, not normalised.");
    w.WriteLine();
    w.WriteLine("  WHICH BC VERSION TO PASS. Do NOT infer it from the app under test. The runner");
    w.WriteLine("  selects within the major its ENGINE was built against, and one engine build");
    w.WriteLine("  spans the minors of that major — a 28.1-built engine runs 28.2 business logic");
    w.WriteLine("  correctly. So the right --bc-version is the one whose service-tier artifacts");
    w.WriteLine("  are present for this engine, which is NOT necessarily the version stamped on");
    w.WriteLine("  the app's dependencies. `--bc-version 28.1` while the app references 28.2");
    w.WriteLine("  packages is a normal, working configuration, not a mismatch to \"fix\".");
    w.WriteLine("  Attributing a runtime failure to \"the engine's BC-X.Y patch set\" without");
    w.WriteLine("  knowing which version was actually selected is a guess. NOTE: the runner does");
    w.WriteLine("  not currently print its selection, so pass --bc-version explicitly rather than");
    w.WriteLine("  relying on the default — leaving it off can silently change test outcomes.");
    w.WriteLine();
    w.WriteLine("  BC artifacts are provisioned AUTOMATICALLY by default (issue #2024): a first run");
    w.WriteLine("  against an empty ~/.local/share/al-runner/artifacts downloads the engine +");
    w.WriteLine("  platform/test apps it needs and continues, no flag required. Pass");
    w.WriteLine("  --no-auto-provision to refuse network access (offline/air-gapped machines) —");
    w.WriteLine("  a refused or failed provision still fails loud, naming exactly what is");
    w.WriteLine("  missing and a fix command that is valid for a `dotnet tool install`, never a");
    w.WriteLine("  silent stub. Other provisioning entry points:");
    w.WriteLine("    al-runner provision <bundle-dir>        # provision for that project's version, then exit");
    w.WriteLine("    al-runner --auto-provision <dirs>       # same as the default, explicit for scripts");
    w.WriteLine("    al-runner --no-auto-provision <dirs>    # fail loud instead of reaching the network");
    w.WriteLine("  If a provisioning-gap message names a specific missing set, force just that one");
    w.WriteLine("  (bypasses need-detection entirely — useful when the default `provision` mis-detects,");
    w.WriteLine("  issue #2085):");
    w.WriteLine("    al-runner provision --platform-apps --bc-version V [--force]");
    w.WriteLine("    al-runner provision --test-apps --bc-version V [--force]");
    w.WriteLine("    al-runner provision --service-tier --bc-version V [--force]");
    w.WriteLine("  Every one of these works from a plain `dotnet tool install -g` with no source");
    w.WriteLine("  checkout — the standalone tools/DownloadArtifacts project these wrap ships only");
    w.WriteLine("  as source in this repo and is unreachable from an installed tool.");
    w.WriteLine("  The engine must be built against the same BC MINOR as the target artifacts");
    w.WriteLine("  (28.1 vs 28.2 matters; a major-only match is not sufficient).");
    w.WriteLine();

    w.WriteLine("PRE-FLIGHT — do these before concluding anything about a failure");
    w.WriteLine("  1. al-runner --version");
    w.WriteLine("  2. Confirm the artifact version you are running against resolves as intended");
    w.WriteLine("     (pass --bc-version explicitly rather than relying on \"latest in cache\").");
    w.WriteLine("  3. Re-run the failing case alone with --test <name> --verbose. --verbose turns");
    w.WriteLine("     on the internal [Component] logs that name the failing subsystem.");
    w.WriteLine("  4. Re-run with --no-cache once. Compiled output is cached by default, and a few");
    w.WriteLine("     defect classes only appear on a cache HIT (or only on a MISS).");
    w.WriteLine();

    w.WriteLine("TROUBLESHOOTING — failure signature, meaning, action");
    w.WriteLine();
    w.WriteLine("  \"NavNCLMissingMethodException: Function ID <n> was called. The object with");
    w.WriteLine("   ID 0 does not have a member with that ID.\"");
    w.WriteLine("      Meaning: the call resolved against a module whose AL objects have no IDs —");
    w.WriteLine("      i.e. a symbols-only package won over a code-bearing one, or a module's");
    w.WriteLine("      emit did not complete. Object ID 0 is the tell. This is a RESOLUTION");
    w.WriteLine("      failure. It does NOT mean the runner is missing a native/platform method:");
    w.WriteLine("      the named function is ordinary AL in some dependency, and the runner");
    w.WriteLine("      failed to find the code for it — it is not an unimplemented intrinsic.");
    w.WriteLine("      Action: re-run with --verbose and read the [dep] lines to see which .app");
    w.WriteLine("      won for every dependency on the failing path (see DEPENDENCIES above);");
    w.WriteLine("      stage every minor the workspace references; re-run with --no-cache. This");
    w.WriteLine("      is a dependency/invocation problem far more often than a runner gap.");
    w.WriteLine();
    w.WriteLine("  Failure inside an install trigger (OnInstallAppPerCompany/PerDatabase)");
    w.WriteLine("      Install triggers are implemented and do run — a large suite exercising the");
    w.WriteLine("      MS test toolkit fires them on every run. A failure here is therefore about");
    w.WriteLine("      the dependency set you supplied, not about trigger support being absent.");
    w.WriteLine("      Apply the ID-0 checklist above.");
    w.WriteLine();
    w.WriteLine("  \"RunnerOutOfScopeException: <api> — <reason>\"");
    w.WriteLine("      Meaning: WORKING AS INTENDED. The test touched a surface the runner");
    w.WriteLine("      deliberately does not emulate (SMTP, outbound HTTP, printing, external");
    w.WriteLine("      file I/O, web-service publishing). The runner throws loudly rather than");
    w.WriteLine("      returning a fake value that would make a green test lie.");
    w.WriteLine("      Action: see docs/scope.md. This is not a bug and not a gap.");
    w.WriteLine();
    w.WriteLine("  Could not resolve dependency / unresolved symbol at compile time");
    w.WriteLine("      Action: add the missing .app's directory via --package-cache.");
    w.WriteLine();
    w.WriteLine("  Artifact version not found");
    w.WriteLine("      Action: auto-provisioning is on by default and should have fetched it —");
    w.WriteLine("      if you passed --no-auto-provision, drop it (or run `al-runner provision`");
    w.WriteLine("      explicitly). If provisioning itself failed (no network), point");
    w.WriteLine("      --artifact-path at an existing artifact root instead.");
    w.WriteLine();
    w.WriteLine("  Compile succeeds, tests still do not run");
    w.WriteLine("      Action: read the DEPENDENCIES trap above. Compilation validates symbols");
    w.WriteLine("      only; it says nothing about whether the code is present to execute.");
    w.WriteLine();

    w.WriteLine("OUTPUT NOTES");
    w.WriteLine("  Console output written from inside a test is captured by the runner, not");
    w.WriteLine("  echoed live — a probe that writes to stdout will appear to produce nothing.");
    w.WriteLine("  Write diagnostics to a FILE instead.");
    w.WriteLine("  In --server mode stdout carries ONLY the JSON protocol; all logs go to stderr.");
    w.WriteLine("  Same for --dap stdio: stdout carries ONLY the DAP wire format, all logs go to");
    w.WriteLine("  stderr. --dap [PORT] (TCP) is unaffected — stdout is normal there.");
    w.WriteLine();

    w.WriteLine("TDD MODE (--tdd) — starting a red-green cycle before the app has the symbol yet");
    w.WriteLine("  Local development only. Not for CI: it deliberately runs tests that reference");
    w.WriteLine("  symbols the implementing app doesn't have yet, instead of failing the compile.");
    w.WriteLine();
    w.WriteLine("  Normally, a test written before its implementation is a compile error —");
    w.WriteLine("  method-body errors drop the WHOLE app group (BC's continue-on-error does not");
    w.WriteLine("  cover them), so the run reports exit 3 and zero test results, not a red test.");
    w.WriteLine("  --tdd infers the missing member's type from how the test uses it (a field's");
    w.WriteLine("  type from what's assigned to it, a procedure's parameter/return types from its");
    w.WriteLine("  call site), generates it into the implementing app's source in memory, and");
    w.WriteLine("  recompiles. The generated body raises a distinctive error naming itself as a");
    w.WriteLine("  generated stub, so the test runs up to that point and fails there — a genuine");
    w.WriteLine("  RED test, for the reason you intended, instead of a compile failure:");
    w.WriteLine("    al-runner --tdd MyApp MyApp.Test");
    w.WriteLine();
    w.WriteLine("  Where nothing anchors a confident guess (a bare-statement call — no way to");
    w.WriteLine("  tell a void procedure from a discarded return value — or both sides of an");
    w.WriteLine("  assignment unresolved), --tdd REFUSES rather than invents: that test is still");
    w.WriteLine("  reported FAILED, naming the missing symbol, exactly as before generation");
    w.WriteLine("  existed. A wrong guess that compiled cleanly would be worse than no guess —");
    w.WriteLine("  a test red for the wrong reason. At the end of the run, --tdd prints every");
    w.WriteLine("  member it actually generated: that list is the API surface the implementing");
    w.WriteLine("  app still has to hand-write to replace the stubs.");
    w.WriteLine();
    w.WriteLine("  Exit code is 1 (a test failed), not 3 (compile failed) — --tdd's whole point");
    w.WriteLine("  is turning that 3 into a 1 you can iterate against.");
    w.WriteLine();
    w.WriteLine("  --tdd disables the AL-output cache for the run (its generated members and");
    w.WriteLine("  synthetic results are derived fresh from source every time; a cache HIT would");
    w.WriteLine("  silently skip generation and serve stale or missing results).");
    w.WriteLine();
    w.WriteLine("  --tdd + --watch is REJECTED (exit 2), not silently combined. --watch's whole");
    w.WriteLine("  premise is a stable, unchanging source tree it can diff between cycles — a");
    w.WriteLine("  --tdd run mutates the app's own in-memory source every cycle by design, so");
    w.WriteLine("  the two are fundamentally incompatible rather than merely untested together.");
    w.WriteLine("  --tdd + --server is rejected the same way.");
    w.WriteLine();
    w.WriteLine("  Scope: source-compiled implementing apps only. A precompiled .app dependency's");
    w.WriteLine("  missing member is not generated — precompiled-dll-respect.md forbids rewriting");
    w.WriteLine("  compiled bodies, and that diagnostic falls through to the same refuse path.");
    w.WriteLine();

    w.WriteLine("REPORTING A RUNNER GAP");
    w.WriteLine("  Only after PRE-FLIGHT and TROUBLESHOOTING have been worked through, and the");
    w.WriteLine("  behaviour is not described in docs/limitations.md or docs/scope.md. A gap");
    w.WriteLine("  report needs a minimal runnable AL reproducer and the exact failing assertion");
    w.WriteLine("  or diagnostic. Do not guess at a cause, and do not silently work around it.");
    w.WriteLine();
    w.WriteLine("  Before filing, you must be able to answer all of:");
    w.WriteLine("    - Which BC version did the run actually select? (--verbose)");
    w.WriteLine("    - What is the full resolved dependency set, with winning paths? ([dep] lines)");
    w.WriteLine("    - Does it still fail with --no-cache?");
    w.WriteLine("    - Is the mechanism you are naming consistent with the error? (\"unimplemented");
    w.WriteLine("      native method\" does not explain an object-ID-0 resolution failure.)");
    w.WriteLine("  A report that names a cause the evidence does not support is worse than none:");
    w.WriteLine("  it sends someone to fix a subsystem that was never involved.");
    w.WriteLine();
    w.WriteLine("  Where to file: https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues");
    w.WriteLine("  Gap reports are welcome — this is the replacement for the telemetry channel");
    w.WriteLine("  that used to exist (#1643, closed not-planned); nothing is sent anywhere");
    w.WriteLine("  automatically, and no report happens without a human reading it first.");
    w.WriteLine();
    w.WriteLine("  Before posting anything: ask the person you are working with for permission,");
    w.WriteLine("  and show them the report text first. Do not open the issue on your own, and");
    w.WriteLine("  do not silently work around the gap instead — both are wrong. Filing without");
    w.WriteLine("  asking treats someone else's repository as yours to post to; working around it");
    w.WriteLine("  silently is exactly what runner-gap tracking exists to prevent.");
    w.WriteLine();
    w.WriteLine("  An uncertain report is still worth offering, as long as the uncertainty is");
    w.WriteLine("  stated plainly. \"I do not know what caused this, here is the reproducer\" is a");
    w.WriteLine("  good report. \"This is a metadata cache bug\", offered with no evidence for");
    w.WriteLine("  that specific mechanism, is not — that is the cause-without-support problem");
    w.WriteLine("  above, just stated with more confidence than the evidence supports.");
    w.WriteLine();

    w.WriteLine("FURTHER READING");
    w.WriteLine("  --help                       full flag reference");
    w.WriteLine("  docs/limitations.md          the real architectural limits");
    w.WriteLine("  docs/scope.md                in-scope vs out-of-scope-by-design surfaces");
    w.WriteLine("  docs/server-mode.md          the --server JSON-RPC protocol");
    w.WriteLine("  docs/dap-mode.md             the --dap Debug Adapter Protocol server");
    w.WriteLine("  docs/subsystems.md           subsystem map");
}

static void PrintHelp(TextWriter w)
{
    // First line so a build carries its own version wherever --help output gets
    // pasted (e.g. into a gap report) without asking separately for --version's
    // output too (#2072).
    w.WriteLine(VersionString());
    w.WriteLine("al-runner — run Business Central AL unit tests in-process.");
    w.WriteLine();
    w.WriteLine("USAGE");
    w.WriteLine("  al-runner [OPTIONS] <bundle-dir>...");
    w.WriteLine("  al-runner provision [<bundle-dir>]");
    w.WriteLine("  al-runner --server [--package-cache PATH ...] [--cache DIR]");
    w.WriteLine("  al-runner --dap [PORT|stdio] <bundle-dir>");
    w.WriteLine("  al-runner --precompile <input.app> --out <output.dll> [--package-cache PATH ...]");
    w.WriteLine("  al-runner --emit-app <bundleDir> <outPath> [--package-cache PATH ...]");
    w.WriteLine("  al-runner --guide      (operating manual for automated callers)");
    w.WriteLine("  al-runner --version   (also: -v, -V, version)");
    w.WriteLine("  al-runner --help");
    w.WriteLine();
    w.WriteLine("Agents and scripted callers should start with --guide: it covers correct");
    w.WriteLine("invocation against a real app + test app, where dependencies are resolved from,");
    w.WriteLine("and what the common runtime failure signatures actually mean.");
    w.WriteLine();
    w.WriteLine("A <bundle-dir> is a folder that either contains an app.json (a single AL");
    w.WriteLine("package) or sits below one — the bucket root is auto-detected by climbing the");
    w.WriteLine("path. Dependencies declared in app.json are resolved against --package-cache");
    w.WriteLine("dirs, the al-runner artifact cache, and the dotnet tool store.");
    w.WriteLine();
    w.WriteLine("Multiple <bundle-dir> arguments run sequentially and aggregate into one");
    w.WriteLine("summary. Pass --out PATH to also emit a failure-classification JSON.");
    w.WriteLine("Arguments that name the same directory run ONCE: they are de-duplicated by");
    w.WriteLine("resolved real path, so `x`, `./x`, `x/` and a symlink to `x` collapse onto one");
    w.WriteLine("another and the run says which argument it dropped. Two different directories");
    w.WriteLine("are never merged, even when they declare the same app id.");
    w.WriteLine();
    w.WriteLine("SELECTION");
    w.WriteLine("  --test PATTERN, --filter PATTERN");
    w.WriteLine("                          Run only tests whose qualified name (CodeunitNNNN.Method)");
    w.WriteLine("                          contains PATTERN (case-insensitive). Leading/trailing '*'");
    w.WriteLine("                          is accepted as a shell-friendly no-op.");
    w.WriteLine("  --isolation MODE, --test-isolation MODE");
    w.WriteLine("                          Test isolation:");
    w.WriteLine("                            codeunit  state shared inside a codeunit, reset between");
    w.WriteLine("                                      codeunits (default; AL TestIsolation = Codeunit,");
    w.WriteLine("                                      BC's \"Isol. Codeunit\" 130450)");
    w.WriteLine("                            test      every [Test] gets a fresh state");
    w.WriteLine("                                      (AL TestIsolation = Function)");
    w.WriteLine("                            disabled  no resets at all (AL TestIsolation = Disabled,");
    w.WriteLine("                                      BC's \"Isol. Disabled\" 130451)");
    w.WriteLine("                            method    v1 alias for 'test' (v1's per-method reset)");
    w.WriteLine("  --test-timeout SECONDS  Per-test timeout override (v1 carryover). Default: 60s, or");
    w.WriteLine("                          the AL_RUNNER_TEST_TIMEOUT_SEC env var if set; this flag");
    w.WriteLine("                          takes precedence over both. On timeout the test fails with");
    w.WriteLine("                          \"Test exceeded {N}s timeout.\" (v1-compatible message text).");
    w.WriteLine();
    w.WriteLine("EXECUTION");
    w.WriteLine("  --bc-version X          Select the BC artifact version (e.g. \"28.1\" or a full");
    w.WriteLine("                          version). Default: the latest version present in");
    w.WriteLine("                          ~/.local/share/al-runner/artifacts. A prefix matches the");
    w.WriteLine("                          highest version with that prefix. Missing artifacts are");
    w.WriteLine("                          auto-provisioned by default (see --no-auto-provision);");
    w.WriteLine("                          an unavailable/refused version still fails loud.");
    w.WriteLine("                          Mutually exclusive with --artifact-path.");
    w.WriteLine("  --artifact-path DIR     Use an explicit BC artifact root (the dir containing");
    w.WriteLine("                          platform/ + w1/), bypassing the cache scan. Its version");
    w.WriteLine("                          is read from the dir name or the contained Ncl.dll.");
    w.WriteLine("                          Mutually exclusive with --bc-version.");
    w.WriteLine("  --auto-provision        Download the BC artifacts for the project's version if");
    w.WriteLine("                          they are missing, then continue the run. ON BY DEFAULT");
    w.WriteLine("                          since issue #2024 — this flag is now redundant with a");
    w.WriteLine("                          plain run, kept for explicit scripts/back-compat. See");
    w.WriteLine("                          --no-auto-provision to turn it off (or the `provision`");
    w.WriteLine("                          subcommand to provision without running tests).");
    w.WriteLine("  --no-auto-provision     Disable automatic provisioning: a missing/incomplete BC");
    w.WriteLine("                          artifact fails loud instead of downloading it. Use this");
    w.WriteLine("                          on offline/air-gapped machines, or anywhere reaching the");
    w.WriteLine("                          network unasked for gigabyte-scale artifacts is unwanted.");
    w.WriteLine("  --package-cache PATH    Extra directory to scan for .app dependencies");
    w.WriteLine("                          (repeatable). Default scan: ~/.bcartifacts.cache,");
    w.WriteLine("                          ~/.local/share/al-runner/artifacts, and bundle .alpackages/.");
    w.WriteLine("  --cache DIR             AL-output cache directory. Default:");
    w.WriteLine("                          ~/.cache/al-runner/al-out. Compiled test DLLs are");
    w.WriteLine("                          re-used on subsequent runs if inputs are unchanged");
    w.WriteLine("                          (key = hash of .al sources, resolved deps, runner mtime).");
    w.WriteLine("  --no-cache              Disable the AL-output cache for this run.");
    w.WriteLine("  --print-cache-key       Diagnostic/test-support mode: compute the AL-output cache");
    w.WriteLine("                          key for the first app group of the first bundle exactly as");
    w.WriteLine("                          a real run would, print \"[cache] KEY key=<hash>\", and exit");
    w.WriteLine("                          before Emit+Compile starts. Requires the cache to be");
    w.WriteLine("                          enabled (default; not --no-cache). Exit code 2 if no key");
    w.WriteLine("                          could be computed.");
    w.WriteLine("  --watch                 Stay resident with warm dependencies and re-run IN-PROCESS");
    w.WriteLine("                          on every .al change (deps loaded once → ~seconds/save, not");
    w.WriteLine("                          a cold re-run). Ctrl+C to quit.");
    w.WriteLine("  --server                Long-running JSON-RPC daemon over stdin/stdout (warm");
    w.WriteLine("                          deps + BC patches loaded once; ~19s->~4s per run). One");
    w.WriteLine("                          JSON request/response per line. stdout carries ONLY the");
    w.WriteLine("                          protocol; all logs go to stderr. Used by the VS Code");
    w.WriteLine("                          extension. Commands: runTests (streaming per-test results),");
    w.WriteLine("                          execute (compile+run inline AL source; one response with");
    w.WriteLine("                          tests, Message() output, coverage and captured values),");
    w.WriteLine("                          shutdown. Full wire schema: docs/server-mode.md. Mutually");
    w.WriteLine("                          exclusive with --watch.");
    w.WriteLine("  --dap [PORT]            Debug Adapter Protocol server (default port 4711):");
    w.WriteLine("                          set AL breakpoints, pause execution, inspect locals over a");
    w.WriteLine("                          real DAP TCP connection. Requires exactly one bundle path.");
    w.WriteLine("                          Compiles on `launch`, pauses AL execution at StmtHit for");
    w.WriteLine("                          any breakpointed statement once `configurationDone` starts");
    w.WriteLine("                          the run. next/stepIn/stepOut pause at a real qualifying");
    w.WriteLine("                          statement (step-over/into/out of the current call), not");
    w.WriteLine("                          just at the next breakpoint. See docs/dap-mode.md.");
    w.WriteLine("                          Mutually exclusive with --server.");
    w.WriteLine("  --dap stdio             Same DAP session, over this process's own stdin/stdout");
    w.WriteLine("                          instead of a TCP socket — for a client that launches");
    w.WriteLine("                          al-runner directly (e.g. VS Code's DebugAdapterExecutable),");
    w.WriteLine("                          no port to pick, no readiness race. stdout carries ONLY the");
    w.WriteLine("                          DAP protocol; all logs go to stderr. See docs/dap-mode.md.");
    w.WriteLine("  --tdd                   Local-development flag (not for CI). A test referencing a");
    w.WriteLine("                          table field / procedure / enum value the implementing app");
    w.WriteLine("                          doesn't have yet normally drops the whole app group with a");
    w.WriteLine("                          compile failure (exit 3, zero test results). --tdd keeps");
    w.WriteLine("                          every object that DID compile and reports each [Test]");
    w.WriteLine("                          procedure in an object that could NOT be recovered as a");
    w.WriteLine("                          FAILED test naming the missing symbol, so a red-green TDD");
    w.WriteLine("                          cycle can start with an honestly red test (exit 1). Works");
    w.WriteLine("                          together with --watch (a cycle with a missing symbol falls");
    w.WriteLine("                          back to a full rebuild instead of the fast incremental");
    w.WriteLine("                          path — the console names the reason). Not yet supported");
    w.WriteLine("                          together with --server.");
    w.WriteLine("  --per-suite             Legacy per-Compilation path. Default is bundled mode");
    w.WriteLine("                          (5-7x faster, parity-verified).");
    w.WriteLine("  --bundled               No-op alias for the default bundled mode (deprecated).");
    w.WriteLine("  --define SYM            Define an AL preprocessor symbol for source compilation");
    w.WriteLine("                          (repeatable). SYM must be a valid AL identifier");
    w.WriteLine("                          (letters/digits/underscores, not starting with a digit).");
    w.WriteLine("                          Merged with the built-in CLEANSCHEMA1..25 set.");
    w.WriteLine("  --preprocessor-symbols A,B,...");
    w.WriteLine("                          Define multiple AL preprocessor symbols (comma-separated).");
    w.WriteLine("                          Each entry is validated identically to --define.");
    w.WriteLine();
    w.WriteLine("OUTPUT");
    w.WriteLine("  --out PATH              Write the failure-classification JSON to PATH and");
    w.WriteLine("                          print the FAILURE CLASSIFICATION block. Off by default —");
    w.WriteLine("                          classification is a runner-development diagnostic.");
    w.WriteLine("  --classify              Print the FAILURE CLASSIFICATION block without writing");
    w.WriteLine("                          a JSON file.");
    w.WriteLine("  --output-json           Replace the normal text output with per-test JSON on");
    w.WriteLine("                          stdout (status: pass/fail/error, message, stackTrace,");
    w.WriteLine("                          durationMs, exitCode). Distinct from --out's failure-");
    w.WriteLine("                          classification JSON.");
    w.WriteLine("  --output-junit PATH     Write a JUnit XML report to PATH, grouped by codeunit.");
    w.WriteLine("                          Independent of --output-json — works with either mode.");
    w.WriteLine("  --coverage              Statement-level coverage via BC's own StmtHit");
    w.WriteLine("                          instrumentation (no rewrite of emitted AL output —");
    w.WriteLine("                          the hook lives in Ncl.dll). Writes Cobertura XML");
    w.WriteLine("                          (default ./cobertura.xml) plus a console table after");
    w.WriteLine("                          the run. Off by default; --coverage-out PATH overrides");
    w.WriteLine("                          the Cobertura output path.");
    w.WriteLine("  --failures-only, --quiet");
    w.WriteLine("                          Print only FAIL/ERROR per-test lines. Default prints both");
    w.WriteLine("                          PASS and FAIL with stack traces (matches v1).");
    w.WriteLine("  --show-pass             Accepted for v1 back-compat; PASS lines are on by default");
    w.WriteLine("                          in v2.");
    w.WriteLine("  --verbose               Show internal [Component] diagnostic logs.");
    w.WriteLine("  --strict                Accepted for back-compat; this is the default since the v2");
    w.WriteLine("                          cut. Exit codes:");
    w.WriteLine("                            0  all tests passed");
    w.WriteLine("                            1  at least one test FAILED or ERRORED");
    w.WriteLine("                            2  a bundle could not execute (process-level error;");
    w.WriteLine("                               also a bad invocation — unknown flag, or a bundle");
    w.WriteLine("                               path that does not exist)");
    w.WriteLine("                            3  a bundle could not compile");
    w.WriteLine("                            4  --count-baseline: a suite's test or app-group count did");
    w.WriteLine("                               not exactly match its declared baseline (see --count-baseline)");
    w.WriteLine("  --no-strict-exit        Always exit 0 regardless of test outcome, so callers can");
    w.WriteLine("                          parse the JSON output without the process failing the step.");
    w.WriteLine("  --dump-csharp DIR       Write the intermediate C# emitted by BC's Compilation.Emit");
    w.WriteLine("                          (one .cs file per AL object) under DIR/<moduleName>/.");
    w.WriteLine("                          Useful for diagnosing codegen issues.");
    w.WriteLine("  --expectations DIR      Load the test-expectations manifest from DIR (schema:");
    w.WriteLine("                          docs/expectations.md). Defaults to ./tests/expectations");
    w.WriteLine("                          when that directory exists; otherwise off. Declared");
    w.WriteLine("                          outcomes reclassify: expect-oos -> pass-oos,");
    w.WriteLine("                          expect-fail-known-gap -> pass-known-gap,");
    w.WriteLine("                          expect-divergence -> pass-divergence, skip -> not");
    w.WriteLine("                          invoked. Manifest drift is loud: an entry whose test now");
    w.WriteLine("                          passes, or an out-of-scope throw with no entry, fails");
    w.WriteLine("                          the run with a diagnostic naming the entry to fix.");
    w.WriteLine("  --count-baseline PATH   Load a per-suite test/app-group expected-count manifest");
    w.WriteLine("                          (schema: AlRunner/Infrastructure/CountBaseline.cs) and");
    w.WriteLine("                          fail the run (exit 4) if a suite's count does not exactly");
    w.WriteLine("                          match its baseline for the selected BC version — the guard");
    w.WriteLine("                          for \"a bundle silently stopped being discovered\" (#1880).");
    w.WriteLine("                          Off by default, unlike --expectations: a baseline sized");
    w.WriteLine("                          for the full corpus must not fire on a narrower run of");
    w.WriteLine("                          the same directory (e.g. one filtered with --test), so");
    w.WriteLine("                          this never auto-activates. A mismatch in EITHER direction");
    w.WriteLine("                          (growth or drop) fails and prints a diagnostic naming");
    w.WriteLine("                          expected vs actual — bump the baseline in the same PR.");
    w.WriteLine();
    w.WriteLine("SUBCOMMANDS");
    w.WriteLine("  provision [<bundle-dir>] Download and install the BC artifacts matching the");
    w.WriteLine("                          project's version, then exit without running tests.");
    w.WriteLine("                          This is the supported way to obtain artifacts on a");
    w.WriteLine("                          fresh machine — including a plain `dotnet tool install`");
    w.WriteLine("                          with no source checkout. Run `al-runner provision --help`");
    w.WriteLine("                          for its full flag list, or use one of:");
    w.WriteLine("                            --platform-apps [--bc-version V] [--force]");
    w.WriteLine("                                          Force-download Microsoft's platform .app");
    w.WriteLine("                                          set into the canonical dir, bypassing");
    w.WriteLine("                                          need-detection.");
    w.WriteLine("                            --test-apps [--bc-version V] [--force]");
    w.WriteLine("                                          Same, for the Microsoft test-toolkit set.");
    w.WriteLine("                            --service-tier [--bc-version V] [--force]");
    w.WriteLine("                                          Same, for the BC engine's service-tier DLLs.");
    w.WriteLine("                            --resolve-version PREFIX");
    w.WriteLine("                                          Print the latest full BC version for a");
    w.WriteLine("                                          prefix (e.g. \"28.4\") to stdout.");
    w.WriteLine("                          --force re-downloads even when the target directory");
    w.WriteLine("                          already looks populated (default: leave it alone).");
    w.WriteLine("  --precompile <input.app> --out <output.dll> [--package-cache PATH ...]");
    w.WriteLine("                          Compile a single .app to a managed DLL without running");
    w.WriteLine("                          tests. Useful for pre-warming caches.");
    w.WriteLine("  --emit-app <bundleDir> <outPath> [--package-cache PATH ...]");
    w.WriteLine("                          Compile a bundle dir and emit it as a .app package");
    w.WriteLine("                          in-process, without running tests.");
    w.WriteLine();
    w.WriteLine("ENVIRONMENT");
    w.WriteLine("  AL_RUNNER_VERBOSE=1          Same as --verbose.");
    w.WriteLine("  AL_RUNNER_FAILURES_ONLY=1    Same as --failures-only.");
    w.WriteLine("  AL_RUNNER_TRACE_NRE=1        Log every first-chance NullReferenceException with");
    w.WriteLine("                               full stack to stderr before AL `asserterror` swallows it.");
    w.WriteLine("  AL_RUNNER_NCL_CACHE=0        Force fresh Cecil rewrite of Ncl.dll (default: use");
    w.WriteLine("                               ~/.cache/al-runner/ncl-cecil/<key>.dll if present).");
    w.WriteLine("  AL_RUNNER_HOOK_TRACE=1       Trace every JmpHook fire to");
    w.WriteLine("                               /tmp/al-runner-hook-trace.log.");
    w.WriteLine("  AL_RUNNER_EMIT_TIMEOUT_SEC=N Override the 120 s default emit-phase timeout.");
    w.WriteLine("  AL_RUNNER_PHASE_LOG=PATH     Append one JSONL cost record per app group, per");
    w.WriteLine("                               bundle and per process to PATH (emit/compile/run");
    w.WriteLine("                               ms, deps, cache HIT/MISS, start + wall clock,");
    w.WriteLine("                               peak RSS; start_ms + wall_ms give an occupancy");
    w.WriteLine("                               timeline across concurrent runners).");
    w.WriteLine("                               Safe for concurrent runners. Summarise with");
    w.WriteLine("                               scripts/phase-log-report.py. Inert when unset.");
    w.WriteLine();
    w.WriteLine("EXAMPLES");
    w.WriteLine("  # Run the al-language corpus");
    w.WriteLine("  al-runner tests/al-language/tests/al-language");
    w.WriteLine();
    w.WriteLine("  # Run a real app together with its separate test app (the usual shape).");
    w.WriteLine("  # Pass BOTH bundle dirs; dependencies resolve from --package-cache.");
    w.WriteLine("  al-runner --bc-version 28.2 --package-cache ./deps MyApp MyApp.Test");
    w.WriteLine();
    w.WriteLine("  # Provision BC artifacts on a fresh machine, then run");
    w.WriteLine("  al-runner provision MyApp");
    w.WriteLine();
    w.WriteLine("  # Run one specific test");
    w.WriteLine("  al-runner --test Record_Insert_DuplicateKey_Throws tests/al-language/tests/al-language");
    w.WriteLine();
    w.WriteLine("  # CI: JUnit report for the test-results tab, strict exit by default");
    w.WriteLine("  al-runner --output-junit ci-results.xml tests/al-language/tests/al-language");
    w.WriteLine();
    w.WriteLine("  # Machine-readable per-test JSON for a scripted caller");
    w.WriteLine("  al-runner --output-json tests/al-language/tests/al-language");
    w.WriteLine();
    w.WriteLine("  # Dump the C# for a debugging session");
    w.WriteLine("  al-runner --dump-csharp /tmp/al-csharp tests/runner-extras/oos-reports");
    w.WriteLine();
    w.WriteLine("  # Pre-compile an .app to a managed DLL");
    w.WriteLine("  al-runner --precompile MyExtension_1.0.0.0.app --out MyExtension.dll");
    w.WriteLine();
    w.WriteLine("NOT YET IMPLEMENTED (see docs/v1-to-v2-migration.md)");
    w.WriteLine("  Nothing in this section is accepted as a flag. Everything documented above IS");
    w.WriteLine("  implemented.");
    w.WriteLine("  --stubs DIR             v1's stub-merge path. Real MS DLLs load in-process so the");
    w.WriteLine("                          original use case mostly evaporated; still possible to");
    w.WriteLine("                          add as an extra source-root merge if needed.");
    w.WriteLine("  --extract-deps          v1's dep-slicer (DepExtractor.cs, ~121 KB). Likely to stay");
    w.WriteLine("                          dropped — the full dep set loads directly instead.");
    w.WriteLine();
    w.WriteLine("DOCUMENTATION");
    w.WriteLine("  al-runner --guide            operating manual: correct invocation against a real");
    w.WriteLine("                               app, dependency resolution, failure signatures");
    w.WriteLine("  docs/v1-to-v2-migration.md  flag-by-flag migration matrix");
    w.WriteLine("  docs/expectations.md         out-of-scope test declarations");
    w.WriteLine("  docs/scope.md                runtime scope (in-scope vs OOS-by-design)");
    w.WriteLine("  docs/limitations.md          architectural limits");
    w.WriteLine("  docs/cecil-migration.md      Cecil rewrite strategy");
    w.WriteLine("  docs/subsystems.md           subsystem map");
}

// Issue #2085: `provision --help` used to fall through to the generic arg-parser's
// unknown-flag error ("Unknown option '--help'. Run with --help for the supported
// flags.") — telling the caller to run the exact thing it just ran. Every subcommand
// must answer --help, not just the top level.
static void PrintProvisionHelp(TextWriter w)
{
    w.WriteLine("al-runner provision — download and install BC artifacts. Every form below works");
    w.WriteLine("from a plain `dotnet tool install -g msdyn365bc.al.runner`; none require a source");
    w.WriteLine("checkout of this repository.");
    w.WriteLine();
    w.WriteLine("USAGE");
    w.WriteLine("  al-runner provision [<bundle-dir>]");
    w.WriteLine("  al-runner provision --platform-apps [--bc-version V] [--force]");
    w.WriteLine("  al-runner provision --test-apps [--bc-version V] [--force]");
    w.WriteLine("  al-runner provision --service-tier [--bc-version V] [--force]");
    w.WriteLine("  al-runner provision --resolve-version PREFIX");
    w.WriteLine("  al-runner provision --help");
    w.WriteLine();
    w.WriteLine("OPTIONS");
    w.WriteLine("  [<bundle-dir>]          With no mode flag: provision everything the named");
    w.WriteLine("                          bundle's app.json needs (engine closure + platform apps");
    w.WriteLine("                          + test toolkit, whichever are missing) and exit. This is");
    w.WriteLine("                          the default `provision` behavior and what a provisioning-");
    w.WriteLine("                          gap error's \"(a) One command (recommended)\" fix means.");
    w.WriteLine("  --bc-version V          Target BC version (a prefix like \"28.4\" or a full");
    w.WriteLine("                          4-part version). Default: this binary's own built");
    w.WriteLine("                          engine version, or the target bundle's app.json.");
    w.WriteLine("  --platform-apps         Force-download Microsoft's platform .app set (Base");
    w.WriteLine("                          Application / System Application / Business Foundation /");
    w.WriteLine("                          Application / Application Test Library) into the");
    w.WriteLine("                          canonical <artifacts>/<version>/platform-apps directory,");
    w.WriteLine("                          bypassing need-detection entirely.");
    w.WriteLine("  --test-apps             Force-download the Microsoft test-toolkit .app set");
    w.WriteLine("                          (Library Assert, Test Runner, Any, Tests-TestLibraries, …)");
    w.WriteLine("                          into <artifacts>/<version>/test-apps, bypassing");
    w.WriteLine("                          need-detection entirely.");
    w.WriteLine("  --service-tier          Force-download the BC engine's ~55-DLL service-tier");
    w.WriteLine("                          closure into <artifacts>/<version>, bypassing");
    w.WriteLine("                          need-detection entirely.");
    w.WriteLine("  --force                 With --platform-apps/--test-apps/--service-tier: re-run");
    w.WriteLine("                          the download even if the canonical directory already");
    w.WriteLine("                          contains files. Without it, a populated directory is");
    w.WriteLine("                          left alone and nothing is re-downloaded.");
    w.WriteLine("  --resolve-version PREFIX");
    w.WriteLine("                          Resolve a BC version prefix (e.g. \"28.4\") to the latest");
    w.WriteLine("                          full version published on the CDN and print it to stdout.");
    w.WriteLine("  --help, -h              Print this text and exit 0.");
    w.WriteLine();
    w.WriteLine("--platform-apps/--test-apps/--service-tier/--resolve-version may be combined in");
    w.WriteLine("one invocation (each named set is fetched); --force applies to all of them.");
}

static int RunPrecompile(string[] subArgs)
{
    string? input = null;
    string? output = null;
    var caches = new List<string>();
    for (int i = 0; i < subArgs.Length; i++)
    {
        if (subArgs[i] == "--out" && i + 1 < subArgs.Length) { output = subArgs[++i]; continue; }
        if (subArgs[i] == "--package-cache" && i + 1 < subArgs.Length) { caches.Add(subArgs[++i]); continue; }
        if (input == null) { input = subArgs[i]; continue; }
    }
    if (input == null || output == null)
    {
        Console.Error.WriteLine("Usage: Runner --precompile <input.app> --out <output.dll> [--package-cache PATH ...]");
        return 2;
    }
    var manifest = AppLoader.ReadManifest(input);
    if (manifest == null) { Console.Error.WriteLine($"Failed to read manifest from {input}"); return 2; }

    // #2131: select the BC version that matches the app being precompiled (its own
    // manifest Version — Microsoft test-apps/platform-apps are versioned identically to
    // the BC build they ship with, e.g. "Library Assert" v28.1.49838.50794 lives under
    // artifacts/28.1.49838.50794/) BEFORE computing the package-cache search dirs below.
    // Without this, DefaultPackageCacheDirs() falls back to ITS OWN lazy "latest version
    // in the artifacts cache" default (BcArtifacts.EnsureSelected), which is almost never
    // the version whose test-apps/platform-apps directories actually hold this app's
    // dependencies — exactly the "search path is too narrow" symptom #2131 reports
    // (AL1022 for System Application / Application Test Library / PEPPOL, none of which
    // exist under whatever version happened to be "latest"). Best-effort: an app whose
    // version does not correspond to any provisioned BC artifact directory (e.g. a
    // non-Microsoft or hand-versioned .app) falls through to the pre-existing lazy-default
    // behavior unchanged. A caller who already selected a version explicitly (a normal
    // bundle run reaching this helper, or a future --bc-version on --precompile) is left
    // alone — SelectVersion is call-once, so this never overrides that choice.
    if (!AlRunner.Infrastructure.BcArtifacts.IsSelected)
    {
        try { AlRunner.Infrastructure.BcArtifacts.SelectVersion(manifest.Version.ToString(), null); }
        catch
        {
            // No artifacts dir named exactly after this app's version — fall through to
            // the pre-existing lazy "latest in cache" default (triggered the first time
            // DefaultPackageCacheDirs() below reads BcArtifacts.SelectedVersion).
        }
    }

    var packageCacheDirs = caches.Count > 0 ? ExpandPackageCacheDirs(caches).ToList() : DefaultPackageCacheDirs().ToList();
    // Mirror the main bundle-run flow's runnerOwnedPlatformAppsDir/runnerOwnedTestAppsDir
    // fold-in (issue #1996) — always include the SELECTED version's own runner-owned
    // platform-apps/test-apps dirs when present on disk, even when the caller passed an
    // EXPLICIT --package-cache that doesn't happen to include them. System Application
    // (needed to compile Microsoft test-toolkit apps like Library Assert, whose own
    // NavxManifest.xml <Dependencies> is empty — the need is via the implicit `Platform=`
    // root, not an explicit dependency edge) lives in platform-apps, not test-apps.
    packageCacheDirs = AlRunner.PrecompileSupport.WidenPackageCacheDirs(
        packageCacheDirs,
        AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir,
        AlRunner.Infrastructure.BcArtifacts.SelectedVersion.ToString());

    // #2156 (found while adding #2152's proving test for this subcommand): --precompile
    // dispatches before the main run flow's own "Cecil-rewrite Ncl.dll in place" step ever
    // runs (that block lives much further down in Main, past the `--precompile` early
    // return), so on a genuinely cold environment — nothing has yet forced a fresh Cecil
    // rewrite of the bin-directory Ncl.dll for the selected BC version — NavEnvironment's
    // real, unpatched static constructor runs for the first time inside
    // BcRuntime.ApplyAllPatches and calls WindowsIdentity.GetCurrent() unconditionally,
    // which throws PlatformNotSupportedException on Linux before a single AL object is
    // even read. Mirror the main flow's own sequence here: rewrite, then re-exec once IF
    // this rewrite was the fresh one — loading a byte-identical freshly-rewritten Ncl in
    // the SAME process that wrote it can intermittently throw BadImageFormatException (see
    // the main flow's own comment on this exact hazard), so a fresh rewrite always re-execs
    // rather than continuing in this process. AL_RUNNER_REEXECED (shared with the main
    // flow's guard) prevents an infinite loop if the child somehow rewrites again.
    //
    // #2065: "mirror the main flow's own sequence" was only half done — the main flow's
    // rewrite is unreachable until the SHADOW HOP below has already run, and this one had
    // no hop in front of it, so on an install that does not ship Ncl.dll the rewrite
    // CREATED the file in the caller's own directory (measured: a 10.7 MB
    // Microsoft.Dynamics.Nav.Ncl.dll appearing in AlRunner/bin/Release/net8.0 after one
    // `--precompile`, which then suppressed the shadow hop for every subsequent invocation
    // from that directory). Take the same hop first; the child runs from a directory that
    // legitimately holds Ncl.dll before ITS trusted-platform-assemblies list is computed,
    // and the rewrite below then replaces a file rather than creating one. See
    // TryShadowReexec's own comment for the full reasoning, and
    // AlRunner.Tests/PrecompileNclShadowHopTests.cs for the proof in both directions.
    //
    // Deliberately variantSwapDir: null — `--precompile` does not do per-BC-minor engine
    // variant selection today (the main bundle-run flow computes variantSwapDir from
    // EngineVariants long after this early dispatch), so passing null keeps this call to
    // exactly the "Ncl.dll isn't shipped" half of the decision and changes nothing else
    // about the subcommand's behaviour.
    {
        var shadowChildExit = TryShadowReexec(variantSwapDir: null);
        if (shadowChildExit.HasValue) return shadowChildExit.Value;
    }

    {
        var srcDir = AlRunner.Infrastructure.BcArtifacts.ServiceTierDir;
        var binNcl = Path.Combine(AppContext.BaseDirectory, "Microsoft.Dynamics.Nav.Ncl.dll");
        var didFreshRewrite = AlRunner.Infrastructure.NclCecilRewrite.RewriteInPlace(srcDir, binNcl);
        if (didFreshRewrite && Environment.GetEnvironmentVariable("AL_RUNNER_REEXECED") != "1")
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
            };
            var argv = Environment.GetCommandLineArgs();
            var underDotnet = Path.GetFileNameWithoutExtension(Environment.ProcessPath!)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
            var userArgs = underDotnet ? argv : argv.Skip(1);
            foreach (var a in userArgs)
                psi.ArgumentList.Add(a);
            psi.Environment["AL_RUNNER_REEXECED"] = "1";
            Console.Error.WriteLine("[reexec] --precompile: fresh Ncl rewrite done — re-execing for a clean load");
            using var child = System.Diagnostics.Process.Start(psi)!;
            child.WaitForExit();
            return child.ExitCode;
        }
    }

    // Apply BC patches before any BC type is touched (BcCompiler uses BC types).
    BcRuntime.EnsureApplied();

    // Resolve transitive deps of THIS app so its compile sees them as symbol refs.
    // Add the implicit Microsoft/Application + Microsoft/System roots from the
    // manifest's Application/Platform attributes — modern .app packages (incl. the
    // BC test toolkit) rely on these instead of listing BaseApp under <Dependencies>.
    // Root-level only: synthesizing transitively would cycle (Application → BaseApp
    // → Application) and the resolver throws on cycles. Mirrors the app.json path.
    var resolver = new DependencyResolver(packageCacheDirs);
    var rootDeps = manifest.Dependencies.Concat(AppLoader.ImplicitRoots(manifest)).ToList();
    var transitive = resolver.Resolve(rootDeps);
    // For apps with empty <Dependencies/> (e.g. Customizations.app), the explicit
    // dep list is empty but the AL source may still use BaseApp/System Application
    // symbols via `using` statements. Enable the all-packages fallback so the compiler
    // can resolve those symbols from the package cache dirs.
    if (transitive.Count == 0)
        BcCompiler.SetPackageCacheFallback(manifest.AppId);
    BcCompiler.SetResolvedDeps(transitive, packageCacheDirs);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var compiler = new BcCompiler();
    var assembler = new BcAssembler();

    var alSources = AppLoader.ExtractAl(input);
    if (alSources.Count == 0)
    {
        Console.Error.WriteLine($"--precompile: {input} contains no src/*.al — nothing to compile");
        return 2;
    }
    var tempDir = Path.Combine(Path.GetTempPath(), "al-runner-precompile",
        Sanitize($"{manifest.Publisher}_{manifest.Name}_{manifest.Version}"));
    Directory.CreateDirectory(tempDir);
    foreach (var existing in Directory.EnumerateFiles(tempDir, "*.al"))
    {
        try { File.Delete(existing); } catch { }
    }
    foreach (var (name, src) in alSources)
        File.WriteAllText(Path.Combine(tempDir, Sanitize(name)), src);

    BcEmitOutput emitOut;
    try
    {
        emitOut = compiler.Emit(new[] { tempDir }, manifest.Name, tempDir);
    }
    catch (Exception ex)
    {
        // Surface the full flattened emit exception so the developer sees the root cause
        // without needing BCCOMPILER_DIAG=1.
        var detail = ex is AggregateException agg
            ? string.Join("\n  ", agg.Flatten().InnerExceptions.Select(e => $"{e.GetType().Name}: {e.Message}"))
            : $"{ex.GetType().Name}: {ex.Message}";
        Console.Error.WriteLine($"--precompile: EMIT-FAIL for {manifest.Publisher}_{manifest.Name} v{manifest.Version}:");
        Console.Error.WriteLine($"  {detail}");
        return 3;
    }
    var emitted = emitOut.Sources;
    if (emitted.Count == 0)
    {
        // Fail LOUDLY — print the diagnostics that explain WHY 0 objects emitted
        // (binding errors and per-object emit crashes), by default. A bare
        // "EMIT-ZERO, set an env var" message is the silent-failure mode issue
        // #1620 / loud-failures.md forbids: the developer must see what broke.
        Console.Error.WriteLine($"--precompile: EMIT-ZERO — 0 of {manifest.Name}'s objects emitted ({manifest.Publisher}_{manifest.Name} v{manifest.Version})");
        var diags = emitOut.Diagnostics;
        if (diags.Count == 0)
            Console.Error.WriteLine("  Compilation.Emit() returned 0 sources with no diagnostics (set BCCOMPILER_DIAG=1 for BC-internal compiler detail).");
        else
        {
            Console.Error.WriteLine($"  {diags.Count} blocking diagnostic(s):");
            const int cap = 40;
            foreach (var d in diags.Take(cap))
                Console.Error.WriteLine($"    {d}");
            if (diags.Count > cap)
                Console.Error.WriteLine($"    ... and {diags.Count - cap} more");
        }
        return 3;
    }
    // AL-diagnostic compile-failure guard (#2150), extended to --precompile (#2152). Same
    // BC ContinueBuildOnError shape as the other three paths: `emitted` can come back
    // non-empty (a broken object's sibling still emitted) at the same time
    // emitOut.Diagnostics also carries an Error-severity diagnostic for the broken one — a
    // real service tier would refuse to publish this dependency app regardless. Failing
    // loudly here matters more than it might look: --precompile's whole point is producing
    // a DLL another bundle run trusts as a precompiled dependency (see
    // precompiled-dll-respect.md), so a silently-accepted compile error here would poison
    // every bundle that later depends on this output.
    if (emitOut.Diagnostics.Count > 0)
    {
        Console.Error.WriteLine(
            $"--precompile: AL-DIAGNOSTIC-FAIL for {manifest.Publisher}_{manifest.Name} v{manifest.Version}: " +
            $"{emitted.Count} object(s) emitted but {emitOut.Diagnostics.Count} AL error(s) were " +
            $"reported by BC's own compiler; a real service tier would refuse to publish this module:");
        foreach (var d in emitOut.Diagnostics)
            Console.Error.WriteLine($"  {d}");
        return 3;
    }
    var asmName = $"Dep_{Sanitize(manifest.Publisher)}_{Sanitize(manifest.Name)}_{manifest.Version.ToString().Replace('.', '_')}";
    var compile = assembler.Compile(asmName, emitted);
    if (!compile.Success)
    {
        Console.Error.WriteLine($"--precompile: COMPILE-FAIL for {manifest.Publisher}_{manifest.Name} v{manifest.Version}:");
        foreach (var err in compile.Errors)
            Console.Error.WriteLine($"  {err.Split('\n')[0]}");
        return 3;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    File.WriteAllBytes(output, compile.AssemblyBytes!);
    sw.Stop();
    Console.WriteLine(
        $"precompiled {manifest.Name} v{manifest.Version} → {output} " +
        $"({compile.AssemblyBytes!.Length} bytes, {sw.ElapsedMilliseconds}ms)");
    return 0;

    static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '/', '\\' }).ToArray();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(Array.IndexOf(bad, ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }
}

// ── --emit-app subcommand ──────────────────────────────────────────────────
// Usage: --emit-app <bundleDir> <outPath> [--package-cache PATH ...]
// Emits the bundle dir as a real NAVX .app package using PackageModuleOutputter.
// Useful as a standalone debug tool and as the core of the layered pre-pass.
static int RunEmitApp(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: al-runner --emit-app <bundleDir> <outPath> [--package-cache PATH ...]");
        return 2;
    }
    var bundleDir = Path.GetFullPath(args[0]);
    var outPath = Path.GetFullPath(args[1]);
    var caches = new List<string>();
    for (int i = 2; i < args.Length; i++)
    {
        if ((args[i] == "--package-cache") && i + 1 < args.Length)
            caches.Add(args[++i]);
    }

    var appJsonPath = Path.Combine(bundleDir, "app.json");
    var identity = AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(appJsonPath);
    if (identity == null)
    {
        Console.Error.WriteLine($"--emit-app: could not read identity from {appJsonPath}");
        return 2;
    }

    Console.WriteLine($"  [{identity.Name}] {identity.Dependencies.Count} dep(s) declared in app.json");

    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        AlRunner.Infrastructure.InProcessAppPackager.EmitAppPackageToFile(
            bundleDir, identity, outPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"--emit-app: EXCEPTION {ex.GetType().Name}: {ex}");
        return 3;
    }
    sw.Stop();
    var info = new FileInfo(outPath);
    Console.WriteLine($"emit-app: {identity.Name} {identity.Version} → {outPath} ({info.Length} bytes, {sw.ElapsedMilliseconds}ms)");
    return 0;
}

// ── Layered source build pre-pass ─────────────────────────────────────────
// Detects inter-bundle dependencies, emits impl bundles in topo order into a
// per-run workspace cache dir, and prepends that dir to packageCacheDirs.
// Completely inert when bundles.Count <= 1 or no inter-bundle dep edges exist.
static List<string> RunLayeredPrePass(List<string> bundles, List<string> packageCacheDirs, List<string> workspaceDirsOut)
{
    // Read identity of every bundle.
    var identities = new Dictionary<string, AlRunner.Infrastructure.BundleIdentity>(StringComparer.OrdinalIgnoreCase);
    foreach (var bundle in bundles)
    {
        var abs = Path.GetFullPath(bundle);
        // FindBucketRoot might point up; prefer direct app.json or bucket root.
        var appJson = Path.Combine(abs, "app.json");
        if (!File.Exists(appJson))
        {
            var root = FindBucketRoot(abs);
            if (root != null) appJson = Path.Combine(root, "app.json");
        }
        if (!File.Exists(appJson)) continue;
        var id = AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(appJson);
        if (id != null) identities[abs] = id;
    }

    if (identities.Count < 2) return packageCacheDirs; // nothing to wire

    // Build dep edges: bundle B "depends on" bundle A if B's deps contain A's AppId
    // (or A's Name+Publisher as fallback).
    var idByKey = identities.ToDictionary(
        kv => kv.Key,
        kv => kv.Value,
        StringComparer.OrdinalIgnoreCase);

    // impls = bundles that at least one other bundle declares as a dependency.
    var implPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (path, id) in idByKey)
    {
        foreach (var (otherPath, otherId) in idByKey)
        {
            if (string.Equals(path, otherPath, StringComparison.OrdinalIgnoreCase)) continue;
            bool dependsOn = otherId.Dependencies.Any(dep =>
                (dep.AppId != Guid.Empty && dep.AppId == id.AppId) ||
                (string.Equals(dep.Name, id.Name, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(dep.Publisher, id.Publisher, StringComparison.OrdinalIgnoreCase)));
            if (dependsOn) implPaths.Add(path);
        }
    }

    if (implPaths.Count == 0) return packageCacheDirs; // no inter-bundle deps

    // Skip any impl that already has a real, compiler-valid prebuilt .app (one with
    // a SymbolReference.json) in the package caches — e.g. RecoverySolutions ships
    // MainApps/Customizations.Test/.alpackages/Customizations.app, a symbol+source
    // package built by alc. That real .app serves BOTH compile-time symbols (via BC's
    // native .app scanner, which merges tableextensions correctly — our synthetic
    // symbols.json does NOT) AND runtime code (DependencyLoader compiles its src/*.al).
    // Synthesizing a competing .app here would only shadow the real one with weaker
    // symbols, reintroducing AL0132/AL0133 on the dependent's tableextension fields.
    foreach (var implPath in implPaths.ToList())
    {
        if (!idByKey.TryGetValue(implPath, out var implId)) continue;
        var prebuilt = packageCacheDirs
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.app", SearchOption.AllDirectories))
            .FirstOrDefault(f =>
            {
                var m = AppLoader.ReadManifest(f);
                return m != null && m.AppId == implId.AppId
                    && AppLoader.HasSymbolReference(f);
            });
        if (prebuilt != null)
        {
            // ...but only while that .app is not STALE. It is matched on AppId alone, so a
            // months-old package in a project's .alpackages would otherwise beat the source
            // directory the user passed on the command line — surfacing as a wall of bogus
            // AL0791 / AL0185 diagnostics against source that is perfectly valid, with only
            // the "[layered] ... skipping in-process synthesis" line above to explain it.
            var prebuiltUtc = File.GetLastWriteTimeUtc(prebuilt);
            var newestSourceUtc = AlRunner.Infrastructure.PrebuiltShadowCheck.NewestAlSourceUtc(implPath);
            if (AlRunner.Infrastructure.PrebuiltShadowCheck.SourceIsNewer(prebuiltUtc, newestSourceUtc))
            {
                Console.WriteLine($"[layered] {implId.Name} {implId.Version} has a prebuilt symbol package " +
                    $"({Path.GetFileName(prebuilt)}) but it is STALE (source modified {newestSourceUtc:u} > " +
                    $"package {prebuiltUtc:u}) — synthesizing from source instead.");
                continue; // keep implPath: build it from source
            }

            Console.WriteLine($"[layered] {implId.Name} {implId.Version} already has a prebuilt symbol package " +
                $"({Path.GetFileName(prebuilt)}) — skipping in-process synthesis.");
            implPaths.Remove(implPath);
        }
    }
    if (implPaths.Count == 0) return packageCacheDirs; // every impl already prebuilt

    // Topological sort of impl paths (deps before dependents).
    var sortedImpls = TopologicalSort(implPaths.ToList(), idByKey);

    // Each impl gets its OWN deterministic cache dir keyed on THAT impl's own
    // sources + dependency identities. Editing one impl therefore only invalidates
    // its own dir — the unchanged siblings keep cache-HITting. (A single shared
    // combined-key dir, the previous design, orphaned every sibling's cache
    // whenever any one impl changed → a full layered rebuild on each edit.)
    // #1821: was hardcoded to ~/.cache/al-runner/workspace-deps regardless of --cache;
    // now follows the same isolation root al-out already honoured.
    var workspaceRoot = AlRunner.Infrastructure.CacheRoots.Resolve("workspace-deps");

    // Each impl dir is recorded as a synthetic-workspace dir (kept out of the
    // compile-time .app scanner — source-only .app, no SymbolReference.json →
    // AL1023) and prepended to the caches so it wins over a stale cached .app.
    var implDirs = new List<string>();
    // Every impl bundle's own .alpackages, collected so they can be added to the shared
    // caches returned to the dependent bundles. A dependent (e.g. a test bundle) resolves
    // its dep on an impl by following the impl's synthesized .app, which declares the impl's
    // OWN deps — including vendored/ISV apps (e.g. a licensing app) that live only in the
    // impl's .alpackages. Without these dirs the dependent's resolution fails with
    // "Dependency not found" for that transitive dep, so the impl never loads and its
    // namespaces read as unknown. (Compile symbols for the impl itself come from the
    // *.symbols.json sidecar; these dirs cover its transitive .app closure.)
    var implAlpackagesDirs = new List<string>();

    int emitted = 0;
    foreach (var implPath in sortedImpls)
    {
        if (!idByKey.TryGetValue(implPath, out var implId)) continue;

        // Remember the impl's SOURCE dir by AppId so NavApp.GetResource can serve its
        // app.json resourceFolders files when the impl loads as a dependency via the
        // synthesized workspace .app (which carries no /resources/ part).
        AlRunner.Patches.NavAppResourcePatches.RegisterSourceDirForApp(implId.AppId, implPath);

        // The impl bundle's own .alpackages (same dirs the main per-bundle compile scans),
        // reused for both this impl's symbol-emit and the dependent-visible caches below.
        var implBucketRootForPkgs = FindBucketRoot(implPath) ?? implPath;
        var thisImplAlpackages = Directory
            .EnumerateDirectories(implBucketRootForPkgs, ".alpackages", SearchOption.AllDirectories)
            .ToList();
        foreach (var d in thisImplAlpackages)
            if (!implAlpackagesDirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                implAlpackagesDirs.Add(d);

        // The workspace dirs of the impls ALREADY built by this loop, snapshotted before
        // this impl's own dir joins the list. sortedImpls is topologically ordered, so an
        // impl that this one depends on has necessarily been written by now — and #2178 was
        // that this snapshot was never taken, so every impl resolved against the same list
        // computed before the loop and could not see any of its siblings' output. A chain
        // three apps deep (test -> middle -> base, middle and base BOTH compiled from
        // source) therefore failed on the middle app with "Dependency not found:
        // <publisher>/<base app>" — naming a package the runner had written itself one
        // line earlier. Two apps never reproduced it: the single impl in a two-app bundle
        // has no impl dependency of its own.
        var priorImplDirs = implDirs.ToList();

        var implKey = ComputeSourceWorkspaceKey(new[] { implPath }, idByKey);
        var wsDir = Path.Combine(workspaceRoot, implKey[..12]);
        Directory.CreateDirectory(wsDir);
        if (!implDirs.Contains(wsDir, StringComparer.OrdinalIgnoreCase))
            implDirs.Add(wsDir);
        if (!workspaceDirsOut.Contains(wsDir, StringComparer.OrdinalIgnoreCase))
            workspaceDirsOut.Add(wsDir);

        var safePublisher = Sanitize(implId.Publisher);
        var safeName = Sanitize(implId.Name);
        var safeVer = implId.Version.ToString().Replace('.', '_');
        var appFileName = $"{safePublisher}_{safeName}_{safeVer}.app";
        var outPath = Path.Combine(wsDir, appFileName);
        var symBase = Path.Combine(wsDir, $"{safePublisher}_{safeName}_{safeVer}");
        var symbolsPath = symBase + ".symbols.json";
        var depsPath = symBase + ".symbols.deps.json";
        var hadApp = File.Exists(outPath);
        var hadSymbols = File.Exists(symbolsPath) && File.Exists(depsPath);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── Step 1: compile the impl's symbols (*.symbols.json + deps sidecar) ──
        // This is the COMPILE-time half of the handoff: the dependent bundle
        // (e.g. Customizations.Test) resolves the impl's symbols from this
        // *.symbols.json via BcCompiler's chained JsonSymbolReferenceLoader (the
        // workspace dir is registered through SetExtraSymbolDirs in Main). The
        // synthetic .app emitted in Step 2 carries source only (no
        // SymbolReference.json) and serves the RUNTIME half.
        //
        // Two traps navigated here:
        // (1) Corpus hang — SetPackageCacheFallback is scoped only to this call and
        //     immediately reset with ResetPackageCacheFallback() so it never leaks
        //     into subsequent per-bundle SetResolvedDeps compiles or corpus runs.
        // (2) Self-reference (AL0275 / AL1023) — when the impl is later compiled as
        //     its OWN bundle, BcCompiler.GetSharedReferences skips any JSON spec whose
        //     AppId == _currentAppId (set per bundle) and also skips the impl's own
        //     AppId from _resolvedDeps, so the impl's own symbols are invisible to
        //     its own compile.
        if (!hadSymbols)
        {
            try
            {
                // Resolve the impl's OWN dependency closure (declared + the implicit
                // Application/System roots from app.json) transitively, exactly like the
                // main per-bundle compile does. This replaces the former all-.app
                // SetPackageCacheFallback, which scanned EVERY package in the caches —
                // 134 apps / 353MB in the RS Extensions dir → ~215s per impl. The
                // Application closure pulls only BaseApp / System App / Business
                // Foundation (≈5 apps), so the symbol compile is fast and identical in
                // coverage (an app that uses BaseApp via namespace depends, implicitly,
                // on Application — never on the whole marketplace).
                // ScopeCurrentAppIdentity sets _currentAppId to the impl so
                // GetSharedReferences excludes the impl from its own specs (self-ref guard).
                // Include the impl bundle's OWN .alpackages in the resolver + symbol dirs —
                // the SAME dirs the main per-bundle compile uses (see the bundlePkgDirs path
                // in the compile loop). They carry the impl's vendored/declared deps (e.g.
                // an ISV licensing app) AND the Microsoft platform `System` app whose symbols
                // define the System.* / System.AI.* namespaces (e.g. the "Copilot Capability"
                // enum). Without them the layered impl symbol-emit resolves against only the
                // global --package-cache and fails where the standalone compile succeeds:
                // "Dependency not found" for a vendored dep, or AL0185/AL0133 "Copilot
                // Capability is missing". The impl compiles fine on its own BECAUSE it uses
                // these dirs; the layered impl-emit must too.
                // ORIGINAL package cache dirs + the impl's .alpackages (NOT extendedCaches,
                // which includes wsDir — wsDir has no valid .app yet at this point anyway).
                var implSymbolDirs = thisImplAlpackages.Concat(packageCacheDirs).Distinct().ToList();
                // RESOLUTION set (#2178): the compile set above, PLUS the workspace dirs of
                // the impls already built by this loop and their .alpackages. This is what
                // lets an impl declare a dependency on ANOTHER impl in the same invocation —
                // the synthetic .app the earlier iteration wrote lives only in its workspace
                // dir, which is otherwise not visible until this whole function returns
                // extendedCaches to the per-bundle compiles below.
                //
                // Deliberately kept SEPARATE from implSymbolDirs, which is what
                // SetResolvedDeps hands to BC's own .app scanner: a synthetic workspace .app
                // carries no SymbolReference.json and makes that scanner report AL1023
                // "package not valid" for the whole compilation. The compile-time half of an
                // earlier impl travels through its *.symbols.json sidecar instead, via
                // SetExtraSymbolDirs below — exactly the split the per-bundle compile in Main
                // already uses (see the SetExtraSymbolDirs(layeredWorkspaceDirs) call sites).
                var implResolveDirs = implSymbolDirs
                    .Concat(priorImplDirs)
                    .Concat(implAlpackagesDirs)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var implResolver = new DependencyResolver(implResolveDirs);
                var implDeps = implResolver.Resolve(implId.Dependencies);
                BcCompiler.SetResolvedDeps(implDeps, implSymbolDirs);
                // AFTER SetResolvedDeps, which resets _extraSymbolDirs. Passing an empty
                // list is not the same as not calling it at all — the previous impl's call
                // has already been cleared by SetResolvedDeps, so this is a no-op either way
                // and the guard is only here to avoid churning the loader signature.
                if (priorImplDirs.Count > 0)
                    BcCompiler.SetExtraSymbolDirs(priorImplDirs);
                using (BcCompiler.ScopeCurrentAppIdentity(implId.AppId, implId.Publisher, implId.Version))
                    new BcCompiler().EmitDepSymbols(new[] { implPath }, implId.Name, implId.AppId, implId.Publisher, implId.Version, symbolsPath, implPath);
                // Declare the FULL compile closure — the resolved deps (real AppIds/versions)
                // UNIONed with the Microsoft platform apps vendored in the impl's own
                // .alpackages. Filtering to non-Optional declared deps drops the implicit
                // platform roots (System Application, platform System, …) that carry types
                // like "Temp Blob"/"Copilot Capability" appearing in the impl's public
                // signatures, degrading them to __MissingTypeSymbol__ downstream. See #1546.
                DepsSidecarWriter.Write(
                    depsPath, implId.Publisher, implId.Name, implId.Version, implId.AppId,
                    DepsSidecarWriter.BuildClosure(
                        implDeps.Select(d => new DepsSidecarWriter.DepEntry(
                            d.Manifest.Publisher, d.Manifest.Name, d.Manifest.Version, d.Manifest.AppId)),
                        ScanVendoredPlatformApps(thisImplAlpackages),
                        implId.AppId));
            }
            catch (Exception ex)
            {
                // Loud failure per repo rule — the dependent bundle cannot compile
                // against this impl without its symbols, so don't continue silently.
                throw new InvalidOperationException(
                    $"[layered] Failed to emit symbols for impl '{implId.Name}' from {implPath}: {ex.Message}", ex);
            }
        }

        // ── Step 2: emit the .app — runtime/identity package ONLY, NO embedded
        // SymbolReference.json ─────────────────────────────────────────────────
        // The synthetic NAVX package we emit (8-byte header) is faithful enough for
        // our own AppLoader/DependencyResolver (identity + runtime source extraction),
        // but it is NOT a byte-valid MS NAVX package (real MS apps use a 40-byte header
        // with version + content-hash + trailing magic). Embedding SymbolReference.json
        // makes BC's *own* package reader try to load the .app as a symbol-reference
        // package, which then fails its header validation with AL1023 "package not valid".
        //
        // Compile-time symbol resolution does NOT need the embed: it is served by the
        // *.symbols.json sidecar written above (Step 1), picked up by BcCompiler's
        // chained JsonSymbolReferenceLoader over the workspace dir — exactly the
        // mechanism BuildSiblingSourceDeps uses for the (green) corpus internalsVisibleTo
        // fixture. So we pass null here and let the sidecar carry the symbols.
        if (!hadApp)
        {
            try
            {
                AlRunner.Infrastructure.InProcessAppPackager.EmitAppPackageToFile(
                    implPath, implId, outPath, symbolReferenceJson: null);
            }
            catch (Exception ex)
            {
                // Loud failure per repo rule — never silently continue.
                throw new InvalidOperationException(
                    $"[layered] Failed to emit impl package '{implId.Name}' from {implPath}: {ex.Message}", ex);
            }
        }

        sw.Stop();
        var info = new FileInfo(outPath);
        var cacheVerb = hadApp && hadSymbols ? "cache HIT" : "WROTE";
        Console.WriteLine($"[layered] {cacheVerb} {implId.Name} {implId.Version} → {appFileName} (src .app + sidecar symbols, {info.Length} bytes, {sw.ElapsedMilliseconds}ms)");
        emitted++;
    }

    if (emitted > 0)
        Console.WriteLine($"[layered] pre-built {emitted} impl package(s) in-process across {implDirs.Count} cache dir(s)");

    // Impl dirs first (win over any stale cached .app), then the original caches, then the
    // impl bundles' own .alpackages (last, so they never shadow a package-cache resolution —
    // they only ADD the impls' transitive/vendored .app closure a dependent needs to resolve
    // its dep on an impl). Distinct preserves order and drops any dir already listed.
    var extendedCaches = new List<string>(implDirs);
    extendedCaches.AddRange(packageCacheDirs);
    extendedCaches.AddRange(implAlpackagesDirs);
    return extendedCaches.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '/', '\\' }).ToArray();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(Array.IndexOf(bad, ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }
}

// Topological sort: return items in dependency-first order.
// Simple Kahn's algorithm over the impl subset.
// ── Sibling source-dependency pre-pass ────────────────────────────────────
// For a dependency declared in a bundle's app.json that has no compiled .app in
// any package cache, look for a matching AL-source app in a sibling directory
// (the parent of the bundle root), compile it in-process to a .app, and prepend
// a fresh workspace cache dir so the per-bundle DependencyResolver finds it like
// any other dep. This is what lets the corpus's two-app internalsVisibleTo
// fixture (tests/.../al-language-internals-fixture next to tests/.../al-language)
// resolve. Inert when no declared dep matches a sibling source app.
static List<string> BuildSiblingSourceDeps(List<string> bundles, List<string> packageCacheDirs, List<string> workspaceDirsOut)
{
    // 1. Collect each bundle's declared (non-implicit) deps + their bundle roots.
    var neededDeps = new List<DependencyRef>();
    var bundleRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var bundle in bundles)
    {
        var abs = Path.GetFullPath(bundle);
        var appJson = Path.Combine(abs, "app.json");
        if (!File.Exists(appJson))
        {
            var root = FindBucketRoot(abs);
            if (root != null) appJson = Path.Combine(root, "app.json");
        }
        if (!File.Exists(appJson)) continue;
        var id = AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(appJson);
        if (id == null) continue;
        bundleRoots.Add(Path.GetFullPath(Path.GetDirectoryName(appJson)!));
        // Skip Optional (implicit Microsoft Application/System) roots — those live
        // in the package caches, never as a sibling source app.
        neededDeps.AddRange(id.Dependencies.Where(d => !d.Optional));
    }
    if (neededDeps.Count == 0) return packageCacheDirs;

    // 2. Discover candidate source apps in the parent dir of each bundle root.
    var sourceApps = new Dictionary<string, AlRunner.Infrastructure.BundleIdentity>(StringComparer.OrdinalIgnoreCase);
    foreach (var bundleRoot in bundleRoots)
    {
        var parent = Path.GetDirectoryName(bundleRoot);
        if (parent == null || !Directory.Exists(parent)) continue;
        foreach (var sub in Directory.EnumerateDirectories(parent))
        {
            var subAbs = Path.GetFullPath(sub);
            if (bundleRoots.Contains(subAbs)) continue; // not a bundle itself
            var aj = Path.Combine(subAbs, "app.json");
            if (!File.Exists(aj)) continue;
            var sid = AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(aj);
            if (sid != null) sourceApps[subAbs] = sid;
        }
    }
    if (sourceApps.Count == 0) return packageCacheDirs;

    var existingPackageDirs = bundleRoots
        .SelectMany(root => Directory.EnumerateDirectories(root, ".alpackages", SearchOption.AllDirectories))
        .Concat(packageCacheDirs)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    // 3. Match needed deps to sibling source apps (by AppId, else Name+Publisher), but
    // only when the dependency is not already available as an .app. Real projects often
    // keep a packaged copy under .alpackages; that package has authoritative symbols
    // (including tableextension field merging) while the sibling source is only needed
    // as a fallback when no package exists.
    // #2178: worklist, not a single pass over the bundles' own declared deps. A sibling
    // source app that gets BUILT here brings its own declared dependencies into scope, and
    // those may themselves only exist as sibling source apps. Matching one level deep left
    // the second level undiscovered entirely, so the chain failed inside this function's
    // own per-dep resolve below with "Dependency not found" / a provisioning-gap report for
    // an app sitting right next to the one being built. Only deps of apps we are actually
    // going to build are enqueued: a dep already satisfied by a real .app package resolves
    // through that package's own closure, exactly as before.
    var toBuild = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var depQueue = new Queue<DependencyRef>(neededDeps);
    var seenDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    while (depQueue.Count > 0)
    {
        var dep = depQueue.Dequeue();
        if (!seenDeps.Add($"{dep.AppId:N}|{dep.Publisher}/{dep.Name}")) continue;
        var packageAvailable = IsDependencyPackageAvailable(dep, existingPackageDirs);
        foreach (var (dir, sid) in sourceApps)
        {
            bool match = (dep.AppId != Guid.Empty && dep.AppId == sid.AppId) ||
                (string.Equals(dep.Name, sid.Name, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(dep.Publisher, sid.Publisher, StringComparison.OrdinalIgnoreCase));
            if (!match) continue;
            AlRunner.Patches.RecordPatches.AddSourceDir(dir);
            // Remember the sibling source dir by AppId so NavApp.GetResource can serve
            // its resourceFolders files even when the dep loads via the synthetic
            // workspace-deps .app (which carries no /resources/ part).
            AlRunner.Patches.NavAppResourcePatches.RegisterSourceDirForApp(sid.AppId, dir);
            if (!packageAvailable)
            {
                toBuild.Add(dir);
                foreach (var transitive in sid.Dependencies.Where(d => !d.Optional))
                    depQueue.Enqueue(transitive);
            }
        }
    }
    if (toBuild.Count == 0) return packageCacheDirs;

    static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '/', '\\' }).ToArray();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(Array.IndexOf(bad, ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }

    // 4. Topo-sort (deps before dependents) + compile each dep to its OWN
    // deterministic workspace dir, keyed on that dep's own sources + dep
    // identities. Editing one source dep then only invalidates its own cache —
    // unchanged sibling source deps keep cache-HITting. (A single shared
    // combined-key dir orphaned every sibling whenever any one changed.)
    var sorted = TopologicalSort(toBuild.ToList(), sourceApps);
    // #1821: was hardcoded to ~/.cache/al-runner/workspace-deps regardless of --cache;
    // now follows the same isolation root al-out already honoured.
    var workspaceRoot = AlRunner.Infrastructure.CacheRoots.Resolve("workspace-deps");
    // Synthetic-workspace dirs (per dep): source-only .apps (no SymbolReference.json)
    // + symbols.json sidecars. Kept out of the compile-time .app scanner (AL1023)
    // but used for runtime resolution + symbols.json handoff. See Main.
    var depDirs = new List<string>();
    // The dependent bundles' own `.alpackages` carry the Microsoft platform symbol
    // closure (Base Application / System Application / Business Foundation / …) as real
    // .app files committed alongside the corpus. On CI, packageCacheDirs is EMPTY
    // (artifacts live in the symbols/service-tier dirs, not bcartifacts.cache), so the
    // Base App a source-dep tableextension extends is ONLY resolvable from here. Index
    // these for the source-dep dependency resolution + symbol loader below.
    var bundleAlpackagesDirs = bundles
        .Where(Directory.Exists)
        .SelectMany(b => Directory.EnumerateDirectories(b, ".alpackages", SearchOption.AllDirectories))
        .Distinct()
        .ToList();
    var resolveDirs = bundleAlpackagesDirs.Concat(packageCacheDirs).Distinct().ToList();
    int emitted = 0;
    foreach (var dir in sorted)
    {
        if (!sourceApps.TryGetValue(dir, out var sid)) continue;
        // The workspace dirs of the source deps already built by this loop, snapshotted
        // before this dep's own dir joins the list. `sorted` is topological, so a dep this
        // one depends on has necessarily been written by now — see the identical snapshot
        // in RunLayeredPrePass and #2178.
        var priorDepDirs = depDirs.ToList();
        // Per-dep cache dir keyed on THIS dep's own sources + dep identities.
        var depKey = ComputeSourceWorkspaceKey(new[] { dir }, sourceApps);
        var wsDir = Path.Combine(workspaceRoot, depKey[..12]);
        Directory.CreateDirectory(wsDir);
        if (!depDirs.Contains(wsDir, StringComparer.OrdinalIgnoreCase))
            depDirs.Add(wsDir);
        if (!workspaceDirsOut.Contains(wsDir, StringComparer.OrdinalIgnoreCase))
            workspaceDirsOut.Add(wsDir);
        // Register the source-dep's AL dir for runtime metadata parsing, so its
        // tableextensions on Base App tables (e.g. a cross-app tableextension adding a
        // field to "Item Journal Batch") get merged into the base table's NCLMetaTable.
        // Without this, runtime field lookup throws "extension field N not found in
        // NCLMetaTable". This runs before RecordPatches.Register(), so the dir is parsed
        // during Register (not immediately) — see ParseAllRegisteredSourceFiles.
        // Compile-time visibility is handled separately by the symbols.json emit below.
        AlRunner.Patches.RecordPatches.AddSourceDir(dir);
        var appFileName = $"{Sanitize(sid.Publisher)}_{Sanitize(sid.Name)}_{sid.Version.ToString().Replace('.', '_')}.app";
        var outPath = Path.Combine(wsDir, appFileName);
        var hadApp = File.Exists(outPath);
        if (!hadApp)
        {
            try
            {
                AlRunner.Infrastructure.InProcessAppPackager.EmitAppPackageToFile(dir, sid, outPath);
            }
            catch (Exception ex)
            {
                // Loud failure per repo rule — never silently continue.
                throw new InvalidOperationException(
                    $"[source-dep] Failed to emit source dependency '{sid.Name}' from {dir}: {ex.Message}", ex);
            }
        }
        // Compile-visible half: emit the dep's AL symbols (*.symbols.json) + deps
        // sidecar so the DEPENDENT app can COMPILE against it. The synthetic .app
        // above carries no SymbolReference.json, so without this the dep is
        // runtime-loadable but invisible to the compiler (AL0185). BcCompiler's
        // GetSharedReferences chains a JsonSymbolReferenceLoader over the workspace
        // dir to pick these up. Revived from main's DepCompiler / SymbolJson.
        // Resolve THIS dep's own dependency closure (declared + transitive) against the
        // dependent bundles' .alpackages + packageCacheDirs, then hand it to BcCompiler —
        // exactly like RunLayeredPrePass and the main per-bundle compile. Without this, a
        // source dep that extends a Base App object (e.g. a tableextension on "Item Journal
        // Batch") cannot resolve its target → AL0247 → BC's converter NREs → crash. The
        // resolver produces CONCRETE resolved manifests (real version + path) from the
        // .alpackages closure, which is present on CI where packageCacheDirs is empty.
        // NOT the all-packages SetPackageCacheFallback (scans every .app, hangs the corpus);
        // Resolve pulls only this dep's declared closure (BaseApp / System App / …).
        // ScopeCurrentAppIdentity sets _currentAppId so GetSharedReferences excludes the dep
        // from its own specs (self-ref guard). Reset by the per-bundle SetResolvedDeps below.
        // #2178: resolve against the workspace dirs written for the source deps BUILT
        // BEFORE this one as well, so a source dep may itself depend on another source dep.
        // As in RunLayeredPrePass, those dirs are kept out of the set handed to
        // SetResolvedDeps — BC's .app scanner reports AL1023 on a synthetic .app with no
        // SymbolReference.json — and reach the compiler as *.symbols.json through
        // SetExtraSymbolDirs instead.
        var depResolveDirs = resolveDirs
            .Concat(priorDepDirs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var depResolver = new DependencyResolver(depResolveDirs);
        var resolvedDepDeps = depResolver.Resolve(sid.Dependencies);
        BcCompiler.SetResolvedDeps(resolvedDepDeps, resolveDirs);
        // AFTER SetResolvedDeps, which resets _extraSymbolDirs.
        if (priorDepDirs.Count > 0)
            BcCompiler.SetExtraSymbolDirs(priorDepDirs);
        var symBase = Path.Combine(wsDir, $"{Sanitize(sid.Publisher)}_{Sanitize(sid.Name)}_{sid.Version.ToString().Replace('.', '_')}");
        var symbolsPath = symBase + ".symbols.json";
        var depsPath = symBase + ".symbols.deps.json";
        var hadSymbols = File.Exists(symbolsPath) && File.Exists(depsPath);
        if (!hadSymbols)
        {
            try
            {
                using (BcCompiler.ScopeCurrentAppIdentity(sid.AppId, sid.Publisher, sid.Version))
                    new BcCompiler().EmitDepSymbols(new[] { dir }, sid.Name, sid.AppId, sid.Publisher, sid.Version, symbolsPath, dir);
                // Full compile closure (resolved deps ∪ vendored platform apps) — see the
                // impl-bundle site above and #1546. Filtering to non-Optional declared deps
                // would drop the implicit platform roots whose types appear in this dep's
                // public surface, yielding __MissingTypeSymbol__ in the dependent compile.
                var depOwnAlpackages = Directory.EnumerateDirectories(
                    FindBucketRoot(dir) ?? dir, ".alpackages", SearchOption.AllDirectories);
                DepsSidecarWriter.Write(
                    depsPath, sid.Publisher, sid.Name, sid.Version, sid.AppId,
                    DepsSidecarWriter.BuildClosure(
                        resolvedDepDeps.Select(d => new DepsSidecarWriter.DepEntry(
                            d.Manifest.Publisher, d.Manifest.Name, d.Manifest.Version, d.Manifest.AppId)),
                        ScanVendoredPlatformApps(depOwnAlpackages),
                        sid.AppId));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"[source-dep] Failed to emit symbols for '{sid.Name}' from {dir}: {ex.Message}", ex);
            }
        }

        var info = new FileInfo(outPath);
        var cacheVerb = hadApp && hadSymbols ? "cache HIT" : "WROTE";
        Console.WriteLine($"[source-dep] {cacheVerb} {sid.Name} {sid.Version} → {appFileName} (+symbols, {info.Length} bytes)");
        emitted++;
    }
    if (emitted == 0) return packageCacheDirs;
    var extended = new List<string>(depDirs);
    extended.AddRange(packageCacheDirs);
    return extended;
}

static bool IsDependencyPackageAvailable(DependencyRef dep, IReadOnlyList<string> packageDirs)
{
    foreach (var dir in packageDirs)
    {
        if (!Directory.Exists(dir)) continue;
        foreach (var file in Directory.EnumerateFiles(dir, "*.app", SearchOption.AllDirectories))
        {
            var manifest = AlRunner.AppLoader.ReadManifest(file);
            if (manifest == null || manifest.Version < dep.Version)
                continue;
            var idMatches = dep.AppId != Guid.Empty && dep.AppId == manifest.AppId;
            var nameMatches = string.Equals(dep.Name, manifest.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(dep.Publisher, manifest.Publisher, StringComparison.OrdinalIgnoreCase);
            if (idMatches || nameMatches)
                return true;
        }
    }

    return false;
}

static string ComputeSourceWorkspaceKey(
    IReadOnlyList<string> sortedDirs,
    IReadOnlyDictionary<string, AlRunner.Infrastructure.BundleIdentity> sourceApps)
{
    using var sha = System.Security.Cryptography.SHA256.Create();
    using var ms = new MemoryStream();
    void WriteLine(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\n");
        ms.Write(bytes, 0, bytes.Length);
    }

    // v2 (issue #1815): runner fingerprint switched from mtime+length to a content hash
    // (mtime moved on every CI rebuild, so a persisted cache could never hit), and an
    // explicit bc:<version> line was added (a content hash alone is identical across
    // every BC-version CI leg building the same commit, so without it all legs would
    // collide on one cache entry). v1 entries carried neither and must not be served.
    WriteLine("schema:v2");
    AlRunner.Infrastructure.RunnerFingerprint.WriteKeyLines(WriteLine);

    foreach (var dir in sortedDirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
    {
        if (!sourceApps.TryGetValue(dir, out var id)) continue;
        WriteLine($"app:{id.AppId}:{id.Publisher}:{id.Name}:{id.Version}");
        foreach (var dep in id.Dependencies.OrderBy(d => $"{d.Publisher}/{d.Name}/{d.Version}/{d.AppId}", StringComparer.OrdinalIgnoreCase))
            WriteLine($"dep:{dep.AppId}:{dep.Publisher}:{dep.Name}:{dep.Version}");
        var files = Directory.EnumerateFiles(dir, "*.al", SearchOption.AllDirectories)
            .Append(Path.Combine(dir, "app.json"))
            .Where(File.Exists)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            using var fs = File.OpenRead(file);
            WriteLine($"file:{Path.GetRelativePath(dir, file)}:{Convert.ToHexString(sha.ComputeHash(fs))}");
        }
    }

    ms.Position = 0;
    return Convert.ToHexString(sha.ComputeHash(ms)).ToLowerInvariant();
}

static List<string> TopologicalSort(
    List<string> implPaths,
    Dictionary<string, AlRunner.Infrastructure.BundleIdentity> idByKey)
{
    var result = new List<string>();
    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    void Visit(string path)
    {
        if (!visited.Add(path)) return;
        if (!idByKey.TryGetValue(path, out var id)) return;
        // Visit impl dependencies first.
        foreach (var dep in id.Dependencies)
        {
            var depImpl = implPaths.FirstOrDefault(p =>
            {
                if (!idByKey.TryGetValue(p, out var pid)) return false;
                return (dep.AppId != Guid.Empty && dep.AppId == pid.AppId) ||
                       (string.Equals(dep.Name, pid.Name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(dep.Publisher, pid.Publisher, StringComparison.OrdinalIgnoreCase));
            });
            if (depImpl != null) Visit(depImpl);
        }
        result.Add(path);
    }

    foreach (var p in implPaths) Visit(p);
    return result;
}

// Expands user-provided --package-cache dirs: returns each dir that exists, plus
// any bcartifacts platform/Applications and platform/ModernDev dirs auto-discovered
// from the same artifact version root. Deduplicates so the same dir isn't listed
// twice if the user already passed it explicitly.
// This ensures that even when only the ISV .alpackages and w1/Extensions are passed,
// the higher-version platform test packages (e.g. Tests-TestLibraries v28.1 in
// platform/Applications/BaseApp/Test) are visible to the version-aware resolver.
static IEnumerable<string> ExpandPackageCacheDirs(IEnumerable<string> userDirs)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var dir in userDirs)
    {
        if (!Directory.Exists(dir)) continue;
        if (seen.Add(dir)) yield return dir;
        foreach (var extra in BcArtifactTestDirs(dir))
            if (seen.Add(extra)) yield return extra;
    }
}

// Auto-discovers bcartifacts platform dirs from an explicit --package-cache path.
// Gated to paths inside ~/.bcartifacts.cache/ so corpus runs and non-bcartifacts
// cache dirs are unaffected. Walks up from the given dir to find the artifact
// version root (the child of sandbox/<version>/ that has a platform/ subdirectory)
// and yields platform/Applications and platform/ModernDev if they exist.
static IEnumerable<string> BcArtifactTestDirs(string cacheDir)
{
    // Cross-platform home (POSIX HOME is null on Windows — see AlRunnerPaths).
    var home = AlRunner.Infrastructure.AlRunnerPaths.UserHome;
    if (string.IsNullOrEmpty(home)) yield break;

    var bcRoot = Path.GetFullPath(Path.Combine(home, ".bcartifacts.cache"));
    var full = Path.GetFullPath(cacheDir);

    // Only auto-expand dirs that are inside the bcartifacts cache root.
    if (!full.StartsWith(bcRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(full, bcRoot, StringComparison.OrdinalIgnoreCase))
        yield break;

    // Walk up from cacheDir toward bcRoot, stopping at the first dir that has
    // a platform/ subdirectory — that is the artifact version root.
    var dir = full;
    while (dir.Length > bcRoot.Length)
    {
        var platApps = Path.Combine(dir, "platform", "Applications");
        if (Directory.Exists(platApps))
        {
            yield return platApps;
            yield break;
        }
        var parent = Path.GetDirectoryName(dir);
        if (parent == null || parent == dir) yield break;
        dir = parent;
    }
}

// ── The one shadow-re-exec decision ────────────────────────────────────────────────
// Returns null when this process may proceed in place, or the child's exit code when it
// handed off to a shadow-runtime process (in which case the caller must return that code
// immediately and touch no BC type).
//
// #2065: this used to be written out inline at the single call site in the main bundle-run
// flow, and `--precompile` — which dispatches near the top of Main, LONG before that call
// site — had no equivalent. It went straight to NclCecilRewrite.RewriteInPlace against
// `AppContext.BaseDirectory`, which on any install that does not ship Ncl.dll (i.e. every
// install since #2023/#2026, including AlRunner's own build output, where
// Directory.Build.targets strips it) CREATES the file instead of replacing one. Two
// separate costs, and neither is theoretical — both were measured while closing #2065:
//
//   * It does not help the process doing the writing. CoreCLR fixes the
//     trusted-platform-assemblies list in the native host from the literal on-disk contents
//     of AppContext.BaseDirectory BEFORE any managed code runs (see NclShadowRuntime's class
//     doc), so a file written a few statements into RunPrecompile is already too late. The
//     `--precompile` run only worked at all because DependencyLoader's Resolving fallback
//     then happened to serve the freshly written copy — and on a genuine cache HIT there is
//     no re-exec to recover, so the whole thing rested on a side effect.
//   * It permanently changes what NclShadowRuntime.NeedsShadow answers for that directory,
//     so every LATER invocation from it silently stops taking the hop. The runner then
//     behaves differently in a used checkout than in a clean one — the exact class of
//     problem a stale shadow cache caused when it faked a packaging bug here.
//
// Two code paths writing the same state where only one held the invariant is the defect, so
// there is now exactly one place that decides. Any future pre-dispatch subcommand that needs
// BC types calls this first.
static int? TryShadowReexec(string? variantSwapDir)
{
    if (!(AlRunner.Infrastructure.NclShadowRuntime.NeedsShadow(AppContext.BaseDirectory) || variantSwapDir != null)
        || Environment.GetEnvironmentVariable("AL_RUNNER_NCL_SHADOW_DONE") == "1")
        return null;

    var srcDirForShadow = AlRunner.Infrastructure.BcArtifacts.ServiceTierDir;
    var shadowDll = AlRunner.Infrastructure.NclShadowRuntime.EnsureShadowDir(
        AppContext.BaseDirectory, srcDirForShadow, variantSwapDir);
    var dotnetMuxer = AlRunner.Infrastructure.NclShadowRuntime.FindDotnetMuxer();

    var psi = new System.Diagnostics.ProcessStartInfo(dotnetMuxer) { UseShellExecute = false };
    psi.ArgumentList.Add("exec");
    psi.ArgumentList.Add(shadowDll);
    // argv[0] is THIS process's own entry path (apphost exe, or the dll path the
    // dotnet muxer forwarded) — never a user arg, and irrelevant here since we've
    // already picked the child's entry point explicitly above.
    var argv = RewriteArtifactPathArg(Environment.GetCommandLineArgs());
    foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
    psi.Environment["AL_RUNNER_NCL_SHADOW_DONE"] = "1";

    Console.Error.WriteLine(variantSwapDir != null
        // #2034: this line explains why a second process is about to launch — a
        // genuinely operational fact, not an internal Cecil-rewrite diagnostic — so it
        // uses the exempted `[reexec]` tag rather than `[Cecil]`. Under `[Cecil]`, Log's
        // filter suppressed a real, live re-exec silently: the shadow dir was built, the
        // child launched, and nothing on stderr said why.
        ? "[reexec] Re-execing into a shadow runtime dir with the matching BC-minor engine variant"
        : "[reexec] Ncl.dll not shipped in this install — re-execing into a shadow runtime dir that has it");
    AlRunner.Infrastructure.PhaseLog.MarkReexecParent();
    using var shadowChild = System.Diagnostics.Process.Start(psi)!;
    shadowChild.WaitForExit();
    return shadowChild.ExitCode;
}

// Rewrite a forwarded argv so that `--artifact-path <dir>` becomes `--bc-version <ver>`
// when <dir> is a version-named child of the standard artifacts cache. Re-exec children
// then take the byte-identical code path as `--bc-version` (the explicit-root selection
// branch otherwise perturbs BC's R2R-precompiled startup bind enough to trigger a
// teardown access violation — see MEMORY.md "R2R-layout-perturbation native AV"). A
// path OUTSIDE the standard cache is left as `--artifact-path` (the child needs it).
static string[] RewriteArtifactPathArg(string[] argv)
{
    var outv = new List<string>(argv.Length);
    for (int i = 0; i < argv.Length; i++)
    {
        if (argv[i] == "--artifact-path" && i + 1 < argv.Length)
        {
            string? ver = null;
            try { ver = AlRunner.Infrastructure.BcArtifacts.TryTranslateArtifactPathToVersion(argv[i + 1]); }
            catch (InvalidOperationException) { ver = null; }
            if (ver != null) { outv.Add("--bc-version"); outv.Add(ver); i++; continue; }
        }
        outv.Add(argv[i]);
    }
    return outv.ToArray();
}

// Default cache: the selected BC version (BcArtifacts.SelectedVersion — latest in the
// artifacts cache, or the --bc-version / --artifact-path override) under
// ~/.bcartifacts.cache/sandbox/ + the curated symbol set under
// ~/.local/share/al-runner/symbols/. These two trees may carry a different *patch*
// level than the artifacts tree (e.g. sandbox 28.1.x vs artifacts 28.1.y), so we match
// on the selected major.minor prefix and pick the highest such version (System.Version
// sort — the old StringComparer.Ordinal sort mis-ordered e.g. "28.1.9" > "28.1.10").
static IEnumerable<string> DefaultPackageCacheDirs()
{
    // Cross-platform home (POSIX HOME is null on Windows — see AlRunnerPaths).
    var home = AlRunner.Infrastructure.AlRunnerPaths.UserHome;
    if (string.IsNullOrEmpty(home)) yield break;

    var sel = AlRunner.Infrastructure.BcArtifacts.SelectedVersion;
    var mmPrefix = $"{sel.Major}.{sel.Minor}";

    var bcRoot = Path.Combine(home, ".bcartifacts.cache", "sandbox");
    var bcLatest = SelectVersionDirOrNull(bcRoot, mmPrefix);
    if (bcLatest != null)
    {
        var w1Ext = Path.Combine(bcLatest, "w1", "Extensions");
        if (Directory.Exists(w1Ext)) yield return w1Ext;
        var platApps = Path.Combine(bcLatest, "platform", "Applications");
        if (Directory.Exists(platApps)) yield return platApps;
        // The `System` platform-symbols app (Microsoft/System) ships here, not in
        // w1/Extensions. The resolver scans *.app recursively, so the ModernDev
        // root suffices despite the version-numbered / case-varying subpath.
        var modernDev = Path.Combine(bcLatest, "platform", "ModernDev");
        if (Directory.Exists(modernDev)) yield return modernDev;
    }

    var symRoot = Path.Combine(home, ".local", "share", "al-runner", "symbols");
    var symLatest = SelectVersionDirOrNull(symRoot, mmPrefix);
    if (symLatest != null) yield return symLatest;

    // The provisioned MS test toolkit for the SELECTED version (see
    // EnsureTestToolkitProvisioned). Scanned by default so a test bundle whose app.json
    // depends on Library Assert / Test Runner / Any resolves them without --package-cache.
    var testApps = AlRunner.Infrastructure.ProvisioningCheck.TestAppsDirFor(
        AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, sel.ToString());
    if (Directory.Exists(testApps)) yield return testApps;

    // The provisioned Microsoft platform R2R runtime apps for the SELECTED version (see
    // --auto-provision, issue #1653). Scanned by default so a --auto-provision run on one
    // invocation is visible on a later run that omits --auto-provision.
    var platformApps = AlRunner.Infrastructure.ProvisioningCheck.PlatformAppsDirFor(
        AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, sel.ToString());
    if (Directory.Exists(platformApps)) yield return platformApps;
}

// Highest version-named child of <root> matching <versionPrefix> (System.Version sort),
// or null if the root is absent or has no matching version dir. Unlike the artifact
// helper this returns null rather than throwing: these caches are optional augmentation
// of the artifact dir, and a missing sandbox/symbols tree is not fatal (the corpus runs
// from the artifact dir alone). The artifact dir itself fails loud via BcArtifacts.
static string? SelectVersionDirOrNull(string root, string versionPrefix)
{
    if (!Directory.Exists(root)) return null;
    try
    {
        return AlRunner.Infrastructure.BcArtifacts.SelectArtifactVersionDir(root, versionPrefix);
    }
    catch (InvalidOperationException)
    {
        // No matching version in this optional cache — fine.
        return null;
    }
}

/// <summary>
/// Issue #1996 (AC #4/#5): before resolving "latest" from the CDN index for a manifest-app
/// download, prefer a full version ALREADY cached under <paramref name="artifactsRootDir"/>
/// whose major.minor matches <paramref name="majorMinorPrefix"/> and whose needed set(s) are
/// already complete — highest patch first. Returns null when no such warm version exists
/// (the caller then falls back to CDN resolution). Reuse-only: never downloads.
///
/// Issue #2003: "complete" used to mean presence alone — a warm set at the same
/// major.minor but an OLDER patch than the bundle's app.json declares (<paramref
/// name="versionFloors"/>) was reused unconditionally, and the run failed later on a
/// compile diagnostic that pointed at the test code instead of the stale provisioning.
/// NoFallbackPlatformAppsPresent/TestToolkitPresent now reject a candidate whose found app
/// is below its floor, so this loop naturally skips it and falls through to the next
/// (older) candidate, and eventually to CDN resolution if none qualify. When a candidate is
/// rejected specifically because it was found-but-stale (not merely absent), <paramref
/// name="onRejected"/> is told which app, the version found, and the version required —
/// a silent re-download is better than reusing something too old, but a message is better
/// than both. Null/empty <paramref name="versionFloors"/> (the default) reproduces the old
/// presence-only behavior verbatim (AC #4).
/// </summary>
static string? FindWarmProvisionedVersion(
    string artifactsRootDir, string majorMinorPrefix, bool needPlatform, bool needTest,
    IReadOnlyDictionary<string, Version>? versionFloors = null,
    Action<string>? onRejected = null)
{
    if (!Directory.Exists(artifactsRootDir)) return null;
    var candidates = Directory.EnumerateDirectories(artifactsRootDir)
        .Select(Path.GetFileName)
        .Where(n => !string.IsNullOrEmpty(n)
            && (n == majorMinorPrefix || n!.StartsWith(majorMinorPrefix + ".", StringComparison.Ordinal)))
        .Select(n => (Name: n!, Ver: Version.TryParse(n, out var v) ? v : null))
        .Where(t => t.Ver != null)
        .OrderByDescending(t => t.Ver)
        .Select(t => t.Name);

    foreach (var name in candidates)
    {
        var platformDir = AlRunner.Infrastructure.ProvisioningCheck.PlatformAppsDirFor(artifactsRootDir, name);
        var testDir = AlRunner.Infrastructure.ProvisioningCheck.TestAppsDirFor(artifactsRootDir, name);
        var platformOk = !needPlatform || AlRunner.Infrastructure.ProvisioningCheck.NoFallbackPlatformAppsPresent(
            new[] { platformDir }, versionFloors);
        var testOk = !needTest || AlRunner.Infrastructure.ProvisioningCheck.TestToolkitPresent(
            new[] { testDir }, versionFloors);
        if (platformOk && testOk) return name;

        if (versionFloors is { Count: > 0 } && onRejected != null)
        {
            var violations = AlRunner.Infrastructure.ProvisioningCheck.FindVersionFloorViolations(
                new[] { platformDir, testDir }, versionFloors);
            foreach (var v in violations)
                onRejected(
                    $"[provision] warm set '{name}' rejected: '{v.AppName}' found at v{v.FoundVersion}, " +
                    $"but this bundle's app.json requires >= v{v.RequiredVersion}.");
        }
    }
    return null;
}

// Walks up from <bundlePath> until it finds a dir containing app.json.
// Returns null if none found before /tests/ or filesystem root.
/// <summary>
/// Write a <c>*.symbols.json</c> (plus its dependency sidecar) for every app in this bundle
/// that another app in the SAME bundle depends on, and register the directory holding them
/// with the compiler so those later apps can reference them.
///
/// <paramref name="appGroups"/> must already be in topological order — the symbols for a
/// dep-of-a-dep have to exist before the app that needs them is compiled. Only genuine
/// sibling-dependency targets are emitted: each one costs an extra compile, and a bundle
/// with no in-bundle dependencies (the corpus, every single-app bundle) does no work at all.
///
/// Failures are loud but non-fatal: without the symbols the dependent app fails to compile
/// with AL0185, which is exactly the state this fixes, and the emit-retry already reports
/// the dropped objects.
///
/// <paramref name="bundleResolvedDeps"/> is this bundle's resolved Microsoft-platform
/// dependency closure (the same set every suite under a parent-of-many-apps bundle
/// implicitly gets — Base Application, System Application, …). It is recorded in each
/// sibling's *.symbols.deps.json sidecar so BC's ReferenceManager can see that the
/// sibling itself depends on (e.g.) Base Application. Without it a sibling whose only
/// AL is a `tableextension ... extends <PlatformTable>` gets an EMPTY sidecar — its
/// declaring module has no recorded path to the module owning the base table, so the
/// extension never attaches: the base table's own fields resolve fine (they come from
/// the primary compile's own direct reference to the platform .app) but the extension
/// field does not, surfacing as AL0132 "'Record X' does not contain a definition for
/// '<field>'" in the app that consumes it. A sibling that only extends another sibling's
/// OWN table (the common case in this bundle) never depended on this fix — its base
/// table lives in the SAME symbols.json as the extension, so no cross-module link was
/// needed. See #1686.
/// </summary>
static void EmitSiblingSymbols(
    List<AlRunner.AppGroup> appGroups, string bundleAbs,
    IReadOnlyList<(AlRunner.AppManifest Manifest, string AppPath)> bundleResolvedDeps)
{
    BcCompiler.SetSiblingSymbolsDir(null);
    // Not a dictionary: two suites in the same tree can (and in tests/runner-extras do)
    // carry the same app id, which would throw on insert. Only membership matters here.
    var presentIds = appGroups.Where(g => g.AppId != null).Select(g => g.AppId!.Value).ToHashSet();
    var targets = appGroups
        .SelectMany(g => g.DependsOn)
        .Where(presentIds.Contains)
        .ToHashSet();
    if (targets.Count == 0) return;

    var dir = Path.Combine(Path.GetTempPath(),
        "al-runner-sibling-symbols", Path.GetFileName(bundleAbs.TrimEnd(Path.DirectorySeparatorChar)));
    try
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  [sibling-symbols] cannot prepare {dir}: {ex.Message}");
        return;
    }

    BcCompiler.SetSiblingSymbolsDir(dir);
    // A sibling's symbol compile inherits the BUNDLE-WIDE dep spec list, which in a
    // parent-of-many-apps bundle includes packages only ONE suite declares — among them
    // synthetic source-only .apps that the compiler's .app scanner cannot read at all.
    // Unlike the primary emit, this compile checks declaration diagnostics, so an unrelated
    // suite's fixture package would fail every sibling here. See ScopeSymbolBearingDepsOnly.
    using var depScope = BcCompiler.ScopeSymbolBearingDepsOnly();
    foreach (var group in appGroups)
    {
        if (group.AppId == null || !targets.Contains(group.AppId.Value)) continue;
        var symbolsPath = Path.Combine(dir, $"{group.AppId:N}.symbols.json");
        try
        {
            using (BcCompiler.ScopeCurrentAppIdentity(
                       group.AppId.Value, group.Publisher ?? "AlRunner",
                       group.Version ?? new Version(1, 0, 0, 0)))
                new BcCompiler().EmitDepSymbols(
                    group.Paths, group.ModuleName, group.AppId.Value,
                    group.Publisher ?? "AlRunner", group.Version ?? new Version(1, 0, 0, 0),
                    symbolsPath, group.SuiteDir);
            // The dependency closure this app compiled against, so BC's ReferenceManager can
            // link types from it that appear in the sibling's public surface — same reason as
            // the source-dep sidecar (#1546); without it those types are __MissingTypeSymbol__.
            //
            // Include the BUNDLE-WIDE Microsoft-platform closure (bundleResolvedDeps) — every
            // suite under a parent-of-many-apps bundle implicitly compiles against it, this
            // sibling included. Without it, a sibling whose only AL is a `tableextension ...
            // extends <PlatformTable>` records an empty dependency closure: its declaring
            // module has no path to the module owning the base table, so the extension never
            // attaches downstream (AL0132 in the consuming app) even though the sibling's own
            // symbols.json genuinely contains the TableExtension entry. See #1686.
            DepsSidecarWriter.Write(
                Path.Combine(dir, $"{group.AppId:N}.symbols.deps.json"),
                group.Publisher ?? "AlRunner", group.ModuleName,
                group.Version ?? new Version(1, 0, 0, 0), group.AppId.Value,
                DepsSidecarWriter.BuildClosure(
                    bundleResolvedDeps.Select(d => new DepsSidecarWriter.DepEntry(
                        d.Manifest.Publisher, d.Manifest.Name, d.Manifest.Version, d.Manifest.AppId)),
                    ScanVendoredPlatformApps(
                        Directory.EnumerateDirectories(bundleAbs, ".alpackages", SearchOption.AllDirectories)),
                    group.AppId.Value));
            // Re-index in place so the NEXT app in this loop — and every app group compiled
            // below — sees it, without rebuilding the expensive shared reference loader.
            BcCompiler.RefreshSiblingSymbols();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"  [sibling-symbols] {group.ModuleName}: {ex.GetType().Name}: {ex.Message} — " +
                "apps depending on it will fail to compile against it");
        }
    }
}

/// <summary>
/// The manifests whose dependency lists together define this bundle's compile closure.
///
/// Normally exactly one: the bucket root's own app.json. But a PARENT directory holding
/// many sibling apps — tests/runner-extras is 25 of them — has no app.json of its own, and
/// FindBucketRoot walks UP looking for one, so it finds nothing. Before this, the entire
/// dep-resolution block was gated on that single file existing, so for such a bundle
/// SetResolvedDeps was never called and NO module got the Microsoft platform symbol
/// closure: `Table "Field"`, `Table "Payment Method"`, `Codeunit "Library - No. Series"`
/// and every platform enum resolved to nothing. The emit-retry then dropped each offending
/// test codeunit as "broken", so 25 suites yielded 9 tests — while each suite run
/// STANDALONE passed, because then FindBucketRoot landed on a directory that does have an
/// app.json. Union the children instead: their manifests are where the `application` /
/// `platform` roots are declared.
/// </summary>
static List<string> CollectBundleManifests(string? bucketRoot, string bundleAbs)
{
    if (bucketRoot != null && File.Exists(Path.Combine(bucketRoot, "app.json")))
        return new List<string> { Path.Combine(bucketRoot, "app.json") };
    if (!Directory.Exists(bundleAbs)) return new List<string>();
    // Direct children only — that is the shape EnumerateSuites recognises, and it keeps
    // the scan away from app.json files buried inside extracted .app packages.
    //
    // Suites declaring a newer BC than the one under test are dropped HERE, before their
    // dependencies join the union. The union is bundle-wide, so one such suite's unmet
    // Microsoft dependency aborts the entire bundle — every sibling included — before a
    // single test runs. Filtering at BuildAppGroups alone is far too late: the run never
    // reaches it. See BcFloorGate.
    //
    // Deliberately NOT applied to the bucket-root branch above: a root manifest speaks for
    // the whole bucket, so honoring a floor there would silently skip everything under it.
    // That case should stay a loud failure.
    var children = Directory.EnumerateDirectories(bundleAbs)
        .Select(d => Path.Combine(d, "app.json"))
        .Where(File.Exists)
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToList();

    var kept = new List<string>();
    foreach (var m in children)
    {
        if (AlRunner.BcFloorGate.DeclaresNewerBcThanRunning(m, out var floor) && floor != null)
        {
            AlRunner.BcFloorGate.ReportSkip(m, AlRunner.BcFloorGate.SuiteNameOf(m), floor);
            continue;
        }
        kept.Add(m);
    }
    return kept;
}

/// <summary>
/// Issue #1996: the manifest-driven provisioning pre-scan. Unlike <see
/// cref="ReadBundleDependencyRoots"/> (used for the REAL dependency-resolution closure),
/// this is deliberately per-manifest fault-tolerant — a malformed/non-object app.json is a
/// PRE-SCAN MISS here (logged, skipped), never a crash: the normal bundle loader reaches
/// the same file moments later and owns the real diagnostic for it (acceptance criterion
/// #9). Returns every Microsoft dependency root across all target <paramref name="bundles"/>
/// (not deduped/sibling-filtered — <see cref="AlRunner.Infrastructure.ProvisioningCheck.DetermineManifestNeeds"/>
/// only cares whether ANY root names a known app, so dedup buys nothing here).
/// </summary>
static List<DependencyRef> ScanManifestDependencyRoots(List<string> bundles)
{
    var allRoots = new List<DependencyRef>();
    foreach (var bundle in bundles)
    {
        List<string> manifests;
        try
        {
            var abs = Path.GetFullPath(bundle);
            var bucketRoot = FindBucketRoot(abs);
            manifests = CollectBundleManifests(bucketRoot, abs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[provision] manifest pre-scan: skipping '{bundle}': {ex.Message}");
            continue;
        }
        var roots = AlRunner.Infrastructure.ProvisioningCheck.TryReadManifestDependencyRoots(
            manifests, ReadDependencies, m => Console.Error.WriteLine(m));
        allRoots.AddRange(roots);
    }
    return allRoots;
}

/// <summary>
/// Union the dependency roots declared across <paramref name="manifests"/>, keeping the
/// highest version when two manifests name the same dependency.
///
/// Sibling apps that are THEMSELVES part of this bundle are dropped: BuildAppGroups already
/// emits them in topological order, so they must not also be resolved from a package cache —
/// they have no .app there and a non-optional root that cannot be found fails the bundle.
/// </summary>
static List<DependencyRef> ReadBundleDependencyRoots(IReadOnlyList<string> manifests)
{
    var siblingIds = new HashSet<Guid>();
    if (manifests.Count > 1)
        foreach (var m in manifests)
        {
            var id = AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(m);
            if (id != null) siblingIds.Add(id.AppId);
        }

    var byKey = new Dictionary<(string, string), DependencyRef>();
    foreach (var m in manifests)
        foreach (var d in ReadDependencies(m))
        {
            if (d.AppId != Guid.Empty && siblingIds.Contains(d.AppId)) continue;
            var key = (d.Name ?? string.Empty, d.Publisher ?? string.Empty);
            if (!byKey.TryGetValue(key, out var cur) || d.Version > cur.Version)
                byKey[key] = d;
        }
    return byKey.Values.ToList();
}

// #1824: de-duplicated. This used to be its own copy of the walk-up loop, kept in sync
// by hand with WatchSource.FindBucketRoot's byte-identical copy (WatchSource couldn't
// call this one — top-level-statement local functions, being nested inside the
// synthesized <Main>$ method, aren't reachable from another file/class regardless of
// accessibility modifiers). WatchSource.FindBucketRoot was promoted to `internal` (see
// its own doc comment) and is now the single shared implementation; this delegates
// rather than reimplementing, so the two can no longer silently drift out of sync. All
// 8 call sites below are unchanged — only this function's body moved.
static string? FindBucketRoot(string bundlePath) => WatchSource.FindBucketRoot(bundlePath);

// Scan the given dirs for Microsoft PLATFORM apps (Application/System/Base Application/
// System Application/Business Foundation) and return one sidecar DepEntry per distinct
// app (real AppId + version read from the .app manifest). These apps enter a source dep's
// compile via the raw package scan of its own .alpackages even when they are NOT part of
// the resolved spec closure (they are synthesized as Optional implicit roots). A dependent
// app can therefore only link the types they carry — e.g. `Codeunit "Temp Blob"`
// (System Application), `Enum "Copilot Capability"` (platform System) — if the dep's
// sidecar declares them. Without this a dependent sees those parameter types as
// __MissingTypeSymbol__ (AL0133). Scoped to the dep's OWN .alpackages (not the global
// package cache) to keep the scan bounded and the declared closure faithful to what the
// dep actually vendored. See DepsSidecarWriter.BuildClosure and issue #1546.
static IEnumerable<DepsSidecarWriter.DepEntry> ScanVendoredPlatformApps(IEnumerable<string> dirs)
{
    foreach (var dir in dirs)
    {
        if (!Directory.Exists(dir)) continue;
        IEnumerable<string> apps;
        try { apps = Directory.EnumerateFiles(dir, "*.app", SearchOption.AllDirectories); }
        catch { continue; }
        foreach (var app in apps)
        {
            var m = AppLoader.ReadManifest(app);
            if (m == null) continue;
            if (AlRunner.DependencyResolver.IsMicrosoftPlatformApp(m.Name, m.Publisher))
                yield return new DepsSidecarWriter.DepEntry(m.Publisher, m.Name, m.Version, m.AppId);
        }
    }
}

// Derive the BC MAJOR version the target project is built for, from the first bundle's
// app.json `application` field (falling back to `platform`). Used to default the BC
// artifact selection when the user gave neither --bc-version nor --artifact-path, so the
// runner picks the cache version matching the project instead of blindly latest-in-cache
// (a stray higher-minor download must not silently become the default). Returns the MAJOR
// as a selection prefix (e.g. "28") — the MAJOR-only engine-consistency contract means any
// cached minor within that major is interchangeable (verified 28.1<->28.2). Returns null
// when no app.json / no version field is found (caller then falls back to latest-in-cache).
static string? TryDeriveBcMajorFromProject(IEnumerable<string> bundlePaths)
{
    foreach (var bundle in bundlePaths)
    {
        string abs;
        try { abs = Path.GetFullPath(bundle); } catch { continue; }
        var root = FindBucketRoot(abs) ?? (Directory.Exists(abs) ? abs : Path.GetDirectoryName(abs));
        if (string.IsNullOrEmpty(root)) continue;
        var appJson = Path.Combine(root, "app.json");
        if (!File.Exists(appJson)) continue;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appJson));
            var r = doc.RootElement;
            foreach (var field in new[] { "application", "platform" })
            {
                if (r.TryGetProperty(field, out var fv)
                    && fv.ValueKind == System.Text.Json.JsonValueKind.String
                    && Version.TryParse(fv.GetString(), out var v)
                    && v.Major > 0)
                    return v.Major.ToString();
            }
        }
        catch { /* unparseable manifest — fall through to next bundle / latest-in-cache */ }
    }
    return null;
}

// Issue #2085: `al-runner provision --platform-apps|--test-apps|--service-tier [--force]`
// — a tool-install-valid replacement for `dotnet run --project tools/DownloadArtifacts --
// <mode> <ver> <dir>`, whose whole body is a switch over the same
// AlRunner.Provisioning.ArtifactDownloader methods this calls. That project ships only as
// source (never part of a packaged `dotnet tool install`), so a user without a checkout had
// no way to reach it. Downloads straight into the canonical directory each mode already
// resolves to at runtime (BcArtifacts.ArtifactDirFor / ProvisioningCheck.PlatformAppsDirFor /
// TestAppsDirFor) — no need-detection, no bundle scan, just "fetch this set for this
// version." `--force` re-downloads even when the directory already looks populated;
// without it, a populated directory is left alone (mirrors EnsureTestToolkitProvisioned's
// existing "already present" short-circuit).
static int RunExplicitProvisionModes(string? bcVersionArg, List<string> bundles,
    bool platformApps, bool testApps, bool serviceTier, bool force, string? resolveVersionPrefix)
{
    if (resolveVersionPrefix != null)
    {
        var resolved = AlRunner.Provisioning.ArtifactDownloader.ResolveVersion(
            resolveVersionPrefix, m => Console.Error.WriteLine($"[provision] {m}"));
        if (resolved == null)
        {
            Console.Error.WriteLine($"[provision] could not resolve a full BC version for prefix '{resolveVersionPrefix}'.");
            return 1;
        }
        Console.WriteLine(resolved); // stdout for script/agent consumption, mirrors tools/DownloadArtifacts
        return 0;
    }

    var full = ResolveFullVersionForExplicitProvision(bcVersionArg, bundles);
    if (full == null)
        return 1; // the resolver already printed a loud, named reason

    bool anyFailed = false;
    if (serviceTier)
    {
        var dir = AlRunner.Infrastructure.BcArtifacts.ArtifactDirFor(full);
        anyFailed |= ForceProvisionMode("BC service-tier engine DLLs", dir, full, force, "*.dll",
            (v, d, log) => AlRunner.Provisioning.ArtifactDownloader.ServiceTier(v, d, log)) != 0;
    }
    if (platformApps)
    {
        var dir = AlRunner.Infrastructure.ProvisioningCheck.PlatformAppsDirFor(
            AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, full);
        anyFailed |= ForceProvisionMode("Microsoft platform apps", dir, full, force, "*.app",
            (v, d, log) => AlRunner.Provisioning.ArtifactDownloader.PlatformApps(v, d, log)) != 0;
    }
    if (testApps)
    {
        var dir = TestAppsDirFor(full);
        anyFailed |= ForceProvisionMode("Microsoft test-toolkit apps", dir, full, force, "*.app",
            (v, d, log) => AlRunner.Provisioning.ArtifactDownloader.TestApps(v, d, log)) != 0;
    }
    return anyFailed ? 1 : 0;
}

// Shared by every explicit provision mode: skip the download when the canonical directory
// already contains at least one file matching <paramref name="expectedGlob"/> (unless
// --force), otherwise run <paramref name="download"/> and report success/failure. Named
// per-mode so the log lines read like the rest of `[provision]` output, not a generic
// "done"/"failed".
static int ForceProvisionMode(string label, string outputDir, string fullVersion, bool force,
    string expectedGlob, Func<string, string, Action<string>, int> download)
{
    if (!force && Directory.Exists(outputDir) && Directory.EnumerateFiles(outputDir, expectedGlob).Any())
    {
        Console.Error.WriteLine($"[provision] {label} already present at {outputDir} for BC {fullVersion} — skipping (pass --force to re-download).");
        return 0;
    }
    Console.Error.WriteLine($"[provision] fetching {label} for BC {fullVersion} → {outputDir}");
    try
    {
        var rc = download(fullVersion, outputDir, m => Console.Error.WriteLine($"[provision] {m}"));
        if (rc != 0)
            Console.Error.WriteLine($"[provision] warning: {label} download failed for BC {fullVersion}.");
        return rc;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[provision] warning: {label} download failed for BC {fullVersion}: {ex.Message}");
        return 1;
    }
}

// Resolves the full 4-part BC version to target for an EXPLICIT provision mode
// (--platform-apps/--test-apps/--service-tier). Deliberately mirrors RunProvisioning's own
// resolution (explicit --bc-version, else the engine's own major, else the target bundle's
// app.json major; prefer an already-cached matching version, else resolve the latest full
// version from the CDN) — kept as a separate small function rather than sharing
// RunProvisioning's inline block because that block's own success message ("verifying
// completeness") describes what RunProvisioning does NEXT (an engine-closure completeness
// check), which does not apply here.
static string? ResolveFullVersionForExplicitProvision(string? bcVersionArg, List<string> bundles)
{
    if (bcVersionArg != null && System.Version.TryParse(bcVersionArg, out var maybeFull) && maybeFull.Revision >= 0
        && bcVersionArg.Split('.').Length == 4)
        return bcVersionArg; // an explicit 4-part version — target exactly that

    var prefix = bcVersionArg
        ?? AlRunner.Infrastructure.BcArtifacts.EngineMajor(AppContext.BaseDirectory)?.ToString()
        ?? TryDeriveBcMajorFromProject(bundles);
    if (prefix == null)
    {
        Console.Error.WriteLine("[provision] cannot determine which BC version to provision — pass " +
            "--bc-version <ver> (no --bc-version, no engine in bin, and no readable project app.json).");
        return null;
    }
    try
    {
        var cachedDir = AlRunner.Infrastructure.BcArtifacts.SelectArtifactVersionDir(
            AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, prefix);
        var full = Path.GetFileName(cachedDir);
        Console.Error.WriteLine($"[provision] found cached BC {full} for prefix '{prefix}'.");
        return full;
    }
    catch (InvalidOperationException)
    {
        Console.Error.WriteLine($"[provision] no cached BC {prefix}.x — resolving latest full version from the CDN...");
        var full = AlRunner.Provisioning.ArtifactDownloader.ResolveVersion(prefix);
        if (full == null)
            Console.Error.WriteLine($"[provision] could not resolve a full BC version for prefix '{prefix}'.");
        return full;
    }
}

// Provisioning driver for the `provision` subcommand / --auto-provision. Resolves the
// target BC version (explicit --bc-version, else the engine major, else the project major),
// prefers an already-cached matching version (completing a partial one) and otherwise
// resolves the latest full version from the CDN, then downloads the engine service-tier
// closure if it is missing/incomplete. Returns 0 on success (already-complete counts) and
// sets provisionedVersion to the full version to run against; 1 on failure. This is the
// only path in the runner that downloads — on by default since issue #2024, refusable
// with --no-auto-provision.
//
// <paramref name="provisionManifestApps"/> (issue #1996, AC #6): whether THIS call should
// also provision platform-apps/test-apps. Pass true only for the `provision` subcommand,
// which never reaches the post-SelectVersion gate in Program's top-level flow (it returns
// immediately after this call) — for a continuing --auto-provision run, that gate is the
// sole owner instead, so passing true there would attempt the SAME download twice in one
// invocation (once here, pre-selection; once there, post-selection).
static int RunProvisioning(string? bcVersionArg, string? artifactPathArg,
    List<string> bundles, bool provisionManifestApps, List<Action>? deferredLines, out string? provisionedVersion)
{
    provisionedVersion = null;

    if (artifactPathArg != null)
    {
        // #2041/#2066: `deferredLines` null means print immediately (the `provision`
        // subcommand call — see the call site's comment for why); non-null means queue
        // this STEADY-STATE success line onto it instead, never an error path. The
        // caller flushes the queue once IT has confirmed no further re-exec follows —
        // see `deferredStartupLines`'s declaration in Program's top-level flow.
        if (deferredLines == null)
            Console.Error.WriteLine("[provision] --artifact-path points at an explicit dir; nothing to provision.");
        else
            deferredLines.Add(() => Console.Error.WriteLine(
                "[provision] --artifact-path points at an explicit dir; nothing to provision."));
        return 0;
    }

    // Resolve the full version to provision.
    string? full = null;
    if (bcVersionArg != null && System.Version.TryParse(bcVersionArg, out var maybeFull) && maybeFull.Revision >= 0
        && bcVersionArg.Split('.').Length == 4)
    {
        full = bcVersionArg; // an explicit 4-part version — provision exactly that
    }
    else
    {
        var prefix = bcVersionArg
            ?? AlRunner.Infrastructure.BcArtifacts.EngineMajor(AppContext.BaseDirectory)?.ToString()
            ?? TryDeriveBcMajorFromProject(bundles);
        if (prefix == null)
        {
            Console.Error.WriteLine("[provision] cannot determine which BC version to provision — pass " +
                "--bc-version <ver> (no --bc-version, no engine in bin, and no readable project app.json).");
            return 1;
        }
        // Prefer an already-cached version matching the prefix (completes a partial one);
        // otherwise resolve the latest full version from the public CDN index.
        try
        {
            var cachedDir = AlRunner.Infrastructure.BcArtifacts.SelectArtifactVersionDir(
                AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, prefix);
            full = Path.GetFileName(cachedDir);
            // Not gated on `quiet`: unlike the two lines below, a re-exec'd child sees a
            // DIFFERENT resolution outcome here than the parent did whenever the parent
            // itself just downloaded (parent: "no cached ... resolving from the CDN",
            // child: "found cached ... verifying completeness") — the two lines are not
            // literal duplicates of each other, so suppressing either risks hiding a
            // real state transition rather than a genuine repeat.
            Console.Error.WriteLine($"[provision] found cached BC {full} for prefix '{prefix}' — verifying completeness.");
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine($"[provision] no cached BC {prefix}.x — resolving latest full version from the CDN...");
            full = AlRunner.Provisioning.ArtifactDownloader.ResolveVersion(prefix);
            if (full == null)
            {
                Console.Error.WriteLine($"[provision] could not resolve a full BC version for prefix '{prefix}'.");
                return 1;
            }
        }
    }

    var serviceTierDir = AlRunner.Infrastructure.BcArtifacts.ArtifactDirFor(full);
    var report = AlRunner.Infrastructure.ProvisioningCheck.Check(full, serviceTierDir);
    if (report.Ok)
    {
        // #2041/#2066: the steady-state "nothing to do" line — deferred, same reasoning
        // as the --artifact-path branch above. AutoProvision's own download progress
        // messages below are NOT gated/deferred: they only fire once regardless (by the
        // time a shadow-re-exec child gets here the download already completed, so IT
        // takes this same Ok branch instead), and a download in progress is exactly the
        // kind of "real one-time work" .claude/rules/loud-failures.md means to stay loud.
        var fullForPrint = full;
        var serviceTierDirForPrint = serviceTierDir;
        if (deferredLines == null)
            Console.Error.WriteLine($"[provision] BC {fullForPrint} engine artifacts already complete at {serviceTierDirForPrint}.");
        else
            deferredLines.Add(() => Console.Error.WriteLine(
                $"[provision] BC {fullForPrint} engine artifacts already complete at {serviceTierDirForPrint}."));
    }
    else if (!AlRunner.Infrastructure.ProvisioningCheck.AutoProvision(full, serviceTierDir))
        return 1;

    if (provisionManifestApps)
    {
        EnsurePlatformAppsProvisioned(full, bundles);
        EnsureTestToolkitProvisioned(full);
    }
    provisionedVersion = full;
    return 0;
}

// Issue #1678: `provision` used to stop at the engine closure + test toolkit, so its half
// of the "[provision-gap]" fix text ("al-runner provision") was wrong — the subcommand
// never touched platform apps at all, and re-running it after the gap message reported the
// engine "already complete" and exited without ever creating <artifacts>/<version>/platform-apps.
// Detect the gap the same way the --auto-provision path does: scan the TARGET bundles'
// own `.alpackages` (the standard shape any AL project's symbol download produces) for
// symbol-only Microsoft platform apps, and download the R2R package set for THEIR version
// (which can be a different minor than <paramref name="full"/>, the engine's own version —
// see DeriveProvisionMajorMinor) into the runner-owned platform-apps dir for that version.
// No-op when no bundle is given (`al-runner provision` with no target) or the bundle(s)
// carry no symbol-only platform apps.
static void EnsurePlatformAppsProvisioned(string engineVersion, List<string> bundles)
{
    // Issue #1996: an empty/nonexistent bundleAlpackagesDirs used to be an early no-op here
    // (the subcommand's own copy of the "empty cache = complete" bug), and CheckPlatformApps
    // alone can never see a manifest need for an app (Application Test Library) that has no
    // service-tier DLL fallback and is therefore simply absent, not symbol-only. Consult the
    // manifest directly — same decision engine the main --auto-provision gate uses.
    var bundleAlpackagesDirs = AlRunner.Infrastructure.ProvisioningCheck.CollectBundleAlpackagesDirs(bundles);
    var platformReport = AlRunner.Infrastructure.ProvisioningCheck.CheckPlatformApps(
        engineVersion, bundleAlpackagesDirs);
    var manifestDependencyRoots = ScanManifestDependencyRoots(bundles);
    var decision = AlRunner.Infrastructure.ProvisioningCheck.DecideManifestProvisioning(
        manifestDependencyRoots, platformReport, bundleAlpackagesDirs);
    if (!decision.ShouldDownloadPlatform)
    {
        // Issue #2073: "already present" is only true when something was actually VERIFIED
        // present — a bundle that was simply determined not to need the set at all (the
        // manifest names nothing platform-only) never checked presence, so claiming
        // presence for it is a silent wrong answer under .claude/rules/loud-failures.md.
        Console.Error.WriteLine(decision.NeedsPlatformApps
            ? "[provision] platform R2R apps already present for the target bundle(s)."
            : "[provision] target bundle(s) do not need the platform R2R apps set — nothing to provision.");
        return;
    }

    // Issue #2077: always target the SELECTED engine version's own major.minor — never one
    // derived from cache contents, which used to silently redirect the download to whatever
    // minor happened to already be vendored in the bundle's own `.alpackages`.
    var mm = AlRunner.Infrastructure.ProvisioningCheck.ResolveProvisionMajorMinor(engineVersion);
    {
        var cacheMm = !platformReport.Ok
            ? AlRunner.Infrastructure.ProvisioningCheck.DeriveProvisionMajorMinor(platformReport, engineVersion)
            : AlRunner.Infrastructure.ProvisioningCheck.DerivePresentPlatformMajorMinor(bundleAlpackagesDirs, engineVersion);
        var skewNote = AlRunner.Infrastructure.ProvisioningCheck.BuildProvisionVersionSkewNote(
            mm, cacheMm,
            !platformReport.Ok
                ? "a symbol-only platform app already in the bundle's package cache"
                : "platform apps already in the bundle's package cache");
        if (skewNote != null)
            Console.Error.WriteLine(skewNote);
    }
    var platformFull = AlRunner.Provisioning.ArtifactDownloader.ResolveVersion(
        mm, m => Console.Error.WriteLine($"[provision] {m}"));
    if (platformFull == null)
    {
        Console.Error.WriteLine(
            $"[provision] warning: could not resolve a full BC artifact version for platform apps '{mm}'. " +
            $"Symbol-only Microsoft platform apps found: {string.Join(", ", platformReport.Issues.Select(i => i.Name))}.");
        return;
    }
    var platformAppsOut = AlRunner.Infrastructure.ProvisioningCheck.PlatformAppsDirFor(
        AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, platformFull);
    Console.Error.WriteLine($"[provision] fetching Microsoft platform R2R apps for BC {platformFull} → {platformAppsOut}");
    try
    {
        var rc = AlRunner.Provisioning.ArtifactDownloader.PlatformApps(
            platformFull, platformAppsOut, m => Console.Error.WriteLine($"[provision] {m}"));
        if (rc != 0)
            Console.Error.WriteLine(
                $"[provision] warning: could not fetch platform apps for BC {platformFull}. " +
                $"Test bundles calling into Base Application / System Application / Business Foundation " +
                $"codeunits will need --package-cache <dir-with-those-apps>.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[provision] warning: platform-apps download failed: {ex.Message}");
    }
}

// The MS test toolkit (Any, Library Assert, Library Variable Storage, Test Runner,
// Tests-TestLibraries, Permissions Mock, System Application Test Library) is what every
// test bundle's app.json actually depends on, but it ships in the platform artifact rather
// than with the engine closure. Without it a test app resolves those deps to whatever
// symbols-only copies its own .alpackages happen to hold and dies at runtime — so the user
// had to hand-assemble a package dir with no command that could produce one.
// Provisioned into <artifacts>/<version>/test-apps/, which DefaultPackageCacheDirs scans,
// so a provisioned machine needs no --package-cache for the toolkit at all.
static void EnsureTestToolkitProvisioned(string fullVersion)
{
    var dir = TestAppsDirFor(fullVersion);
    if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.app").Any())
    {
        Console.Error.WriteLine($"[provision] test toolkit already present at {dir}.");
        return;
    }
    Console.Error.WriteLine($"[provision] fetching the MS test toolkit for BC {fullVersion} → {dir}");
    try
    {
        var rc = AlRunner.Provisioning.ArtifactDownloader.TestApps(
            fullVersion, dir, m => Console.Error.WriteLine($"[provision] {m}"));
        if (rc != 0)
            Console.Error.WriteLine(
                $"[provision] warning: could not fetch the test toolkit for BC {fullVersion}. " +
                $"Test bundles depending on Library Assert / Test Runner / Any will need " +
                $"--package-cache <dir-with-those-apps>.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[provision] warning: test-toolkit download failed: {ex.Message}");
    }
}

static string TestAppsDirFor(string fullVersion)
    => AlRunner.Infrastructure.ProvisioningCheck.TestAppsDirFor(
        AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, fullVersion);

static void SetBundleInfoFromAppJson(string appJsonPath)
{
    // Remember (or clear) the bundle dir for NavApp.GetResource: the emitted test
    // assembly's resources are the files under this dir's app.json resourceFolders.
    AlRunner.Patches.NavAppResourcePatches.SetCurrentBundleDir(
        File.Exists(appJsonPath) ? Path.GetDirectoryName(Path.GetFullPath(appJsonPath)) : null);
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appJsonPath));
        var root = doc.RootElement;
        var idStr = root.TryGetProperty("id", out var pid) ? pid.GetString() : null;
        var name = root.TryGetProperty("name", out var pn) ? pn.GetString() ?? "Unknown" : "Unknown";
        var pub = root.TryGetProperty("publisher", out var pp) ? pp.GetString() ?? "Unknown" : "Unknown";
        var ver = root.TryGetProperty("version", out var pv) ? pv.GetString() ?? "1.0.0.0" : "1.0.0.0";
        Guid appId = Guid.Empty;
        if (!string.IsNullOrEmpty(idStr)) Guid.TryParse(idStr, out appId);
        AlRunner.BcRuntime.SetCurrentBundleInfo(appId, name, pub, ver);
    }
    catch { /* non-fatal */ }
}

static IEnumerable<DependencyRef> ReadDependencies(string appJsonPath)
{
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appJsonPath));
    var root = doc.RootElement;

    // Explicit deps from the `dependencies` array (third-party + any first-party
    // apps the author chose to list).
    if (root.TryGetProperty("dependencies", out var deps)
        && deps.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        foreach (var d in deps.EnumerateArray())
        {
            var idStr = d.TryGetProperty("id", out var pid) ? pid.GetString() : null;
            var name = d.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "";
            var pub = d.TryGetProperty("publisher", out var pp) ? pp.GetString() ?? "" : "";
            var ver = d.TryGetProperty("version", out var pv) ? pv.GetString() ?? "0.0.0.0" : "0.0.0.0";
            Guid id = Guid.Empty;
            if (!string.IsNullOrEmpty(idStr)) Guid.TryParse(idStr, out id);
            if (!Version.TryParse(ver, out var v)) v = new Version(0, 0, 0, 0);
            // Microsoft platform apps (Base Application / System Application / Business
            // Foundation / Application / System) are provided by the precompiled
            // service-tier DLLs at runtime and by the bundle's .alpackages symbols at
            // compile time — never loaded from a resolved .app. Some corpus/ISV manifests
            // list them EXPLICITLY (others rely on the implicit application/platform roots).
            // Mark the explicit ones Optional so resolution skips them when they aren't a
            // findable .app (e.g. on CI, where packageCacheDirs is empty) instead of failing
            // the whole bundle — matching how the implicit roots below are already Optional.
            bool isMsPlatform = AlRunner.DependencyResolver.IsMicrosoftPlatformApp(name, pub);
            yield return new DependencyRef(id, name, pub, v, Optional: isMsPlatform);
        }
    }

    // Implicit first-party deps. Modern AL apps do NOT list the Microsoft apps
    // in `dependencies`; the real `al` compiler injects them from the manifest's
    // `application` and `platform` fields. Synthesize the same roots here so they
    // resolve from the package cache, otherwise every `using Microsoft.*` is an
    // unknown namespace. The `application` umbrella app transitively pulls Base
    // Application + System Application + Business Foundation; `platform` is the
    // System (platform symbols) app. TryFind matches by (Name, Publisher) —
    // version is informational only, so an exact runtime match isn't required.
    foreach (var (field, implName) in new[] { ("application", "Application"), ("platform", "System") })
    {
        if (root.TryGetProperty(field, out var fv)
            && fv.ValueKind == System.Text.Json.JsonValueKind.String
            && !string.IsNullOrWhiteSpace(fv.GetString()))
        {
            if (!Version.TryParse(fv.GetString(), out var iv)) iv = new Version(0, 0, 0, 0);
            yield return new DependencyRef(Guid.Empty, implName, "Microsoft", iv, Optional: true);
        }
    }
}


// Collect this single suite's src/test/app* dirs for emit. Per-suite isolation
// avoids the cross-suite object-id collisions that silently zeroed-out bundled emit.
// When a bucket root is supplied, also include `<bucketRoot>/_shared/` so AL
// files at the bucket level (e.g. an Assert.Codeunit.al that satisfies a
// dependency without a runtime DLL) compile into every suite.
/// <summary>
/// Groups the enumerated suites into one AppGroup per app.json, ordered so that an
/// app comes after every sibling it depends on (a sibling's symbols must exist
/// before the app referencing it compiles). Suites without an app.json cannot carry
/// an identity of their own and are merged into one fallback module named after the
/// bundle, which is the pre-existing behaviour for that shape.
/// </summary>
static List<AlRunner.AppGroup> BuildAppGroups(List<string> suites, string? bucketRoot, string bundleAbs)
{
    var groups = new List<AlRunner.AppGroup>();
    var identified = new List<(AlRunner.AppGroup Group, Guid Id)>();
    var orphanPaths = new List<string>();
    var skippedForBcFloor = new List<(string Name, Version Floor)>();

    foreach (var suite in suites)
    {
        var paths = CollectSuitePaths(suite, bucketRoot);
        var appJson = Path.Combine(suite, "app.json");
        var id = File.Exists(appJson)
            ? AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(appJson)
            : null;
        if (id == null) { orphanPaths.AddRange(paths); continue; }

        // Honor a declared minimum BC version — see BcFloorGate. CollectBundleManifests already
        // drops these before the dependency union, which is the filter that actually keeps the
        // bundle alive; this one covers the paths that reach here with a different suite set
        // (a single suite passed as the target, --watch re-enumeration), so the two must agree.
        // BcFloorGate reports each suite once, so the overlap does not double-print.
        if (AlRunner.BcFloorGate.DeclaresNewerBcThanRunning(appJson, out var floor) && floor != null)
        {
            AlRunner.BcFloorGate.ReportSkip(appJson, id.Name, floor);
            skippedForBcFloor.Add((id.Name, floor));
            continue;
        }

        var group = new AlRunner.AppGroup(
            ModuleName: id.Name,
            AppId: id.AppId,
            Publisher: id.Publisher,
            Version: id.Version,
            Paths: paths,
            DependsOn: id.Dependencies.Select(d => d.AppId).ToList(),
            SuiteDir: Path.GetFullPath(suite));
        identified.Add((group, id.AppId));
    }

    // Topological order over sibling dependencies only. Dependencies on apps outside
    // this bundle are resolved from the package cache as before and are ignored here.
    var siblingIds = identified.Select(t => t.Id).ToHashSet();
    var emitted = new HashSet<Guid>();
    var remaining = new List<(AlRunner.AppGroup Group, Guid Id)>(identified);
    while (remaining.Count > 0)
    {
        // Take every app whose sibling dependencies are already emitted. If none
        // qualify the graph has a cycle — emit the rest in declaration order rather
        // than looping forever; BC will report the unresolved reference loudly.
        var ready = remaining
            .Where(t => t.Group.DependsOn.All(d => !siblingIds.Contains(d) || emitted.Contains(d)))
            .ToList();
        if (ready.Count == 0) ready = remaining.ToList();
        foreach (var t in ready)
        {
            groups.Add(t.Group);
            emitted.Add(t.Id);
            remaining.Remove(t);
        }
    }

    if (orphanPaths.Count > 0)
        groups.Add(new AlRunner.AppGroup(
            ModuleName: $"V2_{Path.GetFileName(bundleAbs)}",
            AppId: null, Publisher: null, Version: null,
            Paths: orphanPaths.Distinct().ToList(),
            DependsOn: Array.Empty<Guid>(),
            SuiteDir: Path.GetFullPath(bundleAbs)));

    // Restate the skips as one line after the per-suite detail. A reader scanning the tail of a
    // green run must be able to see that the run covered less than the tree contains — a skip
    // that only appears 200 lines up is a skip nobody notices.
    if (skippedForBcFloor.Count > 0)
        Console.WriteLine(
            $"  [skip] {skippedForBcFloor.Count} suite(s) need a newer BC than "
            + $"{AlRunner.Infrastructure.BcArtifacts.SelectedVersion}: "
            + string.Join(", ", skippedForBcFloor.Select(s => $"{s.Name} (>= {s.Floor})")));

    return groups;
}

static List<string> CollectSuitePaths(string suite, string? bucketRoot = null)
{
    var all = new List<string>();
    var s = Path.Combine(suite, "src");
    var t = Path.Combine(suite, "test");
    if (Directory.Exists(s)) all.Add(s);
    foreach (var app in Directory.EnumerateDirectories(suite, "app*"))
        all.Add(app);
    if (Directory.Exists(t)) all.Add(t);
    // Flat bundle: if neither src/ nor test/ exist, include the suite root so
    // the emitter can recurse into it and find all .al files.
    if (all.Count == 0 && Directory.EnumerateFiles(suite, "*.al", SearchOption.AllDirectories).Any())
        all.Add(suite);
    if (bucketRoot != null)
    {
        var shared = Path.Combine(bucketRoot, "_shared");
        if (Directory.Exists(shared)) all.Add(shared);
    }
    return all;
}

// Deterministic cache key for the bundled-mode emit:
//   sha256( runner-asm-mtime-ticks
//         | moduleName
//         | each (ordered dep id+version)
//         | each (.al file relpath + sha256-of-contents) sorted )
// Hashed in a single pass with line-separated framing so two different file
// layouts can't collide. The key is hex-encoded sha256 (64 chars).
static string ComputeAlCacheKey(
    IReadOnlyList<string> alFolders,
    string moduleName,
    IReadOnlyList<string> ordered,
    string? appRootDir = null)
{
    using var sha = System.Security.Cryptography.SHA256.Create();
    using var ms = new MemoryStream();
    void WriteLine(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\n");
        ms.Write(bytes, 0, bytes.Length);
    }

    // 0. Cache schema version — bumped whenever the on-disk cache layout
    //    (sidecar set, sidecar shape, or hash framing) changes. Old DLLs
    //    written before the bump simply hash to a different key and become
    //    unreachable garbage in <cacheDir>; the new key MISSes and rebuilds.
    //    v2: added <key>.enum-registry.json sidecar so cache HIT replays the
    //    AlEnumMetadataRegistry side-effects that emit would have set up.
    //    v3: enum sidecar includes interface implementation codeunit ids.
    //    v4: sidecar also carries the AlReportMetadataRegistry (per-report
    //        runtime metadata XML) so cache HIT replays real report metadata.
    //    v5: sidecar also carries the AlReportLayoutRegistry (per-report
    //        `rendering { layout(...) }` declarations) so cache HIT replays the
    //        rows behind the Report Layout List virtual table (2000000234).
    //    v6: sidecar also carries the AlPageMetadataRegistry (per-page runtime
    //        metadata XML) so cache HIT replays the real page control tree that
    //        NCLMetaForm.LoadMetadata() builds from it.
    //    v7: report-layout rows carry IsDefault (the report's DefaultRenderingLayout),
    //        without which a cache HIT could not resolve a multi-layout report's
    //        default-layout render and hydrated nothing.
    //    v8: sidecar also carries the AlXmlPortMetadataRegistry (per-xmlport runtime
    //        metadata XML) so cache HIT replays the real node schema that
    //        NCLMetaXmlPort.LoadMetadata() builds from it.
    //    v9: enum sidecar entries carry per-value Captions (issue #1775 —
    //        Format(<enum value>) must return the declared Caption, not the member
    //        name). A v8 sidecar deserialises with Captions null, which
    //        AlEnumOptionMetadata already treats as "no captions captured" — silently
    //        wrong for a cache HIT, not a cache miss, without this bump.
    //    v10: runner fingerprint switched from mtime+length to a content hash of the
    //        runner assembly (issue #1815 finding 2 — every CI leg rebuilds the runner,
    //        so mtime moved on every run and a persisted cache could never hit), and an
    //        explicit bc:<version> line was added (finding 3 — a content hash is
    //        IDENTICAL across every BC-version leg building the same commit; without an
    //        explicit version line all 8 legs would collide on one cache entry and a leg
    //        could load AL output compiled against another BC version's symbols). v9
    //        entries carried neither and must not be served under the new key shape.
    //    v11: added a manifest fragment (preprocessorSymbols/features/contextSensitiveHelpUrl
    //        read from the app's own app.json — see BcCompiler.ReadManifestCompilerInputs).
    //        #1943: before this, editing app.json changed neither the AL source bytes nor
    //        the CLI --define set, so the key was IDENTICAL before and after — a cache HIT
    //        would silently serve the DLL compiled under the OLD manifest values (wrong #if
    //        branch, missing NoImplicitWith, stale contextSensitiveHelpUrl) forever, until
    //        something else in the key happened to change. v10 entries never hashed the
    //        manifest at all and must not be served under the new key shape.
    //    v12 (issue #1997): added a tdd:<0|1> line. --tdd keeps recovered sources for
    //        objects a normal run drops entirely and can (in a follow-up) inject
    //        generated members into the in-memory compile — a --tdd assembly and a
    //        normal-mode assembly for the SAME source bytes are not the same output.
    //        Without this line a bare run and a --tdd run over identical sources hash
    //        identically, and whichever compiled first would silently serve the other:
    //        a normal run reusing a --tdd-generated DLL, or (just as bad) a later --tdd
    //        run reusing a normal-mode DLL and reporting the excluded tests' vanished
    //        instead of failed. v11 entries never hashed this and must not be served.
    WriteLine("schema:v12");
    WriteLine($"tdd:{(AlRunner.BcCompiler.IsTddMode() ? "1" : "0")}");

    // 1. Runner assembly fingerprint (content hash, not mtime — see v10 note above) +
    //    the selected BC version, so any rewriter/polyfill/patch change in the runner,
    //    or running against a different BC version, forces a cache miss.
    AlRunner.Infrastructure.RunnerFingerprint.WriteKeyLines(WriteLine);

    WriteLine($"module:{moduleName}");

    // 2. Preprocessor symbols from --define / --preprocessor-symbols. They select which
    //    #if branch compiles, so two runs over byte-identical sources but different symbol
    //    sets are different compilations. Omitting them made --define a silent no-op on any
    //    cache hit: a bare run and a --define run produced the same key, and whichever
    //    compiled first served the other. Written unconditionally so the line always frames
    //    the key (existing entries hash differently once and rebuild).
    WriteLine($"defines:{string.Join(",", AlRunner.BcCompiler.GetExtraPreprocessorSymbols())}");

    // 3. The app's OWN manifest properties that feed ParseOptions/CompilationOptions —
    //    preprocessorSymbols, features, contextSensitiveHelpUrl (#1943; see v11 note
    //    above). appRootDir is the directory holding app.json — the same one Emit()
    //    itself reads from (BcCompiler.ReadManifestCompilerInputs) — so an edit to any of
    //    these three properties changes this line and forces a MISS.
    WriteLine($"manifest:{AlRunner.BcCompiler.ReadManifestCacheKeyFragment(appRootDir)}");

    foreach (var d in ordered) WriteLine($"dep:{d}");

    // Enumerate every .al file in stable order, hash each. The key uses paths
    // relative to the common source root, not absolute paths, so invoking the
    // same bundle from a different current directory does not force a rebuild.
    var alFiles = alFolders
        .Where(Directory.Exists)
        .SelectMany(d => Directory.EnumerateFiles(Path.GetFullPath(d), "*.al", SearchOption.AllDirectories))
        .Distinct()
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToList();
    var commonRoot = CommonDirectory(alFiles);
    foreach (var f in alFiles)
    {
        byte[] hash;
        using (var fs = File.OpenRead(f))
            hash = sha.ComputeHash(fs);
        var rel = commonRoot == null ? Path.GetFileName(f) : Path.GetRelativePath(commonRoot, f);
        WriteLine($"al:{rel}:{Convert.ToHexString(hash)}");
    }

    ms.Position = 0;
    var keyBytes = sha.ComputeHash(ms);
    return Convert.ToHexString(keyBytes).ToLowerInvariant();
}

static string? CommonDirectory(IReadOnlyList<string> files)
{
    if (files.Count == 0) return null;
    var common = Path.GetDirectoryName(Path.GetFullPath(files[0]));
    if (common == null) return null;
    foreach (var file in files.Skip(1))
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        while (dir != null && common != null
            && !dir.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(dir, common, StringComparison.OrdinalIgnoreCase))
        {
            common = Path.GetDirectoryName(common);
        }
        if (common == null) return null;
    }
    return common;
}

// Sidecar: serialize AlEnumMetadataRegistry to <key>.enum-registry.json so
// cache HIT can replay the side-effect that emit would have populated.
// Schema (v9): { "enums": [ { "id": int, "name": string, "options": [string], "indexes": [int], "implementations": [[int]], "captions": [string?] }, ... ] }
static int SaveEnumRegistrySidecar(string path)
{
    var entries = AlEnumMetadataRegistry.Snapshot();
    var dto = new
    {
        enums = entries.Select(e => new
        {
            id = e.Id,
            name = e.Name,
            options = e.Options,
            indexes = e.Indexes,
            implementations = e.Implementations,
            captions = e.Captions,
        }).ToArray(),
        // v4: per-report runtime metadata XML captured from emit — replayed on
        // cache HIT so NavReportSync builds real MetaReport instances.
        reportMetadata = AlReportMetadataRegistry.Ids
            .OrderBy(i => i)
            .Select(i => new
            {
                id = i,
                xml = AlReportMetadataRegistry.TryGet(i, out var x) ? x : string.Empty,
            }).ToArray(),
        // v5: per-report rendering-layout declarations captured from the AL
        // compiler's ReportLayoutSymbol — replayed on cache HIT so layout
        // selection by name keeps working on a warm cache.
        reportLayouts = AlReportLayoutRegistry.Snapshot(),
        // v6: per-page runtime metadata XML captured from emit — replayed on cache
        // HIT so NCLMetaForm.LoadMetadata() still builds a real control tree on a
        // warm run. Emit only fires on a MISS; anything captured there and not
        // persisted here is silently gone on the next run.
        pageMetadata = AlPageMetadataRegistry.Ids
            .OrderBy(i => i)
            .Select(i => new
            {
                id = i,
                xml = AlPageMetadataRegistry.TryGet(i, out var x) ? x : string.Empty,
            }).ToArray(),
        // v8: per-xmlport runtime metadata XML captured from emit — replayed on cache
        // HIT so NCLMetaXmlPort.LoadMetadata() still builds a real node schema on a warm
        // run. Same emit-only capture hazard as pageMetadata above.
        xmlPortMetadata = AlXmlPortMetadataRegistry.Ids
            .OrderBy(i => i)
            .Select(i => new
            {
                id = i,
                xml = AlXmlPortMetadataRegistry.TryGet(i, out var x) ? x : string.Empty,
            }).ToArray(),
    };
    var json = System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = false,
    });
    File.WriteAllText(path, json);
    return entries.Count;
}

// Replay AlEnumMetadataRegistry from <key>.enum-registry.json. Throws on
// corrupt JSON; the caller treats any exception as cache MISS and rebuilds.
static int LoadEnumRegistrySidecar(string path)
{
    var json = File.ReadAllText(path);
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    if (!doc.RootElement.TryGetProperty("enums", out var arr)
        || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
        throw new InvalidDataException("enum-registry.json: missing 'enums' array");
    int count = 0;
    foreach (var e in arr.EnumerateArray())
    {
        int id = e.GetProperty("id").GetInt32();
        string name = e.GetProperty("name").GetString() ?? string.Empty;
        var optsEl = e.GetProperty("options");
        var idxEl = e.GetProperty("indexes");
        var opts = new string[optsEl.GetArrayLength()];
        int oi = 0;
        foreach (var o in optsEl.EnumerateArray()) opts[oi++] = o.GetString() ?? string.Empty;
        var idxs = new int[idxEl.GetArrayLength()];
        int ii = 0;
        foreach (var x in idxEl.EnumerateArray()) idxs[ii++] = x.GetInt32();
        int[][] implementations = Array.Empty<int[]>();
        if (e.TryGetProperty("implementations", out var implEl)
            && implEl.ValueKind == System.Text.Json.JsonValueKind.Array
            && implEl.GetArrayLength() == opts.Length)
        {
            implementations = new int[implEl.GetArrayLength()][];
            int vi = 0;
            foreach (var valueImplEl in implEl.EnumerateArray())
            {
                if (valueImplEl.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    implementations = Array.Empty<int[]>();
                    break;
                }
                var ids = new int[valueImplEl.GetArrayLength()];
                int idi = 0;
                foreach (var implId in valueImplEl.EnumerateArray())
                    ids[idi++] = implId.GetInt32();
                implementations[vi++] = ids;
            }
        }
        // v9: per-value Captions (issue #1775). Absent in pre-v9 sidecars — fine, the
        // cache key schema bump above makes those unreachable anyway.
        string?[]? captions = null;
        if (e.TryGetProperty("captions", out var capEl)
            && capEl.ValueKind == System.Text.Json.JsonValueKind.Array
            && capEl.GetArrayLength() == opts.Length)
        {
            captions = new string?[capEl.GetArrayLength()];
            int ci = 0;
            foreach (var c in capEl.EnumerateArray())
                captions[ci++] = c.ValueKind == System.Text.Json.JsonValueKind.Null ? null : c.GetString();
        }
        AlEnumMetadataRegistry.Register(id, name, opts, idxs, implementations, captions);
        count++;
    }
    // v4: replay per-report metadata XML (absent in pre-v4 sidecars — fine,
    // the cache key schema bump makes those unreachable anyway).
    if (doc.RootElement.TryGetProperty("reportMetadata", out var repArr)
        && repArr.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        foreach (var e in repArr.EnumerateArray())
        {
            AlReportMetadataRegistry.Register(
                e.GetProperty("id").GetInt32(),
                e.GetProperty("xml").GetString() ?? string.Empty);
        }
    }
    // v5: replay per-report rendering-layout declarations.
    if (doc.RootElement.TryGetProperty("reportLayouts", out var layoutArr)
        && layoutArr.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        AlReportLayoutRegistry.LoadFromJsonArray(layoutArr);
    }
    // v6: replay per-page runtime metadata XML.
    if (doc.RootElement.TryGetProperty("pageMetadata", out var pageArr)
        && pageArr.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        foreach (var e in pageArr.EnumerateArray())
        {
            AlPageMetadataRegistry.Register(
                e.GetProperty("id").GetInt32(),
                e.GetProperty("xml").GetString() ?? string.Empty);
        }
    }
    // v8: replay per-xmlport runtime metadata XML.
    if (doc.RootElement.TryGetProperty("xmlPortMetadata", out var xmlPortArr)
        && xmlPortArr.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        foreach (var e in xmlPortArr.EnumerateArray())
        {
            AlXmlPortMetadataRegistry.Register(
                e.GetProperty("id").GetInt32(),
                e.GetProperty("xml").GetString() ?? string.Empty);
        }
    }
    return count;
}

// Read app.json deps and feed them through DependencyResolver so the cache key
// reflects the exact resolved set (id+version), not just declared roots. This
// matches what BcCompiler.SetResolvedDeps fed into the compile.
//
// The dirs MUST match the ones the compile resolves against — bundlePkgDirs (the
// bundle's own .alpackages, found by recursive search) CONCAT the package caches. This
// used to be handed only the package caches, and the omission was total, not partial:
// a bundle whose roots live in its own .alpackages could not resolve at all, the throw
// landed in the catch below, and the key got NO dep line whatsoever. So the key was
// blind to the entire dependency closure. Observed: adding a System.app package changed
// the emitted DLL (3175424 -> 3206144 bytes) while the key stayed
// 67c4f8c4622a928aae07bf1857af515bb37fc5df4ac16eb047855f5dd2f9bba8 — a warm cache then
// serves a DLL compiled against a different dependency closure. Same defect family as
// the --define symbols that were missing from this key.
static IReadOnlyList<string> GetOrderedDepIds(
    string? bucketRoot, IReadOnlyList<string> packageCacheDirs, string? bundleAbs = null)
{
    // Same closure the emit actually compiles against — a parent-of-many-apps bundle has no
    // app.json of its own and takes the union of its children (see CollectBundleManifests).
    // Keying on a DIFFERENT closure than the one used to compile would let two bundles that
    // resolve differently share a cache entry.
    var depRootDir = bucketRoot ?? bundleAbs;
    if (depRootDir == null) return Array.Empty<string>();
    var manifests = CollectBundleManifests(bucketRoot, bundleAbs ?? depRootDir);
    if (manifests.Count == 0) return Array.Empty<string>();
    try
    {
        var roots = ReadBundleDependencyRoots(manifests);
        var bundlePkgDirs = Directory
            .EnumerateDirectories(depRootDir, ".alpackages", SearchOption.AllDirectories)
            .ToList();
        var resolver = new AlRunner.DependencyResolver(
            bundlePkgDirs.Concat(packageCacheDirs).Distinct().ToList());
        var ordered = resolver.Resolve(roots);
        return ordered
            // Id:Version alone is NOT a content identity: a sibling source app keeps
            // its app.json version while its schema evolves during development, so a
            // key without the winning .app's file stamp served the test bundle a
            // stale compiled assembly after e.g. a field removal — a runtime
            // NavNCLFieldNotFoundException where a fresh compile correctly fails.
            // mtime+size (not a content hash) keeps big platform .apps cheap to
            // stamp; the layered pre-pass only rewrites a synthesized sibling .app
            // when its source actually changed, so stamps are stable across runs.
            .Select(d =>
            {
                string stamp = "?";
                try
                {
                    var fi = new FileInfo(d.AppPath);
                    if (fi.Exists) stamp = $"{fi.LastWriteTimeUtc.Ticks}:{fi.Length}";
                }
                catch { /* unreadable path keys as "?" and simply cannot HIT */ }
                return $"{d.Manifest.AppId:N}:{d.Manifest.Version}:{stamp}";
            })
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }
    catch (Exception ex)
    {
        // Never collapse to "no deps": an empty list is indistinguishable from a bundle
        // that genuinely has none, so two different closures would share a key and the
        // cache would hand back the wrong DLL. Fold the failure itself into the key
        // instead — an unresolvable closure is its own cache identity, and it changes
        // again as soon as the reason changes.
        Console.Error.WriteLine(
            $"  [cache] dependency resolution failed while computing the cache key for " +
            $"{depRootDir}: {ex.GetType().Name}: {ex.Message}. Keying on the failure so this " +
            $"bundle cannot share a cache entry with a resolvable one.");
        return new[] { $"unresolved:{ex.GetType().Name}:{ex.Message}" };
    }
}

static IEnumerable<string> EnumerateSuites(string root)
{
    // Defence in depth for #1713. The CLI validates the positional roots up front, but
    // this runs again per watch-mode cycle and per bundle, and a directory can vanish
    // between the check and the walk (a watch session while the tree is being moved, a
    // submodule being re-checked-out). Yielding nothing lets the caller print its own
    // loud "SKIP (no suites)" line instead of throwing DirectoryNotFoundException out
    // of Main with exit 134 — the crash code, for a merely absent directory.
    if (!Directory.Exists(root)) yield break;

    // Root first: a directory that is itself one app (app.json at its root, or a
    // src//test/ split) is ONE bucket, however many category sub-directories it
    // holds. This is the al-language corpus shape — checking the root before
    // descending is what keeps the corpus a single compile unit.
    if (LooksLikeSuite(root)) { yield return Path.GetFullPath(root); yield break; }

    // Otherwise the root is a container of suites. Descend, but stop at the first
    // suite on each branch: a suite's own sub-directories are part of that suite,
    // never separate buckets.
    bool found = false;
    foreach (var d in EnumerateSuitesBelow(root))
    {
        found = true;
        yield return d;
    }

    // Flat bundle: no app.json and no src//test/ anywhere, but .al files exist.
    // Treat the whole root as one compilation + test unit.
    if (!found && Directory.EnumerateFiles(root, "*.al", SearchOption.AllDirectories).Any())
        yield return Path.GetFullPath(root);
}

static IEnumerable<string> EnumerateSuitesBelow(string dir)
{
    // Same guard as EnumerateSuites — this is the frame that actually threw in #1713,
    // and it also recurses into directories that may disappear mid-walk.
    if (!Directory.Exists(dir)) yield break;

    foreach (var child in Directory.EnumerateDirectories(dir))
    {
        if (LooksLikeSuite(child))
            yield return Path.GetFullPath(child);
        else
            foreach (var nested in EnumerateSuitesBelow(child))
                yield return nested;
    }
}

// A directory is a suite if it declares its own app (app.json) or uses the
// src//test/ split. The app.json clause is what makes flat suites — app.json plus
// .al files, no sub-structure, the shape every tests/runner-extras suite uses —
// enumerate individually instead of collapsing into one bundle (#1623, #1638).
static bool LooksLikeSuite(string dir)
    => File.Exists(Path.Combine(dir, "app.json"))
    || Directory.Exists(Path.Combine(dir, "test"))
    || Directory.Exists(Path.Combine(dir, "src"));
