// The second linked part, and the one that makes the MissingTestDataDiagnosis half of #3189
// provable. Its row trigger fails on a MISSING SETUP RECORD rather than on a bare Error(), so
// the converted exception carries the typed evidence that diagnosis needs — the AL table id, put
// there by RecordWritePatches.BuildRecordNotFoundException — behind the same TestPage mask.
//
// Without the link that MissingTestDataDiagnosis.TryNameTable now follows, that evidence is
// unreachable and the failure gets no [test-data] explanation, which is exactly the shape #2240
// exists to explain going unexplained because it happened inside a page trigger.
page 70547 "MTD Setup Lines"
{
    PageType = ListPart;
    SourceTable = "MTD Line";
    ApplicationArea = All;

    layout
    {
        area(Content)
        {
            repeater(Group)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
                field("Line No."; Rec."Line No.") { ApplicationArea = All; }
            }
        }
    }

    trigger OnAfterGetRecord()
    var
        Setup: Record "MTD Setup";
    begin
        // A statement-position Get() raises; "MTD Setup" has no rows and never gets any.
        Setup.Get('X');
    end;
}
