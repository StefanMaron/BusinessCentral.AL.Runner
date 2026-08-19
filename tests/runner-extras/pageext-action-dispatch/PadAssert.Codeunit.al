/// Thin standalone assert helper — no dependency on Library Assert, mirroring the
/// dep-tableext-platform-base / EOM suites' own local wrapper convention.
codeunit 64525 "Pad Assert"
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

    procedure AreEqual(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed: %1 (expected ''%2'', got ''%3'')', Msg, Expected, Actual);
    end;
}
