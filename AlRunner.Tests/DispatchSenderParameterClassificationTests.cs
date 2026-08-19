using System.Reflection;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Pins <see cref="BcRuntime.IsSenderParameter"/> — the seam that decides whether a
/// subscriber's leading parameter is the "Sender" of an IncludeSender=true event.
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
/// behaviour. This test pins the specific classification defect at the seam — the same pattern
/// DispatchEventPublisherDeclTypeTests.cs and DispatchCoerceArgByRefTests.cs use for their
/// seams in the same file.
/// </summary>
public class DispatchSenderParameterClassificationTests
{
    private static void CodeunitSenderShape(NavCodeunitHandle sender) { }
    private static void RecordSenderShape(INavRecordHandle sender) { }
    private static void TwoParams_RecordFirst(INavRecordHandle first, int second) { }
    private static void UnrelatedLeadingType(string notASender) { }
    private static void NonLeadingRecordHandle(int first, INavRecordHandle notLeading) { }

    private static ParameterInfo FirstParamOf(string methodName) =>
        typeof(DispatchSenderParameterClassificationTests)
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetParameters()[0];

    [Fact]
    public void CodeunitHandleTypedLeadingParameter_IsRecognizedAsSender()
    {
        // Regression guard: the pre-existing, already-working codeunit case must survive
        // the #1956 fix unchanged.
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
    public void RecordHandleTypedParameter_NotAtLeadingPosition_IsNotSender()
    {
        // Position matters independent of type-shape: only a LEADING parameter can be a
        // sender. A record-typed parameter declared later is a genuinely different argument.
        var p = typeof(DispatchSenderParameterClassificationTests)
            .GetMethod(nameof(NonLeadingRecordHandle), BindingFlags.NonPublic | BindingFlags.Static)!
            .GetParameters()[1];

        Assert.False(BcRuntime.IsSenderParameter(p, paramIndex: 1));
    }

    [Fact]
    public void RecordHandleTypedLeadingParameter_ButPassedIndexOne_IsNotSender()
    {
        // Same "leading" requirement, expressed the other way: even a record-shaped, textually
        // first parameter is not a sender if the caller reports it at a non-zero position.
        var p = FirstParamOf(nameof(TwoParams_RecordFirst));

        Assert.False(BcRuntime.IsSenderParameter(p, paramIndex: 1));
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
}
