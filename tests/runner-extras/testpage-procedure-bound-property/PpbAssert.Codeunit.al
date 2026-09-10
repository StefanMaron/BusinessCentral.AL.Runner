/// Thin standalone assert helper -- no dependency on Library Assert, mirroring
/// tests/runner-extras/testpage-same-value-modify/TsvmAssert.Codeunit.al -- so this suite needs
/// no test-toolkit package cache in CI.
codeunit 65910 "Ppb Assert"
{
    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed: %1 (expected %2, got %3)', Msg, Expected, Actual);
    end;

    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then
            Error('Assert.IsTrue failed: %1', Msg);
    end;

    procedure IsFalse(Condition: Boolean; Msg: Text)
    begin
        if Condition then
            Error('Assert.IsFalse failed: %1', Msg);
    end;

    procedure ExpectedError(Fragment: Text; Msg: Text)
    var
        Actual: Text;
    begin
        Actual := GetLastErrorText();
        if Actual = '' then
            Error('Assert.ExpectedError failed: %1 -- no error was raised at all', Msg);
        if StrPos(Actual, Fragment) = 0 then
            Error('Assert.ExpectedError failed: %1 (expected the message to contain ''%2'', got ''%3'')',
                Msg, Fragment, Actual);
    end;
}
