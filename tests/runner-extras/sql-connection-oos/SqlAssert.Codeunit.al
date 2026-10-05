// Standalone Assert codeunit — this suite must stand alone (tests/runner-extras/README.md).
codeunit 65681 "Sql Assert"
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

    procedure ExpectedError(Fragment: Text)
    begin
        if StrPos(GetLastErrorText(), Fragment) = 0 then
            Error('Expected error containing ''%1'' but got ''%2''', Fragment, GetLastErrorText());
    end;

    procedure NotExpectedError(Fragment: Text)
    begin
        if StrPos(GetLastErrorText(), Fragment) > 0 then
            Error('Error must NOT contain ''%1'', but got ''%2''', Fragment, GetLastErrorText());
    end;

    // #2766: a refusal whose caller appends the doc pointer to a reason that already carries
    // one renders it twice. Counting is the only thing that catches that.
    procedure ErrorContainsExactlyOnce(Fragment: Text)
    var
        Txt: Text;
        Occurrences: Integer;
        Pos: Integer;
    begin
        Txt := GetLastErrorText();
        Pos := StrPos(Txt, Fragment);
        while Pos > 0 do begin
            Occurrences += 1;
            Txt := CopyStr(Txt, Pos + StrLen(Fragment));
            Pos := StrPos(Txt, Fragment);
        end;
        if Occurrences <> 1 then
            Error('Expected ''%1'' exactly once in the error text but found it %2 time(s): ''%3''',
                Fragment, Occurrences, GetLastErrorText());
    end;
}
