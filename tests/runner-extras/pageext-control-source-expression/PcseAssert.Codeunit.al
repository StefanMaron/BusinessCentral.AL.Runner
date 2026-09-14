/// Thin standalone assert helper -- no dependency on Library Assert, mirroring
/// tests/runner-extras/testpage-procedure-bound-property/PpbAssert.Codeunit.al -- so this suite
/// needs no test-toolkit package cache in CI.
codeunit 65980 "Pcse Assert"
{
    procedure AreEqual(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed: %1 (expected ''%2'', got ''%3'')', Msg, Expected, Actual);
    end;

    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then
            Error('Assert.IsTrue failed: %1', Msg);
    end;
}
