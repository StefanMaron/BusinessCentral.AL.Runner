// TestExecutor — discovers and runs AL test methods on compiled BC IL.
// AL test convention: codeunit with [SubType=Test], methods with [Test] attribute.
// In emitted C#: codeunits become classes named CodeunitNNNN; test methods carry
// [NavTest] attribute (via NCLAttribute system). We discover by attribute name to
// avoid coupling to specific BC types.
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace AlRunner;

public enum TestOutcome { Pass, Fail, Error, Skipped }

/// <summary>
/// Test-isolation granularity. Mirrors the BC "Test Runner" codeunits:
///   Codeunit (default, matches 130450 "Test Runner - Isol. Codeunit") — all
///     tests inside a single codeunit share state; reset happens once per CU.
///   Test  (matches 130452 "Test Runner - Isol. Test") — every [Test] gets a
///     fresh in-memory state.
///   Disabled (matches 130453) — no state reset; suite-long sharing.
/// </summary>
public enum TestIsolation { Codeunit, Test, Disabled }

public static class TestIsolationParser
{
    /// <summary>
    /// Parse the CLI/--server <c>testIsolation</c> mode string. Shared by
    /// Program.cs's <c>--isolation</c>/<c>--test-isolation</c> CLI parsing and the
    /// <c>runTests</c>/<c>execute</c> server commands (see #1616) so the two entry
    /// points can never silently drift onto different mappings.
    /// </summary>
    public static TestIsolation Parse(string mode)
    {
        var m = mode.ToLowerInvariant();
        return m switch
        {
            "codeunit"         => TestIsolation.Codeunit,
            // v1's --test-isolation method reset AL table/session state before every
            // [Test] procedure (per v1 Program.cs: doTableReset = testIsolation ==
            // TestIsolation.Method). That is v2's TestIsolation.Test, NOT
            // TestIsolation.Codeunit — see issue #1647. Map 'method' onto the mode
            // that actually reproduces v1's behavior so callers that still pass the
            // v1-idiomatic value (e.g. LethAL) don't silently get weaker isolation
            // than they asked for.
            "test" or "method" => TestIsolation.Test,
            "disabled"         => TestIsolation.Disabled,
            _ => throw new ArgumentException(
                $"unknown test isolation mode '{mode}' (codeunit|test|disabled; 'method' accepted as v1 alias for test)")
        };
    }
}

// Exception is the caught exception object (null for Pass/Skipped) — kept so the
// expectations manifest can classify typed throws (RunnerOutOfScopeException +
// reason) that string messages cannot carry. Expectation is set only when the
// manifest reclassified this result (pass-oos / pass-known-gap / pass-divergence /
// skipped / manifest drift); null means a plain pass/fail untouched by the manifest.
//
// InsideTestProc / TimedOut exist so the failure can be BUCKETED without re-deriving
// the bucket from Message text — see ErrorClassifier.Classify(TestResult) and the
// protocol-v2 `errorKind` field (#1641):
//   InsideTestProc = false marks a failure raised before any [Test] body ran
//     (codeunit instantiation) → AlErrorKind.Setup rather than Runtime.
//   TimedOut = true marks the per-test timeout path. That path deliberately carries
//     NO Exception (the runaway thread is abandoned, nothing is thrown back), so the
//     flag is the only truthful signal that it was a timeout; synthesising a fake
//     exception into Exception would corrupt the expectations classifier's input.
public sealed record TestResult(string Codeunit, string Method, TestOutcome Outcome,
                                string? Message, string? FullException, TimeSpan Duration,
                                string? AlCallStack = null,
                                string? CodeunitDisplayName = null,
                                Exception? Exception = null,
                                Infrastructure.ExpectationResult? Expectation = null,
                                bool InsideTestProc = true,
                                bool TimedOut = false);

public sealed class TestExecutor
{
    private const int DefaultTestTimeoutSeconds = 60;

    public TestIsolation Isolation { get; set; } = TestIsolation.Codeunit;

    /// <summary>
    /// Optional substring filter applied to "Codeunit.Method" and "Codeunit" before
    /// running. Case-insensitive. Null/empty = run everything. Matches if the
    /// filter substring is found in either the codeunit name OR the qualified
    /// "Codeunit.Method" name. Supports a leading '*' wildcard as a no-op for
    /// shell ergonomics (e.g. --test '*Insert*').
    /// </summary>
    public string? TestFilter { get; set; }

