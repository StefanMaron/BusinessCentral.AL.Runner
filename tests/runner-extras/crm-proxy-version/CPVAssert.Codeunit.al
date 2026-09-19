codeunit 66000 "CPV Assert"
{
    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then
            Error('Assert.IsTrue failed. %1', Msg);
    end;

    procedure IsFalse(Condition: Boolean; Msg: Text)
    begin
        if Condition then
            Error('Assert.IsFalse failed. %1', Msg);
    end;

    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed. Expected:<%1>. Actual:<%2>. %3', Expected, Actual, Msg);
    end;

    procedure IsGreaterOrEqual(Actual: Integer; Floor: Integer; Msg: Text)
    begin
        if Actual < Floor then
            Error('Assert.IsGreaterOrEqual failed. Actual:<%1> is below floor:<%2>. %3', Actual, Floor, Msg);
    end;
}
