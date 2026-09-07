// TestFieldValidationErrors — the validation-error ledger BC's own TestPage layer expects
// every ITestField to keep (issue #2900).
//
// WHY THIS EXISTS
//
// AL's `TestPage.<field>.SetValue(x)` compiles to NavTestField.ALValue's SETTER, which is
// BC's own precompiled code in Microsoft.Dynamics.Nav.Ncl.dll and reads (decompiled, 28.1):
//
//     public string ALValue { set { CheckError(delegate { testField.Activate();
//                                                         testField.Value = value; }); } }
//
//     private T CheckError<T>(FieldOperationReturnValue<T> operation)
//     {
//         long lastUsedValidationErrorId = testField.LastUsedValidationErrorId;
//         int  validationErrorCount      = testField.ValidationErrorCount;
//         int  num                       = parent.ALValidationErrorCount();
//         T result = operation();
//         if (testField.MaxValidationErrorId > lastUsedValidationErrorId)
//             throw NavTestValidationException.Create(CultureInfo.CurrentCulture, testField.Name,
//                       testField.GetValidationError(ALValidationErrorCount() - 1));
//         ...
//     }
//
// So BC's contract is NOT "the ITestField setter throws". It is "the ITestField setter
// RECORDS the refusal, and BC itself raises it afterwards" — which is why
// `ValidationErrorCount()` and `GetValidationError(1)` can still be read AFTER the
// `asserterror` that trapped the write. The runner used to throw straight out of the setter
// and hardcode both accessors to 0 / "", so Microsoft's own
// `Codeunit134614.TestRemoveSUPERPermissionsByUserAll` trapped the error and then read
// `ValidationErrorCount() = 0` where real BC answers 1.
//
// THE MESSAGE IS BC'S, NOT OURS
//
// NavTestValidationException.Create formats with Lang.TestValidationException, read straight
// out of Microsoft.Dynamics.Nav.Language.dll's resources on the 28.1 artifact:
//
//     Validation error for Field: {0},  Message = '{1}'
//
// ({0} = ITestField.Name, {1} = the recorded error text — the double space is BC's.) That is
// the same string TestPageMinMaxValue and TestPageBooleanValue used to hand-build themselves;
// now that a refusal is recorded rather than thrown, BC composes it and those helpers carry
// only the inner message.
//
// WHAT THE RECORDED TEXT IS, MEASURED — AND WHY IT DIFFERS BY BINDING
//
// Corpus run 34002487601 (StefanMaron/BusinessCentral.AL.Language.Tests PR #182, identical on
// every leg that reported) measured what a REC-BOUND control records when its OnValidate
// raises `Error('Deliberate OnValidate failure for VAL-1')`:
//
//     Deliberate OnValidate failure for VAL-1 (Select Refresh to discard errors)
//
// So real BC appends " (Select Refresh to discard errors)" — the client's offer to discard the
// staged row edit — to the AL message before storing it. An earlier version of this file
// recorded the bare message and was wrong; that suffix is exactly why TestPageMinMaxValue and
// TestPageBooleanValue already carried it inside their own hand-built strings.
//
// Microsoft's Tests-SINGLESERVER Codeunit134614 asserts the opposite for its own control, with
// exact equality and no suffix:
//
//     Assert.AreEqual('There should be at least one enabled ''SUPER'' user.',
//         PermissionSetByUser.AllUsersHavePermission.GetValidationError(1), ...)
//
// These are not the same shape, and the difference is mechanical rather than guessed: probing
// the runner's own control-binding map (AL_RUNNER_BINDING_PROBE over that test) shows page 9816
// "Permission Set by User" resolves AllUsersHavePermission as a PAGE VARIABLE, while page
// 60797's NameCtl in the corpus test is REC-BOUND. A page-global control stages no row edit, so
// there is nothing for "Refresh to discard" to discard. (An earlier version of this comment said
// page 9807; 9807 is "User Card", and AllUsersHavePermission is not on it.)
//
// Hence the suffix lives on LiveNavTestField (Rec-bound) and NOT on PageVariableTestField.
// BOTH SIDES ARE NOW SERVICE-TIER MEASUREMENTS. The Rec-bound side came from corpus run
// 34002487601 (PR #182). The page-global side was the open question this comment used to flag:
// corpus PR #184 asked it directly and merged 2026-09-06 with all eight BC Cloud legs green
// (run 34016443056), so corpus codeunit 60808 "TP PageVar Validation Error" now states on a real
// tier that a page-global control's stored validation error carries NO refresh suffix. The split
// below is measured, not the most defensible reading of partial evidence.
//
// IDS
//
// BC compares a `LastUsedValidationErrorId` snapshot taken BEFORE the write against
// `MaxValidationErrorId` after it, so ids only have to be monotonic and "consumed" by
// GetValidationError. Errors get ids 1..N in order; reading error i marks id i+1 used, which
// is what stops the very next CheckError (the `.Value` GETTER, say) from re-raising an error
// BC has already reported. BC's own throw path calls GetValidationError itself, so the
// consume happens whether or not the AL test goes on to read the ledger.