    /// <summary>
    /// Per-test timeout, in seconds. v1's `--test-timeout &lt;seconds&gt;` CLI flag
    /// (see #1648); wired from Program.cs. Null = use the
    /// AL_RUNNER_TEST_TIMEOUT_SEC env var if set, else DefaultTestTimeoutSeconds.
    /// Explicit CLI value takes precedence over the env var.
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Expectations manifest (issue #1734). Null = no manifest active, behaviour
    /// unchanged. Non-null: skip-declared tests are never invoked, and every other
    /// result runs through <see cref="Infrastructure.ExpectationClassifier.Classify"/>
    /// here — the one chokepoint the CLI, --watch and --server paths all share — so
    /// reclassification (pass-oos / pass-known-gap / pass-divergence) and manifest-drift failures reach
    /// every counter and exit code identically. Lookup is by the codeunit's AL object
    /// name (the manifest's Microsoft-compatible CodeunitName field), falling back to
    /// the CLR type name ("CodeunitNNNN") when the display name could not be resolved.
    /// </summary>
    public Infrastructure.ExpectationManifest? Expectations { get; set; }

    // #1867: process-lifetime cache of the dependency-assemblies' Install triggers +
    // Company-Initialize (codeunit 2) — the invariant portion of the per-app-group
    // "install-seed" sequence, keyed by InstallTriggerRunner.CurrentDependencySetKey()
    // (the exact ordered set of loaded dependency assemblies, by Module Version ID).
    // #1866 measured this pair at 62.4% + 20.1% = 82.5% of run_ms across 23 runner-extras
    // app groups that all shared the identical 12-assembly dependency closure (issue's own
    // "APP STAGES" table) — every one of the 23 re-executed the SAME MS System-Application
    // Install codeunits (Email Installer, Plan Installer, ...) and re-ran codeunit 2 from
    // scratch.
    //
    // WHY CACHING THE SNAPSHOT IS LOSSLESS — the structural argument, not an appeal to
    // BC's Install-trigger contract:
    //
    // A HIT restores table rows only (RestoreInstallBaselineSnapshot →
    // RecordPatches.InstallBaseline.cs), so at first glance any NON-table side effect a
    // dependency Install trigger left in process-wide state — SingleInstance codeunit
    // instance variables, the shared-object container, write-transaction state, MediaSet /
    // RecordLink / IsolatedStorage entries stashed outside a table row — would not be
    // reproduced on a HIT. That would be a real gap.
    //
    // It isn't one, because every codeunit boundary in this run — INCLUDING the app
    // group's very first codeunit — calls RecordPatches.RestoreInstallBaseline() (see the
    // TestIsolation.Codeunit / TestIsolation.Test branches further down in this file), and
    // that call begins with ResetPerTestState() (RecordPatches.cs), which unconditionally
    // wipes exactly those things: _dataAccessByTable per-table rows,
    // RecordLinkPatches.ResetForTest(), TenantStoragePatches.ResetForTest(),
    // MediaSetPatches.ResetForTest(), ALDatabasePatches.ResetWriteTransactionState(),
    // BcRuntime.DisposeSkeletonSharedObjectContainerChildren(), and
    // BcRuntime.ResetSingleInstanceCache(). So the set of install-seed state that can ever
    // survive to the moment ANY test body runs is exactly
    // {table rows, isolated storage, record links, auto-increment} — precisely the four
    // things InstallBaselineSnapshot captures. A non-table side effect of a dependency
    // Install trigger was already unobservable to every test BEFORE this cache existed;
    // caching the snapshot doesn't create a new gap, it caches the only part of the
    // dependency Install/Company-Initialize output that was ever able to reach a test in
    // the first place.
    //
    // Two supporting facts, both verified rather than assumed: rows are CloneValues-copied
    // (with NavBLOB.DeepCopy for BLOB fields) on BOTH capture (buffer.ToArray()) and
    // restore (new ReadOnlyRecordBuffer(..., CloneValues(values))), so no live row can alias
    // into the process-lifetime snapshot — this matters more here than for the
    // CaptureInstallBaseline singleton this is modelled on, because one aliased row here
    // would corrupt every subsequent app group sharing this dep key, not just one. And the
    // virtual/system metadata tables (Field, AllObj, AllObjWithCaption, table id ≥
    // 2,000,000,000) that grow monotonically as more test assemblies load in-process are not
    // a staleness hazard for a HIT either: GetDataAccessForTableCore re-populates them on
    // EVERY access as an idempotent top-up, so restoring an earlier app group's narrower
    // subset self-corrects on the next read.
    //
    // NOT cached here: the bundle's OWN test assembly's Install triggers
    // (InstallTriggerRunner.RunTestAssemblyOnly, genuinely per-app-group, always re-run) and
    // CaptureInstallBaseline's per-app-group singleton (which layers the bundle's own
    // install-seeded rows on top of whichever dep+company snapshot below was used, and is
    // what RestoreInstallBaseline's per-codeunit/per-test boundary restores — unaffected by
    // this cache either way).
    //
    // Kill switch: set AL_RUNNER_NO_DEP_COMPANY_CACHE=1 to force every lookup to MISS (see
    // the cache-or-compute call site below). Exists for diagnostic blast radius — this cache
    // is process-lifetime and shared across every app group in the process, so it is
    // hypothesis #1 for any future "passes alone, fails in the suite" report, and without a
    // switch the only way to test that hypothesis is a patched rebuild.
    private static readonly Dictionary<string, AlRunner.Patches.RecordPatches.InstallBaselineSnapshot>
        _depCompanyBaselineCache = new();
    private static readonly object _depCompanyBaselineCacheLock = new();

    /// <summary>
    /// Runs every [Test] method in <paramref name="assembly"/>. When
    /// <paramref name="onTestComplete"/> is supplied it fires synchronously right
    /// after each <see cref="TestResult"/> is appended to the returned list — the
    /// hook <c>--server</c>'s streaming <c>runTests</c> uses to emit one NDJSON
    /// <c>test</c> line per completed test instead of waiting for the whole
    /// bundle (see #1641). Null (the CLI's default) is a no-op; behaviour and the
    /// returned list are otherwise unchanged either way.
    ///
    /// <paramref name="cancellationToken"/> is checked cooperatively BETWEEN
    /// tests — before instantiating the next test codeunit and before running the
    /// next [Test] method inside an already-instantiated codeunit — never mid-test
    /// (a test's own AL body is never interrupted). Matches v1's `cancel` command
    /// (#1613): "stop before running the next test," not preemptive abort. The
    /// caller (the <c>--server</c> `runtests` handler) owns the
    /// <see cref="System.Threading.CancellationTokenSource"/> and inspects
    /// <c>IsCancellationRequested</c> after <c>Run</c> returns to decide whether
    /// the summary carries `cancelled:true` — this method does not report that
    /// itself, it only obeys the token. Default is <c>default</c> (never
    /// cancellable), so every existing CLI/non-server caller is unaffected.
    /// </summary>
    public IReadOnlyList<TestResult> Run(Assembly assembly, Action<TestResult>? onTestComplete = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<TestResult>();
        var ctorParam = typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject);
        var filter = NormaliseFilter(TestFilter);
        var typeSw = System.Diagnostics.Stopwatch.StartNew();
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // One unresolvable referenced type (e.g. a dependency whose runtime DLL was not
            // produced) otherwise takes the ENTIRE suite down with an opaque "Unable to load
            // one or more of the requested types". Surface the concrete loader failures (per
            // .claude/rules/loud-failures.md) and continue with the types that DID load — a
            // test codeunit that itself references the missing type will simply not appear.
            types = ex.Types.Where(t => t != null).ToArray()!;
            var reasons = ex.LoaderExceptions
                .Where(e => e != null)
                .Select(e => e!.Message)
                .Distinct()
                .Take(10)
                .ToList();
            Console.Error.WriteLine(
                $"[test-exec] WARNING: {ex.LoaderExceptions.Length} type(s) in the test assembly " +
                $"failed to load; continuing with {types.Length} loadable type(s). Causes:");
            foreach (var r in reasons)
                Console.Error.WriteLine($"    {r}");
        }
        typeSw.Stop();
        PerfTrace.Log($"TestExecutor.GetTypes {types.Length} type(s) {typeSw.ElapsedMilliseconds}ms");
        // #1861: reflecting over the freshly-loaded module's types is one of the issue's
        // named candidates for the flat per-app-group tax. Marked directly (not via
        // PhaseLog.AppStage's `using`) because the Stopwatch above already spans the
        // try/catch and re-timing it would double the cost of GetTypes itself.
        AlRunner.Infrastructure.PhaseLog.AddAppStage("type-discovery", typeSw.Elapsed);

