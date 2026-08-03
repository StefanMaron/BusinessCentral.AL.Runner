// TestExecutor — discovers and runs AL test methods on compiled BC IL.
// AL test convention: codeunit with [SubType=Test], methods with [Test] attribute.
// In emitted C#: codeunits become classes named CodeunitNNNN; test methods carry
// [NavTest] attribute (via NCLAttribute system). We discover by attribute name to
// avoid coupling to specific BC types.
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace AlRunner;

public enum TestOutcome { Pass, Fail, Error }

/// <summary>
/// Test-isolation granularity. Mirrors the BC "Test Runner" codeunits:
///   Codeunit (default, matches 130450 "Test Runner - Isol. Codeunit") — all
///     tests inside a single codeunit share state; reset happens once per CU.
///   Test  (matches 130452 "Test Runner - Isol. Test") — every [Test] gets a
///     fresh in-memory state.
///   Disabled (matches 130453) — no state reset; suite-long sharing.
/// </summary>
public enum TestIsolation { Codeunit, Test, Disabled }

public sealed record TestResult(string Codeunit, string Method, TestOutcome Outcome,
                                string? Message, string? FullException, TimeSpan Duration,
                                string? AlCallStack = null,
                                string? CodeunitDisplayName = null);

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

    public IReadOnlyList<TestResult> Run(Assembly assembly)
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
        AlRunner.Patches.RecordPatches.ResetPerTestState();
        CompanyInitializer.ResetForNewBundle();
        InstallTriggerRunner.SetTestAssembly(assembly);
        InstallTriggerRunner.RunAll();
        // Install triggers do not create a company's baseline rows — company CREATION does,
        // via codeunit 2 "Company-Initialize". Run it before the baseline snapshot so its rows
        // (Company Information, Source Code Setup, …) are part of what every test is restored to.
        CompanyInitializer.EnsureCompanyInitialized();
        AlRunner.Patches.RecordPatches.CaptureInstallBaseline();
        PerfTrace.Log($"TestExecutor.InitialInstallSeed {seedSw.ElapsedMilliseconds}ms");

        long scanMs = 0, instMs = 0, dispMs = 0, methodsMs = 0, disposeMs = 0, methodLoopMs = 0;   // PERF attribution accumulators
        var stageSw = new System.Diagnostics.Stopwatch();
        foreach (var t in types)
        {
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
            PerfTrace.Log($"EventSubscriber.InjectAllUsingStoredLookup {t.Name} {injectSw.ElapsedMilliseconds}ms");

            // Per-codeunit reset: BC's 130450 "Test Runner - Isol. Codeunit" wraps
            // the whole codeunit in one transaction, so tests inside share state but
            // each NEW codeunit starts fresh.
            if (Isolation == TestIsolation.Codeunit)
            {
                var resetSw = System.Diagnostics.Stopwatch.StartNew();
                AlRunner.Patches.RecordPatches.RestoreInstallBaseline();
                PerfTrace.Log($"TestExecutor.CodeunitBoundary {t.Name} restore={resetSw.ElapsedMilliseconds}ms t={totalSw.ElapsedMilliseconds}ms");
            }

            object? instance;
            PerfTrace.Log($"TestExecutor.Instantiate START {t.Name}");
            stageSw.Restart();
            try { instance = InstantiateCodeunit(t); }
            catch (Exception ex)
            {
                results.Add(new TestResult(t.Name, "<ctor>", TestOutcome.Error,
                    Unwrap(ex).Message, ex.ToString(), TimeSpan.Zero));
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
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!IsTestMethod(m)) continue;
                    if (filter != null && !MethodMatchesFilter(t.Name, m.Name, filter)) continue;
                    stageSw.Restart();
                    var result = RunOne(t.Name, m, instance, displayName);
                    methodsMs += stageSw.ElapsedMilliseconds;
                    results.Add(result);
                    if (IsTimeout(result))
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
        return results;
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
                    $"TIMEOUT after {(int)timeout.TotalSeconds}s", null, sw.Elapsed, alStack, displayName);
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
                $"{inner.GetType().Name}: {inner.Message}", inner.ToString(), sw.Elapsed, alStack, displayName);
        }
        catch (Exception ex)
        {
            PerfTrace.Log($"TestExecutor.RunOne ERROR {codeunit}.{m.Name} {sw.ElapsedMilliseconds}ms {ex.GetType().Name}: {ex.Message}");
            var alStack = AlRunner.Infrastructure.AlCallStackCapture.GetCaptured(ex);
            return new TestResult(codeunit, m.Name, TestOutcome.Error,
                ex.Message, ex.ToString(), sw.Elapsed, alStack, displayName);
        }
        finally
        {
            BcRuntime.LeaveTestExecutionScope();
            // Env-gated memory-census diagnostic (AL_RUNNER_MEM_CENSUS=1); no-op when unset — see MemoryCensus.cs.
            MemoryCensus.Log(codeunit, m.Name);
        }
    }

    private static TimeSpan TestTimeout()
    {
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

    private static bool IsTimeout(TestResult result)
        => result.Outcome == TestOutcome.Error
           && result.Message != null
           && result.Message.StartsWith("TIMEOUT after ", StringComparison.Ordinal);

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException tex && tex.InnerException != null)
            ex = tex.InnerException;
        return ex;
    }
}
