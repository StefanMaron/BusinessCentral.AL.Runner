// Codeunit 130002 is the real BC "Library Assert" ID.
// The runner's built-in stub uses 130. This codeunit verifies
// that calls to 130002 are routed to MockAssert just like 130.
codeunit 130002 "Library Assert 130002"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        // Placeholder — the runner routes this to MockAssert at runtime
    end;

    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
    end;

    procedure IsFalse(Condition: Boolean; Msg: Text)
    begin
    end;
}
