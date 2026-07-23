// Isolation lock: proves the SingleInstance instance cache is reset at the
// per-test boundary (RecordPatches.ResetPerTestState -> BcRuntime.ResetSingleInstanceCache),
// so state set by "SIC Tests" does NOT leak into a fresh test codeunit.
//
// In a SEPARATE test codeunit from "SIC Tests" on purpose: TestExecutor resets
// per-test state before EVERY test codeunit under the default (Codeunit)
// isolation, so this proves the reset fires regardless of run order — a naive
// cache-forever fix (no reset wiring) would fail this test whenever "SIC Tests"
// runs first.
codeunit 61304 "SIC Leak Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "SIC Assert";

    [Test]
    procedure SingleInstance_DoesNotLeakAcrossTests()
    var
        S: Codeunit "SIC Single";
    begin
        Assert.AreEqual(0, S.GetValue(),
            'SingleInstance codeunit state must not leak from a previous test codeunit — expected the untouched default');
    end;
}
