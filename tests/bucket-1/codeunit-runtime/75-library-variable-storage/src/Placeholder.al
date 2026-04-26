// Placeholder so the test directory is discovered by the suite runner.
// The actual test is self-contained in test/TestVariableStorage.al.
// Renumbered from 50701 to avoid collision in new bucket layout (#1385).
codeunit 1050701 "Variable Storage Helper"
{
    procedure GetGreeting(): Text
    begin
        exit('Hello');
    end;
}
