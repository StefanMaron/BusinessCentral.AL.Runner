table 84500 "RDT Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Amount; Decimal) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

report 84501 "RDT Report"
{
    dataset
    {
        dataitem(RDTRecord; "RDT Record")
        {
            trigger OnPreDataItem()
            begin
                PreDataItemCount += 1;
            end;

            trigger OnAfterGetRecord()
            begin
                TotalAmount += RDTRecord.Amount;
                RecordCount += 1;
            end;

            trigger OnPostDataItem()
            begin
                PostDataItemCount += 1;
            end;
        }
    }

    var
        TotalAmount: Decimal;
        RecordCount: Integer;
        PreDataItemCount: Integer;
        PostDataItemCount: Integer;

    trigger OnPreReport()
    begin
        TotalAmount := 0;
        RecordCount := 0;
    end;

    trigger OnPostReport()
    begin
        // nothing
    end;

    procedure GetTotalAmount(): Decimal
    begin
        exit(TotalAmount);
    end;

    procedure GetRecordCount(): Integer
    begin
        exit(RecordCount);
    end;

    procedure GetPreDataItemCount(): Integer
    begin
        exit(PreDataItemCount);
    end;

    procedure GetPostDataItemCount(): Integer
    begin
        exit(PostDataItemCount);
    end;
}

codeunit 84502 "RDT Src"
{
    procedure RunReport()
    var
        Rep: Report "RDT Report";
    begin
        Rep.UseRequestPage(false);
        Rep.Run();
    end;
}