        // Model a freshly-installed bundle: register this assembly with the
        // install-trigger runner and fire every loaded app's Subtype=Install
        // codeunit triggers (dep apps first, this bundle last) BEFORE the first
        // test. With Disabled isolation there is no reset below, so this initial
        // firing is the only seeding; for Codeunit/Test isolation the seed is
        // re-applied after every store reset (see below) because the runner's
        // reset wipes the store instead of rolling back to the committed
        // install-seeded baseline real BC restores.
        var seedSw = System.Diagnostics.Stopwatch.StartNew();
        // A TestExecutor instance is reused across bundles. Discard the preceding bundle's
        // final test mutations before creating this bundle's committed installation baseline.
        //
        // #1861 follow-up review: the original single "install-seed" mark wrapped all six
        // calls below and carried 85.1% of run_ms in the PR's own measurement — an opaque
        // span relabelled one level in, not a breakdown. Each call now gets its own
        // AppStage mark (exclusive of the others; no parent mark is emitted alongside them,
        // so nothing here double-counts) so a follow-up fix knows which of the six to chase
        // instead of re-running this whole attribution exercise.
        using (AlRunner.Infrastructure.PhaseLog.AppStage("install-seed-reset-per-test"))
            AlRunner.Patches.RecordPatches.ResetPerTestState();
        using (AlRunner.Infrastructure.PhaseLog.AppStage("install-seed-reset-for-new-bundle"))
            CompanyInitializer.ResetForNewBundle();
        using (AlRunner.Infrastructure.PhaseLog.AppStage("install-seed-set-test-assembly"))
            InstallTriggerRunner.SetTestAssembly(assembly);
        // #1867: install-seed-run-install-triggers + install-seed-ensure-company-initialized
        // were 62.4% + 20.1% = 82.5% of run_ms (#1866's own APP STAGES measurement), and both
        // are re-executing the SAME dependency assemblies' Install triggers / the SAME
        // codeunit 2 body every app group even though the dependency closure had not changed.
        // The dep+company baseline cache field doc above has the full justification; this is
        // just the cache-or-compute call site. Install triggers do not create a company's
        // baseline rows — company CREATION does, via codeunit 2 "Company-Initialize" — so on a
        // miss it still runs right after the dependency triggers, before the snapshot is taken,
        // exactly matching the order the uncached path always ran in.
        using (AlRunner.Infrastructure.PhaseLog.AppStage("install-seed-dep-company-baseline"))
        {
            var depKey = InstallTriggerRunner.CurrentDependencySetKey();
            AlRunner.Patches.RecordPatches.InstallBaselineSnapshot? cached;
            // Permanent kill switch (see the field's doc comment above for why it exists):
            // forces every lookup to MISS, as if the cache were never populated, so the
            // fresh-computation path can always be re-run on demand for diagnosis or to
            // re-verify the speedup without a patched rebuild.
            if (Environment.GetEnvironmentVariable("AL_RUNNER_NO_DEP_COMPANY_CACHE") == "1")
                cached = null;
            else
                lock (_depCompanyBaselineCacheLock)
                    _depCompanyBaselineCache.TryGetValue(depKey, out cached);
            if (cached != null)
            {
                AlRunner.Patches.RecordPatches.RestoreInstallBaselineSnapshot(cached);
                // #1867 proving-test hook: a stable, directly-assertable signal that this app
                // group reused a prior computation instead of re-running dependency Install
                // triggers + Company-Initialize. See InstallSeedDepCompanyCacheTests.
                PerfTrace.Log($"InstallBaseline.DepCompanyCache HIT {depKey[..Math.Min(8, depKey.Length)]}");
            }
            else
            {
                InstallTriggerRunner.RunDependenciesOnly();
                CompanyInitializer.EnsureCompanyInitialized();
                var snapshot = AlRunner.Patches.RecordPatches.CaptureInstallBaselineSnapshot();
                lock (_depCompanyBaselineCacheLock)
                    _depCompanyBaselineCache[depKey] = snapshot;
                PerfTrace.Log($"InstallBaseline.DepCompanyCache MISS {depKey[..Math.Min(8, depKey.Length)]}");
            }
        }
        // Genuinely per-app-group — the bundle's own Install codeunits (if any) are never
        // shared across app groups, so this always runs fresh, cache or no cache.
        using (AlRunner.Infrastructure.PhaseLog.AppStage("install-seed-run-own-install-triggers"))
            InstallTriggerRunner.RunTestAssemblyOnly();
        using (AlRunner.Infrastructure.PhaseLog.AppStage("install-seed-capture-baseline"))
            AlRunner.Patches.RecordPatches.CaptureInstallBaseline();
        seedSw.Stop();
        PerfTrace.Log($"TestExecutor.InitialInstallSeed {seedSw.ElapsedMilliseconds}ms");

