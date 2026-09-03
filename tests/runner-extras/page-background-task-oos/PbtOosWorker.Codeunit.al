codeunit 63700 "Pbtoos Worker"
{
    // The runner's RunnerOutOfScopeException fires before this codeunit ever runs (see
    // PbtOosTest.Codeunit.al) -- its body only needs to compile, never execute.
    trigger OnRun()
    var
        Results: Dictionary of [Text, Text];
    begin
        Results.Add('Count', 'never runs');
    end;
}
