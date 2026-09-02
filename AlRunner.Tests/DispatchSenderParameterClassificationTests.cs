using System.Reflection;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Pins <see cref="BcRuntime.IsSenderParameter"/> — the seam that decides whether a
/// subscriber parameter is the "Sender" of an IncludeSender=true event, at ANY position.
///
/// Issue #1956: a table-declared <c>[IntegrationEvent(true, false)]</c> passed <c>null</c> as
/// the sender. Root cause: <c>IsSenderParameter</c> recognised a sender only by walking the
/// parameter's CLR <c>BaseType</c> chain for <c>NavCodeunitHandle</c> — what AL emits when the
/// publisher is a CODEUNIT. A table publisher emits the sender parameter as
/// <c>INavRecordHandle</c>, an INTERFACE (confirmed by reflecting over an emitted test
/// assembly's subscriber signature: <c>OnTableDiscover(INavRecordHandle sender)</c>) — a
/// <c>BaseType</c> walk can never reach an interface, so the walk terminated immediately and
/// answered false. The parameter then fell through to the scope-field lookup, found no field
/// (an IncludeSender event declares no parameters, so AL never emits one), and
/// <c>CoerceArg(null, ...)</c> passed a silent null — see <c>.claude/rules/loud-failures.md</c>.
///
/// Issue #2348: <c>IsSenderParameter</c> additionally required <c>paramIndex == 0</c>, but real
/// BC binds the sender wherever the subscriber declared it — Base Application's
/// <c>MfgItemJnlPostLine.OnPostOutput</c> declares its sender LAST. The position guard is gone;
/// what still distinguishes an actual sender from a genuinely-declared record/codeunit-typed
/// event argument at the SAME position is the caller-side rule in InvokeOneSubscriber: the
/// scope-field lookup runs first and wins, so a parameter only reaches IsSenderParameter when no
/// publisher-scope field matches its name.
///
/// <see cref="NavCodeunitHandle"/> does NOT implement <see cref="INavRecordHandle"/> (verified:
/// NavCodeunitHandle's interfaces are INavValueMetadata, IEquatable, IComparable, ITreeObject,
/// IDisposable, ITreeObjectReference, INavApplicationObjectBaseHandle, IALAssignable — no
/// INavRecordHandle), so the two branches below are mutually exclusive; there is no type that
/// satisfies both and could misclassify between codeunit- and table-declared senders.
///
/// NOTE ON COVERAGE: constructing a real INavRecordHandle-implementing instance and exercising
/// the full InvokeOneSubscriber pass-through (the "receives the record and can write through
/// it" claim) needs a compiled AL bundle (Record&lt;N&gt; is generated per-table) — the
/// end-to-end proof is the repro in issue #1956, measured manually (before the fix: "at
/// AlRunner.BcRuntime.DispatchCore ... NullReferenceException"; after: the subscriber's
/// AddEntry write lands and the test's Registry.Get('FROM-TABLE-SENDER') succeeds), and belongs
/// upstream in the corpus per bc-behavior-tests-go-upstream.md since it's a claim about BC
/// behaviour. Likewise the #2348 sender-position claim (first / middle / last, plus the
/// asserterror-through-sender case) is upstream. This test pins the specific classification
/// defect at the seam — the same pattern DispatchEventPublisherDeclTypeTests.cs and
/// DispatchCoerceArgByRefTests.cs use for their seams in the same file.
/// </summary>
public class DispatchSenderParameterClassificationTests
{
    private static void CodeunitSenderShape(NavCodeunitHandle sender) { }
    private static void RecordSenderShape(INavRecordHandle sender) { }
    private static void UnrelatedLeadingType(string notASender) { }
    private static void NonLeadingCodeunitHandle(int first, NavCodeunitHandle notLeading) { }
    private static void NonLeadingRecordHandle(int first, INavRecordHandle notLeading) { }
    private static void TrailingCodeunitHandle(int first, string second, NavCodeunitHandle sender) { }

    private static ParameterInfo FirstParamOf(string methodName) =>
        typeof(DispatchSenderParameterClassificationTests)
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetParameters()[0];

    private static ParameterInfo ParamOf(string methodName, int index) =>
        typeof(DispatchSenderParameterClassificationTests)
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetParameters()[index];

    [Fact]
    public void CodeunitHandleTypedLeadingParameter_IsRecognizedAsSender()
    {
        // Regression guard: the pre-existing, already-working codeunit case must survive
        // the #1956/#2348 fixes unchanged.
        var p = FirstParamOf(nameof(CodeunitSenderShape));

        Assert.True(BcRuntime.IsSenderParameter(p, paramIndex: 0));
    }