using System;
using System.Collections.Generic;
using System.Linq;

namespace AlRunner;

/// <summary>
/// Per-control ledger of the validation errors a TestPage write produced, exposed through
/// <see cref="Microsoft.Dynamics.Nav.Types.Data.ITestField"/>'s
/// <c>ValidationErrorCount</c> / <c>GetValidationError</c> / <c>MaxValidationErrorId</c> /
/// <c>LastUsedValidationErrorId</c> members. See this file's header for BC's contract.
/// </summary>
internal sealed class TestFieldValidationErrors
{
    private readonly List<string> _errors = new();
    private long _lastUsedId;

    /// <summary>
    /// The PAGE-level ledger this control reports into as well as its own, when the control
    /// belongs to a live page. See <see cref="TestPageValidationErrors"/> for why both exist
    /// and what BC does with each (#3009).
    /// </summary>
    private readonly TestPageValidationErrors? _page;

    internal TestFieldValidationErrors() { }

    internal TestFieldValidationErrors(TestPageValidationErrors? page) { _page = page; }

    /// <summary>How many refusals this control has recorded. BC's <c>ValidationErrorCount</c>.</summary>
    internal int Count => _errors.Count;

    /// <summary>
    /// The id of the newest recorded error, 0 when none. Ids run 1..<see cref="Count"/>, so
    /// this is just the count — kept as its own member because BC reads it under a different
    /// name and for a different purpose (the "is there something new since the snapshot"
    /// test), and conflating the two in the call site would hide that.
    /// </summary>
    internal long MaxId => _errors.Count;

    /// <summary>The highest id already handed out by <see cref="Get"/>. BC's <c>LastUsedValidationErrorId</c>.</summary>
    internal long LastUsedId => _lastUsedId;

    /// <summary>
    /// The suffix real BC's client appends to a REC-BOUND control's recorded validation error —
    /// its offer to discard the staged row edit. Measured, see this file's header; not added for
    /// a page-global control, which stages no edit.
    /// </summary>
    internal const string RefreshSuffix = " (Select Refresh to discard errors)";

    /// <summary>
    /// Record a refusal, exactly as BC's client stores it. <paramref name="message"/> is the
    /// bare AL error text; <paramref name="appendRefreshSuffix"/> adds
    /// <see cref="RefreshSuffix"/> for the binding shape BC adds it to. Neither carries BC's
    /// "Validation error for Field: …" wrapper — that is composed one layer out.
    /// </summary>
    internal void Record(string message, bool appendRefreshSuffix)
    {
        var stored = (message ?? string.Empty) + (appendRefreshSuffix ? RefreshSuffix : string.Empty);
        _errors.Add(stored);
        // The same refusal is ALSO the page's, and it is stored with the same text — real BC
        // has one client-side error list per page and the field's view of it is a filter, not
        // a separate list (#3009). Feeding both here rather than at the call sites is what
        // keeps them from drifting: every route that records a control refusal goes through
        // this method.
        _page?.Record(stored);
    }

