// Standalone Assert codeunit — this suite must stand alone (tests/runner-extras/README.md),
// it does not import from tests/al-language.
codeunit 66700 "Cov Ext Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Expected ''%1'' but got ''%2'': %3', Expected, Actual, Msg);
    end;

    procedure IsFalse(Condition: Boolean; Msg: Text)
    begin
        if Condition then
            Error('Expected FALSE: %1', Msg);
    end;
}
