/// Thin standalone assert helper -- no dependency on Library Assert, mirroring the pattern
/// used by tests/runner-extras/pageext-action-dispatch/PadAssert.Codeunit.al -- so this suite
/// does not need a Library Assert package cache in CI.
codeunit 65250 "Dbt Assert"
{
    procedure IsTrue(Value: Boolean; Msg: Text)
    begin
        if not Value then
            Error('Assert.IsTrue failed: %1', Msg);
    end;

    procedure IsFalse(Value: Boolean; Msg: Text)
    begin
        if Value then
            Error('Assert.IsFalse failed: %1', Msg);
    end;

    procedure ExpectedError(Expected: Text)
    var
        Actual: Text;
    begin
        Actual := GetLastErrorText();
        if not Actual.Contains(Expected) then
            Error('Assert.ExpectedError failed: expected an error containing ''%1'', got ''%2''', Expected, Actual);
    end;
}