    /// <summary>
    /// The recorded error at a ZERO-based index, marking its id used.
    /// <para>Out of range goes through <see cref="Enumerable.ElementAt{TSource}(IEnumerable{TSource}, int)"/>
    /// because that is literally what BC's own client does — corpus run 34002487601's stack is
    /// <c>System.Linq.Enumerable.ElementAt → TestPageClient.TestFieldProxy.GetValidationError →
    /// NavTestField.ALGetValidationError</c> — so an out-of-range read raises
    /// <see cref="ArgumentOutOfRangeException"/> with parameter name <c>index</c>, exactly as
    /// the tier does.</para>
    /// <para>An earlier version threw <see cref="IndexOutOfRangeException"/>, reasoning that
    /// <c>ALGetValidationError</c> catches that type and would not carry a catch for something
    /// unreachable. Measured, the catch IS unreachable: LINQ raises the Argument flavour, it
    /// does not match, and the exception escapes to the test framework as
    /// <c>"Unexpected CLR exception thrown."</c> — which AL <c>asserterror</c> does NOT trap.
    /// Throwing the Argument flavour reproduces that whole chain; throwing the Index flavour
    /// would have made the runner convert it into a trappable AL error the tier never produces.</para>
    /// </summary>
    internal string Get(int index)
    {
        // Deliberately Enumerable.ElementAt and not _errors[index]: the indexer raises
        // ArgumentOutOfRangeException too, but ElementAt is the call BC makes, and matching the
        // call rather than the outcome is what keeps this faithful if either side changes.
        var message = _errors.ElementAt(index);

        var id = index + 1;
        if (id > _lastUsedId) _lastUsedId = id;
        return message;
    }

    /// <summary>
    /// Run a TestPage control write, recording a BC/AL error instead of letting it escape so
    /// that BC's own <c>NavTestField.CheckError</c> raises it with BC's own wrapper.
    ///
    /// <para>ONLY a <see cref="Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLException"/> is
    /// recorded — that is the base of everything AL's <c>Error()</c>, <c>TestField</c> and
    /// BC's own validate path raise. Anything else is NOT a validation error and tears
    /// straight through: a <see cref="AlRunner.Infrastructure.RunnerOutOfScopeException"/>, a
    /// <see cref="AlRunner.Infrastructure.BcShapeGapException"/> (both plain
    /// <c>System.Exception</c>s, so neither can match this catch), an option-resolution
    /// refusal, or a runner <c>NullReferenceException</c>. Converting a loud refusal into a
    /// recorded "validation error" would let AL's <c>asserterror</c> absorb it and read as a
    /// green test, which is exactly what .claude/rules/loud-failures.md forbids.</para>
    ///
    /// <para>Two further exclusions, both for the same reason — they are already the OUTER
    /// layer's own signal, so recording them would wrap a wrapper: a
    /// <c>NavTestValidationException</c> escaping a nested TestPage operation, and any
    /// exception carrying the <c>out-of-scope:</c> message convention that the Cecil-injected
    /// throw sites use (<see cref="AlRunner.Infrastructure.OutOfScopeMessage"/>), which
    /// tests/expectations/ matches on.</para>
    /// </summary>
    internal void RunRecordingRefusal(Action write, bool appendRefreshSuffix)
    {
        try
        {
            write();
        }
        catch (Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLException ex)
            when (ex is not Microsoft.Dynamics.Nav.Types.Exceptions.NavTestValidationException
                  && AlRunner.Infrastructure.OutOfScopeMessage.FromException(ex) is null)
        {
            Record(ex.Message, appendRefreshSuffix);
        }
    }
}

