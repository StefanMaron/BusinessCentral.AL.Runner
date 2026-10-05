// Standalone Assert codeunit: this suite must stand alone (tests/runner-extras/README.md).
codeunit 66605 "Rtw Assert"
{
    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('%1 Expected: %2, Actual: %3', Msg, Expected, Actual);
    end;

    procedure AreNotEqual(Unexpected: Integer; Actual: Integer; Msg: Text)
    begin
        if Unexpected = Actual then
            Error('%1 Unexpected: %2', Msg, Unexpected);
    end;
}
