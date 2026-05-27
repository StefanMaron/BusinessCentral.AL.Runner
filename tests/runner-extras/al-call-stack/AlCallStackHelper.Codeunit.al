/// <summary>
/// Helper codeunit used by the AL call stack tests to produce a two-frame AL stack:
/// "AlCallStackHelper(CodeUnit 60901).RaiseError" on top of the test frame.
/// </summary>
codeunit 60901 "AL Call Stack Helper"
{
    procedure RaiseError()
    begin
        Error('AL call stack test error');
    end;
}
