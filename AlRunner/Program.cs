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

if (args[0] == "--version")
{
    var asmVer = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
    Console.WriteLine($"al-runner v{asmVer}");
    return 0;
}

// ── Early validation of the BC selection flags ────────────────────────────────
// Must run BEFORE the R2R re-exec below, because that re-exec rewrites
// `--artifact-path <std-cache>` into `--bc-version` for the child (see
// RewriteArtifactPathArg) — which would otherwise mask the mutual-exclusion error.
if (args.Contains("--bc-version") && args.Contains("--artifact-path"))
{
    Console.Error.WriteLine("--bc-version and --artifact-path are mutually exclusive (pick a version OR an explicit path).");
    return 2;
}

// ── Disable ReadyToRun before any BC type loads ───────────────────────────────
// BC's R2R-precompiled native images bypass our Cecil/runtime hooks: when a hot
// caller (e.g. RecordImplementation.IssueFindRequestAsync) is R2R, the JIT inlines
// the call past the method we rewrote, so the interception silently no-ops and the
// skeleton re-enters BC's native find/calc state machines — which AV because they
// expect service-tier transactional/cache state we don't have (virtual Field table
// find, multi-dataitem query find, FlowField calc, …). DOTNET_ReadyToRun=0 forces
// JIT-from-IL, which is byte-identical in behaviour (corpus is the same 1663/69/0
// fail-set either way) and routes every call through our hooks deterministically.
// The setting is read at CLR init, so it can only be applied by re-execing once
// with it set. Escape hatch: AL_RUNNER_ENABLE_R2R=1 keeps R2R on (e.g. for an
// apples-to-apples perf comparison). Guard prevents an infinite re-exec loop.
if (Environment.GetEnvironmentVariable("DOTNET_ReadyToRun") is null
    && Environment.GetEnvironmentVariable("AL_RUNNER_ENABLE_R2R") != "1"
    && Environment.GetEnvironmentVariable("AL_RUNNER_R2R_REEXECED") != "1")
{
    var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
    };
    var argv0 = RewriteArtifactPathArg(Environment.GetCommandLineArgs());
    // Under the `dotnet` muxer, ProcessPath is dotnet and argv[0] (the managed dll)
    // must be forwarded; under the native apphost it must NOT be (see the Cecil
    // re-exec below for the same rule).
    var underDotnet0 = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath!)
        .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    foreach (var a in (underDotnet0 ? argv0 : argv0.Skip(1)))
        psi.ArgumentList.Add(a);
    psi.Environment["DOTNET_ReadyToRun"] = "0";
    psi.Environment["AL_RUNNER_R2R_REEXECED"] = "1";
    Console.Error.WriteLine("[r2r] re-execing with DOTNET_ReadyToRun=0 (hooks fire deterministically; AL_RUNNER_ENABLE_R2R=1 to disable)");
    // Every invocation without DOTNET_ReadyToRun preset takes this branch, so the
    // "one runner spawn" the tests believe they made is really two OS processes.
    // Keep the outer one's row (its wall clock minus the child's IS the cost of the
    // extra process start, which is a real per-spawn tax worth sizing) but label it
    // so aggregates summing `kind=="process"` do not count the child's time twice.
    AlRunner.Infrastructure.PhaseLog.MarkReexecParent();
    using var r2rChild = System.Diagnostics.Process.Start(psi)!;
    r2rChild.WaitForExit();
    return r2rChild.ExitCode;
}

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
string? alCacheDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".cache", "al-runner", "al-out");
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
// docs/expectations.md). Null = probe the default ./tests/expectations below; only an
// existing directory activates classification, so ordinary runs outside this repo are
// untouched.
string? expectationsDirArg = null;
// `provision` subcommand: `al-runner provision [<project>]` provisions the BC artifacts
// for the project's version and exits (no test run). `--auto-provision` provisions on the
// fly when artifacts are missing, then continues the normal run. Both are the opt-in that
// gates the runner's otherwise-forbidden downloads (no silent auto-download).
bool provisionSubcommand = args.Length > 0 && args[0] == "provision";
bool autoProvision = false;
for (int i = 0; i < args.Length; i++)
{
    if (i == 0 && args[i] == "provision") { continue; } // consumed as subcommand
    if (args[i] == "--auto-provision") { autoProvision = true; continue; }
    if (args[i] == "--bc-version" && i + 1 < args.Length) { bcVersionArg = args[++i]; continue; }
    if (args[i] == "--artifact-path" && i + 1 < args.Length) { artifactPathArg = args[++i]; continue; }
    if (args[i] == "--out" && i + 1 < args.Length) { outPath = args[++i]; printClassification = true; continue; }
    if (args[i] == "--classify") { printClassification = true; continue; }
    if (args[i] == "--output-json") { outputJson = true; continue; }
    if (args[i] == "--output-junit" && i + 1 < args.Length) { outputJunitPath = args[++i]; continue; }
    if (args[i] == "--package-cache" && i + 1 < args.Length) { packageCacheArgs.Add(args[++i]); continue; }
    if (args[i] == "--per-suite") { bundledMode = false; continue; }
    if (args[i] == "--bundled") { bundledMode = true; continue; }
    if (args[i] == "--expectations" && i + 1 < args.Length) { expectationsDirArg = args[++i]; continue; }
    if (args[i] == "--cache" && i + 1 < args.Length) { alCacheDir = args[++i]; continue; }
    if (args[i] == "--no-cache") { alCacheDir = null; continue; }
    if (args[i] == "--print-cache-key") { printCacheKeyOnly = true; continue; }
    if (args[i] == "--watch") { watchMode = true; continue; }
    if (args[i] == "--server") { continue; }  // handled above (serverMode); consume so it isn't "unknown"
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
// ── Test-expectations manifest (issue #1734; docs/expectations.md) ────────────────
// Loaded HERE — at parse time, before BC init — so a malformed manifest aborts the
// invocation (exit 2, the "bad invocation" ladder entry) without running a single
// test. An explicit --expectations dir must exist; without the flag, the documented
// default ./tests/expectations activates only when present (this repo's corpus CI),
// leaving every other invocation exactly as before.
AlRunner.Infrastructure.ExpectationManifest? expectations = null;
{
    var expectationsDir = expectationsDirArg;
    if (expectationsDir != null && !Directory.Exists(expectationsDir))
    {
        Console.Error.WriteLine($"--expectations: directory not found: {expectationsDir}");
        return 2;
    }
    expectationsDir ??= Directory.Exists(Path.Combine(Environment.CurrentDirectory, "tests", "expectations"))
        ? Path.Combine(Environment.CurrentDirectory, "tests", "expectations")
        : null;
    if (expectationsDir != null)
    {
        try
        {
            expectations = AlRunner.Infrastructure.ExpectationManifest.LoadFromDirectory(expectationsDir);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"expectations manifest ({expectationsDir}): {ex.Message}");
            return 2;
        }
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
if (bcVersionArg == null && artifactPathArg == null)
{
    // The BUILT version (4-part, baked in at compile time) — not Ncl.dll's assembly
    // version, whose minor is always 0. Falls back to the Ncl major if the attribute is
    // missing (e.g. an older build), which restores the previous major-only behaviour.
    var engineVersion = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion()
        ?? AlRunner.Infrastructure.BcArtifacts.EngineVersion(AppContext.BaseDirectory);
    var engineMajor = engineVersion?.Major;
    if (engineVersion != null && engineMajor != null)
    {
        // Prefer the engine's OWN major.minor. Latest-in-major used to win here, which
        // silently selected a minor the engine was not built for — measured at -45 passing
        // / +42 failing / +3 errors on Pageworks. See BcArtifacts.DefaultVersionPrefix.
        bcVersionArg = AlRunner.Infrastructure.BcArtifacts.DefaultVersionPrefix(
            engineVersion, AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir);

        var engineMajorMinor = $"{engineVersion.Major}.{engineVersion.Minor}";
        if (bcVersionArg == engineVersion.ToString())
            Console.Error.WriteLine($"[bc] no --bc-version given — selecting BC {engineVersion}, the exact " +
                $"build this binary was compiled against. Override with --bc-version.");
        else if (bcVersionArg == engineMajorMinor)
            // Degraded but usually survivable: right minor, different build. The CodeAnalysis
            // assembly version can still differ between builds of one minor, which fails loud
            // at startup rather than silently — see BcArtifacts.DefaultVersionPrefix.
            Console.Error.WriteLine($"[bc] warning: no cached BC {engineVersion} — selecting the latest " +
                $"{engineMajorMinor}.x instead. Build-level skew within a minor can still fail to load " +
                $"Microsoft.Dynamics.Nav.CodeAnalysis. Fix with: al-runner provision --bc-version {engineVersion}");
        else
            Console.Error.WriteLine($"[bc] warning: no cached BC {engineMajorMinor}.x — this binary's engine was " +
                $"built for {engineVersion}, so a different minor is a KNOWN-DEGRADED configuration " +
                $"(measured: dozens of extra failures from engine/artifact minor skew). Falling back to the " +
                $"latest cached {engineMajor}.x. Fix with: al-runner provision --bc-version {engineMajorMinor}");

        var projMajor = TryDeriveBcMajorFromProject(bundles);
        if (projMajor != null && projMajor != engineMajor.Value.ToString())
            Console.Error.WriteLine($"[bc] warning: project app.json targets BC major {projMajor} but this " +
                $"runner build supports major {engineMajor} (cross-major needs a matching runner build).");
    }
}
// ── Provisioning (opt-in): `provision` subcommand or --auto-provision. Resolves the
// target version, downloads the engine service-tier closure if it's missing/incomplete,
// then (subcommand) exits or (flag) continues the run against what was provisioned. This
// is the ONLY path that downloads — a normal run never does.
if (provisionSubcommand || autoProvision)
{
    var prc = RunProvisioning(bcVersionArg, artifactPathArg, bundles, out var provisionedVersion);
    if (provisionSubcommand)
        return prc; // the subcommand always exits after provisioning, never runs tests
    if (prc == 0 && provisionedVersion != null)
        bcVersionArg = provisionedVersion; // run against the version we just ensured
    // On failure with --auto-provision we fall through; SelectVersion below emits the
    // loud, path-naming error (and the detailed ProvisioningCheck report if partial).
}
try
{
    AlRunner.Infrastructure.BcArtifacts.SelectVersion(bcVersionArg, artifactPathArg);
    // Consistency guard: the engine DLLs baked into bin/ are built for a fixed BC
    // major.minor; if the selected version's major.minor differs, dependency symbols
    // and the engine can disagree — fail loud rather than crash deep in BC. Patch-level
    // skew (28.1.x build vs 28.1.y cache) is tolerated.
    AlRunner.Infrastructure.BcArtifacts.VerifyEngineConsistency(AppContext.BaseDirectory);
    Console.Error.WriteLine($"[bc] selected BC {AlRunner.Infrastructure.BcArtifacts.SelectedVersion} " +
        $"({AlRunner.Infrastructure.BcArtifacts.ServiceTierDir})");
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"BC version selection failed: {ex.Message}");
    return 2;
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
Console.WriteLine(serverMode
    ? "al-runner — server mode (JSON-RPC over stdin/stdout)"
    : watchMode
        ? $"al-runner — watch mode, {bundles.Count} bundle(s) (Ctrl+C to quit)"
        : $"al-runner — running {bundles.Count} bundle(s)");

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
        Console.Error.WriteLine("[Cecil] Fresh rewrite done — re-execing for a clean Ncl load");
        // This process waits for the child below, so its wall clock CONTAINS the
        // child's entire run. Re-label the row so aggregates that sum `kind=="process"`
        // do not double-count it.
        AlRunner.Infrastructure.PhaseLog.MarkReexecParent();
        using var child = System.Diagnostics.Process.Start(psi)!;
        child.WaitForExit();
        return child.ExitCode;
    }
}

