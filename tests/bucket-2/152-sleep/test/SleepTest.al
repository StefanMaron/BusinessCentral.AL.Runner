codeunit 61101 "SLP Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: Sleep completes without error; return value unaffected.
    // ------------------------------------------------------------------

    [Test]
    procedure Sleep_Zero_NoError()
    var
        H: Codeunit "SLP Helper";
    begin
        H.DoSleep(0);
        Assert.IsTrue(true, 'Sleep(0) must complete without error');
    end;

    [Test]
    procedure Sleep_Positive_NoError()
    var
        H: Codeunit "SLP Helper";
    begin
        H.DoSleep(1);
        Assert.IsTrue(true, 'Sleep(1) must complete without error');
    end;

    [Test]
    procedure Sleep_ThenReturn_ReturnsValue()
    var
        H: Codeunit "SLP Helper";
    begin
        Assert.AreEqual('done', H.SleepAndReturn(0), 'Sleep followed by exit must return correct value');
    end;

    [Test]
    procedure Sleep_ThenReturn_ValueNotEmpty()
    var
        H: Codeunit "SLP Helper";
    begin
        Assert.AreNotEqual('', H.SleepAndReturn(1), 'SleepAndReturn must not return empty string');
    end;

    // ------------------------------------------------------------------
    // Negative: asserterror still works after Sleep (error handling unaffected).
    // ------------------------------------------------------------------

    [Test]
    procedure Sleep_ErrorAfter_CaughtCorrectly()
    var
        H: Codeunit "SLP Helper";
    begin
        H.DoSleep(0);
        asserterror Error('post-sleep error');
        Assert.ExpectedError('post-sleep error');
    end;
}
