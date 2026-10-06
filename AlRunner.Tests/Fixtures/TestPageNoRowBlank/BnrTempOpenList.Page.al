// Fixture for codeunit 73400 "BNR Blank Part Tests" (and its probe): a temporary-source list that inserts rows in OnOpenPage.
page 73405 "BNR Temp Open List"
{
    PageType = List;
    SourceTable = "BNR Line";
    SourceTableTemporary = true;
    ApplicationArea = All;
    UsageCategory = None;

    layout
    {
        area(Content)
        {
            repeater(Lines)
            {
                field(HeaderNo; Rec."Header No.") { ApplicationArea = All; }
                field(LineNo; Rec."Line No.") { ApplicationArea = All; }
                field(QTxt; Rec.QTxt) { ApplicationArea = All; }
                field(QInt; Rec.QInt) { ApplicationArea = All; }
            }
        }
    }

    trigger OnOpenPage()
    begin
        Rec.Init();
        Rec."Header No." := 'T';
        Rec."Line No." := 1;
        Rec.QTxt := 'opened';
        Rec.QInt := 7;
        Rec.Insert();
        Rec.Init();
        Rec."Header No." := 'T';
        Rec."Line No." := 2;
        Rec.QTxt := 'opened2';
        Rec.QInt := 8;
        Rec.Insert();
    end;
}
