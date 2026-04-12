// Placeholder so the test directory is discovered by the suite runner.
// The actual test is self-contained in test/TestVariableStorage.al.
codeunit 50701 "Variable Storage Helper"
{
    procedure GetGreeting(): Text
    begin
        exit('Hello');
    end;
}
