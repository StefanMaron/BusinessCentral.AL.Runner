codeunit 50951 "ErrorInfo Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Lib: Codeunit "ErrorInfo Lib";

    [Test]
    procedure ErrorInfoUsesMessage()
    begin
        // Positive: Error(ErrorInfo) should use ALMessage for GetLastErrorText
        asserterror Lib.RaiseErrorInfo('Something went wrong');
        Assert.AreEqual('Something went wrong', GetLastErrorText(), 'GetLastErrorText should return ErrorInfo.Message');
    end;

    [Test]
    procedure ErrorInfoDetailedMessage()
    begin
        // Positive: DetailedMessage can be set alongside Message
        asserterror Lib.RaiseDetailedErrorInfo('Main error', 'Detailed info');
        Assert.AreEqual('Main error', GetLastErrorText(), 'GetLastErrorText should return the main message');
    end;

    [Test]
    procedure ErrorInfoEmptyMessage()
    begin
        // Negative: ErrorInfo with empty message should still throw
        asserterror Lib.RaiseErrorInfo('');
        Assert.AreEqual('', GetLastErrorText(), 'GetLastErrorText should return empty string for empty message');
    end;

    [Test]
    procedure CollectSingleErrorBasic()
    begin
        // Positive: collectible error in collecting context adds to list
        Lib.CollectSingleError('err1');
        Assert.IsTrue(HasCollectedErrors(), 'Should have collected errors after collectible Error()');
        ClearCollectedErrors();
    end;

    [Test]
    procedure CollectMultipleErrors()
    var
        Errors: List of [ErrorInfo];
    begin
        // Positive: multiple collectible errors accumulate
        Lib.CollectMultipleErrors('err1', 'err2', 'err3');
        Assert.IsTrue(HasCollectedErrors(), 'Should have collected errors');
        Errors := GetCollectedErrors(true);
        Assert.AreEqual(3, Errors.Count(), 'Should have 3 collected errors');
    end;

    [Test]
    procedure ClearCollectedErrorsWorks()
    begin
        // Positive: ClearCollectedErrors empties the list
        Lib.CollectThenClear('will be cleared');
        Assert.IsFalse(HasCollectedErrors(), 'HasCollectedErrors should be false after ClearCollectedErrors');
    end;

    [Test]
    procedure NonCollectibleStillThrows()
    begin
        // Negative: non-collectible error throws even in collecting context
        asserterror Lib.NonCollectibleInCollectMode('must throw');
        Assert.ExpectedError('must throw');
    end;

    [Test]
    procedure IsCollectingErrorsOutsideCollect()
    begin
        // Negative: outside [ErrorBehavior(Collect)], IsCollectingErrors is false
        Assert.IsFalse(IsCollectingErrors(), 'IsCollectingErrors should be false outside Collect context');
    end;
}
