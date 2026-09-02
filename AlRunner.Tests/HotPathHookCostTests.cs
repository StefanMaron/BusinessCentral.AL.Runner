// HotPathHookCostTests — pins the three per-call costs that made a single MS test take
// 568 seconds (#2304).
//
// The three hooks below sit on paths the BC runtime walks millions of times in one test:
//
//   * BcRuntime.ReturnTrue replaces RecordImplementation.get_IsOpen, which
//     NavRecord.GetFieldValue asks for EVERY field read. It formatted an interpolated
//     diagnostic string (including a reflective a.GetType().Name) and pushed it through
//     Console.Error on every call. The line was never even visible: Log's FilteredWriter
//     drops anything starting with a `[Tag]` at default verbosity, so the whole cost —
//     allocation, reflection, a compiled-Regex match and a synchronized writer — bought
//     nothing. Stack sampling caught it directly:
//         SyncTextWriter.WriteLine <- BcRuntime.ReturnTrue <- NavRecord.GetFieldValue
//
//   * BcRuntime.CodeunitEventDispatch_OnRunEventAsync runs on every AL event scope and
//     re-read two debug environment variables per call. Environment.GetEnvironmentVariable
//     was the resolved leaf in 7 of 73 stack samples of that run.
//
//   * DispatchCore and NavRecordRef_get_Target each re-ran
//     AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "…Ncl")
//     per call. RuntimeAssembly.GetName() calls the native GetCodeBase(), so the scan is
//     not cheap; it was the resolved leaf in 4 more samples of the same run.
//
// These tests assert the CONTRACT each fix rests on — no console traffic from the IsOpen
// hook, one environment read per gate, one AppDomain scan per resolved assembly — not a
// timing threshold, which would be flaky and would not say what broke.
using System;
using System.IO;
using Xunit;

namespace AlRunner.Tests;

public sealed class HotPathHookCostTests
{
    // ── RecordImplementation.get_IsOpen replacement ────────────────────────────

    [Fact]
    public void IsOpenHook_ReturnsTrue()
    {
        Assert.True(BcRuntime.ReturnTrue(new object()));
        Assert.True(BcRuntime.ReturnTrue(null));
    }

    [Fact]
    public void HookTracing_IsOffUnlessExplicitlyRequested()
    {
        // AL_RUNNER_TRACE_HOOKS is not set in this process, so the gate must read false. If it
        // ever defaults to true again, every field read pays a formatted console write and
        // every record construction pays another.
        Assert.False(BcRuntime.HookTraceEnabled);
    }

    [Fact]
    public void IsOpenHook_WritesNothingToTheConsole()
    {
        var savedOut = Console.Out;
        var savedErr = Console.Error;
        var sink = new StringWriter();
        try
        {
            Console.SetOut(sink);
            Console.SetError(sink);
            for (var i = 0; i < 25; i++) BcRuntime.ReturnTrue(new object());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }
        Assert.Equal(string.Empty, sink.ToString());
    }

    // ── dispatcher debug gates ─────────────────────────────────────────────────

