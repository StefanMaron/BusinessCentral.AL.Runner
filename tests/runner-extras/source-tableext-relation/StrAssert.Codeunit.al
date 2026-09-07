// Standalone Assert — this suite does not import from tests/al-language.
codeunit 65720 "STR Assert"
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

    // Asserts the last error text CONTAINS a fragment. Used instead of a bare `asserterror`,
    // which would pass on any error at all — including one raised by the test's own setup.
    procedure ExpectedError(Fragment: Text)
    var
        Actual: Text;
    begin
        Actual := GetLastErrorText();
        if Actual = '' then
            Error('Expected an error containing "%1" but no error was raised', Fragment);
        if StrPos(Actual, Fragment) = 0 then
            Error('Expected an error containing "%1" but got: %2', Fragment, Actual);
    end;
}
