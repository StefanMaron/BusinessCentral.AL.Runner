codeunit 310009 "TNP Get Status"
{
    TableNo = "TNP Test Record";

    trigger OnRun()
    begin
        GetStatus(Rec);
    end;

    local procedure GetStatus(var TestRecord: Record "TNP Test Record")
    begin
        TestRecord.Status := 'FromLocal';
        TestRecord.Modify();
    end;
}
