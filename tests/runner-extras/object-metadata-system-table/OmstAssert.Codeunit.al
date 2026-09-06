// Standalone Assert — this suite must stand alone, it does not import from tests/al-language.
codeunit 65540 "OMST Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Expected %1 but got %2: %3', Format(Expected), Format(Actual), Msg);
    end;

    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then Error('Expected true: %1', Msg);
    end;

    procedure IsFalse(Condition: Boolean; Msg: Text)
    begin
        if Condition then Error('Expected false: %1', Msg);
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
}
