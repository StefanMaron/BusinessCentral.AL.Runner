// Fixture for codeunit 73400 "BNR Blank Part Tests" (and its probe): a list over the stored table whose action inserts and positions Rec.
page 73407 "BNR Real List"
{
    PageType = List;
    SourceTable = "BNR Line";
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
    actions
    {
        area(Processing)
        {
            action(InsertFind)
            {
                ApplicationArea = All;
                trigger OnAction()
                begin
                    Rec.Init();
                    Rec."Header No." := 'R';
                    Rec."Line No." := 1;
                    Rec.QTxt := 'real';
                    Rec.QInt := 4;
                    Rec.Insert();
                    Rec.FindFirst();
                end;
            }
        }
    }
}
