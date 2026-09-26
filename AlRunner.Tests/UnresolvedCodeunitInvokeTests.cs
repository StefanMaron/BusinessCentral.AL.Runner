// UnresolvedCodeunitInvokeTests — #3516.
//
// What is pinned here
// -------------------
// A codeunit whose id falls in the system range (1-9999) or the test-toolkit range
// (130000-139999) and whose CLR type the runner cannot resolve gets a NoOpCodeunit from
// BcRuntime.NavCodeunitHandle_CreateTarget. That is a deliberate contract for
// `Codeunit.Run(<id>)`, and it stays: the archived bucket-1 test
// `codeunit-runtime/128-codeunit-not-found` asserts a missing test-toolkit codeunit runs
// as a silent no-op, and Microsoft's BaseApp buckets depend on it.
//
// NoOpCodeunit declares no members, so any AL call that is NOT the OnRun trigger — an
// ordinary `procedure` on a codeunit VARIABLE — reached NavApplicationObjectBase.OnInvoke,
// whose entire unmodified body is `throw new NavNCLMissingMethodException(..., methodId)`.
// The message a user got named neither the codeunit nor the remedy:
//
//   NavNCLMissingMethodException: Function ID -226019983 was called. The object with ID 0
//   does not have a member with that ID.
//
// That is a silent-scope failure wearing BC's clothes (loud-failures.md): the runner knew
// exactly which codeunit it had failed to load and threw away that knowledge, leaving BC to
// report an object id it never had. Measured at 35 failures on the Microsoft BaseApp
// surface, 19 of them in tests Microsoft runs (issue #3516, run 34169134540).
//
// Why this is a runner-side test rather than an AL corpus test
// -----------------------------------------------------------
// The state under test — "a codeunit reference resolved at COMPILE time against a symbol
// package whose runtime code is not loaded" — cannot exist on a BC service tier: an app is
// either installed, in which case its code is present, or it is not, in which case the AL
// does not compile. There is no AL a corpus test could write that reaches it. The
// corpus-expressible sibling claims (what real BC does with an unassigned codeunit variable,
// and with a variable whose codeunit exists) are asserted upstream in corpus codeunit 60966
// "Test Codeunit Var Dispatch", over fixture 60959 ALTDispatchProbe; see the PR body for the
// verdict.
//
// The specific method id used below is not meaningful: NoOpCodeunit declares no members, so
// EVERY method id is unhandled, which is the whole point.

using System;
using System.Reflection;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// A codeunit that IS resolvable, used as the control: the fix must not change what happens
/// when the runner finds the type. Named <c>Codeunit69004</c> because
/// <c>BcRuntime.FindCodeunitType</c> resolves an id to the type literally named
/// <c>Codeunit{id}</c>; 69004 is outside every AL idRange this repository declares, and
/// outside both no-op ranges, so it can neither collide with a compiled AL object nor be
/// served a NoOpCodeunit.
/// </summary>
internal sealed class Codeunit69004 : NavCodeunit
{
    public Codeunit69004(ITreeObject parent) : base(parent, 69004) { }
}

// Loads Ncl types in-process — see BcEngineCollection.cs.
[Collection(BcEngineCollection.Name)]
public class UnresolvedCodeunitInvokeTests
{
    // In the test-toolkit no-op range (130000-139999) and not a type this assembly or any
    // loaded dependency declares, so CreateTarget takes the NoOpCodeunit branch.
    private const int UnresolvableToolkitId = 139994;

    // In the system no-op range (1-9999), same property. The two ranges are separate
    // conditions in the same `if`, so a fix that covered one and not the other would look
    // complete from either alone.
    private const int UnresolvableSystemId = 9994;

    // Resolvable: Codeunit69004 above.
    private const int ResolvableId = 69004;

    private readonly BcEngineFixture _engine;

    public UnresolvedCodeunitInvokeTests(BcEngineFixture engine) => _engine = engine;

