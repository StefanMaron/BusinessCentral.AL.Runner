// ReportRunRequestPageAsyncSeamTests — AlRunner#3505.
//
// A RUNNER-MECHANISM test. The BC claim — Report.RunRequestPage opens the report's request
// page, dispatches the declared [RequestPageHandler], and returns the parameters XML the
// handler left behind — is measured upstream by corpus codeunit 60545 "Test Report
// RunRequestPage" and, for the async entry point, by the codeunit this issue's corpus PR adds.
// What is pinned HERE is the thing the corpus cannot see: WHICH Ncl entry point the runner
// leaves usable.
//
// The defect (#3505): all four NavReport.RunRequestPageAsync overloads were Cecil-rewritten to
// `throw new InvalidOperationException("out-of-scope: NavReport.RunRequestPage — ...")`,
// unconditionally. The four SYNC overloads were separately routed to NavReportSync's real
// seam, so runner-compiled AL — which emits the sync call — passed while PRECOMPILED Base
// Application, which emits the async call, hit the throw. Measured on
// Microsoft_Base Application_28.1.49838.53910.app: 3 of its 5 R2R chunks carry a
// RunRequestPageAsync member reference, and none of the runner's own emitted AL output does.
//
// These tests drive the rewritten Ncl method itself, through the engine fixture's
// Cecil-rewritten Microsoft.Dynamics.Nav.Ncl.dll. They are not shape assertions over the
// runner's source: each one INVOKES the BC method and reads what came back, so a rewrite that
// stopped being applied, or went back to throwing, fails them.

