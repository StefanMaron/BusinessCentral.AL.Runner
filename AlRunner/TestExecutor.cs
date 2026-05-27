// TestExecutor — discovers and runs AL test methods on compiled BC IL.
// AL test convention: codeunit with [SubType=Test], methods with [Test] attribute.
// In emitted C#: codeunits become classes named CodeunitNNNN; test methods carry
// [NavTest] attribute (via NCLAttribute system). We discover by attribute name to
// avoid coupling to specific BC types.
using System.Reflection;

namespace AlRunnerV2;

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
                                string? AlCallStack = null);

public sealed class TestExecutor
{
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
        var results = new List<TestResult>();
        var ctorParam = typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject);
        var filter = NormaliseFilter(TestFilter);

        foreach (var t in assembly.GetTypes())
        {
            if (!IsTestCodeunit(t)) continue;
            if (filter != null && !CodeunitMatchesFilter(t, filter)) continue;

            // W-8b A-prime: this assembly may contain AL [EventSubscriber] codeunits whose
            // classes weren't in AppDomain when PopulateNclMetadataCache initially ran
            // EventSubscriberPatches.InjectAll. Re-run injection now (idempotent — each
            // subscriber MethodInfo is injected at most once).
            AlRunnerV2.Patches.EventSubscriberPatches.InjectAllUsingStoredLookup();

            // Per-codeunit reset: BC's 130450 "Test Runner - Isol. Codeunit" wraps
            // the whole codeunit in one transaction, so tests inside share state but
            // each NEW codeunit starts fresh.
            if (Isolation == TestIsolation.Codeunit)
                AlRunnerV2.Patches.RecordPatches.ResetPerTestState();

            object? instance;
            try { instance = InstantiateCodeunit(t); }
            catch (Exception ex)
            {
                results.Add(new TestResult(t.Name, "<ctor>", TestOutcome.Error,
                    Unwrap(ex).Message, ex.ToString(), TimeSpan.Zero));
                continue;
            }
            if (instance == null) continue;

            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!IsTestMethod(m)) continue;
                if (filter != null && !MethodMatchesFilter(t.Name, m.Name, filter)) continue;
                results.Add(RunOne(t.Name, m, instance));
            }
        }
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

    private static object? InstantiateCodeunit(Type t)
    {
        var ctor = t.GetConstructors().FirstOrDefault(c =>
            c.GetParameters().Length == 1 &&
            c.GetParameters()[0].ParameterType.Name == "ITreeObject");
        if (ctor == null) return null;
        return ctor.Invoke(new object[] { BcRuntime.RootTreeStub! });
    }

    private TestResult RunOne(string codeunit, MethodInfo m, object instance)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Per-test reset only when isolation == Test. For Codeunit / Disabled the
        // reset (if any) happens at codeunit boundaries instead.
        if (Isolation == TestIsolation.Test)
            AlRunnerV2.Patches.RecordPatches.ResetPerTestState();
        // Clear any AL call stack captured from a previous test on this thread.
        AlRunnerV2.Infrastructure.AlCallStackCapture.Clear();
        try
        {
            var args = m.GetParameters().Length == 0 ? Array.Empty<object>() : null;
            if (args == null)
                return new TestResult(codeunit, m.Name, TestOutcome.Error,
                    $"unsupported test signature ({m.GetParameters().Length} params)", null, sw.Elapsed);
            m.Invoke(instance, args);
            return new TestResult(codeunit, m.Name, TestOutcome.Pass, null, null, sw.Elapsed);
        }
        catch (TargetInvocationException tex)
        {
            var inner = Unwrap(tex);
            var alStack = AlRunnerV2.Infrastructure.AlCallStackCapture.GetCaptured();
            // BC's Assert.* throws specific exception types for test failures.
            // We can't classify Pass/Fail vs Error perfectly without knowing all of them,
            // so for now: any thrown exception is Fail.
            return new TestResult(codeunit, m.Name, TestOutcome.Fail,
                $"{inner.GetType().Name}: {inner.Message}", inner.ToString(), sw.Elapsed, alStack);
        }
        catch (Exception ex)
        {
            var alStack = AlRunnerV2.Infrastructure.AlCallStackCapture.GetCaptured();
            return new TestResult(codeunit, m.Name, TestOutcome.Error,
                ex.Message, ex.ToString(), sw.Elapsed, alStack);
        }
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException tex && tex.InnerException != null)
            ex = tex.InnerException;
        return ex;
    }
}