/// <summary>
/// Per-PAGE ledger of the validation errors a TestPage's controls produced, exposed through
/// <see cref="Microsoft.Dynamics.Nav.Types.Data.ITestPage"/>'s <c>ValidationErrorCount</c> /
/// <c>GetValidationError</c> members — the pair AL's <c>TestPage.ValidationErrorCount()</c>
/// and <c>TestPage.GetValidationError(Index)</c> read (#3009).
///
/// <para>WHY THIS IS SEPARATE FROM THE FIELD LEDGER. BC reads BOTH around every control write.
/// <c>NavTestField.CheckError</c> (unmodified Ncl.dll, 28.1) is:</para>
/// <code>
///     int num = parent.ALValidationErrorCount();          // the PAGE, before the write
///     T result = operation();
///     if (testField.MaxValidationErrorId &gt; lastUsedValidationErrorId)
///         throw ... testField.Name ...;                   // the FIELD branch, checked first
///     int num2 = num - (validationErrorCount - testField.ValidationErrorCount);
///     if (parent.ALValidationErrorCount() &gt; num2)
///         throw ... parent.Name, parent.ALGetValidationError();
/// </code>
/// <para>The field branch is checked first and wins whenever the written control recorded the
/// refusal itself, so feeding this ledger alongside the field's never changes which exception
/// a refused write raises. What it changes is what AL can READ afterwards: the page pair was
/// hardcoded to <c>0</c> / <c>""</c>, so a count real BC reports as 1 came back 0 and the
/// message came back empty.</para>
///
/// <para>THE RANGE CHECK IS NOT THE FIELD'S. This is the one place the two ledgers genuinely
/// differ, and it is a real BC asymmetry rather than a tidy-up. Both AL boundaries subtract 1
/// and translate an out-of-range read, but they catch DIFFERENT exception types (unmodified
/// Ncl.dll, 28.1, read with the decompiler rather than inferred):</para>
/// <code>
///     NavTestField.ALGetValidationError(index)     catch (IndexOutOfRangeException)
///     NavTestPageBase.ALGetValidationError(index)  catch (ArgumentOutOfRangeException)
/// </code>
/// <para>LINQ's <c>ElementAt</c> — the call BC's own client makes — raises the ARGUMENT
/// flavour. So on the FIELD that catch is dead and an out-of-range read escapes as a raw CLR
/// exception (measured, corpus run 34002487601; see <see cref="TestFieldValidationErrors.Get"/>),
/// while on the PAGE it is live and the read becomes a <c>NavNCLIndexOutOfBoundsException</c>.
/// <c>ElementAt</c> here is therefore the call that reproduces BC's page-side chain rather than
/// merely producing a similar outcome.</para>
///
/// <para>WHAT PROVES WHICH, stated precisely because it is easy to overclaim. Corpus
/// <c>TestPart_GetValidationError_ErrorsOnIndexZero</c> asserts
/// <c>GetLastErrorText() &lt;&gt; ''</c>, so it pins that index 0 is OUT of the 1-based range —
/// which is the off-by-one that matters and the assertion a 0-based ledger fails. It does NOT
/// discriminate the two exception flavours: probed here, an <c>IndexOutOfRangeException</c>
/// implementation also passes it, because the runner surfaces that as a trappable AL error too.
/// The flavour is pinned instead by <c>TestPageValidationErrorsTests</c> on the C# side, against
/// the decompiled catch above.</para>
/// </summary>
internal sealed class TestPageValidationErrors
{
    private readonly List<string> _errors = new();

    /// <summary>How many refusals this page has recorded. BC's <c>ITestPage.ValidationErrorCount</c>.</summary>
    internal int Count => _errors.Count;

    /// <summary>
    /// Record a refusal already formatted exactly as the control stored it — including the
    /// refresh suffix when the control's binding earns one. The page does not re-derive the
    /// text: real BC keeps one list and the control's is a view onto it, so re-deriving would
    /// be the one way the two could disagree.
    /// </summary>
    internal void Record(string storedMessage) => _errors.Add(storedMessage ?? string.Empty);

    /// <summary>
    /// The recorded error at a ZERO-based index (BC's AL boundary has already subtracted 1).
    /// <para>Deliberately <see cref="Enumerable.ElementAt{TSource}(IEnumerable{TSource}, int)"/>
    /// and not <c>_errors[index]</c>: it is the call BC's own client makes, and it raises
    /// <see cref="ArgumentOutOfRangeException"/> — the type
    /// <c>NavTestPageBase.ALGetValidationError(int)</c> catches and turns into a
    /// <c>NavNCLIndexOutOfBoundsException</c>. Unlike the field ledger's identical-looking
    /// call, that catch is LIVE here, which is what makes an out-of-range page read trappable
    /// from AL. See this class's summary.</para>
    /// <para>No id is consumed. The page pair has no <c>LastUsedValidationErrorId</c> /
    /// <c>MaxValidationErrorId</c> on <see cref="Microsoft.Dynamics.Nav.Types.Data.ITestPage"/>
    /// at all — the "is there something new since the snapshot" test BC runs against the field
    /// ledger has no page-level counterpart, so there is nothing for a read to mark used.</para>
    /// </summary>
    internal string Get(int index) => _errors.ElementAt(index);
}
