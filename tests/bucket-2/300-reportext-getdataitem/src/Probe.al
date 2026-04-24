// Probe exists so that the companion test codeunit can assert a specific
// constant — proves the reportextension (70701) compiled and the assembly
// loaded successfully. All of these objects share one compilation unit;
// if the reportextension fails to compile (CS1061 on GetDataItem /
// ParentObject — issue #1212), the entire assembly does not load and
// NO tests run.
codeunit 70703 "RptExt GetDI Probe"
{
    procedure GapIssueNumber(): Integer
    begin
        exit(1212);
    end;

    procedure FailWithMarker()
    begin
        Error('gap-1212');
    end;
}
