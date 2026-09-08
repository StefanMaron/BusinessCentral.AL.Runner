codeunit 70641 "PRT Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    var
        E: Text;
        A: Text;
    begin
        E := Format(Expected);
        A := Format(Actual);
        if E <> A then
            Error('%1: Expected:<%2> Actual:<%3>', Msg, E, A);
    end;

    procedure IsTrue(Cond: Boolean; Msg: Text)
    begin
        if not Cond then
            Error('%1: expected true, got false', Msg);
    end;

    procedure IsFalse(Cond: Boolean; Msg: Text)
    begin
        if Cond then
            Error('%1: expected false, got true', Msg);
    end;
}
