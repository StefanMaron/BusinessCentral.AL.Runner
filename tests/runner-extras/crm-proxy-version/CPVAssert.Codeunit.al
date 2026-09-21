// Standalone Assert codeunit — this suite must stand alone (tests/runner-extras/README.md),
// it does not import from tests/al-language.
codeunit 66000 "CPV Assert"
{
    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then
            Error('Assert.IsTrue failed. %1', Msg);
    end;

    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed. Expected:<%1>. Actual:<%2>. %3', Expected, Actual, Msg);
    end;

    procedure ExpectedError(Fragment: Text)
    begin
        if StrPos(GetLastErrorText(), Fragment) = 0 then
            Error('Assert.ExpectedError failed. Expected an error containing ''%1'' but got ''%2''',
                  Fragment, GetLastErrorText());
    end;

    procedure ErrorDoesNotContain(Fragment: Text; Msg: Text)
    begin
        if StrPos(GetLastErrorText(), Fragment) > 0 then
            Error('Assert.ErrorDoesNotContain failed. The error must NOT contain ''%1'' but was ''%2''. %3',
                  Fragment, GetLastErrorText(), Msg);
    end;
}
