/// Source objects for Report.Run 4-arg overload test (issue #1156).
table 29800 "RR4 Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Amount; Decimal) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

/// Report that accepts RR4 Table as a data item.
report 29800 "RR4 Report"
{
    dataset
    {
        dataitem(DataLine; "RR4 Table")
        {
        }
    }
}

/// Exercises Report.Run with 4 arguments: (ReportId, RequestPage, SystemPrinter, Record)
codeunit 29801 "RR4 Source"
{
    procedure CallRunFourArgs(ReportId: Integer; Rec: Record "RR4 Table")
    begin
        Report.Run(ReportId, false, false, Rec);
    end;

    procedure SetupTableAndRun(ReportId: Integer): Text
    var
        Rec: Record "RR4 Table";
    begin
        // Insert a record so SetFilter has something to work with
        Rec.Init();
        Rec."No." := 'R4-001';
        Rec.Amount := 42;
        Rec.Insert();

        // Apply a filter to the record variable before passing to Report.Run
        Rec.SetFilter("No.", 'R4-001');

        // Call Report.Run with 4 args — must not throw
        Report.Run(ReportId, false, false, Rec);

        // Return the filter still applied after the call (filter must survive)
        exit(Rec.GetFilter("No."));
    end;
}
