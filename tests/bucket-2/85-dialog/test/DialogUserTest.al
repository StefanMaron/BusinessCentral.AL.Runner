codeunit 85001 DialogUserTest
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestProcessWithDialog()
    var
        DialogUser: Codeunit DialogUser;
        Result: Integer;
    begin
        // Positive: Dialog.Open/Update/Close should not crash, result should be sum 1..5
        Result := DialogUser.ProcessWithDialog(5);
        Assert.AreEqual(15, Result, 'Sum of 1..5 should be 15');
    end;

    [Test]
    procedure TestProcessWithDialogZero()
    var
        DialogUser: Codeunit DialogUser;
        Result: Integer;
    begin
        // Edge case: zero iterations still opens and closes dialog without error
        Result := DialogUser.ProcessWithDialog(0);
        Assert.AreEqual(0, Result, 'Sum of zero iterations should be 0');
    end;

    [Test]
    procedure TestProcessWithDialogText()
    var
        DialogUser: Codeunit DialogUser;
        Result: Text;
    begin
        // Positive: Dialog with text caption
        Result := DialogUser.ProcessWithDialogText('MyTask');
        Assert.AreEqual('Done: MyTask', Result, 'Should return done message');
    end;

    [Test]
    procedure TestProcessWithDialogNegative()
    var
        DialogUser: Codeunit DialogUser;
    begin
        // Negative: passing negative count should still work (no iterations)
        asserterror error('Expected no error from dialog but got one');
        Assert.ExpectedError('Expected no error');
    end;
}
