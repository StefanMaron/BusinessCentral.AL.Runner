// Declares TableNo and nothing else: the row read back from it pins TableNo (non-zero),
// SingleInstance (AL default false) and Subtype (AL default Normal) in one go.
codeunit 60762 "CMV Bound"
{
    TableNo = "CMV Target";

    trigger OnRun()
    begin
        Rec."No." := 'RAN';
    end;
}