    private ITreeObject Root()
    {
        var root = BcRuntime.RootTreeStub;
        Assert.True(root != null,
            "BcRuntime.RootTreeStub is null after the engine bootstrap — the skeleton tree these " +
            "handles must be parented on does not exist, so this test cannot run at all.");
        return root!;
    }

    private static Exception Unwrap(Exception ex)
        => ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    /// <summary>
    /// The defect, on the sync dispatch path — the one the issue's stack trace shows
    /// (NavApplicationObjectBase.Invoke -> OnInvoke).
    ///
    /// Asserts the CONTENT, not merely that something throws: an unresolved codeunit threw
    /// before this fix too. What was missing is the codeunit id the runner knew and the
    /// remedy the user can act on, so those are what the assertions read.
    /// </summary>
    [SkippableFact]
    public void InvokingAProcedureOnAnUnresolvedToolkitCodeunit_NamesTheCodeunitAndTheRemedy()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var target = new NavCodeunitHandle(Root(), UnresolvableToolkitId).Target;

        var ex = Unwrap(Assert.ThrowsAny<Exception>(() => target.Invoke(1234, Array.Empty<object>())));

        // The id the runner failed to resolve. BC's own message names object id 0, which is
        // the value that made the 35 failures unattributable.
        Assert.Contains(UnresolvableToolkitId.ToString(), ex.Message);

        // The remedy. BuildMissingCodeunitMessage already says this for an out-of-range id;
        // the no-op ranges reached BC's message instead and said none of it.
        Assert.Contains("--package-cache", ex.Message);
        Assert.Contains("al-runner provision", ex.Message);

        // The wrong id must not survive in the text: "object with ID 0" is precisely the
        // claim that sent 35 failures to a dead end.
        Assert.DoesNotContain("object with ID 0", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The system range (1-9999) is the OTHER half of the same `if`. Separate arm so a fix
    /// covering one range only cannot read as complete.
    /// </summary>
    [SkippableFact]
    public void InvokingAProcedureOnAnUnresolvedSystemCodeunit_NamesTheCodeunitAndTheRemedy()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var target = new NavCodeunitHandle(Root(), UnresolvableSystemId).Target;

        var ex = Unwrap(Assert.ThrowsAny<Exception>(() => target.Invoke(1234, Array.Empty<object>())));

        Assert.Contains(UnresolvableSystemId.ToString(), ex.Message);
        Assert.Contains("--package-cache", ex.Message);
    }

    /// <summary>
    /// The async dispatch path. BC's <c>InvokeAsync</c> routes to <c>OnInvokeAsync</c> when
    /// the receiver is async and to <c>OnInvoke</c> otherwise, and BOTH base bodies throw the
    /// same bare NavNCLMissingMethodException — so overriding only the sync one would fix
    /// half the population while every sync-path test stayed green.
    /// </summary>
    [SkippableFact]
    public void InvokingAsyncOnAnUnresolvedToolkitCodeunit_NamesTheCodeunitAndTheRemedy()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var target = new NavCodeunitHandle(Root(), UnresolvableToolkitId).Target;

        // Reach OnInvokeAsync directly: the public InvokeAsync picks the branch from the
        // receiver's __IsAsync, which a NoOpCodeunit answers false for, so calling the public
        // entry point would only ever exercise the sync override.
        var onInvokeAsync = target.GetType().GetMethod("OnInvokeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(onInvokeAsync != null,
            "NavApplicationObjectBase.OnInvokeAsync not found — Ncl shape changed and this test " +
            "is no longer measuring the async dispatch path.");

        var ex = Unwrap(Assert.ThrowsAny<Exception>(
            () => onInvokeAsync!.Invoke(target, new object[] { 1234, Array.Empty<object>() })));

