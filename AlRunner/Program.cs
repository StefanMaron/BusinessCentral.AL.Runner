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
using AlRunnerV2;

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

if (args.Length == 0 || args[0] == "--help" || args[0] == "-h" || args[0] == "help")
{
    PrintHelp(args.Length == 0 ? Console.Error : Console.Out);
    return args.Length == 0 ? 2 : 0;
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
AlRunnerV2.Log.Install();

// Per-test output mode. Default (V1 parity): print PASS and FAIL lines.
// Inverted by --failures-only or AL_RUNNER_FAILURES_ONLY=1 for large-corpus runs
// where the PASS list is too noisy. --show-pass retained as a no-op for back-compat.
bool showPass = Environment.GetEnvironmentVariable("AL_RUNNER_FAILURES_ONLY") != "1";

// AL_RUNNER_TRACE_NRE=1 — log every first-chance NullReferenceException with its
// full stack trace before it gets swallowed by AL `asserterror` / test machinery.
if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_NRE") == "1")
{
    AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
    {
        if (e.Exception is NullReferenceException or ArgumentNullException)
        {
            Console.Error.WriteLine($"[FCE-NRE] {e.Exception}");
        }
    };
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
// Test isolation mode — default matches BC's "Test Runner - Isol. Codeunit" (130450).
var isolation = AlRunnerV2.TestIsolation.Codeunit;
// --strict: exit with non-zero code if any test fails or a bucket fails to compile/execute.
// Default (no --strict): exit 0 regardless of test failures so callers can parse JSON output.
bool strictExitCode = false;
// --test PATTERN: substring filter applied to "Codeunit.Method" — case-insensitive.
string? testFilter = null;
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
    if (args[i] == "--package-cache" && i + 1 < args.Length) { packageCacheArgs.Add(args[++i]); continue; }
    if (args[i] == "--per-suite") { bundledMode = false; continue; }
    if (args[i] == "--bundled") { bundledMode = true; continue; }
    if (args[i] == "--cache" && i + 1 < args.Length) { alCacheDir = args[++i]; continue; }
    if (args[i] == "--no-cache") { alCacheDir = null; continue; }
    if (args[i] == "--watch") { watchMode = true; continue; }
    if (args[i] == "--server") { continue; }  // handled above (serverMode); consume so it isn't "unknown"
    if (args[i] == "--verbose") { AlRunnerV2.Log.Verbose = true; continue; }
    if (args[i] == "--show-pass") { showPass = true; continue; }   // no-op (default in v2); kept for v1 back-compat
    if (args[i] == "--failures-only" || args[i] == "--quiet") { showPass = false; continue; }
    if (args[i] == "--strict") { strictExitCode = true; continue; }
    if ((args[i] == "--test" || args[i] == "--filter") && i + 1 < args.Length) { testFilter = args[++i]; continue; }
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
        var mode = args[++i].ToLowerInvariant();
        isolation = mode switch
        {
            "codeunit" or "method" => AlRunnerV2.TestIsolation.Codeunit,
            "test"                 => AlRunnerV2.TestIsolation.Test,
            "disabled"             => AlRunnerV2.TestIsolation.Disabled,
            _ => throw new ArgumentException(
                $"--isolation: unknown mode '{mode}' (codeunit|test|disabled; 'method' accepted as v1 alias for codeunit)")
        };
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
        var translated = AlRunnerV2.Infrastructure.BcArtifacts.TryTranslateArtifactPathToVersion(artifactPathArg);
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
    var engineMajor = AlRunnerV2.Infrastructure.BcArtifacts.EngineMajor(AppContext.BaseDirectory);
    if (engineMajor != null)
    {
        bcVersionArg = engineMajor.Value.ToString();
        var projMajor = TryDeriveBcMajorFromProject(bundles);
        if (projMajor != null && projMajor != engineMajor.Value.ToString())
            Console.Error.WriteLine($"[bc] warning: project app.json targets BC major {projMajor} but this " +
                $"runner build supports major {engineMajor} (cross-major needs a matching runner build). " +
                $"Selecting latest cached {engineMajor}.x — override with --bc-version if a {projMajor}.x " +
                $"runner is available.");
        else
            Console.Error.WriteLine($"[bc] no --bc-version given — selecting latest cached BC {engineMajor}.x " +
                $"(runner engine major). Override with --bc-version.");
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
    AlRunnerV2.Infrastructure.BcArtifacts.SelectVersion(bcVersionArg, artifactPathArg);
    // Consistency guard: the engine DLLs baked into bin/ are built for a fixed BC
    // major.minor; if the selected version's major.minor differs, dependency symbols
    // and the engine can disagree — fail loud rather than crash deep in BC. Patch-level
    // skew (28.1.x build vs 28.1.y cache) is tolerated.
    AlRunnerV2.Infrastructure.BcArtifacts.VerifyEngineConsistency(AppContext.BaseDirectory);
    Console.Error.WriteLine($"[bc] selected BC {AlRunnerV2.Infrastructure.BcArtifacts.SelectedVersion} " +
        $"({AlRunnerV2.Infrastructure.BcArtifacts.ServiceTierDir})");
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
    var provReport = AlRunnerV2.Infrastructure.ProvisioningCheck.Check(
        AlRunnerV2.Infrastructure.BcArtifacts.SelectedVersion.ToString(),
        AlRunnerV2.Infrastructure.BcArtifacts.ServiceTierDir);
    if (!provReport.Ok)
    {
        Console.Error.WriteLine(provReport.ToDetailedMessage(bundles.Count > 0 ? bundles[0] : null));
        return 2;
    }
}

if (alCacheDir != null) Directory.CreateDirectory(alCacheDir);
Console.WriteLine(serverMode
    ? "al-runner v2 — server mode (JSON-RPC over stdin/stdout)"
    : watchMode
        ? $"al-runner v2 — watch mode, {bundles.Count} bundle(s) (Ctrl+C to quit)"
        : $"al-runner v2 — running {bundles.Count} bundle(s)");

// Cecil-rewrite Ncl.dll IN-PLACE on the bin path BEFORE CoreCLR's TPA probe
// resolves it. Must run BEFORE any reference to BcRuntime (whose field metadata
// triggers Ncl load on class init). Allowed surface per
// .claude/rules/precompiled-dll-respect.md — Ncl is runtime engine, not BaseApp.
{
    var srcDir = AlRunnerV2.Infrastructure.BcArtifacts.ServiceTierDir;
    var binNcl = Path.Combine(AppContext.BaseDirectory, "Microsoft.Dynamics.Nav.Ncl.dll");
    var didFreshRewrite = AlRunnerV2.Infrastructure.NclCecilRewrite.RewriteInPlace(srcDir, binNcl);

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
        using var child = System.Diagnostics.Process.Start(psi)!;
        child.WaitForExit();
        return child.ExitCode;
    }
}

var packageCacheDirs = packageCacheArgs.Count > 0
    ? ExpandPackageCacheDirs(packageCacheArgs).ToList()
    : DefaultPackageCacheDirs().ToList();
Console.WriteLine($"  package caches: {packageCacheDirs.Count} dir(s)");

// Platform-app R2R check: scan the package cache for known Microsoft platform runtime apps
// (System Application, Base Application, Business Foundation). If any are present as
// symbol-only (non-R2R) packages, the runner CANNOT execute their codeunits at runtime —
// the EMIT-ZERO crash is a provisioning gap, not a user-code error. Fail loud here before
// any bundle compile, naming the fix, instead of deep inside the dep-load pipeline.
// (--auto-provision downloads the R2R apps and clears the check.)
if (!provisionSubcommand && packageCacheDirs.Count > 0)
{
    var version = AlRunnerV2.Infrastructure.BcArtifacts.SelectedVersion.ToString();
    var platformReport = AlRunnerV2.Infrastructure.ProvisioningCheck.CheckPlatformApps(
        version, packageCacheDirs);
    if (!platformReport.Ok)
    {
        if (autoProvision)
        {
            Console.Error.WriteLine("[provision] platform R2R apps missing — downloading...");
            // The missing apps carry their OWN version (platformReport.Issues), which can be
            // a different minor than the engine's SelectedVersion — the engine is
            // version-agnostic w.r.t. the R2R apps it dispatches to at runtime. Provision the
            // apps' minor, not a truncation of the engine version (that 404s: PlatformApps
            // needs a FULL artifact version like 28.2.50931.52786, not "28.1").
            var mm = AlRunnerV2.Infrastructure.ProvisioningCheck.DeriveProvisionMajorMinor(platformReport, version);
            var full = AlRunner.Provisioning.ArtifactDownloader.ResolveVersion(
                mm, m => Console.Error.WriteLine($"[provision] {m}"));
            if (full == null)
            {
                Console.Error.WriteLine($"[provision] could not resolve a full BC artifact version for '{mm}'; cannot continue.");
                return 2;
            }
            // Pick first writable cache dir.
            var pkgCacheOut = packageCacheDirs.FirstOrDefault(Directory.Exists)
                ?? packageCacheDirs[0];
            var rc = AlRunner.Provisioning.ArtifactDownloader.PlatformApps(
                full, pkgCacheOut, m => Console.Error.WriteLine($"[provision] {m}"));
            if (rc != 0)
            {
                Console.Error.WriteLine("[provision] platform-apps download failed; cannot continue.");
                return 2;
            }
            // Re-check: never silently continue on a partial/failed provision.
            platformReport = AlRunnerV2.Infrastructure.ProvisioningCheck.CheckPlatformApps(
                version, packageCacheDirs);
            if (!platformReport.Ok)
            {
                var stillMissing = string.Join(", ", platformReport.Issues.Select(i => i.Name));
                Console.Error.WriteLine($"[provision] platform apps still symbol-only after download: {stillMissing}");
                return 2;
            }
        }
        else
        {
            Console.Error.WriteLine(platformReport.ToDetailedMessage());
            return 2;
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
        if (n.Contains("Report") || n.Contains("NullReference") || n.Contains("NavNCL") || n.Contains("InvalidOperation"))
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
AlRunnerV2.PerfTrace.Log($"BcRuntime.EnsureApplied {t0.ElapsedMilliseconds}ms");

var emitter = new BcCompiler();
var assembler = new BcAssembler();
var executor = new TestExecutor { Isolation = isolation, TestFilter = testFilter };
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
if (watchUi && !AlRunnerV2.Log.Verbose)
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
    AlRunnerV2.InstallTriggerRunner.ResetForNewBundle();

    // ── per-bucket dep resolution ──────────────────────────────────────────
    var bucketRoot = FindBucketRoot(bundleAbs);
    if (bucketRoot != null)
    {
        var appJsonPath = Path.Combine(bucketRoot, "app.json");
        if (File.Exists(appJsonPath))
        {
            try
            {
                var roots = ReadDependencies(appJsonPath);
                // Include the bundle's own .alpackages in the resolver search dirs. They
                // carry the committed Microsoft platform symbol closure (Base Application /
                // System Application / …) as real .app files. On CI, packageCacheDirs is
                // empty (artifacts live elsewhere), so a Base App table that the app (or a
                // tableextension it ships) references is ONLY resolvable from here. Resolving
                // it produces the COMPILE spec; LoadAll skips Microsoft platform apps so their
                // runtime still comes from the service-tier DLLs, not a .app source-compile.
                var bundlePkgDirs = Directory
                    .EnumerateDirectories(bucketRoot, ".alpackages", SearchOption.AllDirectories)
                    .ToList();
                var resolverDirs = bundlePkgDirs.Concat(packageCacheDirs).Distinct().ToList();
                var resolver = new DependencyResolver(resolverDirs);
                var ordered = resolver.Resolve(roots);
                Console.WriteLine($"  [{rel}] resolved {ordered.Count} dep(s)");
                // Compiler sees only non-workspace dirs in its .app scanner; the
                // synthetic workspace dirs are registered as symbols.json-only
                // sources via SetExtraSymbolDirs (called AFTER SetResolvedDeps,
                // which resets _extraSymbolDirs). Runtime resolution above used the
                // full packageCacheDirs (incl. workspace) so dep code still loads.
                // Include the bundle .alpackages (real .apps w/ SymbolReference.json — safe
                // for the .app scanner) so the loader can resolve the Microsoft platform
                // specs (Base App etc.) on CI, where compilerPackageDirs is otherwise empty.
                var compilerDirs = bundlePkgDirs.Concat(compilerPackageDirs).Distinct().ToList();
                BcCompiler.SetResolvedDeps(ordered, compilerDirs);
                if (layeredWorkspaceDirs.Count > 0)
                    BcCompiler.SetExtraSymbolDirs(layeredWorkspaceDirs);
                var loaded = depLoader.LoadAll(ordered, bucketRoot);
                Console.WriteLine($"  [{rel}] loaded {loaded.Count} dep assembl(ies)");
                // Register dep assemblies (dependency order) so their Subtype=Install
                // codeunit lifecycle triggers fire before this bundle's tests run.
                AlRunnerV2.InstallTriggerRunner.SetDependencyAssemblies(loaded);
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
                    AlRunnerV2.Patches.RecordPatches.AddBcAppPath(appPath);
                // Register any prebuilt bundle-root .app (with SymbolReference.json) so the
                // generic NCLMetaQuery builder can read this bundle's own query column ids.
                AlRunnerV2.Patches.RecordPatches.RegisterBundleSymbolApps(bucketRoot);
                // Populate BcRuntime with this bundle's identity for the
                // NavApp.GetCurrentModuleInfo polyfill shim.
                SetBundleInfoFromAppJson(appJsonPath);
                // Compile this bundle under its REAL app.json identity so a dependency's
                // internalsVisibleTo grant (which names this app) matches — otherwise the
                // synthetic compile identity fails the grant check (AL0161).
                var bundleId = AlRunnerV2.Infrastructure.InProcessAppPackager.ReadIdentity(appJsonPath);
                if (bundleId != null)
                    BcCompiler.SetCurrentAppIdentity(bundleId.AppId, bundleId.Publisher, bundleId.Version);
                else
                    BcCompiler.SetCurrentAppIdentity(null, null, null);
            }
            catch (AlRunnerV2.Infrastructure.DependencyLoadException ex)
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
            catch (AlRunnerV2.Infrastructure.MissingDependencyException ex)
            {
                // A declared dependency is completely absent from every package-cache directory.
                // Continuing to compile would produce thousands of misleading AL0185 "X is missing"
                // errors that blame the user's own code. Instead: restore streams, print ONE loud
                // provisioning-gap message naming the dep + fix commands, and abort.
                if (stdoutSilenced) { Console.SetOut(savedOut); Console.SetError(savedErr); }
                var bcVer = AlRunnerV2.Infrastructure.BcArtifacts.SelectedVersion.ToString();
                Console.Error.WriteLine();
                Console.Error.WriteLine(ex.ToDetailedMessage(bcVer));
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
            Console.Error.WriteLine($"  [{rel}] WARN: no {appJsonPath} — skipping dep loading");
        }
    }
    // If there's no bucketRoot app.json but the bundle itself has one, use it.
    else if (File.Exists(Path.Combine(bundleAbs, "app.json")))
    {
        SetBundleInfoFromAppJson(Path.Combine(bundleAbs, "app.json"));
    }

    var suites = EnumerateSuites(bundleAbs).ToList();
    if (suites.Count == 0) { Console.WriteLine($"[{i2}/{bundles.Count}] {rel} ... SKIP (no suites)"); continue; }
    Console.WriteLine($"[{i2}/{bundles.Count}] {rel} — {suites.Count} suites");

    // Pre-register every src dir for RecordPatches at the bundle level.
    foreach (var suite in suites)
    {
        var s = Path.Combine(suite, "src");
        if (Directory.Exists(s))
            AlRunnerV2.Patches.RecordPatches.AddSourceDir(s);
        else if (!Directory.Exists(Path.Combine(suite, "test")))
            // Flat bundle: register the suite root so table parsers can find .al files.
            AlRunnerV2.Patches.RecordPatches.AddSourceDir(suite);
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
        // ── Bundled mode (default): ONE Emit + ONE Compile + ONE Run across
        // all suites. 5-7× faster than per-suite, parity-verified on all 4
        // sub-buckets. Suites whose AL hits BC emit bugs or bundled-only
        // strictness checks are quarantined under tests/excluded/ with a
        // RUNNER-GAP-*.md note explaining the gap.
        var allPaths = new List<string>();
        foreach (var suite in suites)
            allPaths.AddRange(CollectSuitePaths(suite, bucketRoot));
        allPaths = allPaths.Distinct().ToList();

        var moduleName = $"V2_{Path.GetFileName(bundleAbs)}";

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
        if (alCacheDir != null)
        {
            cacheKey = ComputeAlCacheKey(allPaths, moduleName, ordered: GetOrderedDepIds(bucketRoot, packageCacheDirs));
            cachePath = Path.Combine(alCacheDir, cacheKey + ".dll");
            sidecarPath = Path.Combine(alCacheDir, cacheKey + ".enum-registry.json");
            if (File.Exists(cachePath) && File.Exists(sidecarPath))
            {
                try { cachedBytes = File.ReadAllBytes(cachePath); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [cache] read failed for {cachePath}: {ex.Message}");
                    cachedBytes = null;
                }
            }
            else if (File.Exists(cachePath))
            {
                Console.Error.WriteLine($"  [cache] DLL present but sidecar missing — treating as MISS ({sidecarPath})");
            }
        }

        byte[]? assemblyBytes = null;
        if (cachedBytes != null)
        {
            // Replay the enum-registry sidecar BEFORE Assembly.Load. Test
            // execution is what reads the registry (via the
            // NCLEnumMetadata_CreateByIdAlAware hook), so as long as replay
            // completes before executor.Run that's sufficient — but doing it
            // pre-Load is cheap insurance against any module-cctor that
            // touches enum metadata.
            int replayed = 0;
            try { replayed = LoadEnumRegistrySidecar(sidecarPath!); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [cache] sidecar replay failed for {sidecarPath}: {ex.Message} — falling through to MISS");
                cachedBytes = null;
            }
            if (cachedBytes != null)
            {
                Console.Error.WriteLine($"  [cache] HIT  key={cacheKey} path={cachePath} ({cachedBytes.Length} bytes, {replayed} enum entries replayed) — skipping Emit+Compile");
                assemblyBytes = cachedBytes;
            }
        }
        if (assemblyBytes == null)
        {
            if (alCacheDir != null) Console.Error.WriteLine($"  [cache] MISS key={cacheKey} — running Emit+Compile");
            var et = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<EmittedSource> sources = Array.Empty<EmittedSource>();
            IReadOnlyList<string> alDiagnostics = Array.Empty<string>();
            // Emit-phase timeout: default 120 s, override via AL_RUNNER_EMIT_TIMEOUT_SEC.
            // Note: Task.Run thread continues in background after timeout — acceptable for a CLI tool.
            int emitTimeoutSec = int.TryParse(
                Environment.GetEnvironmentVariable("AL_RUNNER_EMIT_TIMEOUT_SEC"), out var ts) ? ts : 120;
            var emitTask = Task.Run(() => emitter.Emit(allPaths, moduleName));
            try
            {
                if (!emitTask.Wait(TimeSpan.FromSeconds(emitTimeoutSec)))
                {
                    Console.Error.WriteLine(
                        $"<bundled>: EMIT-TIMEOUT after {emitTimeoutSec}s on {allPaths.Count} AL paths");
                    Console.Error.WriteLine(
                        "Hint: increase AL_RUNNER_EMIT_TIMEOUT_SEC or quarantine the offending suite under tests/excluded/.");
                    bundleErrors.Add($"<bundled>: EMIT-TIMEOUT after {emitTimeoutSec}s");
                }
                else
                {
                    var emitOutput = emitTask.Result;
                    sources = emitOutput.Sources;
                    alDiagnostics = emitOutput.Diagnostics;

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
                            File.WriteAllBytes(cachePath, assemblyBytes);
                            // Sidecar: persist the AlEnumMetadataRegistry side-effect that
                            // emit just populated. Without this, cache HIT replays the DLL
                            // but leaves the registry empty → enum tests fail.
                            int written = SaveEnumRegistrySidecar(sidecarPath!);
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
        if (assemblyBytes != null)
        {
            var rt = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<TestResult> tests;
            try
            {
                var loadSw = System.Diagnostics.Stopwatch.StartNew();
                var asm = Assembly.Load(assemblyBytes);
                loadSw.Stop();
                AlRunnerV2.PerfTrace.Log($"test assembly load {rel} {loadSw.ElapsedMilliseconds}ms");
                var registerSw = System.Diagnostics.Stopwatch.StartNew();
                BcRuntime.SetTestAssembly(asm);
                BcRuntime.RegisterTestAssemblyInfo(asm);
                registerSw.Stop();
                AlRunnerV2.PerfTrace.Log($"RegisterTestAssemblyInfo {rel} {registerSw.ElapsedMilliseconds}ms");
                BcRuntime.OosHooksActive = true;
                var execSw = System.Diagnostics.Stopwatch.StartNew();
                tests = executor.Run(asm);
                execSw.Stop();
                AlRunnerV2.PerfTrace.Log($"TestExecutor.Run {rel} {execSw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                rt.Stop(); bundleRun += rt.Elapsed;
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
                Console.Error.WriteLine($"{suiteName}: EMIT-FAIL — {ex.GetType().Name}: {ex.Message}");
                if (ex.StackTrace is { } st) Console.Error.WriteLine(st);
                bundleErrors.Add($"{suiteName}: EMIT-FAIL: {ex.Message.Split('\n')[0]}");
                continue;
            }
            et.Stop(); bundleEmit += et.Elapsed;

            var ct = System.Diagnostics.Stopwatch.StartNew();
            var compile = assembler.Compile($"V2_{Path.GetFileName(suite)}", sources);
            ct.Stop(); bundleComp += ct.Elapsed;
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
                BcRuntime.SetTestAssembly(asm);
                BcRuntime.RegisterTestAssemblyInfo(asm);
                BcRuntime.OosHooksActive = true;
                tests = executor.Run(asm);
            }
            catch (Exception ex)
            {
                rt.Stop(); bundleRun += rt.Elapsed;
                bundleErrors.Add($"{suiteName}: EXEC-FAIL: {ex.Message.Split('\n')[0]}");
                continue;
            }
            finally
            {
                BcRuntime.OosHooksActive = false;
            }
            rt.Stop(); bundleRun += rt.Elapsed;
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
    if (bundleTests.Count == 0 && bundleErrors.Count > 0) bundleStage = BucketStage.CompileFailed;
    results.Add(new BucketResult(bundleAbs, bundleStage,
        bundleErrors, null, bundleTests,
        bundleEmit, bundleComp, bundleRun));
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
    var idleTs = DateTime.Now;
    watchScroll = 0;
    var lines = RenderDashboardLines(WatchStatus.Idle, idleTs, cycleDur);
    watchScroll = PaintWatchViewport(lines, watchScroll);

    var armed = ArmSourceWatch(bundles);
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
    Console.WriteLine("[watch] waiting for AL source changes… (Ctrl+C to quit)");
    // Flush before blocking: when stdout is a pipe/file (a TUI front-end, VS Code, or
    // a test harness) it is block-buffered, so the cycle's results + this marker would
    // otherwise sit unflushed for the entire idle wait. A TTY auto-flushes, but piped
    // consumers must see each cycle as it completes.
    Console.Out.Flush();
    if (!WaitForSourceChange(bundles))
        return 0;
    Console.WriteLine("[watch] change detected — re-running…");
    Console.Out.Flush();
}
} // end while(true) watch loop

Reporter.PrintPerTest(results, Console.Out, showPass);
if (printClassification)
    Reporter.PrintFailureClassification(results, Console.Out);
Reporter.PrintSummary(results, Console.Out);
if (outPath != null)
{
    Reporter.WriteClassification(results, outPath);
    Console.WriteLine($"Classification → {outPath}");
}

// --strict: exit non-zero if anything failed. Matches v1 semantics so CI shell
// loops can `set -e` against the run command. Default exit is 0 regardless,
// so tooling that consumes the JSON can keep parsing across bundles.
if (strictExitCode)
{
    int failed = 0, errored = 0, compileFail = 0, execFail = 0;
    foreach (var b in results)
    {
        if (b.Stage == BucketStage.CompileFailed) { compileFail++; continue; }
        if (b.Stage == BucketStage.ExecuteFailed) { execFail++; continue; }
        foreach (var t in b.Tests)
        {
            if (t.Outcome == TestOutcome.Fail) failed++;
            else if (t.Outcome == TestOutcome.Error) errored++;
        }
    }
    if (compileFail > 0) return 3;       // compile errors
    if (execFail > 0) return 2;          // bucket-level execution error
    if (failed + errored > 0) return 1;  // at least one test failed
}
return 0;

// ── --server loop ──────────────────────────────────────────────────────────────
// Non-static so it captures the warm pipeline objects (emitter/assembler/executor/
// depLoader) and the resolved cache dirs established above. Reads newline-delimited
// JSON requests from stdin, writes one JSON response line per request to stdout.
// Protocol shape matches v1 (see ServerProtocol). Returns the process exit code.
int RunServerLoop(System.IO.TextReader input, System.IO.TextWriter output)
{
    // Per-session memory of the last served request's .al file hashes, so a cache
    // miss can report which files changed (v1 `changedFiles`).
    Dictionary<string, string>? lastFileHashes = null;

    // Readiness handshake — MUST be the first line on stdout.
    output.WriteLine("{\"ready\":true}");
    output.Flush();

    string? line;
    while ((line = input.ReadLine()) != null)
    {
        if (line.Length == 0) continue;
        string response;
        bool shuttingDown = false;
        try
        {
            var req = AlRunnerV2.ServerProtocol.Parse(line);
            switch (req?.Command?.ToLowerInvariant())
            {
                case null:
                    response = AlRunnerV2.ServerProtocol.Error("Invalid request (missing 'command')");
                    break;
                case "runtests":
                    response = HandleServerRunTests(req);
                    break;
                case "execute":
                    response = HandleServerExecute(req);
                    break;
                case "shutdown":
                    response = AlRunnerV2.ServerProtocol.Shutdown();
                    shuttingDown = true;
                    break;
                default:
                    response = AlRunnerV2.ServerProtocol.Error($"Unknown command: {req.Command}");
                    break;
            }
        }
        catch (Exception ex)
        {
            response = AlRunnerV2.ServerProtocol.Error(ex.Message);
        }

        output.WriteLine(response);
        output.Flush();
        if (shuttingDown) return 0;
    }
    // EOF — client disconnected.
    return 0;

    // ── runTests: re-emit + run the requested bundle in-process. ───────────────
    string HandleServerRunTests(AlRunnerV2.ServerRequest req)
    {
        if (req.SourcePaths == null || req.SourcePaths.Length == 0)
            return AlRunnerV2.ServerProtocol.Error("sourcePaths is required");

        // v2 runs a single bundle per request; the extension passes one app root.
        var bundleDir = req.SourcePaths[0];
        if (!Directory.Exists(bundleDir))
            return AlRunnerV2.ServerProtocol.Error($"bundle directory not found: {bundleDir}");

        var run = RunBundleForServer(bundleDir, req.PackagePaths, asm => executor.Run(asm));

        // changedFiles is only meaningful on a cache miss (a hit means nothing changed).
        List<string>? changed = null;
        if (!run.Cached)
            changed = DiffServerFiles(lastFileHashes, run.FileHashes);
        lastFileHashes = run.FileHashes;

        return AlRunnerV2.ServerProtocol.RunTests(
            run.Tests, run.ExitCode, run.Cached, changed, run.CompileErrors);
    }

    // ── execute: run the bundle's first OnRun-bearing codeunit (run-mode). v1 also
    // accepted an inline `code` string; v2 has no inline-AL compile path yet, so
    // that case fails LOUD (never a silent fake) per .claude/rules/loud-failures.md.
    string HandleServerExecute(AlRunnerV2.ServerRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.Code))
            return AlRunnerV2.ServerProtocol.Error(
                "execute: inline AL 'code' is not yet supported in v2 — pass 'sourcePaths' "
                + "to run the bundle's OnRun codeunit. See docs/server-mode.md.");
        if (req.CaptureValues == true)
            return AlRunnerV2.ServerProtocol.Error(
                "execute: 'captureValues' is not yet supported in v2. See docs/server-mode.md.");
        if (req.SourcePaths == null || req.SourcePaths.Length == 0)
            return AlRunnerV2.ServerProtocol.Error("sourcePaths is required");
        var bundleDir = req.SourcePaths[0];
        if (!Directory.Exists(bundleDir))
            return AlRunnerV2.ServerProtocol.Error($"bundle directory not found: {bundleDir}");

        var run = RunBundleForServer(bundleDir, req.PackagePaths, RunFirstCodeunitOnRun);
        lastFileHashes = run.FileHashes;
        return AlRunnerV2.ServerProtocol.Execute(run.Tests, run.ExitCode, null, run.CompileErrors);
    }

    // Compile + run one bundle, resetting bundle-derived caches first so an edited
    // same-identity bundle is picked up (server reload contract). Mirrors the
    // bundled-mode path of the normal run loop for a single bundle. The run step
    // (executor.Run for runTests, OnRun dispatch for execute) is supplied by the caller.
    ServerRunResult RunBundleForServer(string bundleDir, string[]? requestPackagePaths,
        Func<Assembly, IReadOnlyList<TestResult>> runStep)
    {
        // CRITICAL: drop the previous request's bundle-derived caches so a reloaded
        // same-named bundle resolves the freshly-emitted Record/Codeunit types and
        // starts with empty in-memory tables. See BcRuntime.ResetForNewBundleReload.
        BcRuntime.ResetForNewBundleReload();

        var bundleAbs = Path.GetFullPath(bundleDir);
        var bucketRoot = FindBucketRoot(bundleAbs) ?? bundleAbs;

        // Request package paths augment the server's default caches.
        var effectivePkgDirs = (requestPackagePaths ?? Array.Empty<string>())
            .Where(Directory.Exists)
            .Concat(packageCacheDirs)
            .Distinct()
            .ToList();

        IReadOnlyList<(AlRunnerV2.AppManifest Manifest, string AppPath)> ordered =
            Array.Empty<(AlRunnerV2.AppManifest, string)>();
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
                AlRunnerV2.InstallTriggerRunner.ResetForNewBundle();
                AlRunnerV2.InstallTriggerRunner.SetDependencyAssemblies(loaded);
                BcCompiler.SetResolvedDeps(ordered, resolverDirs);
                foreach (var (_, appPath) in ordered)
                    AlRunnerV2.Patches.RecordPatches.AddBcAppPath(appPath);
                AlRunnerV2.Patches.RecordPatches.RegisterBundleSymbolApps(bucketRoot);
                SetBundleInfoFromAppJson(appJsonPath);
                var bundleId = AlRunnerV2.Infrastructure.InProcessAppPackager.ReadIdentity(appJsonPath);
                if (bundleId != null)
                    BcCompiler.SetCurrentAppIdentity(bundleId.AppId, bundleId.Publisher, bundleId.Version);
                else
                    BcCompiler.SetCurrentAppIdentity(null, null, null);
            }
            catch (AlRunnerV2.Infrastructure.DependencyLoadException ex)
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
        foreach (var suite in suites)
        {
            var s = Path.Combine(suite, "src");
            if (Directory.Exists(s)) AlRunnerV2.Patches.RecordPatches.AddSourceDir(s);
            else if (!Directory.Exists(Path.Combine(suite, "test")))
                AlRunnerV2.Patches.RecordPatches.AddSourceDir(suite);
            allPaths.AddRange(CollectSuitePaths(suite, bucketRoot));
        }
        allPaths = allPaths.Distinct().ToList();
        var fileHashes = ComputeServerFileHashes(allPaths);

        if (allPaths.Count == 0)
            return new ServerRunResult(Array.Empty<TestResult>(), 1, false, null, fileHashes);

        var moduleName = $"V2_{Path.GetFileName(bundleAbs)}";

        // AL-output cache: HIT short-circuits Emit+Compile, like the normal loop.
        byte[]? assemblyBytes = null;
        bool cached = false;
        string? cacheKey = null, cachePath = null, sidecarPath = null;
        if (alCacheDir != null)
        {
            cacheKey = ComputeAlCacheKey(allPaths, moduleName,
                ordered: GetOrderedDepIds(bucketRoot, effectivePkgDirs));
            cachePath = Path.Combine(alCacheDir, cacheKey + ".dll");
            sidecarPath = Path.Combine(alCacheDir, cacheKey + ".enum-registry.json");
            if (File.Exists(cachePath) && File.Exists(sidecarPath))
            {
                try
                {
                    var bytes = File.ReadAllBytes(cachePath);
                    LoadEnumRegistrySidecar(sidecarPath);
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
            try
            {
                var emitOutput = emitter.Emit(allPaths, moduleName);
                sources = emitOutput.Sources;
                alDiagnostics = emitOutput.Diagnostics;
            }
            catch (Exception ex)
            {
                return ServerRunResult.Failure(3, moduleName, $"EMIT-FAIL: {ex.Message.Split('\n')[0]}", fileHashes);
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
                    File.WriteAllBytes(cachePath, assemblyBytes);
                    SaveEnumRegistrySidecar(sidecarPath!);
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
        AlRunnerV2.Infrastructure.AlCallStackCapture.Clear();
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
            var alStack = AlRunnerV2.Infrastructure.AlCallStackCapture.GetCaptured(inner);
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

// Block until an .al file changes under any watched bundle's bucket root. Returns true
// on a change (loop again), false if there is nothing to watch.
static bool WaitForSourceChange(List<string> bundles)
{
    var armed = ArmSourceWatch(bundles);
    if (armed == null) return false;
    var (signal, watchers) = armed.Value;
    try
    {
        signal.Wait();                       // block until the first .al change
        System.Threading.Thread.Sleep(250);  // debounce: let a save storm settle
        return true;
    }
    finally
    {
        foreach (var w in watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
        signal.Dispose();
    }
}

/// <summary>
/// Arm FileSystemWatchers over the bundles' source roots and return a settable
/// signal + the watchers so the caller can interleave the file-change wait with
/// other work (e.g. the interactive dashboard's keyboard polling). Returns null
/// if there are no source dirs to watch. The caller owns disposal of both.
/// </summary>
static (System.Threading.ManualResetEventSlim Signal, List<FileSystemWatcher> Watchers)? ArmSourceWatch(List<string> bundles)
{
    var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var b in bundles)
    {
        var abs = Path.GetFullPath(b);
        var root = FindBucketRoot(abs) ?? abs;
        if (Directory.Exists(root)) dirs.Add(root);
    }
    if (dirs.Count == 0)
    {
        Console.Error.WriteLine("[watch] no source directories to watch.");
        return null;
    }

    var signal = new System.Threading.ManualResetEventSlim(false);
    void OnChanged(object _, FileSystemEventArgs e)
    {
        if (e.FullPath.EndsWith(".al", StringComparison.OrdinalIgnoreCase)) signal.Set();
    }
    void OnRenamed(object _, RenamedEventArgs e)
    {
        if (e.FullPath.EndsWith(".al", StringComparison.OrdinalIgnoreCase)) signal.Set();
    }

    var watchers = new List<FileSystemWatcher>();
    foreach (var d in dirs)
    {
        var w = new FileSystemWatcher(d)
        {
            IncludeSubdirectories = true,
            Filter = "*.al",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        w.Changed += OnChanged; w.Created += OnChanged; w.Deleted += OnChanged; w.Renamed += OnRenamed;
        watchers.Add(w);
    }
    return (signal, watchers);
}

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

static void PrintHelp(TextWriter w)
{
    w.WriteLine("al-runner — run Business Central AL unit tests in-process.");
    w.WriteLine();
    w.WriteLine("USAGE");
    w.WriteLine("  al-runner [OPTIONS] <bundle-dir>...");
    w.WriteLine("  al-runner --server [--package-cache PATH ...] [--cache DIR]");
    w.WriteLine("  al-runner --precompile <input.app> --out <output.dll> [--package-cache PATH ...]");
    w.WriteLine("  al-runner --version");
    w.WriteLine("  al-runner --help");
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
    w.WriteLine("                            method    accepted as v1 alias for codeunit");
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
    w.WriteLine("  --package-cache PATH    Extra directory to scan for .app dependencies");
    w.WriteLine("                          (repeatable). Default scan: ~/.bcartifacts.cache,");
    w.WriteLine("                          ~/.local/share/al-runner/artifacts, and bundle .alpackages/.");
    w.WriteLine("  --cache DIR             AL-output cache directory. Default:");
    w.WriteLine("                          ~/.cache/al-runner/al-out. Compiled test DLLs are");
    w.WriteLine("                          re-used on subsequent runs if inputs are unchanged");
    w.WriteLine("                          (key = hash of .al sources, resolved deps, runner mtime).");
    w.WriteLine("  --no-cache              Disable the AL-output cache for this run.");
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
    w.WriteLine("  --failures-only, --quiet");
    w.WriteLine("                          Print only FAIL/ERROR per-test lines. Default prints both");
    w.WriteLine("                          PASS and FAIL with stack traces (matches v1).");
    w.WriteLine("  --show-pass             Accepted for v1 back-compat; PASS lines are on by default");
    w.WriteLine("                          in v2.");
    w.WriteLine("  --verbose               Show internal [Component] diagnostic logs.");
    w.WriteLine("  --strict                Exit non-zero if anything failed.  Exit codes:");
    w.WriteLine("                            0  all tests passed");
    w.WriteLine("                            1  at least one test FAILED or ERRORED");
    w.WriteLine("                            2  a bundle could not execute (process-level error)");
    w.WriteLine("                            3  a bundle could not compile");
    w.WriteLine("                          Without --strict the runner always exits 0 so callers can");
    w.WriteLine("                          parse the JSON regardless of test outcomes.");
    w.WriteLine("  --dump-csharp DIR       Write the intermediate C# emitted by BC's Compilation.Emit");
    w.WriteLine("                          (one .cs file per AL object) under DIR/<moduleName>/.");
    w.WriteLine("                          Useful for diagnosing codegen issues.");
    w.WriteLine();
    w.WriteLine("SUBCOMMANDS");
    w.WriteLine("  --precompile <input.app> --out <output.dll> [--package-cache PATH ...]");
    w.WriteLine("                          Compile a single .app to a managed DLL without running");
    w.WriteLine("                          tests. Useful for pre-warming caches.");
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
    w.WriteLine();
    w.WriteLine("EXAMPLES");
    w.WriteLine("  # Run the al-language corpus");
    w.WriteLine("  al-runner tests/al-language/tests/al-language");
    w.WriteLine();
    w.WriteLine("  # Run one specific test");
    w.WriteLine("  al-runner --test Record_Insert_DuplicateKey_Throws tests/al-language/tests/al-language");
    w.WriteLine();
    w.WriteLine("  # CI: strict exit, quiet, JUnit-friendly JSON path");
    w.WriteLine("  al-runner --strict --quiet --out ci-results.json tests/al-language/tests/al-language");
    w.WriteLine();
    w.WriteLine("  # Dump the C# for a debugging session");
    w.WriteLine("  al-runner --dump-csharp /tmp/al-csharp tests/runner-extras/oos-reports");
    w.WriteLine();
    w.WriteLine("  # Pre-compile an .app to a managed DLL");
    w.WriteLine("  al-runner --precompile MyExtension_1.0.0.0.app --out MyExtension.dll");
    w.WriteLine();
    w.WriteLine("NOT YET IN V2 (see docs/v1-to-v2-migration.md)");
    w.WriteLine("  --server                Debug-adapter-protocol server (was DapServer.cs in v1).");
    w.WriteLine("                          Needs an AL→C# source map BC's emit pipeline does not");
    w.WriteLine("                          currently expose; tracked as a separate workstream.");
    w.WriteLine("  --coverage              Cobertura coverage output. v1 instrumented its Roslyn");
    w.WriteLine("                          rewrite pass to count method hits; v2 has no rewrite pass.");
    w.WriteLine("                          A Cecil-based implementation is feasible (2-4 d) but not");
    w.WriteLine("                          yet built.");
    w.WriteLine("  --junit PATH            JUnit-XML output. Self-contained add (~80 LOC).");
    w.WriteLine("  --stubs DIR             v1's stub-merge path. v2 loads real MS DLLs so the");
    w.WriteLine("                          original use case mostly evaporated; still possible to");
    w.WriteLine("                          add as an extra source-root merge if needed.");
    w.WriteLine("  --extract-deps          v1's dep-slicer (DepExtractor.cs, ~121 KB). Likely to be");
    w.WriteLine("                          dropped — v2 loads the full dep set directly.");
    w.WriteLine();
    w.WriteLine("DOCUMENTATION");
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
    var identity = AlRunnerV2.Infrastructure.InProcessAppPackager.ReadIdentity(appJsonPath);
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
        AlRunnerV2.Infrastructure.InProcessAppPackager.EmitAppPackageToFile(
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
    var identities = new Dictionary<string, AlRunnerV2.Infrastructure.BundleIdentity>(StringComparer.OrdinalIgnoreCase);
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
        var id = AlRunnerV2.Infrastructure.InProcessAppPackager.ReadIdentity(appJson);
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
        AlRunnerV2.Patches.NavAppResourcePatches.RegisterSourceDirForApp(implId.AppId, implPath);

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
                AlRunnerV2.Infrastructure.InProcessAppPackager.EmitAppPackageToFile(
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
        var id = AlRunnerV2.Infrastructure.InProcessAppPackager.ReadIdentity(appJson);
        if (id == null) continue;
        bundleRoots.Add(Path.GetFullPath(Path.GetDirectoryName(appJson)!));
        // Skip Optional (implicit Microsoft Application/System) roots — those live
        // in the package caches, never as a sibling source app.
        neededDeps.AddRange(id.Dependencies.Where(d => !d.Optional));
    }
    if (neededDeps.Count == 0) return packageCacheDirs;

    // 2. Discover candidate source apps in the parent dir of each bundle root.
    var sourceApps = new Dictionary<string, AlRunnerV2.Infrastructure.BundleIdentity>(StringComparer.OrdinalIgnoreCase);
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
            var sid = AlRunnerV2.Infrastructure.InProcessAppPackager.ReadIdentity(aj);
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
            AlRunnerV2.Patches.RecordPatches.AddSourceDir(dir);
            // Remember the sibling source dir by AppId so NavApp.GetResource can serve
            // its resourceFolders files even when the dep loads via the synthetic
            // workspace-deps .app (which carries no /resources/ part).
            AlRunnerV2.Patches.NavAppResourcePatches.RegisterSourceDirForApp(sid.AppId, dir);
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
        AlRunnerV2.Patches.RecordPatches.AddSourceDir(dir);
        var appFileName = $"{Sanitize(sid.Publisher)}_{Sanitize(sid.Name)}_{sid.Version.ToString().Replace('.', '_')}.app";
        var outPath = Path.Combine(wsDir, appFileName);
        var hadApp = File.Exists(outPath);
        if (!hadApp)
        {
            try
            {
                AlRunnerV2.Infrastructure.InProcessAppPackager.EmitAppPackageToFile(dir, sid, outPath);
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
            var manifest = AlRunnerV2.AppLoader.ReadManifest(file);
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
    IReadOnlyDictionary<string, AlRunnerV2.Infrastructure.BundleIdentity> sourceApps)
{
    using var sha = System.Security.Cryptography.SHA256.Create();
    using var ms = new MemoryStream();
    void WriteLine(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\n");
        ms.Write(bytes, 0, bytes.Length);
    }

    WriteLine("schema:v1");
    var runnerLoc = typeof(AlRunnerV2.BcAssembler).Assembly.Location;
    if (!string.IsNullOrEmpty(runnerLoc) && File.Exists(runnerLoc))
        WriteLine($"runner:{File.GetLastWriteTimeUtc(runnerLoc).Ticks}:{new FileInfo(runnerLoc).Length}");
    else
        WriteLine("runner:unknown");

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
    Dictionary<string, AlRunnerV2.Infrastructure.BundleIdentity> idByKey)
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
    var home = AlRunnerV2.Infrastructure.AlRunnerPaths.UserHome;
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
            try { ver = AlRunnerV2.Infrastructure.BcArtifacts.TryTranslateArtifactPathToVersion(argv[i + 1]); }
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
    var home = AlRunnerV2.Infrastructure.AlRunnerPaths.UserHome;
    if (string.IsNullOrEmpty(home)) yield break;

    var sel = AlRunnerV2.Infrastructure.BcArtifacts.SelectedVersion;
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
        return AlRunnerV2.Infrastructure.BcArtifacts.SelectArtifactVersionDir(root, versionPrefix);
    }
    catch (InvalidOperationException)
    {
        // No matching version in this optional cache — fine.
        return null;
    }
}

// Walks up from <bundlePath> until it finds a dir containing app.json.
// Returns null if none found before /tests/ or filesystem root.
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
            if (AlRunnerV2.DependencyResolver.IsMicrosoftPlatformApp(m.Name, m.Publisher))
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
            ?? AlRunnerV2.Infrastructure.BcArtifacts.EngineMajor(AppContext.BaseDirectory)?.ToString()
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
            var cachedDir = AlRunnerV2.Infrastructure.BcArtifacts.SelectArtifactVersionDir(
                AlRunnerV2.Infrastructure.BcArtifacts.ArtifactsRootDir, prefix);
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

    var serviceTierDir = AlRunnerV2.Infrastructure.BcArtifacts.ArtifactDirFor(full);
    var report = AlRunnerV2.Infrastructure.ProvisioningCheck.Check(full, serviceTierDir);
    if (report.Ok)
    {
        Console.Error.WriteLine($"[provision] BC {full} engine artifacts already complete at {serviceTierDir}.");
        provisionedVersion = full;
        return 0;
    }

    if (!AlRunnerV2.Infrastructure.ProvisioningCheck.AutoProvision(full, serviceTierDir))
        return 1;
    provisionedVersion = full;
    return 0;
}

static void SetBundleInfoFromAppJson(string appJsonPath)
{
    // Remember (or clear) the bundle dir for NavApp.GetResource: the emitted test
    // assembly's resources are the files under this dir's app.json resourceFolders.
    AlRunnerV2.Patches.NavAppResourcePatches.SetCurrentBundleDir(
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
        AlRunnerV2.BcRuntime.SetCurrentBundleInfo(appId, name, pub, ver);
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
            bool isMsPlatform = AlRunnerV2.DependencyResolver.IsMicrosoftPlatformApp(name, pub);
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
    WriteLine("schema:v4");

    // 1. Runner assembly fingerprint — any rewriter / polyfill / patch change
    //    in the runner forces a cache miss.
    var runnerLoc = typeof(AlRunnerV2.BcAssembler).Assembly.Location;
    if (!string.IsNullOrEmpty(runnerLoc) && File.Exists(runnerLoc))
        WriteLine($"runner:{File.GetLastWriteTimeUtc(runnerLoc).Ticks}:{new FileInfo(runnerLoc).Length}");
    else
        WriteLine("runner:unknown");

    WriteLine($"module:{moduleName}");

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
// Schema (v3): { "enums": [ { "id": int, "name": string, "options": [string], "indexes": [int], "implementations": [[int]] }, ... ] }
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
        AlEnumMetadataRegistry.Register(id, name, opts, idxs, implementations);
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
    return count;
}

// Read app.json deps and feed them through DependencyResolver so the cache key
// reflects the exact resolved set (id+version), not just declared roots. This
// matches what BcCompiler.SetResolvedDeps fed into the compile.
static IReadOnlyList<string> GetOrderedDepIds(string? bucketRoot, IReadOnlyList<string> packageCacheDirs)
{
    if (bucketRoot == null) return Array.Empty<string>();
    var appJsonPath = Path.Combine(bucketRoot, "app.json");
    if (!File.Exists(appJsonPath)) return Array.Empty<string>();
    try
    {
        var roots = ReadDependencies(appJsonPath).ToList();
        var resolver = new AlRunnerV2.DependencyResolver(packageCacheDirs);
        var ordered = resolver.Resolve(roots);
        return ordered
            .Select(d => $"{d.Manifest.AppId:N}:{d.Manifest.Version}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }
    catch
    {
        return Array.Empty<string>();
    }
}

static IEnumerable<string> EnumerateSuites(string root)
{
    if (LooksLikeSuite(root)) { yield return Path.GetFullPath(root); yield break; }
    bool found = false;
    foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        if (LooksLikeSuite(d))
        {
            found = true;
            yield return Path.GetFullPath(d);
        }
    // Flat bundle: no src/test sub-structure but .al files exist (e.g. a standalone
    // test library with its own app.json and category sub-directories). Treat the
    // whole root as one compilation + test unit.
    if (!found && Directory.EnumerateFiles(root, "*.al", SearchOption.AllDirectories).Any())
        yield return Path.GetFullPath(root);
}

static bool LooksLikeSuite(string dir)
    => Directory.Exists(Path.Combine(dir, "test"))
    || Directory.Exists(Path.Combine(dir, "src"));