    [Fact]
    public void RecordHandleInterfaceTypedLeadingParameter_IsRecognizedAsSender()
    {
        // The #1956 fix: a table publisher's sender parameter, INavRecordHandle, must now
        // be recognized — this is FALSE before the fix (the defect this test pins).
        var p = FirstParamOf(nameof(RecordSenderShape));

        Assert.True(BcRuntime.IsSenderParameter(p, paramIndex: 0));
    }

    [Fact]
    public void CodeunitHandleTypedParameter_NotAtLeadingPosition_IsStillSender()
    {
        // The #2348 fix: position no longer matters — only type-shape does. A codeunit-handle
        // typed parameter declared at a non-zero index (e.g. Base App's
        // MfgItemJnlPostLine.OnPostOutput, sender LAST) must be recognized. Before the fix this
        // was FALSE solely because of the removed `paramIndex != 0` guard.
        var p = ParamOf(nameof(NonLeadingCodeunitHandle), 1);

        Assert.True(BcRuntime.IsSenderParameter(p, paramIndex: 1));
    }

    [Fact]
    public void RecordHandleTypedParameter_NotAtLeadingPosition_IsStillSender()
    {
        // Same #2348 fix, table-publisher (interface) shape.
        var p = ParamOf(nameof(NonLeadingRecordHandle), 1);

        Assert.True(BcRuntime.IsSenderParameter(p, paramIndex: 1));
    }

    [Fact]
    public void CodeunitHandleTypedParameter_AtTrailingPositionTwo_IsStillSender()
    {
        // Position 2 of 3 — not just "second of two" — matching the real MfgItemJnlPostLine
        // shape where the sender is the last of several declared parameters.
        var p = ParamOf(nameof(TrailingCodeunitHandle), 2);

        Assert.True(BcRuntime.IsSenderParameter(p, paramIndex: 2));
    }

    [Fact]
    public void UnrelatedTypedLeadingParameter_IsNotSender()
    {
        // Negative direction: a leading parameter whose type is neither a codeunit-handle nor
        // a record-handle shape (e.g. a plain declared string argument) must not be
        // misclassified as a sender just because it's first.
        var p = FirstParamOf(nameof(UnrelatedLeadingType));

        Assert.False(BcRuntime.IsSenderParameter(p, paramIndex: 0));
    }

    // ── #2348 follow-up: AllowsSenderSubstitution (the omitted-trailing-parameter fix) ──
    //
    // AL lets a subscriber omit trailing publisher parameters entirely, sender included
    // (confirmed empirically against a real compiled bundle: a subscriber declaring only a
    // PREFIX of the publisher's parameters compiles and dispatches). An earlier version of
    // this fix required the subscriber's parameter count to be EXACTLY one more than the
    // publisher's declared arity, which rejected that legal shape and regressed a
    // sender-first subscriber that also omits a trailing parameter — worse than even the
    // pre-#2348 position-0-only behaviour, which tolerated it. AllowsSenderSubstitution
    // replaces the arity count with counting how many of THIS subscriber's own parameters
    // had no matching scope field: exactly one is the only shape IncludeSender's contract
    // produces, at any position, with any number of trailing parameters omitted.

    [Fact]
    public void AllowsSenderSubstitution_ExactlyOneUnmatchedParameter_IsAllowed()
    {
        // The IncludeSender shape: every declared event argument matched a scope field,
        // and the sender itself is the sole leftover.
        Assert.True(BcRuntime.AllowsSenderSubstitution(unmatchedFieldCount: 1));
    }

    [Fact]
    public void AllowsSenderSubstitution_NoUnmatchedParameters_IsNotAllowed()
    {
        // A subscriber that omits trailing parameters and does NOT declare a sender: every
        // parameter it kept matched a scope field, so there is nothing left to substitute.
        Assert.False(BcRuntime.AllowsSenderSubstitution(unmatchedFieldCount: 0));
    }

    [Fact]
    public void AllowsSenderSubstitution_TwoOrMoreUnmatchedParameters_IsNotAllowed()
    {
        // Two or more parameters missing a scope field is not a shape IncludeSender's
        // contract can produce — substituting a guess here would be exactly the silent
        // wrong answer .claude/rules/loud-failures.md forbids.
        Assert.False(BcRuntime.AllowsSenderSubstitution(unmatchedFieldCount: 2));
    }
}