var packageCacheDirs = packageCacheArgs.Count > 0
    ? ExpandPackageCacheDirs(packageCacheArgs).ToList()
    : DefaultPackageCacheDirs().ToList();
Console.WriteLine($"  package caches: {packageCacheDirs.Count} dir(s)");
AlRunner.Infrastructure.PhaseLog.SetPackageCacheDirs(packageCacheDirs.Count);
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
List<string> PlatformCheckDirs() => packageCacheDirs.Concat(bundleAlpackagesDirs).Distinct().ToList();

// Platform-app R2R check: scan the package cache for known Microsoft platform runtime apps
// (System Application, Base Application, Business Foundation). If any are present as
// symbol-only (non-R2R) packages, the runner CANNOT execute their codeunits at runtime —
// the EMIT-ZERO crash is a provisioning gap, not a user-code error. Fail loud here before
// any bundle compile, naming the fix, instead of deep inside the dep-load pipeline.
// (--auto-provision downloads the R2R apps and clears the check.)
if (!provisionSubcommand && (packageCacheDirs.Count > 0 || bundleAlpackagesDirs.Count > 0))
{
    var version = AlRunner.Infrastructure.BcArtifacts.SelectedVersion.ToString();
    var platformReport = AlRunner.Infrastructure.ProvisioningCheck.CheckPlatformApps(
        version, PlatformCheckDirs());
    // Test-toolkit apps (Business Foundation Test Libraries, Application Test Library, …)
    // are a SEPARATE artifact set from the w1 platform apps (they live under the
    // `platform` artifact, not `w1`) — a cache can have complete R2R platform apps and
    // still be missing the whole test toolkit, which fails compiling any test bundle.
    var toolkitPresent = AlRunner.Infrastructure.ProvisioningCheck.TestToolkitPresent(PlatformCheckDirs());

    if (!platformReport.Ok && !autoProvision)
    {
        Console.Error.WriteLine(platformReport.ToDetailedMessage());
        return 2;
    }

    if (autoProvision && (!platformReport.Ok || !toolkitPresent))
    {
        // Resolve ONE target full version and reuse it for both artifact sets — avoids
        // resolving two different minors for what should be the same provisioning pass.
        // Priority: (a) the missing/symbol-only platform apps carry their OWN version
        // (the engine is version-agnostic w.r.t. the R2R apps it dispatches to at
        // runtime, so this can be a different minor than the engine's SelectedVersion);
        // (b) else derive from whatever platform app IS already present in the cache
        // (only the toolkit is missing); (c) else fall back to the engine's own version.
        var mm = !platformReport.Ok
            ? AlRunner.Infrastructure.ProvisioningCheck.DeriveProvisionMajorMinor(platformReport, version)
            : AlRunner.Infrastructure.ProvisioningCheck.DerivePresentPlatformMajorMinor(PlatformCheckDirs(), version);
        var full = AlRunner.Provisioning.ArtifactDownloader.ResolveVersion(
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

        if (!platformReport.Ok)
        {
            Console.Error.WriteLine("[provision] platform R2R apps missing — downloading...");
            var rc = AlRunner.Provisioning.ArtifactDownloader.PlatformApps(
                full, platformAppsOut, m => Console.Error.WriteLine($"[provision] {m}"));
            if (rc != 0)
            {
                Console.Error.WriteLine("[provision] platform-apps download failed; cannot continue.");
                return 2;
            }
            // Make the downloaded apps visible to resolution: add the artifact-cache dir as
            // an additional search root rather than copying its contents into the project.
            if (!packageCacheDirs.Contains(platformAppsOut))
                packageCacheDirs.Add(platformAppsOut);
            // Re-check: never silently continue on a partial/failed provision.
            platformReport = AlRunner.Infrastructure.ProvisioningCheck.CheckPlatformApps(
                version, PlatformCheckDirs());
            if (!platformReport.Ok)
            {
                var stillMissing = string.Join(", ", platformReport.Issues.Select(i => i.Name));
                Console.Error.WriteLine($"[provision] platform apps still symbol-only after download: {stillMissing}");
                return 2;
            }
        }

        if (!toolkitPresent)
        {
            Console.Error.WriteLine("[provision] test-toolkit apps missing — downloading...");
            var rc = AlRunner.Provisioning.ArtifactDownloader.TestApps(
                full, testAppsOut, m => Console.Error.WriteLine($"[provision] {m}"));
            if (rc != 0)
            {
                Console.Error.WriteLine("[provision] test-toolkit download failed; cannot continue.");
                return 2;
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

// One-time runtime setup. Must happen BEFORE any BC type is touched.
// Install the assembly Resolving handler FIRST so patch reflection or generic
// instantiation in BC code can resolve transitively-referenced service-tier DLLs
// (Microsoft.Dynamics.Nav.Core, .AL.Common, .Apps, .TableProxyBuilder, etc. — 19
// of the 24 BC DLLs Ncl.dll references aren't project-referenced).
DependencyLoader.EnsureResolverInstalled_Public();
if (extraPreprocessorSymbols.Count > 0)
    BcCompiler.SetExtraPreprocessorSymbols(extraPreprocessorSymbols.Distinct().ToList());
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
if (bundles.Count > 1)
    packageCacheDirs = RunLayeredPrePass(bundles, packageCacheDirs, layeredWorkspaceDirs);
// Discover + compile sibling source-only dependency apps. Some apps declare a
// dependency that ships ONLY as AL source in a sibling directory (not a compiled
// .app in any cache) — e.g. the corpus internalsVisibleTo fixture next to the
// main test app. Inert when no declared dep matches a sibling source app.
packageCacheDirs = BuildSiblingSourceDeps(bundles, packageCacheDirs, layeredWorkspaceDirs);

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
    rec.Write(WatchDashboard.Build(results, watchBundleName, status, ts, dur));
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
results.Clear();
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
            cacheKey = ComputeAlCacheKey(allPaths, moduleName, ordered: GetOrderedDepIds(bucketRoot, packageCacheDirs, bundleAbs));
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
            var emitTask = Task.Run(() => emitter.Emit(allPaths, moduleName));
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
                if (declaredObjects.Count > sources.Count)
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
                tests = executor.Run(asm);
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
                var emitOutput = emitter.Emit(suitePaths, $"V2_{Path.GetFileName(suite)}");
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
                tests = executor.Run(asm);
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
        bundleEmit, bundleComp, bundleRun));
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
    var (signal, watchers) = armed.Value;
    bool changed = false;
    // Console.KeyAvailable throws InvalidOperationException when stdin is redirected
    // (a pipe/file rather than a real terminal). We still want the dashboard + file
    // watching in that case (output is a TTY), just without scroll keys. Probe once.
    bool keyboard = !Console.IsInputRedirected;
    try
    {
        while (true)
        {
            if (signal.IsSet) { System.Threading.Thread.Sleep(250); changed = true; break; }

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
} // end while(true) watch loop

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
        : (failed + errored > 0 ? 1 : 0);        // at least one test failed
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

// Exit non-zero if anything failed — the default since the v2 cut, matching main/v1.
// --no-strict-exit restores the old always-0 behaviour for JSON-only consumers.
return strictExitCode ? computedExitCode : 0;

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

            lock (outputLock)
            {
                output.WriteLine(AlRunner.ServerProtocol.Summary(
                    allTests, exitCode, cached, changed,
                    allCompileErrors.Count > 0 ? allCompileErrors : null,
                    cancelled: cancelled));
                output.Flush();
            }
        }
        finally
        {
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
    // (run-mode), aggregating the results. v1 also accepted an inline `code`
    // string; v2 has no inline-AL compile path yet, so that case fails LOUD
    // (never a silent fake) per .claude/rules/loud-failures.md.
    string HandleServerExecute(AlRunner.ServerRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.Code))
            return AlRunner.ServerProtocol.Error(
                "execute: inline AL 'code' is not yet supported in v2 — pass 'sourcePaths' "
                + "to run the bundle's OnRun codeunit. See docs/server-mode.md.");
        if (req.CaptureValues == true)
            return AlRunner.ServerProtocol.Error(
                "execute: 'captureValues' is not yet supported in v2. See docs/server-mode.md.");
        if (req.SourcePaths == null || req.SourcePaths.Length == 0)
            return AlRunner.ServerProtocol.Error("sourcePaths is required");
        foreach (var p in req.SourcePaths)
            if (!Directory.Exists(p))
                return AlRunner.ServerProtocol.Error($"bundle directory not found: {p}");

        var isolationError = ApplyRequestIsolation(req);
        if (isolationError != null) return isolationError;

        var runs = RunAllBundlesForServer(req.SourcePaths, req.PackagePaths, RunFirstCodeunitOnRun);

        var allTests = runs.SelectMany(r => r.Tests).ToList();
        var allCompileErrors = runs.SelectMany(r => r.CompileErrors ?? Array.Empty<CompilationErrorGroup>()).ToList();
        var exitCode = runs.Count > 0 ? runs.Max(r => r.ExitCode) : 0;

        var combinedHashes = new Dictionary<string, string>();
        foreach (var r in runs)
            foreach (var kv in r.FileHashes)
                combinedHashes[kv.Key] = kv.Value;
        lastFileHashes = combinedHashes;

        return AlRunner.ServerProtocol.Execute(allTests, exitCode, null,
            allCompileErrors.Count > 0 ? allCompileErrors : null);
    }

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
        // Drop the previous REQUEST's bundle-derived caches so a reloaded same-named
        // bundle resolves the freshly-emitted Record/Codeunit types and starts with
        // empty in-memory tables. Once per request, NOT per bundle: the CLI bucket
        // loop never resets between bundles, so AddSourceDir accumulates across an
        // app + test-app pair — resetting per bundle wiped the app bundle's parsed
        // table schemas before the test bundle ran, and every Record op on an
        // app-defined table died with "no NCLMetaTable for table N (AL source not
        // parsed)" while the identical CLI invocation passed.
        BcRuntime.ResetForNewBundleReload();

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
        foreach (var bundleDir in sourcePaths)
        {
            if (cancellationToken.IsCancellationRequested) break;
            results.Add(RunBundleForServer(bundleDir, requestPackagePaths, runStep));
        }
        return results;
    }

    // Compile + run one bundle, resetting bundle-derived caches first so an edited
    // same-identity bundle is picked up (server reload contract). Mirrors the
    // bundled-mode path of the normal run loop for a single bundle. The run step
    // (executor.Run for runTests, OnRun dispatch for execute) is supplied by the caller.
    ServerRunResult RunBundleForServer(string bundleDir, string[]? requestPackagePaths,
        Func<Assembly, IReadOnlyList<TestResult>> runStep)
    {
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
                BcCompiler.SetResolvedDeps(ordered, resolverDirs);
                var loaded = depLoader.LoadAll(ordered, bucketRoot);
                // New bundle in the server session: replace (not inherit) the
                // install-trigger registrations, then register this bundle's deps.
                AlRunner.InstallTriggerRunner.ResetForNewBundle();
                AlRunner.InstallTriggerRunner.SetDependencyAssemblies(loaded);
                BcCompiler.SetResolvedDeps(ordered, resolverDirs);
                foreach (var (_, appPath) in ordered)
                    AlRunner.Patches.RecordPatches.AddBcAppPath(appPath);
                AlRunner.Patches.RecordPatches.RegisterBundleSymbolApps(bucketRoot);
                SetBundleInfoFromAppJson(appJsonPath);
                var bundleId = AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(appJsonPath);
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

        // AL-output cache: HIT short-circuits Emit+Compile, like the normal loop.
        byte[]? assemblyBytes = null;
        bool cached = false;
        string? cacheKey = null, cachePath = null, sidecarPath = null, querySidecarPath = null;
        // See AlCacheSidecars: a query bundle without its query-symbols sidecar must MISS.
        bool bundleDeclaresQuery = BcCompiler.BundleDeclaresQuery(allPaths);
        if (alCacheDir != null)
        {
            cacheKey = ComputeAlCacheKey(allPaths, moduleName,
                ordered: GetOrderedDepIds(bucketRoot, effectivePkgDirs));
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
        if (assemblyBytes == null)
        {
            IReadOnlyList<EmittedSource> sources;
            IReadOnlyList<string> alDiagnostics;
            IReadOnlyList<string> excludedObjects;
            try
            {
                var emitOutput = emitter.Emit(allPaths, moduleName);
                sources = emitOutput.Sources;
                alDiagnostics = emitOutput.Diagnostics;
                excludedObjects = emitOutput.ExcludedObjects;
            }
            catch (Exception ex)
            {
                return ServerRunResult.Failure(3, moduleName, $"EMIT-FAIL: {ex.Message.Split('\n')[0]}", fileHashes);
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
            var compile = assembler.Compile(moduleName, sources);
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

        if (assemblyBytes == null)
            return ServerRunResult.Failure(2, moduleName, "no assembly produced", fileHashes);

        IReadOnlyList<TestResult> tests;
        try
        {
            var asm = Assembly.Load(assemblyBytes);
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
        }

        int exit = 0;
        if (tests.Any(t => t.Outcome == TestOutcome.Fail || t.Outcome == TestOutcome.Error)) exit = 1;
        return new ServerRunResult(tests, exit, cached, null, fileHashes);
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
            return new[] { new TestResult(target.Name, "OnRun", TestOutcome.Pass, null, null, sw.Elapsed) };
        }
        catch (System.Reflection.TargetInvocationException tex)
        {
            var inner = tex.InnerException ?? tex;
            var alStack = AlRunner.Infrastructure.AlCallStackCapture.GetCaptured(inner);
            return new[] { new TestResult(target.Name, "OnRun", TestOutcome.Fail,
                $"{inner.GetType().Name}: {inner.Message}", inner.ToString(), sw.Elapsed, alStack) };
        }
        catch (Exception ex)
        {
            return new[] { new TestResult(target.Name, "OnRun", TestOutcome.Error,
                ex.Message, ex.ToString(), sw.Elapsed) };
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
    w.WriteLine("  BC artifacts are never downloaded silently. Obtain them explicitly:");
    w.WriteLine("    al-runner provision <bundle-dir>     # provision for that project's version, then exit");
    w.WriteLine("    al-runner --auto-provision <dirs>    # provision on demand, then continue the run");
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
    w.WriteLine("      Action: the runner never auto-downloads. Run `al-runner provision`, or pass");
    w.WriteLine("      --auto-provision, or point --artifact-path at an existing artifact root.");
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

    w.WriteLine("FURTHER READING");
    w.WriteLine("  --help                       full flag reference");
    w.WriteLine("  docs/limitations.md          the real architectural limits");
    w.WriteLine("  docs/scope.md                in-scope vs out-of-scope-by-design surfaces");
    w.WriteLine("  docs/server-mode.md          the --server JSON-RPC protocol");
    w.WriteLine("  docs/subsystems.md           subsystem map");
}

static void PrintHelp(TextWriter w)
{
    w.WriteLine("al-runner — run Business Central AL unit tests in-process.");
    w.WriteLine();
    w.WriteLine("USAGE");
    w.WriteLine("  al-runner [OPTIONS] <bundle-dir>...");
    w.WriteLine("  al-runner provision [<bundle-dir>]");
    w.WriteLine("  al-runner --server [--package-cache PATH ...] [--cache DIR]");
    w.WriteLine("  al-runner --precompile <input.app> --out <output.dll> [--package-cache PATH ...]");
    w.WriteLine("  al-runner --emit-app <bundleDir> <outPath> [--package-cache PATH ...]");
    w.WriteLine("  al-runner --guide      (operating manual for automated callers)");
    w.WriteLine("  al-runner --version");
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
    w.WriteLine();
    w.WriteLine("SELECTION");
    w.WriteLine("  --test PATTERN, --filter PATTERN");
    w.WriteLine("                          Run only tests whose qualified name (CodeunitNNNN.Method)");
    w.WriteLine("                          contains PATTERN (case-insensitive). Leading/trailing '*'");
    w.WriteLine("                          is accepted as a shell-friendly no-op.");
    w.WriteLine("  --isolation MODE, --test-isolation MODE");
    w.WriteLine("                          Test isolation:");
    w.WriteLine("                            codeunit  state shared inside a codeunit, reset between");
    w.WriteLine("                                      (default; BC's \"Isol. Codeunit\" 130450)");
    w.WriteLine("                            test      every [Test] gets a fresh state (BC's 130452)");
    w.WriteLine("                            disabled  no resets at all (BC's 130453)");
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
    w.WriteLine("                          highest version with that prefix. The runner never");
    w.WriteLine("                          auto-downloads; an unavailable version fails loud.");
    w.WriteLine("                          Mutually exclusive with --artifact-path.");
    w.WriteLine("  --artifact-path DIR     Use an explicit BC artifact root (the dir containing");
    w.WriteLine("                          platform/ + w1/), bypassing the cache scan. Its version");
    w.WriteLine("                          is read from the dir name or the contained Ncl.dll.");
    w.WriteLine("                          Mutually exclusive with --bc-version.");
    w.WriteLine("  --auto-provision        Download the BC artifacts for the project's version if");
    w.WriteLine("                          they are missing, then continue the run. The runner never");
    w.WriteLine("                          downloads without this flag (or the `provision`");
    w.WriteLine("                          subcommand) — a missing artifact otherwise fails loud.");
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
    w.WriteLine("                          extension. Commands: runTests, shutdown (execute: TODO).");
    w.WriteLine("                          See docs/server-mode.md. Mutually exclusive with --watch.");
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
    w.WriteLine();
    w.WriteLine("SUBCOMMANDS");
    w.WriteLine("  provision [<bundle-dir>] Download and install the BC artifacts matching the");
    w.WriteLine("                          project's version, then exit without running tests.");
    w.WriteLine("                          This is the supported way to obtain artifacts on a");
    w.WriteLine("                          fresh machine.");
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
    w.WriteLine("  (debug adapter)         v1's DAP debug server (DapServer.cs). Needs an AL→C#");
    w.WriteLine("                          source map the compile pipeline does not currently");
    w.WriteLine("                          expose; tracked as a separate workstream. Distinct from");
    w.WriteLine("                          the JSON-RPC daemon in EXECUTION above, which IS supported.");
    w.WriteLine("  --coverage              Cobertura coverage output. There is currently no rewrite");
    w.WriteLine("                          pass over emitted AL output to instrument; a Cecil-based");
    w.WriteLine("                          implementation is feasible (2-4 d) but not yet built.");
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

    var packageCacheDirs = caches.Count > 0 ? ExpandPackageCacheDirs(caches).ToList() : DefaultPackageCacheDirs().ToList();

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
        emitOut = compiler.Emit(new[] { tempDir }, manifest.Name);
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
    var workspaceRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "al-runner", "workspace-deps");

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
                var implResolver = new DependencyResolver(implSymbolDirs);
                var implDeps = implResolver.Resolve(implId.Dependencies);
                BcCompiler.SetResolvedDeps(implDeps, implSymbolDirs);
                using (BcCompiler.ScopeCurrentAppIdentity(implId.AppId, implId.Publisher, implId.Version))
                    new BcCompiler().EmitDepSymbols(new[] { implPath }, implId.Name, implId.AppId, implId.Publisher, implId.Version, symbolsPath);
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
    var toBuild = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var dep in neededDeps)
    {
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
                toBuild.Add(dir);
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
    var workspaceRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "al-runner", "workspace-deps");
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
        // by ParseAllSources during Register (not immediately). Compile-time visibility is
        // handled separately by the symbols.json emit below.
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
        var depResolver = new DependencyResolver(resolveDirs);
        var resolvedDepDeps = depResolver.Resolve(sid.Dependencies);
        BcCompiler.SetResolvedDeps(resolvedDepDeps, resolveDirs);
        var symBase = Path.Combine(wsDir, $"{Sanitize(sid.Publisher)}_{Sanitize(sid.Name)}_{sid.Version.ToString().Replace('.', '_')}");
        var symbolsPath = symBase + ".symbols.json";
        var depsPath = symBase + ".symbols.deps.json";
        var hadSymbols = File.Exists(symbolsPath) && File.Exists(depsPath);
        if (!hadSymbols)
        {
            try
            {
                using (BcCompiler.ScopeCurrentAppIdentity(sid.AppId, sid.Publisher, sid.Version))
                    new BcCompiler().EmitDepSymbols(new[] { dir }, sid.Name, sid.AppId, sid.Publisher, sid.Version, symbolsPath);
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
                    symbolsPath);
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

// NOTE: AlRunner/WatchSource.cs has its own private copy of this exact walk-up
// (it can't call this one — top-level-statement local functions aren't
// reachable from another file/class). The two must stay in sync; see #1824
// for the follow-up to de-duplicate them.
static string? FindBucketRoot(string bundlePath)
{
    var cur = Directory.Exists(bundlePath) ? bundlePath : Path.GetDirectoryName(bundlePath);
    while (!string.IsNullOrEmpty(cur))
    {
        if (File.Exists(Path.Combine(cur, "app.json"))) return cur;
        var parent = Path.GetDirectoryName(cur);
        if (parent == cur) return null;
        cur = parent;
    }
    return null;
}

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

// Provisioning driver for the `provision` subcommand / --auto-provision. Resolves the
// target BC version (explicit --bc-version, else the engine major, else the project major),
// prefers an already-cached matching version (completing a partial one) and otherwise
// resolves the latest full version from the CDN, then downloads the engine service-tier
// closure if it is missing/incomplete. Returns 0 on success (already-complete counts) and
// sets provisionedVersion to the full version to run against; 1 on failure. This is opt-in
// — the only path in the runner that downloads.
static int RunProvisioning(string? bcVersionArg, string? artifactPathArg,
    List<string> bundles, out string? provisionedVersion)
{
    provisionedVersion = null;

    if (artifactPathArg != null)
    {
        Console.Error.WriteLine("[provision] --artifact-path points at an explicit dir; nothing to provision.");
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
        Console.Error.WriteLine($"[provision] BC {full} engine artifacts already complete at {serviceTierDir}.");
    else if (!AlRunner.Infrastructure.ProvisioningCheck.AutoProvision(full, serviceTierDir))
        return 1;

    EnsurePlatformAppsProvisioned(full, bundles);
    EnsureTestToolkitProvisioned(full);
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
    var bundleAlpackagesDirs = AlRunner.Infrastructure.ProvisioningCheck.CollectBundleAlpackagesDirs(bundles);
    if (bundleAlpackagesDirs.Count == 0)
        return;

    var platformReport = AlRunner.Infrastructure.ProvisioningCheck.CheckPlatformApps(
        engineVersion, bundleAlpackagesDirs);
    if (platformReport.Ok)
    {
        Console.Error.WriteLine("[provision] platform R2R apps already present for the target bundle(s).");
        return;
    }

    var mm = AlRunner.Infrastructure.ProvisioningCheck.DeriveProvisionMajorMinor(platformReport, engineVersion);
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
    IReadOnlyList<string> ordered)
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
    WriteLine("schema:v10");

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
