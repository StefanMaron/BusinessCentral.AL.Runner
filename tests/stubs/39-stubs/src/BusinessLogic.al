codeunit 53902 "Business Logic"
{
    procedure RunWithProcessor(var Rec: Record "Stub Table"): Decimal
    var
        Processor: Codeunit "Heavy Processor";
    begin
        if Processor.ProcessData(Rec) then
            exit(Rec."Value")
        else
            exit(-1);
    end;
}
