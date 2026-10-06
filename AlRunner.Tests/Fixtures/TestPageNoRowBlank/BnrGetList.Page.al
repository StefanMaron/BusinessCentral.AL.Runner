// Fixture for codeunit 73400 "BNR Blank Part Tests" (and its probe): a list filtered to nothing whose action positions Rec on a stored row with Get.
page 73406 "BNR Get List"
{
    PageType = List;
    SourceTable = "BNR Line";
    SourceTableView = where("Header No." = const('ZZZ'));
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
            action(GetRow)
            {
                ApplicationArea = All;
                trigger OnAction()
                begin
                    Rec.Get('H1', 10);
                end;
            }
            action(InsertMatching)
            {
                ApplicationArea = All;
                trigger OnAction()
                begin
                    Rec.Init();
                    Rec."Header No." := 'ZZZ';
                    Rec."Line No." := 1;
                    Rec.QTxt := 'matching';
                    Rec.QInt := 3;
                    Rec.Insert();
                    Rec.FindFirst();
                end;
            }
        }
    }
}