        Assert.Contains(UnresolvableToolkitId.ToString(), ex.Message);
        Assert.Contains("--package-cache", ex.Message);
    }

    /// <summary>
    /// #4600: both throw sites raise the typed exception carrying the codeunit id, which is what
    /// lets the console point at the Action needed entry for the app declaring it — the sync
    /// and async no-op overrides, and CreateTarget itself for an id outside the no-op ranges.
    /// </summary>
    [SkippableFact]
    public void BothThrowSites_RaiseMissingDependencyCodeunitException_WithTheId()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var target = new NavCodeunitHandle(Root(), UnresolvableToolkitId).Target;
        var viaNoOp = Unwrap(Assert.ThrowsAny<Exception>(() => target.Invoke(1234, Array.Empty<object>())));
        Assert.Equal(UnresolvableToolkitId,
            Assert.IsType<AlRunner.Infrastructure.MissingDependencyCodeunitException>(viaNoOp).CodeunitId);

        const int outOfRange = 69005;   // outside both no-op ranges, and no Codeunit69005 type exists
        var viaCreate = Unwrap(Assert.ThrowsAny<Exception>(() => new NavCodeunitHandle(Root(), outOfRange).Target));
        Assert.Equal(outOfRange,
            Assert.IsType<AlRunner.Infrastructure.MissingDependencyCodeunitException>(viaCreate).CodeunitId);
    }

    /// <summary>
    /// Control 1 — the behaviour that must NOT change: <c>Codeunit.Run</c> on a missing
    /// test-toolkit codeunit stays a silent no-op. That is the contract the archived
    /// bucket-1 test `codeunit-runtime/128-codeunit-not-found` pins and the reason
    /// NoOpCodeunit exists at all; a fix that made every touch of an unresolved codeunit
    /// throw would break Microsoft's BaseApp buckets far more widely than #3516 does.
    ///
    /// Asserts through the same seam AL's `Codeunit.Run` takes —
    /// <c>BcRuntime.ResolveOnRunTrigger</c> is the single resolver every Run call site uses,
    /// and invoking what it returns is exactly what <c>NavCodeunit_RunCodeunit</c> does next.
    /// </summary>
    [SkippableFact]
    public void RunningAnUnresolvedToolkitCodeunit_IsStillASilentNoOp()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var target = new NavCodeunitHandle(Root(), UnresolvableToolkitId).Target;

        var trigger = BcRuntime.ResolveOnRunTrigger(target.GetType());
        Assert.True(trigger != null,
            "ResolveOnRunTrigger found no OnRun on a NoOpCodeunit — Codeunit.Run would then " +
            "do nothing for a reason this test is not measuring.");

        // The no-op contract: running it raises nothing. If this throws, the fix has
        // overreached from 'a procedure call' to 'any touch', which would break Microsoft's
        // BaseApp buckets far more widely than #3516 does.
        var ex = Record.Exception(() => BcRuntime.AwaitIfTask(
            trigger!.GetParameters().Length == 1
                ? trigger.Invoke(target, new object?[] { null })
                : trigger.Invoke(target, null)));
        Assert.Null(ex);
    }

    /// <summary>
    /// Control 2 — a codeunit the runner CAN resolve is untouched. Its dispatch still goes
    /// to BC's own OnInvoke, which throws BC's own NavNCLMissingMethodException for a method
    /// id the type does not declare. Codeunit69004 declares none, so that is every id.
    ///
    /// This is the arm that keeps the fix from being "throw the runner's message whenever a
    /// method id is unknown": a resolvable codeunit with a genuinely missing member is a
    /// different fact and must keep reporting as one.
    /// </summary>
    [SkippableFact]
    public void InvokingAnUnknownMethodOnARESOLVEDCodeunit_StillReportsBcsOwnMissingMethod()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var target = new NavCodeunitHandle(Root(), ResolvableId).Target;
        Assert.IsType<Codeunit69004>(target);

        var ex = Unwrap(Assert.ThrowsAny<Exception>(() => target.Invoke(1234, Array.Empty<object>())));

        // BC's message, not the runner's: this codeunit loaded fine, so --package-cache is
        // not the remedy and saying so would be a false diagnosis.
        Assert.DoesNotContain("--package-cache", ex.Message);
        Assert.Equal("NavNCLMissingMethodException", ex.GetType().Name);
    }
}
