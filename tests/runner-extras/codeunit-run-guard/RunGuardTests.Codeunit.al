/// <summary>
/// Proves the runner honours BC's guarded/unguarded Codeunit.Run distinction.
///
/// In BC AL, Codeunit.Run is a GUARDED call only when its boolean return value
/// is consumed: the inner error is TRAPPED, Run returns false, and the error is
/// readable via GetLastErrorText. When the return value is discarded (statement
/// form), the inner error PROPAGATES to the caller.
///
/// codeunit 60401 "Run Guard Erroring".OnRun unconditionally Error()s, so:
///   - guarded   `ok := Codeunit.Run(60401)` must set ok = false and stash the
///     error text in GetLastErrorText.
///   - unguarded `Codeunit.Run(60401)` as a statement must throw 'BOOM-FROM-ONRUN'.
/// </summary>
codeunit 60402 "Run Guard Tests"
{
    Subtype = Test;

    [Test]
    procedure GuardedRun_TrapsInnerError_ReturnsFalse()
    var
        Assert: Codeunit "Run Guard Assert";
        Ok: Boolean;
    begin
        // [WHEN] the boolean return value of Codeunit.Run is consumed (guarded)
        Ok := Codeunit.Run(Codeunit::"Run Guard Erroring");

        // [THEN] the inner error is trapped: Run returns false ...
        Assert.IsFalse(Ok, 'Guarded Codeunit.Run on an erroring OnRun must return false.');

        // [THEN] ... and the inner error text is readable via GetLastErrorText.
        Assert.ExpectedError('BOOM-FROM-ONRUN', GetLastErrorText());
    end;

    [Test]
    procedure UnguardedRun_PropagatesInnerError()
    var
        Assert: Codeunit "Run Guard Assert";
    begin
        // [WHEN] the return value is discarded (statement form, unguarded)
        // [THEN] the inner error propagates to the caller
        asserterror Codeunit.Run(Codeunit::"Run Guard Erroring");
        Assert.ExpectedError('BOOM-FROM-ONRUN', GetLastErrorText());
    end;
}