        long scanMs = 0, instMs = 0, dispMs = 0, methodsMs = 0, disposeMs = 0, methodLoopMs = 0;   // PERF attribution accumulators
        long injectMs = 0, resetMs = 0;   // #1861 app-stage accumulators, same shape as the above
        var stageSw = new System.Diagnostics.Stopwatch();
        foreach (var t in types)
        {
            // Cooperative cancellation: stop before instantiating the next test
            // codeunit. See the Run() doc comment — never mid-test.
            if (cancellationToken.IsCancellationRequested) break;

            stageSw.Restart();
            var isTestCu = IsTestCodeunit(t);
            scanMs += stageSw.ElapsedMilliseconds;
            if (!isTestCu) continue;
            if (filter != null && !CodeunitMatchesFilter(t, filter)) continue;

            // W-8b A-prime: this assembly may contain AL [EventSubscriber] codeunits whose
            // classes weren't in AppDomain when PopulateNclMetadataCache initially ran
            // EventSubscriberPatches.InjectAll. Re-run injection now (idempotent — each
            // subscriber MethodInfo is injected at most once).
            var injectSw = System.Diagnostics.Stopwatch.StartNew();
            AlRunner.Patches.EventSubscriberPatches.InjectAllUsingStoredLookup();
            injectSw.Stop();
            injectMs += injectSw.ElapsedMilliseconds;
            PerfTrace.Log($"EventSubscriber.InjectAllUsingStoredLookup {t.Name} {injectSw.ElapsedMilliseconds}ms");

            // Per-codeunit reset: BC's 130450 "Test Runner - Isol. Codeunit" wraps
            // the whole codeunit in one transaction, so tests inside share state but
            // each NEW codeunit starts fresh.
            if (Isolation == TestIsolation.Codeunit)
            {
                var resetSw = System.Diagnostics.Stopwatch.StartNew();
                AlRunner.Patches.RecordPatches.RestoreInstallBaseline();
                resetSw.Stop();
                resetMs += resetSw.ElapsedMilliseconds;
                PerfTrace.Log($"TestExecutor.CodeunitBoundary {t.Name} restore={resetSw.ElapsedMilliseconds}ms t={totalSw.ElapsedMilliseconds}ms");
            }

            object? instance;
            PerfTrace.Log($"TestExecutor.Instantiate START {t.Name}");
            stageSw.Restart();
            try { instance = InstantiateCodeunit(t); }
            catch (Exception ex)
            {
                // InsideTestProc: false — instantiation blew up, so no [Test] body
                // ever ran. That is what makes this a `setup` errorKind on the wire
                // rather than a test-runtime failure (see ErrorClassifier).
                var ctorResult = new TestResult(t.Name, "<ctor>", TestOutcome.Error,
                    Unwrap(ex).Message, ex.ToString(), TimeSpan.Zero,
                    Exception: Unwrap(ex), InsideTestProc: false);
                results.Add(ctorResult);
                onTestComplete?.Invoke(ctorResult);
                continue;
            }
            instMs += stageSw.ElapsedMilliseconds;
            PerfTrace.Log($"TestExecutor.Instantiate END {t.Name} {stageSw.ElapsedMilliseconds}ms");
            if (instance == null) continue;

            // Resolve the AL object name (e.g. "Test Table Event Dispatch") off the
            // instantiated codeunit — it derives from NavApplicationObjectBase and exposes
            // a public ObjectName property. Falls back to the .NET type name on failure.
            stageSw.Restart();
            var displayName = ResolveDisplayName(instance, t.Name);
            dispMs += stageSw.ElapsedMilliseconds;

            var loopSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                foreach (var m in OrderTestMethodsBySourceDeclaration(t))
                {
                    // Cooperative cancellation: stop before running the next test
                    // method inside this already-instantiated codeunit.
                    if (cancellationToken.IsCancellationRequested) break;

                    if (!IsTestMethod(m)) continue;
                    if (filter != null && !MethodMatchesFilter(t.Name, m.Name, filter)) continue;
                    var entry = LookupExpectation(t.Name, displayName, m.Name);
                    if (entry is { Mode: Infrastructure.ExpectationMode.Skip })
                    {
                        // skip = "must not be invoked" (docs/expectations.md), so the
                        // decision has to sit HERE, before the body runs — a post-run
                        // classification could only hide the result, not the side effects.
                        var skippedResult = new TestResult(t.Name, m.Name, TestOutcome.Skipped,
                            $"skipped — declared in {entry.SourceFile}", null, TimeSpan.Zero,
                            null, displayName, null, Infrastructure.ExpectationResult.Skipped);
                        results.Add(skippedResult);
                        onTestComplete?.Invoke(skippedResult);
                        continue;
                    }
                    stageSw.Restart();
                    var raw = RunOne(t.Name, m, instance, displayName);
                    methodsMs += stageSw.ElapsedMilliseconds;
                    var result = Expectations != null
                        ? ApplyExpectation(raw, displayName, entry)
                        : raw;
                    results.Add(result);
                    onTestComplete?.Invoke(result);
                    // Timeout is judged on the RAW outcome: even if a manifest entry
                    // reclassifies the hung test, its runaway thread still poisons the
                    // process, so the suite must stop either way.
                    if (IsTimeout(raw))
                        return results;
                }
                methodLoopMs += loopSw.ElapsedMilliseconds;
            }
            finally
            {
                // MEMORY LEAK FIX: InstantiateCodeunit parents every test codeunit
                // instance to the process-wide BcRuntime.RootTreeStub (ITreeObject ctor →
                // TreeHandler.CreateTreeHandler → parentHandler.InternalAddChild), which
                // permanently links it into RootTreeStub's child chain unless disposed.
                // With one instance retained per test codeunit for the life of the
                // process, this is a real (if smaller than the install-trigger
                // amplification — see InstallTriggerRunner.RunAll) base leak. Nothing
                // needs this instance once its test methods have all run, so dispose it
                // here to unlink it from RootTreeStub (TreeHandler.Dispose() →
                // InternalRemoveChild).
                stageSw.Restart();
                (instance as IDisposable)?.Dispose();
                disposeMs += stageSw.ElapsedMilliseconds;
            }
        }
        totalSw.Stop();
        PerfTrace.Log($"TestExecutor stages scan={scanMs}ms instantiate={instMs}ms displayName={dispMs}ms runOneOuter={methodsMs}ms dispose={disposeMs}ms methodLoop={methodLoopMs}ms");
        PerfTrace.Log($"TestExecutor total {results.Count} test(s) {totalSw.ElapsedMilliseconds}ms");
        // #1861: hand the same per-loop accumulators PerfTrace has always logged
        // (unstructured, easy to miss under CI's noise) to the phase log too, so the
        // per-app-group sub-stage report can attribute run_ms instead of leaving it as
        // one opaque span. "run-test-methods" is the ONE stage here that is genuine
        // per-test workload, not a flat tax — it is what the issue's 18.3s summed-PASS-
        // duration figure roughly reconciles against; every other mark below is exactly
        // the kind of cost the issue is hunting: paid once per app group, independent of
        // how much test content the group holds.
        AlRunner.Infrastructure.PhaseLog.AddAppStage("codeunit-scan", TimeSpan.FromMilliseconds(scanMs));
        AlRunner.Infrastructure.PhaseLog.AddAppStage("event-subscriber-inject", TimeSpan.FromMilliseconds(injectMs));
        AlRunner.Infrastructure.PhaseLog.AddAppStage("codeunit-reset", TimeSpan.FromMilliseconds(resetMs));
        AlRunner.Infrastructure.PhaseLog.AddAppStage("codeunit-instantiate", TimeSpan.FromMilliseconds(instMs));
        AlRunner.Infrastructure.PhaseLog.AddAppStage("resolve-display-name", TimeSpan.FromMilliseconds(dispMs));
        AlRunner.Infrastructure.PhaseLog.AddAppStage("run-test-methods", TimeSpan.FromMilliseconds(methodsMs));
        AlRunner.Infrastructure.PhaseLog.AddAppStage("codeunit-dispose", TimeSpan.FromMilliseconds(disposeMs));
        return results;
    }

    private Infrastructure.ExpectationEntry? LookupExpectation(
        string typeName, string displayName, string method)
    {
        if (Expectations == null) return null;
        // Manifest entries hold the AL object name (Microsoft's CodeunitName field);
        // the CLR type name ("CodeunitNNNN") is the fallback identity when display-name
        // resolution failed, so honour entries written against either.
        return Expectations.Lookup(displayName, method)
            ?? (displayName != typeName ? Expectations.Lookup(typeName, method) : null);
    }

    private static TestResult ApplyExpectation(
        TestResult raw, string displayName, Infrastructure.ExpectationEntry? entry)
    {
        // The whole exception chain is handed to the classifier: it recognises both
        // the typed RunnerOutOfScopeException (wherever BC's error machinery
        // wrapped it) and the `out-of-scope: <api> — <reason>` message convention
        // that Cecil-injected throw sites carry (#1743).
        var observed = new Infrastructure.TestOutcome(displayName, raw.Method,
            raw.Outcome == TestOutcome.Pass, raw.Exception);
        var classified = Infrastructure.ExpectationClassifier.Classify(observed, entry);
        switch (classified.Result)
        {
            case Infrastructure.ExpectationResult.Pass:
            case Infrastructure.ExpectationResult.Fail:
                return raw;   // no entry, no drift — untouched
            case Infrastructure.ExpectationResult.PassOos:
            case Infrastructure.ExpectationResult.PassKnownGap:
            case Infrastructure.ExpectationResult.PassDivergence:
                // Counts as a pass everywhere (totals, exit code, --strict), reported
                // distinctly via Expectation so the summary can subdivide.
                return raw with { Outcome = TestOutcome.Pass, Expectation = classified.Result };
            case Infrastructure.ExpectationResult.FailManifestDrift:
                return raw with
                {
                    Outcome = TestOutcome.Fail,
                    Expectation = classified.Result,
                    Message = raw.Message == null
                        ? classified.Diagnostic
                        : $"{classified.Diagnostic} (original result: {raw.Message})",
                };
            default:
                throw new InvalidOperationException(
                    $"Unhandled ExpectationResult: {classified.Result}");
        }
    }

    private static string? NormaliseFilter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var f = raw.Trim();
        // Strip a leading '*' wildcard (shell ergonomics). Internal '*' is treated
        // as a literal '*' — we don't implement true glob matching here.
        if (f.StartsWith("*")) f = f[1..];
        if (f.EndsWith("*")) f = f[..^1];
        return f.Length == 0 ? null : f.ToLowerInvariant();
    }

    private static bool CodeunitMatchesFilter(Type t, string filterLower)
    {
        // Match if the filter hits the codeunit name OR any test method inside.
        // We can't cheaply enumerate methods twice, so we accept on codeunit-level
        // here and let the method-level check filter the rest below.
        if (t.Name.ToLowerInvariant().Contains(filterLower)) return true;
        return t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(IsTestMethod)
                .Any(m => MethodMatchesFilter(t.Name, m.Name, filterLower));
    }

    private static bool MethodMatchesFilter(string codeunit, string method, string filterLower)
    {
        var qualified = $"{codeunit}.{method}".ToLowerInvariant();
        return qualified.Contains(filterLower) || method.ToLowerInvariant().Contains(filterLower);
    }

    private static bool IsTestCodeunit(Type t)
    {
        if (!t.Name.StartsWith("Codeunit")) return false;
        // Has any method tagged with NavTest attribute?
        return t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(IsTestMethod);
    }

    private static bool IsTestMethod(MethodInfo m) =>
        m.GetCustomAttributes(inherit: false)
         .Any(a => a.GetType().Name is "NavTestAttribute" or "TestAttribute");

    // ── AL source declaration order ────────────────────────────────────────────
    //
    // BC's own AL compiler — which we must not rewrite (.claude/rules/precompiled-dll-
    // respect.md) — does not preserve AL source order in the emitted MethodDef table: a
    // codeunit whose AL source declares test A before test B can (and empirically does)
    // compile to IL where B's token precedes A's, because the compiler alphabetizes
    // members. Real BC's test framework runs [Test] procedures in AL SOURCE declaration
    // order, not compiled-metadata order, and AL test-writing conventions assume it
    // (Initialize() re-seeding at the top of a codeunit, an early test committing a
    // baseline a later test relies on, etc.) — see StefanMaron/BusinessCentral.AL.Runner#1766.
    // Running in reflection order silently reorders those dependencies and produces
    // order-dependent divergence from real BC that has nothing to do with the (correct,
    // already-implemented — see RecordPatches.TransactionSnapshot) asserterror rollback
    // mechanism itself.
    //
    // The AL compiler still records the true declaration position even though it does not
    // preserve it in method order: every compiled procedure gets its own nested
    // "{MethodName}_Scope_<hash>" type carrying a SignatureSpanAttribute whose EncodedSpan
    // holds the absolute source line the procedure's own `procedure` keyword sits on — the
    // same metadata AlCallStackCapture already decodes for stack-trace line numbers. Sorting
    // by that line recovers true declaration order without touching the compiler's own
    // (unmodifiable) member ordering.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo[]> _sourceOrderCache = new();
    private static Type? _signatureSpanAttrType;
    private static bool _signatureSpanAttrTypeResolved;
    private static readonly System.Text.RegularExpressions.Regex _scopeTypeSuffix =
        new(@"_Scope_+\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="t"/>'s public instance methods ordered by AL source
    /// declaration line where resolvable. Falls back to reflection order for any method
    /// whose scope type or span attribute can't be found — never worse than the previous
    /// (pure-reflection) behaviour, only ever more faithful to real BC.
    /// </summary>
    private static MethodInfo[] OrderTestMethodsBySourceDeclaration(Type t) =>
        _sourceOrderCache.GetOrAdd(t, static type =>
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var lineByMethod = ResolveSignatureLines(type, methods);
            if (lineByMethod.Count == 0) return methods; // nothing resolved — keep original order
            // Stable: a method whose line we couldn't resolve keeps its relative
            // reflection-order position, sorted after every line we DID resolve.
            return methods
                .Select((m, i) => (m, i, line: lineByMethod.TryGetValue(m, out var l) ? l : int.MaxValue))
                .OrderBy(x => x.line)
                .ThenBy(x => x.i)
                .Select(x => x.m)
                .ToArray();
        });

    private static Dictionary<MethodInfo, int> ResolveSignatureLines(Type codeunitType, MethodInfo[] methods)
    {
        var result = new Dictionary<MethodInfo, int>();
        if (!_signatureSpanAttrTypeResolved)
        {
            _signatureSpanAttrTypeResolved = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                _signatureSpanAttrType = asm.GetType("Microsoft.Dynamics.Nav.Runtime.SignatureSpanAttribute");
                if (_signatureSpanAttrType != null) break;
            }
        }
        var tSig = _signatureSpanAttrType;
        var piSig = tSig?.GetProperty("EncodedSpan");
        if (tSig == null || piSig == null) return result;

        var nested = codeunitType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var m in methods)
        {
            var scopeType = nested.FirstOrDefault(nt =>
                nt.Name.StartsWith(m.Name, StringComparison.Ordinal) &&
                _scopeTypeSuffix.IsMatch(nt.Name[m.Name.Length..]));
            if (scopeType == null) continue;
            var attr = scopeType.GetCustomAttribute(tSig);
            if (attr == null) continue;
            var encoded = (long)(piSig.GetValue(attr) ?? 0L);
            // SignatureSpan layout matches SourceSpan (StructLayout.Explicit, little-endian):
            // from.line occupies bits 48-63 — see AlCallStackCapture.GetRelativeLine.
            result[m] = (ushort)((ulong)encoded >> 48);
        }
        return result;
    }

    // Cached reflection handle for NavApplicationObjectBase.ObjectName (same pattern as
    // AlCallStackCapture._piObjectName). Resolved lazily from the instance's runtime type
    // so we don't hard-depend on the type being loaded at JIT time.
    private static PropertyInfo? _piObjectName;
    private static bool _piObjectNameResolved;

    private static string ResolveDisplayName(object instance, string fallback)
    {
        try
        {
            if (!_piObjectNameResolved)
            {
                _piObjectNameResolved = true;
                var appObjType = typeof(Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase);
                _piObjectName = appObjType.GetProperty("ObjectName",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            if (_piObjectName != null && _piObjectName.GetValue(instance) is string name
                && !string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch { /* fall through to fallback */ }
        return fallback;
    }

    private static object? InstantiateCodeunit(Type t)
    {
        var ctor = t.GetConstructors().FirstOrDefault(c =>
            c.GetParameters().Length == 1 &&
            c.GetParameters()[0].ParameterType.Name == "ITreeObject");
        if (ctor == null) return null;
        return ctor.Invoke(new object[] { BcRuntime.RootTreeStub! });
    }

    private TestResult RunOne(string codeunit, MethodInfo m, object instance, string displayName)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        PerfTrace.Log($"TestExecutor.RunOne START {codeunit}.{m.Name}");
        // Per-test reset only when isolation == Test. For Codeunit / Disabled the
        // reset (if any) happens at codeunit boundaries instead.
        if (Isolation == TestIsolation.Test)
        {
            AlRunner.Patches.RecordPatches.RestoreInstallBaseline();
        }
        // BC's test framework commits between test methods, whatever the isolation mode.
        // That commit is what a rollback inside this test unwinds to, so an asserterror here
        // restores what the PREVIOUS test method left rather than the state the codeunit
        // started with — see RecordPatches.TransactionSnapshot.
        AlRunner.Patches.RecordPatches.MarkCommitPoint();
        // Clear any AL call stack captured from a previous test on this thread.
        AlRunner.Infrastructure.AlCallStackCapture.Clear();
        // Enter BC's own "in test" scope for the duration of this test (mirrors
        // NavTestExecution.EnterTestCodeunit/LeaveTestCodeunit) — see BcRuntime.EnterTestExecutionScope
        // for why: it's what makes NavTenantSettingsHelper.IsSandbox()/IsProduction() (Codeunit 457
        // "Environment Information") report a sandbox during test execution, exactly like real BC.
        BcRuntime.EnterTestExecutionScope(instance, m);
        try
        {
            var args = m.GetParameters().Length == 0 ? Array.Empty<object>() : null;
            if (args == null)
                return new TestResult(codeunit, m.Name, TestOutcome.Error,
                    $"unsupported test signature ({m.GetParameters().Length} params)", null, sw.Elapsed,
                    null, displayName);
            var timeout = TestTimeout();
            var invokeResult = InvokeWithTimeout(() => m.Invoke(instance, args), timeout);
            if (!invokeResult.Completed)
            {
                PerfTrace.Log($"TestExecutor.RunOne TIMEOUT {codeunit}.{m.Name} {sw.ElapsedMilliseconds}ms");
                var alStack = AlRunner.Infrastructure.AlCallStackCapture.CaptureCurrent();
                return new TestResult(codeunit, m.Name, TestOutcome.Error,
                    $"Test exceeded {(int)timeout.TotalSeconds}s timeout.", null, sw.Elapsed, alStack, displayName,
                    TimedOut: true);
            }
            invokeResult.Exception?.Throw();
            // The body succeeded — now BC's own check that every handler the test DECLARED was
            // actually consumed. A handler procedure that no [HandlerFunctions] names is not in
            // the list and is never an error; a declared one whose dialog never came up is.
            BcRuntime.CheckAllHandlersConsumed();
            PerfTrace.Log($"TestExecutor.RunOne PASS {codeunit}.{m.Name} {sw.ElapsedMilliseconds}ms");
            return new TestResult(codeunit, m.Name, TestOutcome.Pass, null, null, sw.Elapsed,
                null, displayName);
        }
        catch (TargetInvocationException tex)
        {
            var inner = Unwrap(tex);
            PerfTrace.Log($"TestExecutor.RunOne FAIL {codeunit}.{m.Name} {sw.ElapsedMilliseconds}ms {inner.GetType().Name}: {inner.Message}");
            var alStack = AlRunner.Infrastructure.AlCallStackCapture.GetCaptured(inner);
            // BC's Assert.* throws specific exception types for test failures.
            // We can't classify Pass/Fail vs Error perfectly without knowing all of them,
            // so for now: any thrown exception is Fail.
            return new TestResult(codeunit, m.Name, TestOutcome.Fail,
                $"{inner.GetType().Name}: {inner.Message}", inner.ToString(), sw.Elapsed, alStack, displayName,
                inner);
        }
        catch (Exception ex)
        {
            PerfTrace.Log($"TestExecutor.RunOne ERROR {codeunit}.{m.Name} {sw.ElapsedMilliseconds}ms {ex.GetType().Name}: {ex.Message}");
            var alStack = AlRunner.Infrastructure.AlCallStackCapture.GetCaptured(ex);
            return new TestResult(codeunit, m.Name, TestOutcome.Error,
                ex.Message, ex.ToString(), sw.Elapsed, alStack, displayName,
                ex);
        }
        finally
        {
            BcRuntime.LeaveTestExecutionScope();
            // Env-gated memory-census diagnostic (AL_RUNNER_MEM_CENSUS=1); no-op when unset — see MemoryCensus.cs.
            MemoryCensus.Log(codeunit, m.Name);
        }
    }

    private TimeSpan TestTimeout()
    {
        // Explicit --test-timeout (via TestExecutor.TimeoutSeconds) wins over the env var,
        // which in turn wins over the hardcoded default. See #1648.
        if (TimeoutSeconds is int explicitSeconds && explicitSeconds > 0)
            return TimeSpan.FromSeconds(explicitSeconds);
        if (int.TryParse(Environment.GetEnvironmentVariable("AL_RUNNER_TEST_TIMEOUT_SEC"), out var seconds)
            && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.FromSeconds(DefaultTestTimeoutSeconds);
    }

    private static (bool Completed, ExceptionDispatchInfo? Exception) InvokeWithTimeout(Action action, TimeSpan timeout)
    {
        ExceptionDispatchInfo? exception = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { exception = ExceptionDispatchInfo.Capture(ex); }
        })
        {
            IsBackground = true,
            Name = "al-runner-test"
        };
        thread.Start();
        return thread.Join(timeout) ? (true, exception) : (false, null);
    }

    // Reads the flag the timeout path sets rather than re-deriving the verdict from
    // the message text: the message is a v1-compatibility STRING contract (see
    // TestTimeoutFlagTests), not a classification channel, and the same fact now has
    // to be answered for protocol-v2's `errorKind` too. One source of truth.
    private static bool IsTimeout(TestResult result) => result.TimedOut;

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException tex && tex.InnerException != null)
            ex = tex.InnerException;
        return ex;
    }
}
