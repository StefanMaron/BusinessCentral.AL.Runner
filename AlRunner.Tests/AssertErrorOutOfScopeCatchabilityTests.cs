// AssertErrorOutOfScopeCatchabilityTests — what AL `asserterror` does with a
// RunnerOutOfScopeException raised inside it.
//
// Why this file exists (issue #2871)
// ----------------------------------
// RunnerOutOfScopeException.cs documented itself as a plain System.Exception "so AL
// `asserterror` cannot swallow it", and three sibling comments repeated the claim. It was
// false: BcRuntime.NavMethodScope_AssertError — the Cecil-bound replacement for
// NavMethodScope::AssertError/1 (NclCecilRewrite.Runtime.cs) — is an unfiltered
// `catch (Exception)`, so a refusal raised inside an `asserterror` block makes that
// asserterror PASS.
//
// The claim survived because nothing named the behaviour as its subject. Several
// runner-extras suites depend on it (tests/runner-extras/table-connection-live-oos,
// tests/runner-extras/date-virtual-table-window), so they WOULD have gone red — but they
// would have gone red saying "the table-connection refusal stopped working", not "refusals
// became untrappable from AL". These tests say the second thing.
//
// Deliberately NOT a behaviour change. Whether AL should be able to swallow an out-of-scope
// refusal is a real design question (.claude/rules/loud-failures.md exists because a green
// test must not lie about what executed) and a maintainer decision. If that decision is ever
// taken, this file is what makes the change visible instead of silent.
//
// Same shape as TryFunctionOutOfScopeTrapTests: call the runner's own helper directly with
// self = null. NavMethodScope_AssertError tolerates a null scope — TryRemapToALException
// catches its own failure, StoreLastExceptionOnSkeletonSession returns early on a null
// session, and RollbackToCommitPoint returns early with no commit point recorded.
using System;
using AlRunner;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class AssertErrorOutOfScopeCatchabilityTests
{
    private static RunnerOutOfScopeException PermanentOos() =>
        new("NavFile.Upload", "browser-roundtrip", "file-storage");

    private static RunnerOutOfScopeException NotYetImplemented() =>
        new("INCLObjectXmlMetadataLoader.GetMetaObjectXmlMetadata",
            "not-yet-implemented — report metadata loader",
            "todo");

    // ── Positive: `asserterror` catches a refusal, so the asserterror passes ──

    [Fact]
    public void AssertError_Passes_WhenBodyThrowsPermanentOutOfScopeRefusal()
    {
        // Returning normally IS the pass signal: NavMethodScope_AssertError throws
        // NavNCLAssertErrorException when the body did NOT error.
        BcRuntime.NavMethodScope_AssertError(null!, () => throw PermanentOos());
    }

    [Fact]
    public void AssertError_Passes_WhenBodyThrowsNotYetImplementedRefusal()
    {
        // Both OOS flavours, unlike TryInvoke — see the asymmetry test below.
        BcRuntime.NavMethodScope_AssertError(null!, () => throw NotYetImplemented());
    }

    // ── Negative: the pass above is not unconditional ──

    [Fact]
    public void AssertError_Fails_WhenBodyDoesNotThrowAtAll()
    {
        var ran = false;
        var ex = Record.Exception(
            () => BcRuntime.NavMethodScope_AssertError(null!, () => { ran = true; }));

        Assert.True(ran, "the asserterror replacement must actually invoke the body");
        Assert.NotNull(ex);
        Assert.Equal(
            "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLAssertErrorException",
            ex!.GetType().FullName);
    }

    [Fact]
    public void AssertError_Fails_WhenBodyThrowsNothingButTouchesTheOosTypeWithoutRaisingIt()
    {
        // Constructing a RunnerOutOfScopeException is not raising one. Guards against a
        // future "asserterror passes if a refusal was constructed anywhere" shortcut.
        var ex = Record.Exception(() => BcRuntime.NavMethodScope_AssertError(
            null!, () => { _ = PermanentOos().Message; }));

        Assert.NotNull(ex);
        Assert.Equal(
            "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLAssertErrorException",
            ex!.GetType().FullName);
    }

    // ── The exception's message is what Assert.ExpectedError matches, and it survives ──

    [Fact]
    public void RefusalMessage_CarriesTheTokensRunnerExtrasTestsMatchOn()
    {
        var message = PermanentOos().Message;

        Assert.StartsWith("out-of-scope: ", message, StringComparison.Ordinal);
        Assert.Contains("out-of-scope: NavFile.Upload", message, StringComparison.Ordinal);
        Assert.Contains("browser-roundtrip", message, StringComparison.Ordinal);
        Assert.Contains("docs/scope.md#file-storage", message, StringComparison.Ordinal);
        Assert.DoesNotContain("NavFile.Download", message, StringComparison.Ordinal);
    }

    // ── The asymmetry with [TryFunction], which is the part that IS deliberate ──

    [Fact]
    public void AssertErrorAndTryInvoke_AgreeOnPermanentOos_AndDisagreeOnNotYetImplemented()
    {
        // Permanent OOS: TryInvoke traps it into `false` (a real BC environment lacking the
        // surface answers false too), and asserterror catches it as well.
        Assert.False(BcRuntime.NavApplicationObjectBase_TryInvoke(
            null, () => throw PermanentOos()));
        BcRuntime.NavMethodScope_AssertError(null!, () => throw PermanentOos());

        // not-yet-implemented: TryInvoke deliberately lets it tear through, so a runner gap
        // can never read as a green `if not TryX() then`. asserterror still catches it —
        // that difference is the point, and it is why "RunnerOutOfScopeException cannot be
        // swallowed" was never a single fact about the type.
        var tore = Record.Exception(() => BcRuntime.NavApplicationObjectBase_TryInvoke(
            null, () => throw NotYetImplemented()));
        Assert.IsType<RunnerOutOfScopeException>(tore);

        BcRuntime.NavMethodScope_AssertError(null!, () => throw NotYetImplemented());
    }
}
