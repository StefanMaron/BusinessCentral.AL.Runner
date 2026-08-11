// NavReportStaticRunModalHookBindingTests — proves the #1771 fix: the static
// NavReport.Run(int, ...) / NavReport.RunModal(int, ...) overloads are Cecil-owned (a real
// managed call into NavReportSync.SyncStaticRun), not the dead JmpHook they used to be.
//
// Root cause (issue #1771): the static overload bodies were Cecil-blanked to a bare `ret`,
// with a SEPARATE JmpHook (AlRunner/Patches/ReportPatches.cs, pre-fix) meant to throw an
// out-of-scope InvalidOperationException on top. JmpHook.Apply silently skips any target
// that is not in NclCecilRewrite.CecilOwned once the (default) Cecil-only runtime disables
// the JmpHook layer — see JmpHook.cs `_disabled`. So the throw never fired: the call fell
// straight through the `ret` and did nothing. `AL_RUNNER_HOOK_AUDIT=1` confirmed all eight
// static Run/RunModal overloads sitting in JmpHook.OrphanedHooks before the fix.
//
// This is deliberately a RUNNER-INTERNAL claim, not a BC-behaviour one: it asserts that OUR
// Cecil rewrite pipeline emits a real call (reachable, not inlined away) and that OUR
// exception contract (RunnerOutOfScopeException, "not-yet-implemented") fires for a report id
// the runner's metadata cannot resolve. Whether real BC executes `Report.RunModal(id, ...)`
// end-to-end is a plain BC-behaviour claim and belongs in the upstream corpus
// (tests/al-language) once it can be verified against a real service tier — see the PR
// description for #1771.
using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

// Loads Ncl types in-process (must share the serial bc-engine collection — see
// BcEngineCollection.cs comment header).
[Collection(BcEngineCollection.Name)]
public class NavReportStaticRunModalHookBindingTests
{
    private readonly BcEngineFixture _engine;

    public NavReportStaticRunModalHookBindingTests(BcEngineFixture engine) => _engine = engine;

    private static Type NavReportType => typeof(ITreeObject).Assembly
        .GetType("Microsoft.Dynamics.Nav.Runtime.NavReport")!;

    [Theory]
    [InlineData("Run")]
    [InlineData("RunModal")]
    public void StaticRunOverloads_AreNotOrphanedHooks(string methodName)
    {
        if (!_engine.Ready) return; // no BC artifacts provisioned — skip, don't fail

        // Every static int-based overload (1..4 params) must be Cecil-owned so a JmpHook
        // registered against it (present in older code, and possibly future
        // AL_RUNNER_ENABLE_JMPHOOK=1 diagnostics) is correctly recognised as redundant
        // rather than silently orphaned.
        foreach (var m in NavReportType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .Where(m => m.Name == methodName && m.GetParameters().Length is >= 1 and <= 4
                                 && m.GetParameters()[0].ParameterType == typeof(int)))
        {
            var key = NclCecilRewrite.Key(m);
            Assert.Contains(key, NclCecilRewrite.CecilOwned);
        }
    }

    [Theory]
    [InlineData("Run", 1)]
    [InlineData("Run", 2)]
    [InlineData("Run", 3)]
    [InlineData("RunModal", 1)]
    [InlineData("RunModal", 2)]
    [InlineData("RunModal", 3)]
    public void StaticRunOverloads_UnresolvableReportId_ThrowsRunnerOutOfScope_NotSilentNoOp(
        string methodName, int arity)
    {
        if (!_engine.Ready) return; // no BC artifacts provisioned — skip, don't fail

        // A report id that exists nowhere (no AL test assembly declares it, so the runner's
        // NCLMetadata cache never learns it) — the one path SyncStaticRun cannot construct a
        // report for. Before the fix this silently did nothing (the JmpHook never fired,
        // BC's Cecil-blanked `ret` ran instead). After the fix it must reach
        // NavReportSync.SyncStaticRun and throw loudly rather than return normally.
        const int unknownReportId = 987654321;

        var m = NavReportType.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.Static, null,
            Enumerable.Repeat(typeof(bool), arity - 1).Prepend(typeof(int)).ToArray(), null)!;
        Assert.NotNull(m);

        var args = new object[arity];
        args[0] = unknownReportId;
        for (int i = 1; i < arity; i++) args[i] = false;

        var tie = Assert.Throws<TargetInvocationException>(() => m.Invoke(null, args));
        var inner = tie.InnerException;
        Assert.NotNull(inner);
        Assert.IsType<RunnerOutOfScopeException>(inner);
        Assert.Contains($"NavReport.Run/RunModal({unknownReportId})", inner!.Message);
        Assert.Contains("not-yet-implemented", inner.Message);
        Assert.StartsWith("out-of-scope: ", inner.Message);
    }

    [Fact]
    public void StaticRun_ReportRunOptionsOverload_ThrowsUnrecognisedShapeOos_NotSilentNoOp()
    {
        if (!_engine.Ready) return; // no BC artifacts provisioned — skip, don't fail

        // The one static Run overload whose first parameter isn't `int` — deliberately NOT
        // routed to SyncStaticRun (the runner has no ReportRunOptions construction path).
        // Before the fix this also silently no-op'd via the same dead JmpHook; after the fix
        // the Cecil-emitted body throws instead, so a caller cannot mistake "unimplemented"
        // for "ran successfully and did nothing".
        var reportRunOptionsType = typeof(ITreeObject).Assembly.GetType(
            "Microsoft.Dynamics.Nav.Types.Report.Base.ReportRunOptions")
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == "Microsoft.Dynamics.Nav.Types.Report.Base.ReportRunOptions");
        if (reportRunOptionsType == null) return; // type not present in this BC version — nothing to assert

        var m = NavReportType.GetMethod("Run",
            BindingFlags.Public | BindingFlags.Static, null, new[] { reportRunOptionsType }, null);
        if (m == null) return; // overload not present in this BC version — nothing to assert

        // The unrecognised-shape branch never dereferences its argument before throwing, so an
        // uninitialized instance is sufficient — this test is about which branch the Cecil
        // rewrite took, not ReportRunOptions construction semantics.
        var dummy = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(reportRunOptionsType);

        var tie = Assert.Throws<TargetInvocationException>(() => m.Invoke(null, new object[] { dummy }));
        var inner = tie.InnerException;
        Assert.NotNull(inner);
        Assert.Contains("out-of-scope: static NavReport.Run (unrecognised overload shape)", inner!.Message);
    }
}
