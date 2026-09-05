// Standalone Assert codeunit — this suite must stand alone (tests/runner-extras/README.md),
// it does not import from tests/al-language.
codeunit 64550 "Heb Assert"
{
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
}
