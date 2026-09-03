/// Thin standalone assert helper -- no dependency on Library Assert, mirroring the pattern
/// used by tests/runner-extras/db-trigger-inject-timing/DbtAssert.Codeunit.al -- so this suite
/// does not need a Library Assert package cache in CI.
codeunit 65510 "Dss Assert"
{
    procedure AreEqual(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed: %1. Expected: ''%2''. Actual: ''%3''.', Msg, Expected, Actual);
    end;

    procedure ExpectedError(Expected: Text)
    var
        Actual: Text;
    begin
        Actual := GetLastErrorText();
        if not Actual.Contains(Expected) then
            Error('Assert.ExpectedError failed: expected an error containing ''%1'', got ''%2''', Expected, Actual);
    end;

    procedure ErrorDoesNotContain(NotExpected: Text)
    var
        Actual: Text;
    begin
        Actual := GetLastErrorText();
        if Actual.Contains(NotExpected) then
            Error('Assert.ErrorDoesNotContain failed: error text ''%1'' unexpectedly contains ''%2''', Actual, NotExpected);
    end;
}
