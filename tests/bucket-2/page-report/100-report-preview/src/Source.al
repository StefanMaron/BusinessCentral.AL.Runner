table 98000 "RP Test Data"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

report 98000 "RP Preview Report"
{
    dataset
    {
        dataitem(RPTestData; "RP Test Data") { }
    }

    trigger OnInitReport()
    var
        InPreviewMode: Boolean;
    begin
        InPreviewMode := CurrReport.Preview();
    end;
}

codeunit 98001 "RP Preview Runner"
{
    procedure RunPreviewReport()
    var
        Rep: Report "RP Preview Report";
    begin
        Rep.Run();
    end;
}
