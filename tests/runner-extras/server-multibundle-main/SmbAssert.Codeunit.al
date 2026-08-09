codeunit 64350 "SMB Assert"
{
    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Expected %1 but got %2: %3', Expected, Actual, Msg);
    end;

    procedure AreEqualText(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Expected ''%1'' but got ''%2'': %3', Expected, Actual, Msg);
    end;

    procedure IsFalse(Actual: Boolean; Msg: Text)
    begin
        if Actual then
            Error('Expected FALSE: %1', Msg);
    end;

    procedure ExpectedError(Fragment: Text)
    begin
        if StrPos(GetLastErrorText(), Fragment) = 0 then
            Error('Expected error containing ''%1'' but got ''%2''', Fragment, GetLastErrorText());
    end;
}
