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
using static AlRunner.ProgramSupport;
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
int jobs = 1;   // --jobs N: fan out across N worker processes (#2280)
int resumeAborts = AlRunner.Infrastructure.AbortResume.DefaultBudget;   // #2280: resume past a watchdog abort
var excludeTests = new List<string>();   // --exclude-test: skip these, so a run can resume past a watchdog abort (#2280)
var mergeCountsFiles = new List<string>();   // #2280: totals carried in from earlier resume attempts
var allAbortReasons = new List<string>();   // #2280: watchdog aborts seen this run, for auto-resume
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
// #1821/#2555: mirrors alCacheDir for the OTHER caches CacheRoots redirects — set by an
// explicit --cache flag directly, or by --no-cache indirectly via noCacheRequested below
// (CacheRoots.DisableForRun() mints the actual throwaway directory once both flags have
// been read — see the --cache/--no-cache parsing branch below and
// AlRunner.Infrastructure.CacheRoots for what this drives).
string? cacheRootOverride = null;
// #2555: --cache and --no-cache are last-wins against each other for ALL of alCacheDir,
// cacheRootOverride and this flag — whichever appears last on the command line decides
// both "is the AL-output cache on" (alCacheDir) and "are the other CacheRoots caches
// redirected to a throwaway root" (cacheRootOverride, resolved from this flag right
// before either re-exec decision point, so a re-exec'd child inherits the SAME throwaway
// directory rather than minting its own — see CacheRoots.NoCacheRootEnvVar).
bool noCacheRequested = false;
// --print-cache-key (issue #1851): a diagnostic/test-support mode. Reaches the SAME
// ComputeAlCacheKey call, with the SAME arguments, that a real run would use for the
// first app group it processes — then prints it and exits, without emitting or compiling
// that app group. Exists so callers that only need to assert a property of the KEY (not
// of a compiled DLL) don't have to pay for a full cold AL compile to get one. There is no
// second/parallel key computation — see the call site below, unchanged from the normal
// path up to and including the ComputeAlCacheKey call itself.
//
// It is NOT free, and the help text must not imply it is. The short-circuit sits inside
// the per-app-group loop, so RunLayeredPrePass has already built every dependency
// implementation bundle from source by the time a key is printed. That cost cannot be
// skipped: the key covers the resolved dependency set, so a run that skipped the pre-pass
// would print a different key than the run it stands in for.
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
// Issue #2236: BC artifact country/localization channel — "w1" (worldwide, default) or a
// country code such as "us"/"de"/"gb". Not validated against a hardcoded allowlist here
// (Microsoft adds codes on its own schedule); an unresolvable code fails loud, naming the
// exact CDN URL that 404'd, the first time provisioning actually needs it (see
// ArtifactDownloader.PlatformApps / TryHeadContentLength). Applies to BOTH a normal run's
// --auto-provision AND the `provision` subcommand (including `provision --platform-apps`).
string countryArg = "w1";
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
    if (args[i] == "--country" && i + 1 < args.Length) { countryArg = args[++i]; continue; }
    // #2258: --test-data / --test-data=PATH. Off by default; absent the flag nothing about
    // the run changes (no backup opened, no reader located, install-baseline cache key
    // unchanged). The optional value uses the EQUALS form on purpose — a space-separated
    // optional value could not be told apart from the bundle path that follows it, so
    // `al-runner --test-data tests/foo` would be ambiguous. See TestDataOptions.
    if (args[i] == "--test-data-company" && i + 1 < args.Length)
    { AlRunner.Infrastructure.TestDataOptions.CompanyOverride = args[++i]; continue; }
    if (AlRunner.Infrastructure.TestDataOptions.TryParseArg(args[i])) { continue; }
    if (args[i] == "--bc-version" && i + 1 < args.Length) { bcVersionArg = args[++i]; continue; }
    if (args[i] == "--artifact-path" && i + 1 < args.Length) { artifactPathArg = args[++i]; continue; }
    if (args[i] == "--out" && i + 1 < args.Length) { outPath = args[++i]; printClassification = true; continue; }
    if (args[i] == "--classify") { printClassification = true; continue; }
    if (args[i] == "--output-json") { outputJson = true; continue; }
    if (args[i] == "--output-junit" && i + 1 < args.Length) { outputJunitPath = args[++i]; continue; }
    if ((args[i] == "--jobs" || args[i] == "-j") && i + 1 < args.Length)
    {
        if (!int.TryParse(args[++i], out jobs) || jobs < 1)
        {
            Console.Error.WriteLine($"--jobs expects a positive integer, got '{args[i]}'.");
            return 2;
        }
        continue;
    }
    if (args[i] == "--exclude-test" && i + 1 < args.Length) { excludeTests.Add(args[++i]); continue; }
    if (args[i] == "--merge-counts" && i + 1 < args.Length) { mergeCountsFiles.Add(args[++i]); continue; }
    if (args[i] == "--resume-aborts" && i + 1 < args.Length)
    {
        if (!int.TryParse(args[++i], out resumeAborts) || resumeAborts < 0)
        {
            Console.Error.WriteLine("--resume-aborts expects a non-negative integer.");
            return 2;
        }
        continue;
    }
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
    // #1821: the SAME --cache value also becomes the isolation root for every other
    // cache CacheRoots redirects (compiled-deps/workspace-deps/ncl-cecil/bc-symbols/
    // ncl-shadow/app-manifests/r2r-chunks/install-baseline) — see
    // AlRunner.Infrastructure.CacheRoots for why al-out itself is unaffected.
    // #2555: --cache and --no-cache are last-wins against each other for BOTH
    // alCacheDir and cacheRootOverride — an explicit --cache re-enables everything
    // a preceding --no-cache turned off, including the other caches' redirect.
    if (args[i] == "--cache" && i + 1 < args.Length) { alCacheDir = args[++i]; cacheRootOverride = alCacheDir; noCacheRequested = false; continue; }
    // #2555: previously only disabled the AL-output cache (alCacheDir); the other
    // caches CacheRoots redirects stayed warm, so a run reached for specifically to
    // reproduce/measure a cold compile still got most of what "cold" is supposed to
    // cost. noCacheRequested is resolved to an actual throwaway directory (via
    // CacheRoots.DisableForRun()) right before either re-exec decision point below —
    // resolving it here instead would mint a fresh directory per --no-cache token
    // even when --cache later overrides it, and more importantly a --no-cache/--cache
    // combination earlier on the command line would already have committed to a
    // directory that a LATER flag on the same line should be able to undo.
    if (args[i] == "--no-cache") { alCacheDir = null; noCacheRequested = true; continue; }
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
// Issue #2236: set the process-wide selected country as early as possible — before
// RunExplicitProvisionModes, PlatformCheckDirs, DefaultPackageCacheDirs, or any other
// resolver that reads AlRunner.Infrastructure.BcArtifacts.SelectedCountry gets a chance
// to run. The setter itself normalizes (trim + lowercase, empty/whitespace -> "w1").
AlRunner.Infrastructure.BcArtifacts.SelectedCountry = countryArg;
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
// #2560: `provision --force` alone (subcommand present, --force present, but none of
// --platform-apps/--test-apps/--service-tier) matched neither guard above -- it fell
// through into the ordinary auto-detect run path with --force silently discarded and no
// message. Same failure mode as the two guards above, just the missing THIRD combination:
// deliberately excludes --resolve-version (that flag never reaches a download step at
// all -- see RunExplicitProvisionModes -- so pairing it with --force is equally
// meaningless, but is its own, differently-worded misuse if ever rejected).
if (provisionSubcommand && provisionForce
    && !(provisionPlatformApps || provisionTestApps || provisionServiceTier))
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
            //
            // Issue #2239: this is the "which artifact was selected and why" reasoning a
            // clean run does not need to see — the outcome is already named once, later,
            // by the unconditional `[bc] selected BC ...` line. Gated on --verbose like
            // its siblings below rather than printed unconditionally.
            if (AlRunner.Log.Verbose)
                Console.Error.WriteLine($"[bc] no --bc-version given — selecting BC {bcVersionArg}, the latest " +
                    $"cached artifact ({shippedVariantsForDefault.Count} engine variant(s) shipped; the matching " +
                    $"one is selected automatically below). Override with --bc-version.");
        }
        catch (InvalidOperationException)
        {
            // No artifacts cached at all — leave bcVersionArg null. SelectVersion below
            // throws the loud, path-naming "no artifacts" error users already see today.
        }

        // Issue #2239: same shape as #2210's DescribeCrossMajorNote gate a few dozen
        // lines below (the no-variants-shipped half of this if/else) — a project
        // declaring an older major floor than what got selected is the expected case
        // (application/platform are minima, not pins), not a risk this branch's
        // sibling warning below is exempt from just because it lives in a different
        // half of the if/else. Gated the same way for the same reason.
        var projMajorV = TryDeriveBcMajorFromProject(bundles);
        if (AlRunner.Log.Verbose && projMajorV != null && bcVersionArg != null
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
                    // Issue #2239: normal-path reasoning, no risk — the outcome is
                    // already named once, unconditionally, by the `[bc] selected BC
                    // ...` line further down. Gated behind --verbose like its sibling
                    // above (the shipped-variants branch's own auto-select line).
                    deferredStartupLines.Add(() =>
                    {
                        if (AlRunner.Log.Verbose)
                            Console.Error.WriteLine(
                                $"[bc] no --bc-version given — selecting BC {engineVersion}, the exact " +
                                $"build this binary was compiled against. Override with --bc-version.");
                    });
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

            // #2210: gated on --verbose, not printed at default verbosity at all. The
            // issue asked to decide between "refuse" and "stop warning" for a condition
            // that fires on most runs of an affected project and then passes — training
            // users to skim past everything the runner prints, including the warnings
            // that matter. Measured (#2210): this mismatch (declared major trailing the
            // engine's own by one) produced no divergence on AL exercising real
            // Base/System Application logic, and BC's own floor semantics say there
            // should not be one — application/platform are minima, not pins, so an app
            // declaring an older floor running on a newer major is the expected case,
            // not a degraded one. A condition that is expected and carries no measured
            // risk does not belong in a normal run's output; it stays available to
            // anyone chasing exact-major parity via --verbose. See BcArtifacts.
            // DescribeCrossMajorNote for the full measurement and wording.
            //
            // Still deferred when it DOES print (--verbose): printing it immediately
            // duplicated once per re-exec generation (before the shadow-hop re-exec AND
            // again in the child that performs it, sometimes a third time on a stacked
            // Cecil-fresh-rewrite re-exec) — this note does not explain a subsequent
            // failure (unlike the KNOWN-DEGRADED tier branches above), so nothing is
            // lost by holding it to the terminal generation's flush point.
            if (AlRunner.Log.Verbose)
            {
                var projMajor = TryDeriveBcMajorFromProject(bundles);
                var crossMajorNote = AlRunner.Infrastructure.BcArtifacts.DescribeCrossMajorNote(projMajor, engineMajor.Value);
                if (crossMajorNote != null)
                    deferredStartupLines.Add(() => Console.Error.WriteLine($"[bc] note: {crossMajorNote}"));
            }
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
    // Issue #2236: name the selected country whenever it is not the (invisible, so far
    // unremarkable) w1 default — a --country run producing the byte-identical "[bc]
    // selected BC ..." line as a w1 run would leave no visible trace that --country was
    // even recognized, which is exactly the kind of silent-no-op this repo's
    // loud-failures.md rule exists to prevent for a flag that changes what gets downloaded.
    var selectedCountryForPrint = AlRunner.Infrastructure.BcArtifacts.SelectedCountry;
    deferredStartupLines.Add(() => Console.Error.WriteLine(
        selectedCountryForPrint == "w1"
            ? $"[bc] selected BC {selectedVersionForPrint} ({serviceTierDirForPrint})"
            : $"[bc] selected BC {selectedVersionForPrint} ({serviceTierDirForPrint}) [country: {selectedCountryForPrint}]"));
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
            // Issue #2239: engine-variant selection mechanics — a diagnostic, not the
            // result. The `[reexec] Re-execing into a shadow runtime dir with the
            // matching BC-minor engine variant` line right after this decision already
            // explains that a hand-off is happening; this line is the WHY, gated the
            // same way.
            if (AlRunner.Log.Verbose)
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
// #1821/#2555: must run before the Cecil rewrite below (first ncl-cecil consumer), the
// shadow-hop re-exec decision right after it (first ncl-shadow consumer), and well
// before any DependencyLoader/BcAppSymbolCache/workspace-deps/AppLoader call — every one
// of those reads CacheRoots.Resolve for its cache directory. --no-cache resolves to an
// actual throwaway directory HERE (not at parse time) so a later --cache on the same
// command line can still override it (last-wins, see the parsing branch above), and the
// directory is minted (or, on a re-exec'd child, adopted from CacheRoots.NoCacheRootEnvVar
// — see that constant's doc) before either re-exec decision point can hand off to a child
// that would otherwise mint its own.
if (noCacheRequested)
{
    AlRunner.Infrastructure.CacheRoots.DisableForRun();
    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        AlRunner.Infrastructure.CacheRoots.CleanupThrowawayRoot();
}
else
{
    AlRunner.Infrastructure.CacheRoots.SetOverride(cacheRootOverride);
}
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
        // #2239: gated behind --verbose, same as the other re-exec notices — see
        // TryShadowReexec's own comment for why.
        if (AlRunner.Log.Verbose)
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

// --jobs: fan out across worker processes (#2280). Deliberately placed HERE, after the
// deferred-startup flush, because that line marks the terminal generation — both re-exec
// decision points (the shadow hop and the Cecil-fresh-rewrite hop) are behind us, so BC is
// selected and the rewritten Ncl is on disk. Fanning out earlier would have every worker race
// to perform the same first-ever Cecil rewrite; fanning out later would make the parent pay a
// full bundle run it is not going to use.
//
// Only the plain multi-bundle CLI path. --watch, --server and --dap are long-lived single
// processes whose whole contract is warm in-process state, and one bundle cannot be split
// across processes without splitting it by test, which this does not do yet.
if (jobs > 1 && bundles.Count > 1 && !watchMode && !serverMode && !dapMode)
    return AlRunner.Infrastructure.ParallelFanOut.Run(bundles, args, jobs);


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
// pin this exact label + count as a precondition on the explicit-arg branch (both now
// pass --verbose to see it).
// Issue #2239: package-cache directory counts are diagnostic detail, not a result —
// gated behind --verbose like the rest of this file's startup bookkeeping.
if (AlRunner.Log.Verbose)
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
var bundleAlpackagesDirs = AlRunner.Infrastructure.ProvisioningCheck.CollectBundleAlpackagesDirs(
    bundles, out var inaccessibleBundleDirs);
// Issue #2206: an unreadable subdirectory used to abort this scan with an unhandled
// UnauthorizedAccessException (exit 134). It is now skipped — but skipping SILENTLY would
// hide the case where the `.alpackages` the user expected lives under one of these, turning
// a permissions problem into a missing-dependency mystery later. Measured: real repo and
// workspace roots hit zero of these, so this stays quiet on the trees people actually use.
var inaccessibleScanWarning =
    AlRunner.Infrastructure.ProvisioningCheck.FormatInaccessibleScanWarning(inaccessibleBundleDirs);
if (inaccessibleScanWarning != null) Console.WriteLine(inaccessibleScanWarning);

// Issue #1996 (AC #3/#4): the runner-owned versioned destination(s) from a PRIOR
// --auto-provision / `provision` run — checked BEFORE any network attempt, and BEFORE any
// --package-cache dir even needs to exist, so a warm re-run (even one still passing an
// empty/nonexistent --package-cache, as issue #1996's own repro does) never re-hits the
// CDN. Populated once the selected BC version is known (already true at this point).
var selectedVersionForProvisioning = AlRunner.Infrastructure.BcArtifacts.SelectedVersion.ToString();
// Issue #2234: scan every patch directory sharing the selected engine's major.minor, not
// just its own exact patch — #2226 (separate, still open) can leave `provision`'s
// platform-app and test-app sub-steps under DIFFERENT patch directories of the same
// major.minor, and need detection used to look only at the engine's own patch, missing a
// sibling directory the run path found (as an incidental side effect of the auto-provision
// reuse scan) and reporting "missing" forever even right after `provision` succeeded.
var extraProvisionSearchDirsMajorMinor = AlRunner.Infrastructure.ProvisioningCheck.ResolveProvisionMajorMinor(
    selectedVersionForProvisioning);
var extraProvisionSearchDirs = AlRunner.Infrastructure.ProvisioningCheck.CollectRunnerOwnedProvisionDirs(
    AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, extraProvisionSearchDirsMajorMinor).ToList();
List<string> PlatformCheckDirs() =>
    packageCacheDirs.Concat(bundleAlpackagesDirs).Concat(extraProvisionSearchDirs).Distinct().ToList();
// These are read-only, already-on-disk runner-owned dirs for the SELECTED version's
// major.minor line — fold them into the set dependency resolution actually uses too
// (mirrors what DefaultPackageCacheDirs already does automatically when no explicit
// --package-cache is given; this closes the same gap for an EXPLICIT --package-cache,
// e.g. this exact invocation's own CLI flags). Without this, a prior run's warm
// test-apps/platform-apps stay invisible to compilation even though the provisioning
// DECISION already sees them.
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
                decision.ShouldDownloadPlatform, decision.ShouldDownloadTest, PlatformCheckDirs(),
                decision.MissingPlatformApps));
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
        // Same rule DecideManifestProvisioning applies: a floor above the version being
        // provisioned is not something a download can fix, so it must not reject a warm
        // set (see ProvisioningCheck.DropUnsatisfiableFloors).
        var versionFloors = AlRunner.Infrastructure.ProvisioningCheck.DropUnsatisfiableFloors(
            AlRunner.Infrastructure.ProvisioningCheck.DetermineVersionFloors(manifestDependencyRoots), version);
        var full = (platformReport.Ok
                ? FindWarmProvisionedVersion(
                    AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, mm,
                    decision.RequiredPlatformApps, decision.ShouldDownloadTest,
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

        // Issue #2103: re-derive the need from the manifests that are now READABLE.
        //
        // The pre-scan above only ever sees the BUNDLE's own app.json roots. Learning that
        // (say) "Tests-TestLibraries" itself depends on "Application Test Library" means
        // reading THAT app's NavxManifest.xml, which lives inside the test-apps set — the
        // very thing that had not been fetched yet. That chicken-and-egg used to be broken
        // by a hand-transcribed edge table, which was correct for the BC version whoever
        // wrote it checked and silently wrong for the rest: on BC 27.x the same app declares
        // no Application Test Library dependency at all (and no 27.x artifact ships that
        // app), so the table sent provisioning after something unobtainable and the run died
        // with "platform apps (Application Test Library) still missing after download".
        //
        // Downloading the test set FIRST removes the guess: its manifests are the real,
        // per-version answer, and DecideManifestProvisioning reads them straight off disk.
        // Hence the order here — test-apps, then re-decide, then platform-apps.
        decision = AlRunner.Infrastructure.ProvisioningCheck.DecideManifestProvisioning(
            manifestDependencyRoots, platformReport, PlatformCheckDirs());
        foreach (var badPkg in decision.UnreadablePackages)
            Console.Error.WriteLine(
                $"[provision] warning: could not read the manifest of '{badPkg}' — its Microsoft " +
                "dependency edges are unknown, so a provisioning need it implies may be missed.");

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
                    full, platformAppsOut, AlRunner.Infrastructure.BcArtifacts.SelectedCountry,
                    m => Console.Error.WriteLine($"[provision] {m}"));
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
            // Only demand the apps the MANIFEST actually named — some BC versions never
            // ship some of them at all (Application Test Library does not exist on 27.x),
            // so a fixed list would fail every platform-apps download triggered by a bundle
            // that never needed that app. Issue #2205: this used to hardcode Application
            // Test Library, which the broadened need-detection would have made fatal for
            // every ordinary bundle on 27.x.
            var stillAbsent = AlRunner.Infrastructure.ProvisioningCheck.FindMissingPlatformApps(
                decision.RequiredPlatformApps, PlatformCheckDirs());
            if (stillAbsent.Count > 0)
            {
                Console.Error.WriteLine(
                    $"[provision] platform apps still missing after download: {string.Join(", ", stillAbsent)}.");
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
// Issue #2239: same as the "(requested)" line above — gated behind --verbose.
if (AlRunner.Log.Verbose)
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
// Issue #2239: patch-apply timing is diagnostic, not a result — gated behind --verbose.
if (AlRunner.Log.Verbose)
    Console.WriteLine($"BC runtime patches applied ({t0.ElapsedMilliseconds}ms)");
AlRunner.Infrastructure.PhaseLog.SetPatchesMs(t0.ElapsedMilliseconds);
AlRunner.PerfTrace.Log($"BcRuntime.EnsureApplied {t0.ElapsedMilliseconds}ms");

var emitter = new BcCompiler();
var assembler = new BcAssembler();
var executor = new TestExecutor { Isolation = isolation, TestFilter = testFilter, TimeoutSeconds = testTimeoutSeconds, Expectations = expectations };
// --exclude-test: the only way to reach tests a watchdog abort abandoned. TestExecutor stops
// the whole suite when a test hangs — correctly, since the hung thread is never killed and
// keeps mutating shared BC state — so those tests are reachable only from a fresh process that
// skips the offender by name (#2280).
if (excludeTests.Count > 0)
    executor.Exclusions = new AlRunner.Infrastructure.TestExclusionFilter(excludeTests);
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
// The package caches AS THE USER GAVE THEM, before either pre-pass prepends its synthetic
// workspace dirs. RunLayeredPrePass is re-run once per --watch cycle (see the call inside
// the watch loop), and it must be fed this list every time rather than its own previous
// output: feeding the extended list back in would leave the PREVIOUS cycle's workspace dir
// still at the front of the search order, where it wins resolution over the one this cycle
// just wrote — the same stale-dependency answer the re-run exists to prevent, only harder
// to see.
var basePackageCacheDirs = packageCacheDirs.ToList();

// The workspace package each depended-upon bundle resolved to, as of the most recent
// RunDependencyPrePasses call, and the AppIds whose package CHANGED between the previous call
// and that one. Under --watch this is what tells a dependent bundle that the ground moved
// underneath it even though its own files did not (#2683).
//
// A path comparison rather than a "did the pre-pass rewrite it" flag, because each impl's
// workspace directory is keyed on its source content: reverting an edit resolves straight back
// to a package written cycles ago, writes nothing, and still has to invalidate everything the
// EDITED module was serving in between.
var previousImplAppPaths = new Dictionary<Guid, string>();
var changedImplAppIds = new HashSet<Guid>();

// Dirs the COMPILE-time .app scanner may safely enumerate: everything except the
// synthetic workspace dirs (whose source-only .apps would trip AL1023).
var compilerPackageDirs = new List<string>();

// Both dependency pre-passes, from the untouched base list, with compilerPackageDirs
// recomputed to match. Returns null on success, or the exit code the caller should return.
//
// #2683: this used to be straight-line code that ran ONCE, before the `while (true)` watch
// loop below. Every later cycle therefore reused the workspace dirs synthesised from the
// FIRST cycle's dependency sources, and a dependent bundle went on compiling against the
// frozen *.symbols.json and EXECUTING the frozen .app's source — reporting a confident green
// for a dependency whose code had changed underneath it. Its AL-output cache could not catch
// that either: GetOrderedDepIds stamps each resolved .app with mtime:length precisely so a
// sibling source app's content invalidates the dependent, but nothing rewrote that .app after
// cycle 1, so the stamp never moved and the key HIT.
//
// Re-running is cheap when nothing changed. Each impl's workspace dir is keyed on its own
// source content (ComputeSourceWorkspaceKey), so an unchanged dependency finds its .app and
// sidecar already on disk and short-circuits both compiles, printing "[layered] cache HIT".
int? RunDependencyPrePasses()
{
    packageCacheDirs = basePackageCacheDirs.ToList();
    layeredWorkspaceDirs.Clear();
    changedImplAppIds.Clear();
    var implAppPaths = new Dictionary<Guid, string>();

    if (bundles.Count > 1)
    {
        try
        {
            packageCacheDirs = RunLayeredPrePass(bundles, packageCacheDirs, layeredWorkspaceDirs, implAppPaths);
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

    compilerPackageDirs.Clear();
    compilerPackageDirs.AddRange(packageCacheDirs
        .Where(d => !layeredWorkspaceDirs.Contains(d, StringComparer.OrdinalIgnoreCase)));

    // An impl counts as changed only if it was ALSO present last time: on the very first call
    // nothing has been compiled or loaded against it yet, so there is nothing to invalidate and
    // reporting every impl as "changed" would force a pointless full rebuild of cycle 1.
    foreach (var (appId, appPath) in implAppPaths)
        if (previousImplAppPaths.TryGetValue(appId, out var before)
            && !string.Equals(before, appPath, StringComparison.OrdinalIgnoreCase))
            changedImplAppIds.Add(appId);
    previousImplAppPaths = implAppPaths;

    // #2683, the runtime half. A dependency that was re-synthesised has new CODE, and
    // DependencyLoader caches the compiled module per AppId with a reuse gate that compares
    // Name/Publisher/Version — none of which move during development. Recompiling the dependent
    // against fresh symbols is not enough on its own: without this, the fresh compile still
    // EXECUTES the module built in the first cycle, and the only symptom is a test that keeps
    // passing.
    DependencyLoader.InvalidateApps(changedImplAppIds);
    return null;
}

{
    var prePassExit = RunDependencyPrePasses();
    if (prePassExit != null) return prePassExit.Value;
}

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

// #2683: re-synthesise the dependency workspace before re-running. The pre-passes above
// ran against the sources as they were when the process started; a --watch edit to a
// bundle that another bundle DEPENDS ON changes both halves of that handoff — the
// *.symbols.json the dependent compiles against, and the .app whose source
// DependencyLoader executes — and without this, both stay frozen at cycle 1. The
// dependent then re-emits from its AL-output cache in 0.0s and reports the previous
// compile's results as this cycle's, which is worse than a wrong answer because a
// developer in a tight edit loop trusts --watch more than a CI run.
//
// Skipped on the first cycle: the call above already did it, and repeating it would pay
// the pre-pass twice before the first test runs.
//
// A pre-pass failure does NOT exit the process here the way it does at startup. Watch mode
// exists to survive a broken intermediate edit, and the next keystroke may well fix it. The
// failure is printed and the cycle continues with the base caches and NO workspace dirs, so
// the dependent bundle fails its own compile naming the dependency it cannot resolve —
// loud and true — rather than quietly resolving the previous cycle's copy.
if (watchMode && watchCycleIndex > 0)
{
    var prePassExit = RunDependencyPrePasses();
    if (prePassExit != null)
        Console.Error.WriteLine(
            "[watch] dependency pre-pass FAILED this cycle (see the diagnostic above). Bundles that " +
            "depend on another bundle will fail to resolve it until the next edit fixes the build; " +
            "no result below is carried over from the previous cycle.");
}
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

    // Everything about this bundle that says "your package cache cannot serve this run":
    // dependencies no loader tier can implement (DependencyResolver.UnservableDependencies,
    // added below where they are printed) plus platform runtime apps found symbol-only
    // (reported from inside the dependency load, hence the collector). Collected as well as
    // printed so the run summary can name them again at the end — see Reporter.PrintSummary.
    // Declared this high because dependency resolution happens far above the bundle's other
    // per-bucket state, and reset per bundle so one bundle's missing package is not attributed
    // to every later bundle and every later --watch cycle.
    var bundleProvisionGaps = new List<string>();
    AlRunner.Infrastructure.ProvisionGapLog.Reset();

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
                    bundlePkgDirs = AlRunner.Infrastructure.SafeDirectoryScan.Directories(depRootDir, ".alpackages")
                        .ToList();
                var resolverDirs = bundlePkgDirs.Concat(packageCacheDirs).Distinct().ToList();
                var resolver = new DependencyResolver(resolverDirs, AlRunner.Infrastructure.CacheRoots.SourceBuiltPackageDirs());
                IReadOnlyList<(AlRunner.AppManifest Manifest, string AppPath)> ordered;
                using (AlRunner.Infrastructure.PhaseLog.Stage("dep-resolve"))
                    ordered = resolver.Resolve(roots);
                bundleResolvedDeps = ordered;
                // Issue #2239: per-bundle dep counts are diagnostic detail — gated behind
                // --verbose.
                if (AlRunner.Log.Verbose)
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
                {
                    Console.Error.WriteLine(u);
                    bundleProvisionGaps.Add(u);
                }
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
                // Platform runtime apps the load found symbol-only. Read straight after the load
                // that produces them, before anything else can reset the collector.
                bundleProvisionGaps.AddRange(AlRunner.Infrastructure.ProvisionGapLog.Collected);
                // Issue #2239: same as "resolved N dep(s)" above — gated behind --verbose.
                if (AlRunner.Log.Verbose)
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
            catch (AlRunner.Infrastructure.BcAppSymbolReadException ex)
            {
                // #2712: a resolved dependency .app's SymbolReference.json could not be read
                // to completion (reported: OutOfMemoryException parsing Base Application's
                // under a 1 GB heap limit). Before this handler that failure fell through to
                // the generic DEP-RESOLVE-FAIL catch below, which prints one line and keeps
                // going — and the run then reported 212 instead of 259 passing with exit 0.
                // Same posture as DependencyLoadException above: abort with exit 1 rather
                // than produce plausible-looking wrong results.
                if (stdoutSilenced) { Console.SetOut(savedOut); Console.SetError(savedErr); }
                Console.Error.WriteLine(
                    $"FATAL: dependency symbols unreadable — cannot continue. {ex.Message}");
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
                // Issue #2239: cache HIT/MISS classification is diagnostic detail, not a
                // result — gated behind --verbose.
                if (AlRunner.Log.Verbose)
                    Console.Error.WriteLine($"  [cache] HIT  key={cacheKey} path={cachePath} ({cachedBytes.Length} bytes, {replayed} enum entries replayed) — skipping Emit+Compile");
                AlRunner.Infrastructure.PhaseLog.NoteCacheHit();
                assemblyBytes = cachedBytes;
            }
        }
        if (needCompile && assemblyBytes == null)
        {
            if (alCacheDir != null)
            {
                if (AlRunner.Log.Verbose)
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
            // Emit-phase timeout: default 120 s, override via AL_RUNNER_EMIT_TIMEOUT_SEC. Under
            // --jobs, ParallelFanOut.WorkerEnvironment sets AL_RUNNER_EMIT_TIMEOUT_SEC on each
            // worker's environment to DefaultEmitTimeoutSec scaled by the shard count (#2715),
            // so this single-process default and that scaled one share the same source of truth.
            // Note: Task.Run thread continues in background after timeout — acceptable for a CLI tool.
            int emitTimeoutSec = int.TryParse(
                Environment.GetEnvironmentVariable("AL_RUNNER_EMIT_TIMEOUT_SEC"), out var ts)
                    ? ts : AlRunner.Infrastructure.ParallelFanOut.DefaultEmitTimeoutSec;
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
            // #2683: the incremental path decides from THIS bundle's own AL files alone. A bundle
            // whose files are byte-identical replays its previous generated C# verbatim — which
            // was compiled against, and baked member ids resolved from, its dependencies'
            // PREVIOUS surfaces. So when a bundle this one depends on was re-synthesised by the
            // pre-pass this cycle, "nothing of mine changed" is not a safe answer and the
            // incremental path is skipped outright. This is the --watch counterpart of the
            // forward fallback propagation #2603 added to RunAllBundlesForServer; --server got
            // that fix and --watch never did.
            //
            // The signal comes from the pre-pass rather than from the sibling bundle's own emit,
            // and that is deliberate: the pre-pass runs before ANY bundle compiles, so it answers
            // correctly whether the dependency is listed before or after this bundle in the
            // command line — the ordering hole ChangedLaterDependencyBundles exists to plug on
            // the server side.
            var changedDependency = changedImplAppIds.Count == 0
                ? null
                : DeclaredDependencyOn(appGroup.SuiteDir, changedImplAppIds);
            if (watchMode && changedDependency != null && watchCycleIndex > 0)
            {
                watchFullRebuildReasons.Add((moduleName, $"dependency '{changedDependency}' changed this cycle"));
                Console.Error.WriteLine(
                    $"[watch] {moduleName}: FULL REBUILD this cycle — the bundle it depends on " +
                    $"('{changedDependency}') changed, so replaying this bundle's previous output " +
                    "would test it against the previous compile of that dependency.");
            }
            if (watchMode && changedDependency == null)
            {
                incrementalOutput = emitter.TryEmitIncremental(
                    allPaths, moduleName, appGroup.SuiteDir,
                    out var incrementalFallbackReason, out _);
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
                            // #2207: same fix as the non-tdd branch below — actually print
                            // them under --verbose. Each synthetic FAILED test already carries
                            // its own object's diagnostic in its failure message, but that
                            // requires reading the per-test result; this gives the same
                            // information right at the summary line the message above points at.
                            var tddExclDiags = emitOutput.ExcludedObjectDiagnostics ?? Array.Empty<string>();
                            if (AlRunner.Log.Verbose && tddExclDiags.Count > 0)
                            {
                                Console.Error.WriteLine(
                                    $"<bundled>: AL diagnostics that identified the excluded object(s):");
                                foreach (var d in tddExclDiags)
                                    Console.Error.WriteLine($"  {d}");
                            }
                            bundleTests.AddRange(synthetic);
                            tddExcludedCount = emitOutput.ExcludedObjects.Count;
                            // sources stays as BcCompiler returned it (the recovered set) — do
                            // NOT clear it, unlike the non-tdd branch below.
                        }
                        else
                        {
                            // Issue #2238: a `profile` object carries no executable AL at
                            // all — no procedures, no [Test] attributes, nothing a headless
                            // run could ever execute or observe. It is role-center
                            // presentation metadata (Caption/Description/RoleCenter), which
                            // this runner never renders. So when EVERY excluded object is a
                            // profile, none of loud-failures.md's concern applies: there is
                            // no test a profile could have declared to go silently missing.
                            // (The crash this guards against — BC's own ProfileMetadataEmitter
                            // throwing a NullReferenceException in SymbolExtensions.
                            // ShouldBeEmitted when the profile's RoleCenter page reference
                            // fails to bind — is otherwise atomic-per-module: without this,
                            // one broken profile took every OTHER object in the same bundle
                            // down with it, including codeunits that DO declare tests.)
                            // This check is deliberately narrow and typed to the "Profile "
                            // label prefix BcCompiler.cs's exclusion loop always writes for a
                            // profile — not a general "ignore whatever failed to emit", which
                            // is exactly the mechanism .claude/rules/loud-failures.md forbids.
                            // A single non-profile object in the excluded set still fails the
                            // whole bundle via the branch below.
                            var allProfiles = emitOutput.ExcludedObjects.All(
                                o => o.StartsWith("Profile ", StringComparison.Ordinal));

                            // Untagged on purpose: a `[Component]` prefix would be swallowed by
                            // Log's filter at default verbosity, which is the original defect.
                            Console.Error.WriteLine(allProfiles
                                ? $"<bundled>: EMIT-EXCLUDED — {moduleName}: {emitOutput.ExcludedObjects.Count} " +
                                  $"profile object(s) could not be compiled and were dropped from the module: " +
                                  $"[{names}]. A profile declares no executable AL and no [Test] procedures, so " +
                                  $"the module compiles and runs without it. Re-run with --verbose for the AL " +
                                  $"diagnostics that identified them."
                                : $"<bundled>: EMIT-EXCLUDED — {moduleName}: {emitOutput.ExcludedObjects.Count} object(s) " +
                                  $"could not be compiled and were dropped from the module, so any tests they declare " +
                                  $"are MISSING from this run: [{names}]. Re-run with --verbose for the AL diagnostics " +
                                  $"that identified them.");
                            // #2207: the message above promises the AL diagnostics under
                            // --verbose — actually print them, gated on Log.Verbose directly
                            // (not Console.Error.WriteLine's usual [Component] path) so a
                            // developer following the instruction gets something, not another
                            // copy of the same summary line. Deliberately reads
                            // emitOutput.ExcludedObjectDiagnostics, NOT `alDiagnostics` — the
                            // latter reflects only the final (recovered) compile round and
                            // backs the EMIT-ZERO / AL-DIAGNOSTIC-FAIL guards below; it is
                            // formatted alc-style with no leading `[Tag]`, so it is never eaten
                            // by Log's filter either way.
                            var exclDiags = emitOutput.ExcludedObjectDiagnostics ?? Array.Empty<string>();
                            if (AlRunner.Log.Verbose && exclDiags.Count > 0)
                            {
                                Console.Error.WriteLine(
                                    $"<bundled>: AL diagnostics that identified the excluded object(s):");
                                foreach (var d in exclDiags)
                                    Console.Error.WriteLine($"  {d}");
                            }
                            if (!allProfiles)
                            {
                                bundleErrors.Add(
                                    $"<bundled>: EMIT-EXCLUDED for {moduleName}: {emitOutput.ExcludedObjects.Count} " +
                                    $"object(s) dropped from the module — tests they declare are missing: [{names}].");
                                sources = Array.Empty<EmittedSource>(); // do not run a module that is missing objects
                            }
                            // allProfiles: keep `sources` as BcCompiler returned it (the
                            // recovered set with only the broken profile(s) dropped) and do
                            // NOT add to bundleErrors — this is not a compile failure.
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
                        .SelectMany(d => AlRunner.Infrastructure.SafeDirectoryScan.Files(d, "*.al")))
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
                            // Issue #2239: same category as [cache] HIT/MISS above — cache
                            // population detail, not a result. Observed printing
                            // unconditionally on every cold run while verifying that fix
                            // (a clean run wrote this line even with HIT/MISS gated), the
                            // same sibling-defect shape — gated the same way.
                            if (AlRunner.Log.Verbose)
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
                // #2415: Run() returns normally even when a watchdog timeout aborted the
                // rest of this app group's codeunits — it doesn't throw, so the catch
                // below never sees it. Fold its own suite-error lines in here so the
                // "N suite errors" summary and the exit code (computedExitCode's
                // CompileErrors check) both reflect the abandoned tests.
                if (executor.AbortReasons.Count > 0)
                    bundleErrors.AddRange(executor.AbortReasons.Select(r => $"{rel}: TEST-TIMEOUT-ABORT: {r}"));
                    allAbortReasons.AddRange(executor.AbortReasons);
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
                // Named after the APP GROUP, not the bundle: this catch sits inside the loop
                // over the bundle's app groups, so it means THIS app contributed zero results
                // while its siblings ran normally. With "<bundled>" here instead, an app's whole
                // test set could disappear from a run and no line said whose — and the
                // TEST-TIMEOUT-ABORT line a few lines up already names its app group.
                bundleErrors.Add(AlRunner.Infrastructure.ExecFailure.Describe(
                    asm.GetName().Name ?? $"<asm {runIdx}>", ex));
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
                // #2415: see the bundled-mode call site's identical comment — Run()
                // returns normally on a watchdog-timeout abort, so the catch below
                // never sees it.
                if (executor.AbortReasons.Count > 0)
                    bundleErrors.AddRange(executor.AbortReasons.Select(r => $"{suiteName}: TEST-TIMEOUT-ABORT: {r}"));
                    allAbortReasons.AddRange(executor.AbortReasons);
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
        bundleEmit, bundleComp, bundleRun, ranGroupCount, bundleProvisionGaps));
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
    Reporter.PrintSummary(results, Console.Out, ProgramSupport.CarriedFromEarlierAttempts(mergeCountsFiles));
}

// #2280: one hung codeunit must not take the whole run down. TestExecutor abandons the rest of
// the bundle when a test's watchdog fires — correctly, because the hung thread is never killed
// and keeps mutating shared BC state — so the abandoned tests are only reachable from a FRESH
// process. Re-run there with the hung codeunit excluded, and let that process resume again if it
// hits a different hang. A resumed attempt re-runs the bundle from the start, so its result
// REPLACES this one rather than needing to be merged into it; the excluded codeunits are named
// so the total is not mistaken for a complete one.
if (resumeAborts > 0
    && AlRunner.Infrastructure.AbortResumePlan.MakesProgress(allAbortReasons, excludeTests))
{
    // Exclude every codeunit already ATTEMPTED, not just the hung one, so the retry runs only
    // work no attempt has reached. Re-running from the start made a bundle pay for its whole
    // successful prefix again — and under --jobs the unit of retry is the shard, so eight
    // buckets re-ran because one codeunit in one of them hung.
    var attemptedTests = results.SelectMany(b => b.Tests).ToList();
    var nextExclusions = AlRunner.Infrastructure.AbortResumePlan.NextExclusions(
        allAbortReasons, excludeTests, attemptedTests);

    // This attempt's results are a partial view; carry them forward so the final summary is the
    // whole run rather than the last slice of it.
    var carryDir = Path.Combine(Path.GetTempPath(), "al-runner-resume-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(carryDir);
    var carryPath = Path.Combine(carryDir, "attempt.xml");
    // THIS attempt's results only — deliberately not the carried-files overload. Each carry file
    // must hold exactly one attempt, because the final attempt folds the whole list into its
    // --output-junit (#2716); a carry file that already contained the earlier files would put
    // every attempt into the final XML once per later resume.
    try { JUnitReport.WriteJUnit(carryPath, results); } catch { carryPath = null!; }
    var carry = new List<string>(mergeCountsFiles);
    if (carryPath != null) carry.Add(carryPath);

    return AlRunner.Infrastructure.AbortResume.Rerun(args, nextExclusions, resumeAborts - 1, carry);
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
    // #2716: after a watchdog resume this process ran only the codeunits no earlier attempt
    // reached, so `results` is a slice. The earlier attempts' cases arrive as --merge-counts
    // files and go into the XML too — the printed summary above already folded their totals in,
    // and under --jobs the parent reads ONLY this file, so a slice here silently shrank the
    // aggregate by everything the earlier attempts ran. Empty list on a run that never resumed.
    JUnitReport.WriteJUnit(outputJunitPath, results, mergeCountsFiles);
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

// Issue #2481's behavioural regression gate — dumps the per-statement/per-scope
// instrumentation call counters to stderr so a subprocess-spawning test (which cannot
// read this process's static fields directly) can assert "the Cecil-rewritten hook
// fired on every statement, but did zero bookkeeping work" for a plain run. Never
// printed on a normal run: opt-in via an undocumented env var, not a CLI flag, because
// this exists for AlRunner.Tests/PlainRunInstrumentationGateTests.cs only. Computing
// and printing four longs/bools costs nothing worth gating further.
if (Environment.GetEnvironmentVariable("AL_RUNNER_DUMP_INSTRUMENTATION_COUNTERS") == "1")
{
    Console.Error.WriteLine(
        "[instrumentation-counters] " +
        $"coverage.CallCount={AlRunner.Infrastructure.AlCoverageTracker.CallCount} " +
        $"coverage.HasRecordedAnyHits={AlRunner.Infrastructure.AlCoverageTracker.HasRecordedAnyHits} " +
        $"captureValues.CallCount={AlRunner.Infrastructure.AlValueCapture.CallCount} " +
        $"captureValues.CollectedCount={AlRunner.Infrastructure.AlValueCapture.Collect().Count} " +
        $"dap.CallCount={AlRunner.Infrastructure.AlDapSession.CallCount} " +
        $"dap.WorkPerformedCount={AlRunner.Infrastructure.AlDapSession.WorkPerformedCount}");
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
    // per-bundle loop. Then, regardless of bundle count, run sibling source-dep
    // discovery so a single-bundle request can still resolve source-only sibling apps.
    //
    // `cancellationToken` (default: none, for the `execute` caller which has no
    // active-run CTS) is checked BETWEEN bundles: a `cancel` landing while bundle 1
    // of a multi-bundle runTests request is still running must stop bundle 2 from
    // ever starting, not just stop mid-bundle-1 (that half is TestExecutor.Run's job).
    List<ServerRunResult> RunAllBundlesForServer(string[] sourcePaths, string[]? requestPackagePaths,
        Func<Assembly, IReadOnlyList<TestResult>> runStep,
        System.Threading.CancellationToken cancellationToken = default,
        bool useIncrementalChangeModel = false,
        // #2539: 6th arg is the REQUEST-WIDE procedure-granular peek (PeekChangedScopes,
        // unioned across every bundle — see requestWideChangedScopes), appended by the
        // effectiveBeforeRun wrap below. RunBundleForServer's own (5-arg) beforeRun delegate
        // is unchanged, since it has no access to that request-wide list.
        Action<string, string, string, IReadOnlyList<AffectedObjectId>?, string?, IReadOnlyList<AffectedScopeId>?>? beforeRun = null)
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

        var bundleList = sourcePaths.ToList();
        var workspaceScratch = new List<string>();
        if (sourcePaths.Length > 1)
        {
            try
            {
                packageCacheDirs = RunLayeredPrePass(bundleList, packageCacheDirs, workspaceScratch);
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

        try
        {
            packageCacheDirs = BuildSiblingSourceDeps(bundleList, packageCacheDirs, workspaceScratch);
        }
        catch (Exception ex)
        {
            // Same failure contract as the layered pre-pass above.
            return new List<ServerRunResult>
            {
                ServerRunResult.Failure(3, "<sibling-source-deps>", $"SIBLING-SOURCE-DEPS-FAIL: {ex.Message}", new())
            };
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
        // #2492: a test in one bundle can cover an AL object declared in ANOTHER bundle in
        // this SAME multi-sourcePaths request — a real cross-app dependency call, not a
        // hypothetical (the Pageworks/Pageworks.Test repro in that issue). Each bundle's own
        // affectedOnly selection below only ever sees ITS OWN changed-object set, so a change
        // to a DEPENDENCY app silently narrowed away every test in a DEPENDENT app that
        // covered it — a green run reporting fewer tests than a from-scratch run, with no
        // error. Peek every bundle's own changed-object set BEFORE any bundle's selection
        // decision (cheap: file-hash diff + parsing only the touched files, no BC Compilation/
        // Emit — see PeekChangedObjects), and union them so each bundle's overlap check can see
        // changes that happened in a sibling bundle. Gated on affectedOnly and more than one
        // bundle — a single-bundle request already sees its own full change set.
        List<AffectedObjectId>? requestWideChangedObjects = null;
        // Per bundle: how many objects its own peek saw change, or null when the peek could not
        // answer for it. See ChangedDependencyForcesFullCompile below.
        var peekedChangedCountByBundle = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        // #2539: procedure-granular peek, unioned across EVERY bundle in the request — the
        // scope-level mirror of requestWideChangedObjects above, and for the same reason:
        // Pageworks/Pageworks.Test is a real repro where the edited procedure lives in the
        // DEPENDENCY bundle and the tests that could narrow live in the SEPARATE test bundle,
        // so restricting this signal to "a bundle's own peek only" would leave the #2535-fixed
        // multi-bundle shape with zero additional narrowing from #2539. Unlike
        // requestWideChangedObjects, a single uncertain/null bundle does NOT null the whole
        // list — this signal is a pure OPTIMIZATION on top of the object-level keys
        // BuildAffectedChangedKeys always falls back to, so one bundle's peek failing only
        // costs that bundle's own refinement, never anyone's correctness. Also unlike
        // requestWideChangedObjects, populated even for a single-bundle request — procedure
        // narrowing is useful there too, so it is not gated on sourcePaths.Length > 1.
        // peekedChangedCountByBundle stays only populated where requestWideChangedObjects is
        // non-null (i.e. sourcePaths.Length > 1) below — its only consumer,
        // ChangedLaterDependencyBundles, already no-ops on fewer than two bundles.
        var requestWideChangedScopes = new List<AffectedScopeId>();
        if (useIncrementalChangeModel)
        {
            if (sourcePaths.Length > 1) requestWideChangedObjects = new List<AffectedObjectId>();
            foreach (var peekBundleDir in sourcePaths)
            {
                var peekAbs = Path.GetFullPath(peekBundleDir);
                var peekBucketRoot = FindBucketRoot(peekAbs) ?? peekAbs;
                var peekModuleName = $"V2_{Path.GetFileName(peekAbs)}";
                var peekPaths = new List<string>();
                foreach (var suite in EnumerateSuites(peekAbs))
                    peekPaths.AddRange(CollectSuitePaths(suite, peekBucketRoot));
                peekPaths = peekPaths.Distinct().ToList();

                var peekedScopes = emitter.PeekChangedScopes(peekPaths, peekModuleName, peekBucketRoot);
                if (peekedScopes != null) requestWideChangedScopes.AddRange(peekedScopes);

                if (requestWideChangedObjects == null) continue; // single-bundle request: no cross-bundle union needed

                var peeked = emitter.PeekChangedObjects(peekPaths, peekModuleName, peekBucketRoot);
                // #2603: the same peek answers a second question — "did THIS bundle change this
                // cycle?" — which is what lets a bundle listed BEFORE its own dependency decide
                // safely. Recorded per bundle, not folded into the union.
                peekedChangedCountByBundle[peekAbs] = peeked?.Count;
                if (peeked == null)
                {
                    // Unknown for at least one bundle (no baseline yet, app.json changed, an
                    // unclassifiable file) — cannot safely narrow ANYWHERE in this request.
                    // Null the whole union: every bundle below then merges nothing new in and
                    // keeps whatever its OWN cycle already decided, same as before this fix —
                    // never worse than the pre-#2492 behaviour, only better when every bundle's
                    // own change set peeks clean. #2539: keep peeking later bundles' OWN scopes
                    // above regardless — that signal is independent of this union.
                    requestWideChangedObjects = null;
                    continue;
                }
                requestWideChangedObjects.AddRange(peeked);
            }
        }
        // #2492, second half: the union above widens what a bundle SEES as changed, but this
        // corpus's per-test coverage only ever attributes a passing test to the codeunit that
        // DECLARES it — a helper (same-app OR cross-app) it calls into never appears in its
        // coveredObjects set (confirmed empirically: of 1012 tests, only the handful whose
        // OWN declaring codeunit id happened to be in `changedObjects` had ANY coverage entry
        // at all; every other test's statement-to-object mapping missed and fell back to
        // "unknown", which is why they were never at risk). For the few tests that DO have an
        // entry, the union above cannot help them — their entry never mentions the sibling
        // bundle's changed object no matter how complete the union is, because the entry was
        // never built from statements INSIDE that object to begin with. So when a sibling
        // bundle's own incremental cycle fell back to a full rebuild (changeModelFallbackReason
        // != null) this cycle, coverage-based narrowing cannot be trusted to have seen whatever
        // that bundle's tests actually depend on — propagate that bundle's fallback as a reason
        // for EVERY bundle processed AFTER it in this SAME request, so their own narrowing is
        // disabled (forcedFull) instead of silently trusting an attribution gap. Bundles are
        // processed in `sourcePaths` order — the one documented/supported shape is dependency
        // app before test app (see README), which is exactly the order this needs to see the
        // dependency's fallback before deciding the test app's selection.
        //
        // #2603: the SAME signal has to gate this request's later bundles' COMPILATION too, not
        // only their test selection. A bundle whose own files all hash identical to the last cycle
        // takes TryEmitIncremental's "genuinely zero work: replay the last cycle's result verbatim"
        // short-circuit — which is correct in isolation and wrong when a DEPENDENCY bundle in the
        // same request just re-emitted a surface this bundle's generated C# baked member ids
        // against. Measured: a dependency app that gains an overload correctly falls back to a full
        // compile (#2548), and the consuming test app, whose own sources did not move, replayed its
        // previous C# and dispatched the PREVIOUS overload — a green-looking run returning the wrong
        // answer, visible only because the AL test happened to assert the value.
        //
        // So `sawFallbackReason` is hoisted out of the closure below and read by the bundle loop:
        // once any bundle in this request has fallen back, every later bundle compiles in full
        // instead of replaying. Conservative on purpose — without an object-reference graph
        // (#2571) there is no way to ask whether THIS bundle actually binds to what that one
        // re-emitted, and the failure this prevents is silent while the cost is one full compile of
        // a bundle whose dependency just changed anyway.
        //
        // Same forward-only, `sourcePaths`-order limitation as the selection half above, and for
        // the same reason. #2571 tracks the order-independent version.
        string? sawFallbackReason = null;
        // #2539: explicitly typed as the 5-arg shape RunBundleForServer actually calls (see
        // its own beforeRun parameter below) — `beforeRun` itself is now 6-arg (carries the
        // ownChangedScopes lookup), so `var effectiveBeforeRun = beforeRun` would infer the
        // wrong delegate type here.
        Action<string, string, string, IReadOnlyList<AffectedObjectId>?, string?>? effectiveBeforeRun = null;
        if (beforeRun != null)
        {
            var union = requestWideChangedObjects;
            effectiveBeforeRun = (bundlePath, moduleName, selectionEnvironmentKey, changedObjects, changeModelFallbackReason) =>
            {
                var merged = union == null || changedObjects == null ? changedObjects : changedObjects.Concat(union).ToList();
                var effectiveFallbackReason = changeModelFallbackReason
                    ?? (sawFallbackReason != null
                        ? $"an earlier bundle in this request fell back this cycle ({sawFallbackReason})"
                        : null);
                if (changeModelFallbackReason != null)
                    sawFallbackReason ??= $"{moduleName}: {changeModelFallbackReason}";
                // #2539: the SAME request-wide scope list for every bundle — narrowing an
                // object changed in bundle A is legitimate for a test in bundle B exactly
                // when A's change is legitimate for B's overlap check at all (i.e. it is
                // already present in `merged` above, via the object-level union).
                beforeRun(bundlePath, moduleName, selectionEnvironmentKey, merged, effectiveFallbackReason, requestWideChangedScopes);
            };
        }

        // #2603, the order-independent half. The forward propagation above is only sound while a
        // bundle's in-request dependencies are compiled BEFORE it — the `sourcePaths` order the
        // README documents. Measured with that order reversed, the defect returns in full: the
        // consuming bundle is processed first, `sawFallbackReason` is still null, it replays its
        // previous C# verbatim, and the run reports BOUND-TO=1 where a cold run gives 2. An
        // undocumented argument order is still an accepted one, and answering it wrongly in
        // silence is the thing this whole change exists to stop.
        //
        // The request already knows enough to decide this without compiling anything and without
        // reordering execution (which would change the order test events stream out):
        //   * which bundles this one declares a dependency on — the same app.json identity read
        //     RunLayeredPrePass already does to decide which bundles are "impls";
        //   * whether each of those changed this cycle — the per-bundle peek above.
        //
        // The rule: a bundle may use the incremental change model only if every in-request bundle
        // it depends on either comes BEFORE it (the forward propagation covers it) or is
        // known-unchanged this cycle. In the documented order the first clause always holds, so
        // this costs nothing and changes no behaviour there.
        var forcedFullBundles = ChangedLaterDependencyBundles(sourcePaths, peekedChangedCountByBundle);

        var bundleIndex = 0;
        foreach (var bundleDir in sourcePaths)
        {
            if (cancellationToken.IsCancellationRequested) break;
            bundleIndex++;
            var relBundle = Path.GetRelativePath(
                Environment.CurrentDirectory, Path.GetFullPath(bundleDir));
            AlRunner.Infrastructure.PhaseLog.BeginBundle(relBundle, bundleIndex);
            // #2603: see the two comments above. An earlier bundle fell back, or a dependency of
            // this bundle is listed after it and changed this cycle — either way this bundle must
            // not serve its C# from a baseline recorded before that other bundle moved.
            var result = RunBundleForServer(bundleDir, requestPackagePaths, runStep,
                useIncrementalChangeModel && sawFallbackReason == null
                    && !forcedFullBundles.Contains(Path.GetFullPath(bundleDir)),
                effectiveBeforeRun,
                out var emitElapsed, out var compileElapsed, out var runElapsed);
            AlRunner.Infrastructure.PhaseLog.EndBundle(emitElapsed, compileElapsed, runElapsed);
            results.Add(result);
        }
        return results;
    }

    /// <summary>
    /// The bundles in this request that must NOT use the incremental change model because a bundle
    /// they depend on is listed AFTER them in <paramref name="sourcePaths"/> and changed this cycle.
    ///
    /// <para>Issue #2603. A bundle whose own files all hash identical replays its previous
    /// generated C# verbatim, which bakes the member ids its dependencies' PREVIOUS surfaces
    /// resolved to. The forward fallback propagation in <c>RunAllBundlesForServer</c> covers a
    /// dependency compiled earlier in the same request; it cannot cover one compiled later,
    /// because the signal it reads does not exist yet.</para>
    ///
    /// <para>Both inputs are already paid for: the dependency relation is the same app.json
    /// identity read <see cref="RunLayeredPrePass"/> does, and "did it change" is the per-bundle
    /// result of the peek <c>RunAllBundlesForServer</c> already performs for #2492's union.
    /// Nothing here compiles anything, and execution order is deliberately left alone — reordering
    /// would change the order test events stream out.</para>
    ///
    /// <para><b>Fail-closed on an unreadable answer.</b> A dependency whose peek could not answer
    /// (no baseline yet, app.json changed, an unclassifiable file — <c>PeekChangedObjects</c>
    /// returns null) counts as changed, and so does one that was never peeked at all. "I do not
    /// know whether it moved" and "it moved" have the same safe handling; only a positive
    /// zero-changes answer earns the fast path.</para>
    ///
    /// <para>Matching is by declared <c>AppId</c>, falling back to Name+Publisher — the same pair
    /// <see cref="RunLayeredPrePass"/> uses, and for the same reason: a bundle without a declared
    /// id still has to be recognisable as somebody's dependency.</para>
    /// </summary>
    static HashSet<string> ChangedLaterDependencyBundles(
        string[] sourcePaths, Dictionary<string, int?> peekedChangedCountByBundle)
    {
        var forced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sourcePaths.Length < 2 || peekedChangedCountByBundle.Count == 0) return forced;

        var order = new List<string>();
        var identities = new Dictionary<string, AlRunner.Infrastructure.BundleIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in sourcePaths)
        {
            var abs = Path.GetFullPath(bundle);
            order.Add(abs);
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
        if (identities.Count < 2) return forced;

        bool ChangedOrUnknown(string abs) =>
            !peekedChangedCountByBundle.TryGetValue(abs, out var count) || count is not 0;

        for (int i = 0; i < order.Count; i++)
        {
            if (!identities.TryGetValue(order[i], out var mine)) continue;
            for (int j = i + 1; j < order.Count; j++)
            {
                if (!identities.TryGetValue(order[j], out var later)) continue;
                bool dependsOnLater = mine.Dependencies.Any(dep =>
                    (dep.AppId != Guid.Empty && dep.AppId == later.AppId)
                    || (string.Equals(dep.Name, later.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(dep.Publisher, later.Publisher, StringComparison.OrdinalIgnoreCase)));
                if (dependsOnLater && ChangedOrUnknown(order[j]))
                {
                    forced.Add(order[i]);
                    break;
                }
            }
        }
        return forced;
    }

    // True when `path` is `root` itself or nested somewhere under it. Both are
    // full-pathed by the caller; trailing-separator-insensitive.
    static bool IsUnderDirectory(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // Compile + run one bundle, resetting bundle-derived caches first so an edited
    // same-identity bundle is picked up (server reload contract). Mirrors the
    // bundled-mode path of the normal run loop for a single bundle. The run step
    // (executor.Run for runTests, OnRun dispatch for execute) is supplied by the caller.
    ServerRunResult RunBundleForServer(string bundleDir, string[]? requestPackagePaths,
        Func<Assembly, IReadOnlyList<TestResult>> runStep,
        bool useIncrementalChangeModel,
        Action<string, string, string, IReadOnlyList<AffectedObjectId>?, string?>? beforeRun,
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
        // #2479: the environment key gates affectedOnly's per-test coverage baseline on
        // "did the resolved environment change in a way per-test coverage can't reason
        // about" (BC version/artifact/configured package caches) — deliberately a COARSER
        // signal than the incremental change model, which already answers "did a
        // dependency's CONTENT change" precisely via changedObjects. RunLayeredPrePass /
        // BuildSiblingSourceDeps (multi-sourcePaths requests only) give each impl a
        // content-keyed workspace dir under CacheRoots "workspace-deps" and add it to the process-wide
        // packageCacheDirs — that path changes every time the impl's own source changes,
        // since it is keyed on exactly that content, and OLD entries are never removed (the
        // process-lifetime list only ever grows across requests). Folding either into this
        // key made every dependency edit look like an "environment changed" event too,
        // forcing a full re-run of the WHOLE multi-bundle request on the NEXT request even
        // though changedObjects had already identified precisely what changed — the
        // environment check re-litigated a question the incremental model had already
        // answered correctly. Exclude every dir under the workspace-deps root (not just
        // the ones this specific request happened to add — a per-request list would still
        // leave a PRIOR request's stale entry in effectivePkgDirs, which is exactly what a
        // first attempt at this fix, scoped only to "this request's own additions", missed);
        // effectivePkgDirs above is untouched, so actual dependency resolution still sees
        // every synthesized dir it needs.
        var workspaceDepsRoot = AlRunner.Infrastructure.CacheRoots.Resolve("workspace-deps");
        var envKeyPkgDirs = effectivePkgDirs
            .Where(d => !IsUnderDirectory(Path.GetFullPath(d), workspaceDepsRoot))
            .ToList();
        var selectionEnvironmentKey =
            $"{AlRunner.Infrastructure.BcArtifacts.SelectedVersion}|{AlRunner.Infrastructure.BcArtifacts.ServiceTierDir}|"
            + string.Join("|", envKeyPkgDirs
                .Select(d => Path.GetFullPath(d))
                .OrderBy(d => d, StringComparer.Ordinal));

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
                var bundlePkgDirs = AlRunner.Infrastructure.SafeDirectoryScan.Directories(bucketRoot, ".alpackages")
                    .ToList();
                var resolverDirs = bundlePkgDirs.Concat(effectivePkgDirs).Distinct().ToList();
                var resolver = new DependencyResolver(resolverDirs, AlRunner.Infrastructure.CacheRoots.SourceBuiltPackageDirs());
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
            IReadOnlyList<AffectedObjectId>? changedObjects = Array.Empty<AffectedObjectId>();
            string? changeModelFallbackReason = null;
            if (reusedAsm == null && assemblyBytes == null)
            {
                if (alCacheDir != null)
                    AlRunner.Infrastructure.PhaseLog.NoteCacheMiss();
                IReadOnlyList<EmittedSource> sources;
                IReadOnlyList<string> alDiagnostics;
                IReadOnlyList<string> excludedObjects;
                IReadOnlyList<string> excludedObjectDiagnostics;
                var et = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    BcEmitOutput emitOutput;
                    if (beforeRun != null && useIncrementalChangeModel)
                    {
                        var incrementalOutput = emitter.TryEmitIncremental(
                            allPaths, moduleName, bucketRoot,
                            out var incrementalFallbackReason, out var incrementalChangedObjects);
                        if (incrementalOutput != null)
                        {
                            emitOutput = incrementalOutput;
                            changedObjects = incrementalChangedObjects ?? Array.Empty<AffectedObjectId>();
                        }
                        else
                        {
                            changeModelFallbackReason = incrementalFallbackReason;
                            emitOutput = emitter.Emit(allPaths, moduleName, bucketRoot, trackIncrementalBaseline: true);
                            changedObjects = null;
                        }
                    }
                    else
                    {
                        emitOutput = emitter.Emit(allPaths, moduleName, bucketRoot, trackIncrementalBaseline: true);
                    }
                    sources = emitOutput.Sources;
                    alDiagnostics = emitOutput.Diagnostics;
                    excludedObjects = emitOutput.ExcludedObjects;
                    excludedObjectDiagnostics = emitOutput.ExcludedObjectDiagnostics ?? Array.Empty<string>();
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
                    // #2207: read the dedicated ExcludedObjectDiagnostics field, not
                    // `alDiagnostics` — the latter reflects only the final (recovered)
                    // compile round, which by construction has none of its own left once
                    // BcCompiler's retry against the surviving objects has succeeded.
                    compileErrors.Add(
                        $"EMIT-EXCLUDED: {excludedObjects.Count} object(s) dropped from the module — " +
                        $"tests they declare are missing: [{names}]." +
                        (excludedObjectDiagnostics.Count > 0
                            ? " The AL diagnostics that identified them follow."
                            : " Re-run with --verbose for the AL diagnostics that identified them."));
                    foreach (var d in excludedObjectDiagnostics) compileErrors.Add(d);
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
            else if (beforeRun != null)
            {
                // Cache hit (or cross-bundle reuse) means this bundle's AL content is unchanged.
                changedObjects = Array.Empty<AffectedObjectId>();
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
                beforeRun?.Invoke(bundleAbs, moduleName, selectionEnvironmentKey, changedObjects, changeModelFallbackReason);
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
        () => RunAllBundlesForServer(new[] { bundleDir }, null, dapRunStep, cts.Token, false, null));

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
    // Per-bundle memory for affected-only test selection (#2441): previous run's
    // per-test object coverage (object-key strings), tests that were unknown on that
    // run (no mappable coverage or non-pass outcome), and the runtime environment key
    // this coverage was recorded under.
    var affectedCoverageByBundle = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
    var affectedUnknownTestsByBundle = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    var affectedEnvironmentKeyByBundle = new Dictionary<string, string>(StringComparer.Ordinal);

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

    static string ToAffectedObjectKey(AffectedObjectId id)
        => $"{id.Kind}|{(id.Id.HasValue ? "id:" + id.Id.Value : "name:" + id.Name)}";

    static string ToAffectedObjectDisplay(AffectedObjectId id)
        => id.Id.HasValue
            ? $"{id.Kind} {id.Id.Value} {id.Name}"
            : $"{id.Kind} {id.Name}";

    // #2539: the SAME compound key both the changed side (below) and the coverage-attribution
    // side (the collectPerTestForSelection loop) must build for a specific changed procedure —
    // an object-level key plus its scope name, separated so it can never collide with a bare
    // ToAffectedObjectKey result (no AL identifier can contain "::").
    static string ToAffectedScopeKey(string objectKey, string scopeName) => $"{objectKey}::proc:{scopeName}";

    /// <summary>
    /// Builds the set affectedOnly's overlap check compares a test's coveredObjects against —
    /// object-level keys by default (identical to pre-#2539 behaviour), REFINED to a
    /// procedure-level compound key wherever <paramref name="requestWideChangedScopes"/> (the
    /// REQUEST-WIDE <c>PeekChangedScopes</c> union — every bundle in the request, not just
    /// this one, mirroring #2492's own object-level union) confidently narrowed that SAME
    /// object this cycle. An object <paramref name="requestWideChangedScopes"/> explicitly
    /// widened (a null-ScopeName entry) is NEVER narrowed, even if some other entry for the
    /// same object looks narrow — the widen always wins. An object with NO entry in
    /// <paramref name="requestWideChangedScopes"/> at all (its own bundle's peek was
    /// uncertain, or #2539's scope-level peek simply hasn't run for it) falls back to the
    /// plain object-level key — the same safe default as pre-#2539.
    /// </summary>
    static HashSet<string>? BuildAffectedChangedKeys(
        IReadOnlyList<AffectedObjectId>? changedObjects, IReadOnlyList<AffectedScopeId>? requestWideChangedScopes)
    {
        if (changedObjects == null) return null;

        var widenedObjectKeys = new HashSet<string>(StringComparer.Ordinal);
        var scopesByObjectKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (requestWideChangedScopes != null)
        {
            foreach (var sc in requestWideChangedScopes)
            {
                var objKey = ToAffectedObjectKey(sc.Object);
                if (sc.ScopeName == null) { widenedObjectKeys.Add(objKey); continue; }
                if (!scopesByObjectKey.TryGetValue(objKey, out var list))
                    scopesByObjectKey[objKey] = list = new List<string>();
                list.Add(sc.ScopeName);
            }
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in changedObjects)
        {
            var objKey = ToAffectedObjectKey(id);
            if (!widenedObjectKeys.Contains(objKey)
                && scopesByObjectKey.TryGetValue(objKey, out var scopes) && scopes.Count > 0)
            {
                foreach (var s in scopes) keys.Add(ToAffectedScopeKey(objKey, s));
            }
            else
            {
                keys.Add(objKey);
            }
        }
        return keys;
    }

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
        var requestCoverage = req.Coverage == true;
        var requestPerTestCoverage = req.PerTestCoverage == true;
        var affectedOnly = req.AffectedOnly == true;
        // #2441: affected-only selection needs per-test coverage from this run to seed
        // the next run's selection baseline, even when the caller doesn't ask to emit
        // `perTestCoverage` on the wire.
        var collectPerTestForSelection = requestPerTestCoverage || affectedOnly;
        AlRunner.Infrastructure.AlCoverageTracker.Enabled = requestCoverage;
        if (requestCoverage) AlRunner.Infrastructure.AlCoverageTracker.Reset();
        AlRunner.Infrastructure.AlCoverageTracker.PerTestEnabled = collectPerTestForSelection;
        if (collectPerTestForSelection) AlRunner.Infrastructure.AlCoverageTracker.ResetPerTest();

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

            var requestDiscoveredTestsByBundle = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var requestModuleByBundle = new Dictionary<string, string>(StringComparer.Ordinal);
            var requestEnvironmentByBundle = new Dictionary<string, string>(StringComparer.Ordinal);
            var selectionByBundle = new Dictionary<string, ServerSelection>(StringComparer.Ordinal);
            var activeBundleKey = "";
            Dictionary<string, HashSet<string>>? activePreviousCoverage = null;
            HashSet<string>? activePreviousUnknown = null;
            HashSet<string>? activeChangedObjectKeys = null;
            List<string> activeChangedObjectDisplay = new();
            bool activeForcedFull = false;
            string? activeForcedReason = null;

            var runs = RunAllBundlesForServer(req.SourcePaths, req.PackagePaths,
                asm =>
                {
                    var discovered = executor.DiscoverTests(asm);
                    requestDiscoveredTestsByBundle[activeBundleKey] =
                        new HashSet<string>(discovered, StringComparer.Ordinal);

                    HashSet<string>? exactSelection = null;
                    var plannedRan = discovered.Count;
                    var plannedSkipped = 0;
                    if (affectedOnly && !activeForcedFull)
                    {
                        exactSelection = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var testKey in discovered)
                        {
                            if (activePreviousCoverage == null
                                || !activePreviousCoverage.TryGetValue(testKey, out var coveredObjects)
                                || coveredObjects.Count == 0
                                || (activePreviousUnknown?.Contains(testKey) ?? false))
                            {
                                exactSelection.Add(testKey);
                                continue;
                            }
                            if (activeChangedObjectKeys != null && coveredObjects.Overlaps(activeChangedObjectKeys))
                                exactSelection.Add(testKey);
                        }
                        plannedRan = exactSelection.Count;
                        plannedSkipped = Math.Max(0, discovered.Count - plannedRan);
                    }

                    if (affectedOnly)
                    {
                        selectionByBundle[activeBundleKey] = new ServerSelection(
                            "affected",
                            plannedRan,
                            plannedSkipped,
                            activeChangedObjectDisplay,
                            activeForcedFull,
                            activeForcedReason);
                    }

                    var previousExact = executor.ExactTestFilter;
                    executor.ExactTestFilter = exactSelection;
                    try { return executor.Run(asm, OnTestComplete, cts.Token); }
                    finally { executor.ExactTestFilter = previousExact; }
                },
                cts.Token,
                affectedOnly,
                (bundlePath, moduleName, selectionEnvironmentKey, changedObjects, changeModelFallbackReason, ownChangedScopes) =>
                {
                    activeBundleKey = bundlePath;
                    requestModuleByBundle[bundlePath] = moduleName;
                    requestEnvironmentByBundle[bundlePath] = selectionEnvironmentKey;
                    activeChangedObjectKeys = BuildAffectedChangedKeys(changedObjects, ownChangedScopes);
                    activeChangedObjectDisplay = (changedObjects ?? Array.Empty<AffectedObjectId>())
                        .Select(ToAffectedObjectDisplay)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .ToList();
                    activePreviousCoverage = affectedCoverageByBundle.TryGetValue(bundlePath, out var prevCov)
                        ? prevCov : null;
                    activePreviousUnknown = affectedUnknownTestsByBundle.TryGetValue(bundlePath, out var prevUnknown)
                        ? prevUnknown : null;

                    activeForcedFull = false;
                    activeForcedReason = null;
                    if (!affectedOnly) return;

                    if (changeModelFallbackReason != null)
                    {
                        activeForcedFull = true;
                        activeForcedReason = $"change model unavailable: {changeModelFallbackReason}";
                        return;
                    }
                    if (changedObjects == null)
                    {
                        activeForcedFull = true;
                        activeForcedReason = "changed files could not be attributed to AL objects";
                        return;
                    }
                    if (activePreviousCoverage == null || activePreviousUnknown == null)
                    {
                        activeForcedFull = true;
                        activeForcedReason = "no previous per-test coverage baseline for this bundle";
                        return;
                    }
                    if (!affectedEnvironmentKeyByBundle.TryGetValue(bundlePath, out var previousEnv)
                        || !string.Equals(previousEnv, selectionEnvironmentKey, StringComparison.Ordinal))
                    {
                        activeForcedFull = true;
                        activeForcedReason =
                            "coverage baseline environment changed (BC version/artifact/package cache)";
                    }
                });

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
            if (requestCoverage || collectPerTestForSelection)
            {
                var covSourceMap = AlRunner.Infrastructure.AlCoverageSourceMap.Build(
                    req.SourcePaths, relativeTo: null);
                if (requestCoverage)
                    statementTable = AlRunner.Infrastructure.AlCoverageTracker.CollectStatementTable(covSourceMap);
                // #2135: independent of the aggregate table above — see
                // AlCoverageTracker.CollectPerTestStatementTable's doc comment.
                if (collectPerTestForSelection)
                    perTestStatementTable = AlRunner.Infrastructure.AlCoverageTracker.CollectPerTestStatementTable(covSourceMap);
            }

            if (collectPerTestForSelection && perTestStatementTable != null)
            {
                var resultByTest = allTests
                    .Where(t => t.Method != "<ctor>")
                    .GroupBy(t => $"{t.Codeunit}.{t.Method}", StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

                // #2535: file-to-object attribution must be REQUEST-WIDE (every bundle's
                // module unioned into one map), not per-module. `statements` is RUNTIME
                // coverage — it contains every statement the test actually executed,
                // including statements in a DEPENDENCY bundle's files (a real cross-app
                // call, not a hypothetical: see the Pageworks/Pageworks.Test repro in
                // #2535, mirroring #2492's PeekChangedObjects cross-bundle case on the
                // CHANGED-object side — see that method's docstring). A per-module map
                // cannot resolve a path belonging to a sibling bundle's module, so a single
                // ordinary cross-app helper call made the whole test "unmappable" ->
                // permanently "unknown" -> the test reran on EVERY future edit no matter
                // how unrelated, forever. Measured on the real corpus (1012 tests): 897
                // were unmappable this way, only 9 ever got a real coverage entry, and
                // every one of those 9 had a coverage-set size of exactly 1 (their own
                // declaring codeunit only) — so the defect is unmappable cross-bundle
                // statements, not over-broad coverage sets. Built ONCE per request (not
                // per bundle) from every module this request has already resolved.
                var requestWideTrackedObjectsByPath = new Dictionary<string, AffectedObjectId>(StringComparer.Ordinal);
                foreach (var trackedModuleName in requestModuleByBundle.Values.Distinct(StringComparer.Ordinal))
                {
                    var m = emitter.TryGetTrackedObjectsByPath(trackedModuleName);
                    if (m == null) continue;
                    foreach (var kv in m)
                        requestWideTrackedObjectsByPath[kv.Key] = kv.Value;
                }

                foreach (var (bundlePath, discoveredTests) in requestDiscoveredTestsByBundle)
                {
                    if (!requestModuleByBundle.TryGetValue(bundlePath, out var moduleName)) continue;
                    if (!requestEnvironmentByBundle.TryGetValue(bundlePath, out var envKey)) continue;
                    // Still require THIS bundle's own module to have a RAD baseline before
                    // attributing ITS tests at all — same posture as before #2535, only the
                    // per-statement lookup below now consults the request-wide map instead
                    // of just this one module's.
                    if (emitter.TryGetTrackedObjectsByPath(moduleName) == null) continue;

                    var nextCoverage = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                    var nextUnknown = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var testKey in discoveredTests)
                    {
                        if (!resultByTest.TryGetValue(testKey, out var result)
                            || result.Outcome != TestOutcome.Pass
                            || result.TimedOut)
                        {
                            nextUnknown.Add(testKey);
                            continue;
                        }

                        if (!perTestStatementTable.TryGetValue(testKey, out var statements) || statements.Count == 0)
                        {
                            nextUnknown.Add(testKey);
                            continue;
                        }

                        var coveredObjects = new HashSet<string>(StringComparer.Ordinal);
                        var unmappable = false;
                        foreach (var s in statements)
                        {
                            if (!requestWideTrackedObjectsByPath.TryGetValue(s.FilePath, out var identity))
                            {
                                unmappable = true;
                                break;
                            }
                            var objKey = ToAffectedObjectKey(identity);
                            coveredObjects.Add(objKey);
                            // #2539: ALSO record the procedure-level compound key for this
                            // statement's scope, so a test whose coverage never leaves the one
                            // procedure that changed can be selected WITHOUT the plain
                            // object-level key matching every other test that merely touched a
                            // DIFFERENT procedure of the same object. The plain object-level key
                            // stays too — it is what makes a WIDENED (whole-object) changed
                            // entry still match every test that covered the object at all.
                            if (!string.IsNullOrEmpty(s.ScopeName))
                                coveredObjects.Add(ToAffectedScopeKey(objKey, s.ScopeName));
                        }

                        if (unmappable || coveredObjects.Count == 0)
                        {
                            nextUnknown.Add(testKey);
                            continue;
                        }
                        nextCoverage[testKey] = coveredObjects;
                    }

                    affectedCoverageByBundle[bundlePath] = nextCoverage;
                    affectedUnknownTestsByBundle[bundlePath] = nextUnknown;
                    affectedEnvironmentKeyByBundle[bundlePath] = envKey;
                }
            }

            ServerSelection? requestSelection = null;
            if (affectedOnly)
            {
                var changedObjects = selectionByBundle.Values
                    .SelectMany(s => s.ChangedObjects)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList();
                var forcedReasons = selectionByBundle.Values
                    .Where(s => s.ForcedFull && !string.IsNullOrWhiteSpace(s.Reason))
                    .Select(s => s.Reason!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (selectionByBundle.Count == 0 && allCompileErrors.Count > 0)
                    forcedReasons.Add("bundle did not reach test execution (compile/dependency failure)");
                requestSelection = new ServerSelection(
                    "affected",
                    selectionByBundle.Values.Sum(s => s.Ran),
                    selectionByBundle.Values.Sum(s => s.Skipped),
                    changedObjects,
                    forcedReasons.Count > 0,
                    forcedReasons.Count > 0 ? string.Join(" | ", forcedReasons) : null);
            }

            lock (outputLock)
            {
                output.WriteLine(AlRunner.ServerProtocol.Summary(
                    allTests, exitCode, cached, changed,
                    allCompileErrors.Count > 0 ? allCompileErrors : null,
                    cancelled: cancelled, wallSeconds: reqSw.Elapsed.TotalSeconds,
                    selection: requestSelection,
                    statementTable: statementTable,
                    perTestStatementTable: requestPerTestCoverage ? perTestStatementTable : null));
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
        // #2056: loop iteration segmentation, scoped to this request like the flags above.
        AlRunner.Infrastructure.AlIterationTracker.Enabled = req.IterationTracking == true;
        AlRunner.Infrastructure.AlIterationTracker.ConfigureResponse();
        // Syntax facts for captureValues (write sets) and iterationTracking (loops): one parse per request.
        if (req.CaptureValues == true || req.IterationTracking == true)
            AlRunner.Infrastructure.AlScopeSyntaxResolver.Configure(
                AlRunner.Infrastructure.AlMemberSyntaxIndex.Build(sourcePaths),
                AlRunner.Infrastructure.AlCoverageSourceMap.Build(sourcePaths, relativeTo: null));
        else
            AlRunner.Infrastructure.AlScopeSyntaxResolver.Clear();
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
            var runs = RunAllBundlesForServer(sourcePaths, req.PackagePaths, RunFirstCodeunitOnRun, default, false, null);

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

            ServerSelection? selection = null;
            if (req.AffectedOnly == true)
            {
                selection = new ServerSelection(
                    "affected",
                    allTests.Count,
                    0,
                    Array.Empty<string>(),
                    true,
                    "affectedOnly selection is applied to runTests; execute always runs the first OnRun codeunit");
            }

            return AlRunner.ServerProtocol.Execute(allTests, exitCode,
                AlRunner.Infrastructure.AlMessageCapture.Snapshot(),
                AlRunner.Infrastructure.AlIterationTracker.Enabled
                    ? AlRunner.Infrastructure.AlIterationTracker.ResponseMessageTags : null,
                allCompileErrors.Count > 0 ? allCompileErrors : null,
                selection: selection,
                statementTable: statementTable,
                perTestStatementTable: perTestStatementTable);
        }
        finally
        {
            AlRunner.Infrastructure.AlValueCapture.Enabled = false;
            AlRunner.Infrastructure.AlIterationTracker.Enabled = false;
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
        // #2056: same per-bundle bracket as AlValueCapture. Collect() drains the segmenter
        // and advances the response loop-id base, so call it at most once per bundle.
        AlRunner.Infrastructure.AlIterationTracker.Reset();
        AlRunner.Infrastructure.AlIterationCollect? _itc = null;
        AlRunner.Infrastructure.AlIterationCollect Itc() =>
            _itc ??= AlRunner.Infrastructure.AlIterationTracker.Collect();
        IReadOnlyList<AlRunner.Infrastructure.AlLoopRecord>? Loops() =>
            AlRunner.Infrastructure.AlIterationTracker.Enabled ? Itc().Loops : null;
        IReadOnlyDictionary<int, (int Loop, int Iteration)>? CaptureTags() =>
            AlRunner.Infrastructure.AlIterationTracker.Enabled ? Itc().CaptureTags : null;
        IReadOnlyList<string>? UnresolvedScopes() =>
            AlRunner.Infrastructure.AlIterationTracker.Enabled
                ? AlRunner.Infrastructure.AlScopeSyntaxResolver.UnresolvedScopes.ToList()
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
                CapturedValues: Captured(), Loops: Loops(), CaptureTags: CaptureTags(), UnresolvedScopes: UnresolvedScopes()) };
        }
        catch (System.Reflection.TargetInvocationException tex)
        {
            var inner = tex.InnerException ?? tex;
            var alStack = AlRunner.Infrastructure.AlCallStackCapture.GetCaptured(inner);
            return new[] { new TestResult(target.Name, "OnRun", TestOutcome.Fail,
                $"{inner.GetType().Name}: {inner.Message}", inner.ToString(), sw.Elapsed, alStack,
                CapturedValues: Captured(), Loops: Loops(), CaptureTags: CaptureTags(), UnresolvedScopes: UnresolvedScopes()) };
        }
        catch (Exception ex)
        {
            return new[] { new TestResult(target.Name, "OnRun", TestOutcome.Error,
                ex.Message, ex.ToString(), sw.Elapsed, CapturedValues: Captured(), Loops: Loops(), CaptureTags: CaptureTags(), UnresolvedScopes: UnresolvedScopes()) };
        }
        finally
        {
            AlRunner.Infrastructure.AlCoverageTracker.EndTest();
        }
    }
}

