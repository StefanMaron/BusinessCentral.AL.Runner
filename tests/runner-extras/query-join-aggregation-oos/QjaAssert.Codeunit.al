// Standalone Assert codeunit — this suite must stand alone (README.md), it does not
// import from tests/al-language.
codeunit 64534 "Qja Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Expected %1 but got %2: %3', Format(Expected), Format(Actual), Msg);
    end;

    procedure ExpectedError(Fragment: Text)
    begin
        if StrPos(GetLastErrorText(), Fragment) = 0 then
            Error('Expected error containing ''%1'' but got ''%2''', Fragment, GetLastErrorText());
    end;
}