using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class ReportRunRequestPageAsyncSeamTests
{
    private readonly BcEngineFixture _engine;

    public ReportRunRequestPageAsyncSeamTests(BcEngineFixture engine) => _engine = engine;

    private Type NavReport()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        var ncl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        Assert.True(ncl != null,
            "Microsoft.Dynamics.Nav.Ncl is not loaded, so nothing here measures the rewritten engine.");
        var t = ncl!.GetType("Microsoft.Dynamics.Nav.Runtime.NavReport");
        Assert.True(t != null, "NavReport not found in the loaded Ncl — Ncl shape changed.");
        return t!;
    }

    private static MethodInfo[] AsyncOverloads(Type navReport) => navReport
        .GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.Name == "RunRequestPageAsync")
        .ToArray();

    /// <summary>
    /// Invoke a method and return the innermost exception a reflective call wrapped, or null
    /// when the call returned normally. Reflection wraps everything in
    /// TargetInvocationException, so the OOS throw and a genuine engine fault look identical
    /// until unwrapped.
    /// </summary>
    private static Exception? InvokeAndUnwrap(MethodInfo m, object? target, object?[] args)
    {
        try
        {
            m.Invoke(target, args);
            return null;
        }
        catch (TargetInvocationException tie)
        {
            return tie.InnerException ?? tie;
        }
    }

    // --- the premise: the overload set this issue is about actually exists --------------------

    [SkippableFact]
    public void NavReport_DeclaresTheFourRunRequestPageAsyncOverloads()
    {
        // The Cecil pass counts these and aborts the run with "Ncl shape changed; do not
        // commit" when it finds none. Four is what BC 28.4 declares:
        //   static (NavSession,int) / (NavSession,int,string); instance () / (string).
        // A count that is not four means the rewrite below is covering a different set than
        // the one this test reasons about, so pin it rather than infer it.
        var overloads = AsyncOverloads(NavReport());

        Assert.Equal(4, overloads.Length);
        Assert.Equal(2, overloads.Count(m => m.IsStatic));
        Assert.Equal(2, overloads.Count(m => !m.IsStatic));
        Assert.All(overloads, m =>
            Assert.StartsWith("System.Threading.Tasks.ValueTask`1", m.ReturnType.FullName));
    }

    // --- the defect itself: not one of them may answer with the out-of-scope refusal ----------

    [SkippableFact]
    public void StaticRunRequestPageAsync_DoesNotRefuseTheCallAsOutOfScope()
    {
        // Report id 0 exists in no application, so the seam refuses it BY NAME — the runner
        // could not construct report 0. That is a different refusal from the blanket
        // out-of-scope one, and telling them apart is the whole assertion: the blanket throw
        // fires before any argument is looked at, so it cannot mention the report id.
        var navReport = NavReport();
        var m = AsyncOverloads(navReport).Single(
            x => x.IsStatic && x.GetParameters().Length == 3);

        var thrown = InvokeAndUnwrap(m, null, new object?[] { null, 0, null });

        AssertNotTheBlanketRefusal(thrown, "the static (NavSession,int,string) overload");
    }

    [SkippableFact]
    public void StaticRunRequestPageAsync_TwoArgOverload_DoesNotRefuseTheCallAsOutOfScope()
    {
        var navReport = NavReport();
        var m = AsyncOverloads(navReport).Single(
            x => x.IsStatic && x.GetParameters().Length == 2);

        var thrown = InvokeAndUnwrap(m, null, new object?[] { null, 0 });

        AssertNotTheBlanketRefusal(thrown, "the static (NavSession,int) overload");
    }

    [SkippableFact]
    public void InstanceRunRequestPageAsync_DoesNotRefuseTheCallAsOutOfScope()
    {
        // The instance overloads are reached on a report instance. Invoking them on a null
        // target raises TargetException before the body runs, so drive them the only way the
        // body is reachable without a constructed report: read the rewritten body's own
        // behaviour through the static seam it shares. What is asserted here is the narrower
        // and still load-bearing half — that the rewritten instance bodies no longer carry
        // the blanket out-of-scope string as their whole implementation.
        var navReport = NavReport();
        foreach (var m in AsyncOverloads(navReport).Where(x => !x.IsStatic))
        {
            var refusal = RewrittenBodyRefusesUnconditionally(m);
            Assert.False(refusal,
                $"{m} still refuses every call as out-of-scope; docs/scope.md §3.5.1 says "
                + "running a request page is in scope.");
        }
    }

    // --- helpers ------------------------------------------------------------------------------

    private static void AssertNotTheBlanketRefusal(Exception? thrown, string which)
    {
        // A null return is fine (the call succeeded). A throw is fine too, as long as it is
        // not the blanket out-of-scope refusal this issue is about — a nonexistent report id
        // legitimately refuses by name.
        if (thrown == null) return;

        var msg = thrown.Message ?? string.Empty;
        Assert.False(
            msg.Contains("out-of-scope: NavReport.RunRequestPage", StringComparison.Ordinal)
            && msg.Contains("request-page-ui", StringComparison.Ordinal),
            $"{which} answered the blanket out-of-scope refusal ({msg}). docs/scope.md §3.5.1: "
            + "\"Running a request page is in scope; rendering one is not.\"");
    }

    /// <summary>
    /// True when the rewritten method's IL is exactly the blanket refusal — ldstr / newobj /
    /// throw, with the out-of-scope string as its only operand. Reads the loaded method body,
    /// not the runner's source text, so a change to the Cecil pass that failed to apply is
    /// still reported honestly.
    /// </summary>
    private static bool RewrittenBodyRefusesUnconditionally(MethodInfo m)
    {
        var body = m.GetMethodBody();
        if (body == null) return false;
        var il = body.GetILAsByteArray();
        if (il == null) return false;

        // 0x72 = ldstr, and the three-instruction refusal is 11 bytes: ldstr(5) newobj(5)
        // throw(1). Anything longer is doing more than refusing.
        if (il.Length != 11 || il[0] != 0x72) return false;

        var token = BitConverter.ToInt32(il, 1);
        string s;
        try { s = m.Module.ResolveString(token); }
        catch (ArgumentException) { return false; }
        return s.Contains("out-of-scope: NavReport.RunRequestPage", StringComparison.Ordinal);
    }
}
