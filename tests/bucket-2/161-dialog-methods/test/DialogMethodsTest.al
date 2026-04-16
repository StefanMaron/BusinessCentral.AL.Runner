codeunit 59741 "DLGM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "DLGM Src";

    [Test]
    procedure HideSubsequentDialogs_True_NoOp()
    begin
        // Positive: standalone stub must complete without error.
        Src.CallHideSubsequentDialogs(true);
        Assert.IsTrue(true, 'HideSubsequentDialogs(true) must not throw');
    end;

    [Test]
    procedure HideSubsequentDialogs_False_NoOp()
    begin
        Src.CallHideSubsequentDialogs(false);
        Assert.IsTrue(true, 'HideSubsequentDialogs(false) must not throw');
    end;

    [Test]
    procedure HideSubsequentDialogs_ExecutionContinues()
    begin
        // Proving: execution continues past the call.
        Assert.IsTrue(Src.CallHideAndReturnFlag(true),
            'Caller must reach `exit(true)` after HideSubsequentDialogs');
    end;

    [Test]
    procedure LogInternalError_NoOp()
    begin
        Src.CallLogInternalError('Something went wrong');
        Assert.IsTrue(true, 'LogInternalError must not throw');
    end;

    [Test]
    procedure LogInternalError_EmptyMessage()
    begin
        // Edge: empty message must not crash.
        Src.CallLogInternalError('');
        Assert.IsTrue(true, 'LogInternalError with empty message must not throw');
    end;

    [Test]
    procedure LogInternalError_LongMessage_NegativeTrap()
    begin
        // Negative trap: guard against crash on large input.
        Src.CallLogInternalError('A very long internal-error description that might stress a naive implementation_0123456789_abcdefghij');
        Assert.IsTrue(true, 'LogInternalError with long message must not throw');
    end;

    [Test]
    procedure BothMethodsTogether_NoOp()
    begin
        // Proving: both methods can be invoked sequentially without interference.
        Assert.IsTrue(Src.CallBothAndReturnFlag('nested-call-msg'),
            'HideSubsequentDialogs + LogInternalError in sequence must complete');
    end;
}
