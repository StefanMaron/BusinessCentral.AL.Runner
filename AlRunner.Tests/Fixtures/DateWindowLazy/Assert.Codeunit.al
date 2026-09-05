// Standalone Assert codeunit — this fixture must stand alone, it does not import from
// tests/al-language or tests/runner-extras.
codeunit 61630 "DWL Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Expected %1 but got %2: %3', Format(Expected), Format(Actual), Msg);
    end;

    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then
            Error('Expected true: %1', Msg);
    end;

    procedure ExpectedError(Fragment: Text)
    var
        Actual: Text;
    begin
        Actual := GetLastErrorText();
        if Actual = '' then
            Error('Expected an error containing "%1", but no error was raised.', Fragment);
        if StrPos(Actual, Fragment) = 0 then
            Error('Expected an error containing "%1", but got: %2', Fragment, Actual);
    end;
}
