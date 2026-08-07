codeunit 63410 "DTB Assert"
{
    procedure AreEqual(Expected: Boolean; Actual: Boolean; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Expected %1 but got %2: %3', Expected, Actual, Msg);
    end;

    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Expected %1 but got %2: %3', Expected, Actual, Msg);
    end;

    procedure ExpectedErrorContains(ErrorText: Text; Fragment: Text; Msg: Text)
    begin
        if not ErrorText.Contains(Fragment) then
            Error('Expected error text to contain ''%1'' but got ''%2'': %3', Fragment, ErrorText, Msg);
    end;
}
