// Standalone Assert codeunit — this suite must stand alone (tests/runner-extras/README.md),
// it does not import from tests/al-language.
codeunit 65600 "Tsk Assert"
{
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

    // #2766: about 45 throw sites render "... — see docs/scope.md — see docs/scope.md"
    // because the caller appends the anchor to a reason that already carries one. A new
    // refusal must not become the next one, and "the message mentions the doc" cannot
    // catch that — only counting can.
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

    procedure AreEqualText(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Expected ''%1'' but got ''%2'': %3', Expected, Actual, Msg);
    end;
}
