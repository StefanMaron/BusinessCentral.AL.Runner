// Standalone Assert codeunit -- this suite must stand alone (README.md), it does not import
// from tests/al-language.
codeunit 65795 "Tls Assert"
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
    begin
        if StrPos(GetLastErrorText(), Fragment) = 0 then
            Error('Expected error containing ''%1'' but got ''%2''', Fragment, GetLastErrorText());
    end;

    // The negative half of ExpectedError, and the reason the two refusals can be told apart at
    // all: asserting that a message DOES NOT carry the other refusal's wording is what makes
    // "each names its own cause" testable, rather than both passing on a shared message.
    procedure ErrorDoesNotContain(Fragment: Text)
    begin
        if StrPos(GetLastErrorText(), Fragment) > 0 then
            Error('Error must NOT contain ''%1'', but it did: ''%2''', Fragment, GetLastErrorText());
    end;
}
