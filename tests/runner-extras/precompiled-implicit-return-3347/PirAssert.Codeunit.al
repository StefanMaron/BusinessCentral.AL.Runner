/// Thin standalone assert helper -- no dependency on Library Assert, mirroring the pattern
/// used by tests/runner-extras/db-trigger-inject-timing/DbtAssert.Codeunit.al -- so this
/// suite does not need a Library Assert package cache in CI.
codeunit 65901 "Pir Assert"
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

    procedure AreEqualText(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqualText failed: expected ''%1'', got ''%2''. %3', Expected, Actual, Msg);
    end;

    procedure AreEqualBool(Expected: Boolean; Actual: Boolean; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqualBool failed: expected %1, got %2. %3', Expected, Actual, Msg);
    end;
}