    [Fact]
    public void DispatcherOffGate_ReadsTheEnvironmentOnceAndKeepsTheAnswer()
    {
        var saved = Environment.GetEnvironmentVariable("AL_RUNNER_DISPATCHER_OFF");
        try
        {
            BcRuntime.ResetDispatchGatesForTests();
            Environment.SetEnvironmentVariable("AL_RUNNER_DISPATCHER_OFF", "1");
            Assert.True(BcRuntime.DispatcherDisabled);

            // Changing the variable afterwards must NOT be observed: that is what proves the
            // value is memoised rather than re-read on every dispatch.
            Environment.SetEnvironmentVariable("AL_RUNNER_DISPATCHER_OFF", "0");
            Assert.True(BcRuntime.DispatcherDisabled);

            BcRuntime.ResetDispatchGatesForTests();
            Assert.False(BcRuntime.DispatcherDisabled);

            Environment.SetEnvironmentVariable("AL_RUNNER_DISPATCHER_OFF", null);
            BcRuntime.ResetDispatchGatesForTests();
            Assert.False(BcRuntime.DispatcherDisabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_DISPATCHER_OFF", saved);
            BcRuntime.ResetDispatchGatesForTests();
        }
    }

    [Fact]
    public void DispatchTraceGate_ReadsTheEnvironmentOnceAndKeepsTheAnswer()
    {
        var saved = Environment.GetEnvironmentVariable("ALRUNNER_DISPATCH_TRACE");
        try
        {
            BcRuntime.ResetDispatchGatesForTests();
            Environment.SetEnvironmentVariable("ALRUNNER_DISPATCH_TRACE", "1");
            Assert.True(BcRuntime.DispatchTraceEnabled);

            Environment.SetEnvironmentVariable("ALRUNNER_DISPATCH_TRACE", "nope");
            Assert.True(BcRuntime.DispatchTraceEnabled);

            BcRuntime.ResetDispatchGatesForTests();
            Assert.False(BcRuntime.DispatchTraceEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALRUNNER_DISPATCH_TRACE", saved);
            BcRuntime.ResetDispatchGatesForTests();
        }
    }

    // ── loaded-runtime-assembly lookup ─────────────────────────────────────────

    [Fact]
    public void RuntimeAssemblyLookup_ScansTheAppDomainOncePerResolvedAssembly()
    {
        BcRuntime.ResetRuntimeAssemblyCacheForTests();

        // Resolve the runner's own assembly by whatever simple name it actually carries —
        // hardcoding it would make this test about the csproj's AssemblyName, not the cache.
        var self = typeof(BcRuntime).Assembly;
        var selfName = self.GetName().Name!;

        var first = BcRuntime.FindRuntimeAssembly(selfName);
        Assert.Same(self, first);
        Assert.Equal(1, BcRuntime.RuntimeAssemblyScanCount);

        for (var i = 0; i < 10; i++)
            Assert.Same(self, BcRuntime.FindRuntimeAssembly(selfName));

        Assert.Equal(1, BcRuntime.RuntimeAssemblyScanCount);
    }

    [Fact]
    public void RuntimeAssemblyLookup_DoesNotCacheAMiss()
    {
        // Ncl is loaded LATE (Program.cs byte-array-loads the Cecil-rewritten copy), so a
        // negative answer must never be frozen — a cached miss would permanently break every
        // caller that resolves it before the load.
        BcRuntime.ResetRuntimeAssemblyCacheForTests();

        Assert.Null(BcRuntime.FindRuntimeAssembly("No.Such.Assembly.Exists"));
        Assert.Equal(1, BcRuntime.RuntimeAssemblyScanCount);
        Assert.Null(BcRuntime.FindRuntimeAssembly("No.Such.Assembly.Exists"));
        Assert.Equal(2, BcRuntime.RuntimeAssemblyScanCount);
    }

    // ── NavMethodScope ctor: GetMethodScopeFlags lookup ───────────────────────

    private sealed class ScopeWithFlags
    {
        // Same shape the real NavMethodScope subtypes have: a non-public instance method.
        private int GetMethodScopeFlags() => 42;
    }

    private sealed class ScopeWithoutFlags
    {
    }

    [Fact]
    public void MethodScopeFlagsLookup_HappensOncePerScopeType()
    {
        BcRuntime.ResetGetMethodScopeFlagsCacheForTests();

        var first = BcRuntime.ResolveGetMethodScopeFlags(typeof(ScopeWithFlags));
        Assert.NotNull(first);
        Assert.Equal("GetMethodScopeFlags", first!.Name);
        Assert.Equal(42, first.Invoke(new ScopeWithFlags(), null));
        Assert.Equal(1, BcRuntime.GetMethodScopeFlagsLookupCount);

        for (var i = 0; i < 20; i++)
            Assert.Same(first, BcRuntime.ResolveGetMethodScopeFlags(typeof(ScopeWithFlags)));
        Assert.Equal(1, BcRuntime.GetMethodScopeFlagsLookupCount);

        // A second type costs exactly one more lookup, not one per call.
        Assert.Null(BcRuntime.ResolveGetMethodScopeFlags(typeof(ScopeWithoutFlags)));
        Assert.Equal(2, BcRuntime.GetMethodScopeFlagsLookupCount);
        Assert.Null(BcRuntime.ResolveGetMethodScopeFlags(typeof(ScopeWithoutFlags)));
        Assert.Equal(2, BcRuntime.GetMethodScopeFlagsLookupCount);
    }

    [Fact]
    public void RuntimeAssemblyLookup_RequireNamesTheMissingAssembly()
    {
        BcRuntime.ResetRuntimeAssemblyCacheForTests();
        var ex = Assert.Throws<InvalidOperationException>(
            () => BcRuntime.RequireRuntimeAssembly("No.Such.Assembly.Exists"));
        Assert.Contains("No.Such.Assembly.Exists", ex.Message);
    }
}
