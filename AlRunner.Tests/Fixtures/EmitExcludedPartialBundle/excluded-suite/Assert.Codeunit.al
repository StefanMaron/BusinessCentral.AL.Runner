/// <summary>
/// Minimal assert for this fixture. Deliberately private to the fixture: nothing here
/// needs the Base Application floor (.claude/rules/no-base-app-in-csharp-tests.md).
/// </summary>
codeunit 61300 "Emit Excl Part Assert"
{
    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('%1: expected %2, got %3', Msg, Expected, Actual);
    end;
}
