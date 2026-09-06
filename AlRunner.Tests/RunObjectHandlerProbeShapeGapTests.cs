// RunObjectHandlerProbeShapeGapTests — the three BC-internals guards on the RunObject
// unattended-open path must refuse with the type AL cannot catch.
//
// WHAT THIS PINS
// --------------
// AlRunner/Patches/RunnerPageInstance.ActionRunObject.cs asks BC two questions before it
// decides whether a RunObject action's target is dispatched to a handler or opened
// unattended, and both questions are reflection reads of Ncl internals:
//
//     NavTestExecution.HasTrap(int)                    — is a TestPage.Trap() outstanding?
//     NavTestExecution.FindHandler(…, throwIfNotFound) — is a [PageHandler] bound?
//     NavTestExecution.executingHandlers               — the worklist the probe restores
//
// All three are guarded, and all three originally raised InvalidOperationException with the
// text "Ncl shape changed; do not commit".
//
// WHY THAT TYPE IS WRONG *HERE* SPECIFICALLY
// ------------------------------------------
// These three reads happen during Invoke() — on AL's own call stack, inside whatever
// asserterror the test wrote around the action invoke. MethodScopePatches's replacement for
// NavMethodScope.AssertError rethrows exactly ONE type and catches everything else:
//
//     try { body(); }
//     catch (AlRunner.Infrastructure.BcShapeGapException) { throw; }   // not catchable by AL
//     …
//
// So on a BC build where one of the three members moved, an `asserterror` wrapped around the
// invoke would CATCH the InvalidOperationException and PASS — the exact inverted result
// BcShapeGapException was created for in #2946, recorded as settled in docs/expectations.md.
// The two arms below measure that inversion rather than asserting it from the source.
//
// The precedent the original change cited does not transfer. Every other
// "Ncl shape changed; do not commit" InvalidOperationException in the tree lives under
// AlRunner/Infrastructure/NclCecilRewrite.*, which runs at bootstrap, before any AL statement
// exists and with no asserterror in scope. Nothing there is reachable from AL's call stack.
//
// WHY A SOURCE GUARD AND NOT FAULT INJECTION
// ------------------------------------------
// The two probe methods reflect on `session.TestExecution.GetType()` — a live NavTestExecution
// off a live NavSession — not on a cached static a test can poison the way
// FieldTriggerShapeGapCallSiteTests does. There is no seam to inject a moved member through
// without restructuring the probes, so the call-site claim is structural (same shape as
// BcShapeGapConventionTests.NoReaderOfThePrivateProviderStructure_StillRaisesARetiredType and
// FieldTriggerShapeGapCallSiteTests.AllSeventeenCallSitesStillStand), and the CONSEQUENCE of
// getting it wrong is behavioural and measured here.
//
// It is latent today: HasTrap, FindHandler and executingHandlers all exist on every BC major
// this runner supports (checked on 27.0 and 28.1), so no run reaches these throws. That is
// what makes it worth a guard — nothing else would notice a revert.
using System;
using System.IO;
using System.Linq;
using AlRunner;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class RunObjectHandlerProbeShapeGapTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", ".."   , ".."));

    private static readonly string SitePath = Path.Combine(
        RepoRoot, "AlRunner", "Patches", "RunnerPageInstance.ActionRunObject.cs");

    private const string Surface = "TestPage action 42 on page 60281";

    // ══ 1. The behavioural half — why the type is not interchangeable here ════════════════

    // An InvalidOperationException raised on AL's call stack is CAUGHT by the asserterror
    // replacement, and catching is how an asserterror reports success. So a BC build that
    // moved HasTrap would turn `asserterror InvokeTheAction()` from a FAILURE (real BC runs
    // the action and returns) into a PASS. Returning normally from the call below IS that
    // inverted pass — asserted here so the claim is measured, not argued.
    [Fact]
    public void AssertError_SwallowsAnInvalidOperationException_SoAGuardRaisingOneInvertsTheResult()
    {
        var ex = Record.Exception(() => BcRuntime.NavMethodScope_AssertError(
            null!, () => throw new InvalidOperationException(
                "NavTestExecution.HasTrap(int) not found — Ncl shape changed; do not commit")));

        Assert.Null(ex);   // no throw == the asserterror PASSED on a build where BC's shape moved
    }

    // The same refusal, raised as the type these guards now use, tears through instead — so
    // the asserterror fails and the run reports the shape gap by name.
    [Fact]
    public void AssertError_TearsThrough_ForTheSameRefusalRaisedAsAShapeGap()
    {
        var gap = Assert.Throws<BcShapeGapException>(() => BcRuntime.NavMethodScope_AssertError(
            null!, () => throw new BcShapeGapException(
                Surface,
                "NavTestExecution.HasTrap(int)",
                "method not found — the runner cannot ask BC whether a TestPage.Trap() is "
                + "outstanding for this RunObject target")));

        Assert.Equal(Surface, gap.Surface);
        Assert.Equal("NavTestExecution.HasTrap(int)", gap.Member);
        Assert.StartsWith(BcShapeGapException.Prefix, gap.Message, StringComparison.Ordinal);
    }

    // CONTROL: "tears through" must not be satisfied by an asserterror that rethrows
    // everything. A permanent out-of-scope refusal is still caught, and a body that does not
    // throw at all still fails.
    [Fact]
    public void AssertError_StillCatchesARefusalAndStillFailsOnNoThrow()
    {
        BcRuntime.NavMethodScope_AssertError(
            null!, () => throw new RunnerOutOfScopeException(
                "NavEmail.Send", "email-smtp — no SMTP transport in the runner", "email"));

        var ex = Record.Exception(() => BcRuntime.NavMethodScope_AssertError(null!, () => { }));
        Assert.NotNull(ex);
        Assert.Equal("Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLAssertErrorException",
            ex!.GetType().FullName);
    }

    // ══ 2. The call-site half — the three guards, by member name ═════════════════════════

    [Fact]
    public void NoGuardOnTheRunObjectPathStillRaisesAnAlCatchableType()
    {
        var offenders = File.ReadLines(SitePath)
            .Select((line, i) => (line, n: i + 1))
            .Where(t => t.line.Contains("throw new InvalidOperationException", StringComparison.Ordinal))
            .Select(t => $"RunnerPageInstance.ActionRunObject.cs:{t.n}")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "these guards run on AL's call stack and raise a type an asserterror CATCHES, "
            + "which inverts the test's result on a BC build whose shape moved: "
            + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("NavTestExecution.HasTrap(int)")]
    [InlineData("NavTestExecution.FindHandler")]
    [InlineData("NavTestExecution.executingHandlers")]
    public void EachBcInternalsReadRefusesAsAShapeGap_NamingTheMember(string member)
    {
        var source = File.ReadAllText(SitePath);

        Assert.Contains("throw new AlRunner.Infrastructure.BcShapeGapException(", source,
            StringComparison.Ordinal);
        Assert.Contains(member, source, StringComparison.Ordinal);
    }

    // Exactly three, so adding a fourth BC-internals read without a guard — or dropping one —
    // is visible. The count is the claim; the member names above say which.
    [Fact]
    public void AllThreeGuardsAreStillThere()
    {
        var count = File.ReadLines(SitePath)
            .Count(l => l.Contains("throw new AlRunner.Infrastructure.BcShapeGapException(",
                StringComparison.Ordinal));

        Assert.Equal(3, count);
    }
}
