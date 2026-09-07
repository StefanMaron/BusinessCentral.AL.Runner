/// Thin standalone assert helper -- no dependency on Library Assert, mirroring the pattern used
/// by tests/runner-extras/testpage-trigger-inject-timing/TtitAssert.Codeunit.al -- so this suite
/// does not need a Library Assert package cache in CI.
codeunit 65760 "Tsvm Assert"
{
    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed: %1 (expected %2, got %3)', Msg, Expected, Actual);
    end;

    procedure AreEqualText(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqualText failed: %1 (expected ''%2'', got ''%3'')', Msg, Expected, Actual);
    end;
}
