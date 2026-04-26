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
    var
        Errors: List of [ErrorInfo];
        CollectedError: ErrorInfo;
    begin
        // Positive: DetailedMessage is preserved on collected errors
        Lib.CollectDetailedError('Main error', 'Detailed info');
        Assert.IsTrue(HasCollectedErrors(), 'Should have collected the error');
        Errors := GetCollectedErrors(true);
        Assert.AreEqual(1, Errors.Count(), 'Should have exactly 1 collected error');
        CollectedError := Errors.Get(1);
        Assert.AreEqual('Main error', CollectedError.Message, 'Collected error should preserve the main message');
        Assert.AreEqual('Detailed info', CollectedError.DetailedMessage, 'Collected error should preserve the detailed message');
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
        CollectedError: ErrorInfo;
    begin
        // Positive: multiple collectible errors accumulate with correct messages and ordering
        Lib.CollectMultipleErrors('err1', 'err2', 'err3');
        Assert.IsTrue(HasCollectedErrors(), 'Should have collected errors');
        Errors := GetCollectedErrors(true);
        Assert.AreEqual(3, Errors.Count(), 'Should have 3 collected errors');
        CollectedError := Errors.Get(1);
        Assert.AreEqual('err1', CollectedError.Message, 'First error message preserved');
        CollectedError := Errors.Get(2);
        Assert.AreEqual('err2', CollectedError.Message, 'Second error message preserved');
        CollectedError := Errors.Get(3);
        Assert.AreEqual('err3', CollectedError.Message, 'Third error message preserved');
        // GetCollectedErrors(true) should have cleared the store
        Assert.IsFalse(HasCollectedErrors(), 'GetCollectedErrors(true) should clear the backing store');
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
