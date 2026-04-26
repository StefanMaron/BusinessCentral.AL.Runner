codeunit 410003 "NIW Get Status"
{
    TableNo = "NIW Test Record";

    trigger OnRun()
    begin
        GetStatus(Rec);
    end;

    local procedure GetStatus(var TestRecord: Record "NIW Test Record")
    begin
        TestRecord.Status := 'FromLocal';
        TestRecord.Modify();
    end;
}
