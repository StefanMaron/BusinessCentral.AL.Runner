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
// only the inner message. Microsoft's own Tests-SINGLESERVER asserts both halves —
// `Assert.ExpectedError('Validation error for Field')` in UserRoleTest, and
// `GetValidationError(1)` equal to the BARE inner text in TestAppPermissions — which is the
// evidence that the wrapper belongs to the outer layer and the recorded text does not carry it.
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

    /// <summary>Record a refusal. The text is the bare error message, without BC's wrapper.</summary>
    internal void Record(string message) => _errors.Add(message ?? string.Empty);

    /// <summary>
    /// The recorded error at a ZERO-based index, marking its id used.
    /// <para>Out of range throws <see cref="IndexOutOfRangeException"/> rather than answering
    /// an empty string, because that is the exception BC's own
    /// <c>NavTestField.ALGetValidationError(int)</c> is written to catch — it converts it into
    /// <c>NavNCLIndexOutOfBoundsException</c>, the AL-visible error for
    /// <c>GetValidationError(0)</c> or an index past the end. Answering "" here would swallow
    /// BC's own bounds check and let an AL test compare two empty strings successfully.</para>
    /// </summary>
    internal string Get(int index)
    {
        if (index < 0 || index >= _errors.Count)
            throw new IndexOutOfRangeException(
                $"validation error index {index} is outside 0..{_errors.Count - 1}");

        var id = index + 1;
        if (id > _lastUsedId) _lastUsedId = id;
        return _errors[index];
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
    internal void RunRecordingRefusal(Action write)
    {
        try
        {
            write();
        }
        catch (Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLException ex)
            when (ex is not Microsoft.Dynamics.Nav.Types.Exceptions.NavTestValidationException
                  && AlRunner.Infrastructure.OutOfScopeMessage.FromException(ex) is null)
        {
            Record(ex.Message);
        }
    }
}
