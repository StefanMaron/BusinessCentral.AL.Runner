codeunit 311444 "StopSession Comment Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure StopSession_WithComment_DoesNotThrow()
    var
        Src: Codeunit "StopSession Comment Src";
    begin
        // Positive: StopSession(SessionId, Comment) must not crash.
        // The comment parameter is accepted and silently ignored (no live session).
        Src.DoStopSessionWithComment(42, 'Shutting down test session');
        Assert.IsTrue(true, 'StopSession(Integer, Text) completed without error');
    end;

    [Test]
    procedure StopSession_WithComment_DifferentSessionIds_DoesNotThrow()
    var
        Src: Codeunit "StopSession Comment Src";
    begin
        // Positive: verify multiple session IDs are all accepted — including 0 and
        // large values — to ensure the overload handles any integer input.
        Src.DoStopSessionWithComment(0, 'comment for zero');
        Src.DoStopSessionWithComment(99999, 'comment for large id');
        Assert.IsTrue(true, 'StopSession(Integer, Text) handled all session IDs');
    end;

    [Test]
    procedure StopSession_WithEmptyComment_DoesNotThrow()
    var
        Src: Codeunit "StopSession Comment Src";
    begin
        // Positive: empty comment string must be accepted (Text default).
        Src.DoStopSessionWithComment(1, '');
        Assert.IsTrue(true, 'StopSession(Integer, Text) accepted empty comment');
    end;

    [Test]
    procedure StopSession_1Arg_StillWorks()
    var
        Src: Codeunit "StopSession Comment Src";
    begin
        // Regression: the original 1-arg StopSession(SessionId) must still compile
        // and run after adding the 2-arg overload.
        Src.DoStopSession(7);
        Assert.IsTrue(true, 'StopSession(Integer) still works after adding 2-arg overload');
    end;
}
