// Standalone Assert codeunit — this suite must stand alone (tests/runner-extras/README.md).
codeunit 65740 "Gcr Assert"
{
    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then
            Error('Expected TRUE: %1', Msg);
    end;

    procedure IsFalse(Condition: Boolean; Msg: Text)
    begin
        if Condition then
            Error('Expected FALSE: %1', Msg);
    end;

    procedure AreEqualText(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Expected ''%1'' but got ''%2'': %3', Expected, Actual, Msg);
    end;

    procedure ExpectedError(Fragment: Text)
    begin
        if StrPos(GetLastErrorText(), Fragment) = 0 then
            Error('Expected error containing ''%1'' but got ''%2''', Fragment, GetLastErrorText());
    end;

    procedure ErrorContains(Text: Text; Fragment: Text; Msg: Text)
    begin
        if StrPos(Text, Fragment) = 0 then
            Error('Expected ''%1'' to contain ''%2'': %3', Text, Fragment, Msg);
    end;
}
