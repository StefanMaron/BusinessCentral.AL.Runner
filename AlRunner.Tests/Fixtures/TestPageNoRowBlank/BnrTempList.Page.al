// Fixture for codeunit 73400 "BNR Blank Part Tests" (and its probe): a temporary-source list whose actions fill and position Rec from AL (the shape of Navigate).
page 73404 "BNR Temp List"
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
                    Rec."Header No." := 'T';
                    Rec."Line No." := 1;
                    Rec.QTxt := 'found';
                    Rec.QInt := 7;
                    Rec.Insert();
                    Rec.FindFirst();
                end;
            }
            action(InsertFindUpdate)
            {
                ApplicationArea = All;
                trigger OnAction()
                begin
                    Rec.Init();
                    Rec."Header No." := 'T';
                    Rec."Line No." := 1;
                    Rec.QTxt := 'found';
                    Rec.QInt := 7;
                    Rec.Insert();
                    Rec.FindFirst();
                    CurrPage.Update(false);
                end;
            }
            action(InsertOnly)
            {
                ApplicationArea = All;
                trigger OnAction()
                begin
                    Rec.Init();
                    Rec."Header No." := 'T';
                    Rec."Line No." := 1;
                    Rec.QTxt := 'found';
                    Rec.QInt := 7;
                    Rec.Insert();
                end;
            }
            action(FieldsOnly)
            {
                ApplicationArea = All;
                trigger OnAction()
                begin
                    Rec.Init();
                    Rec.QTxt := 'unsaved';
                    Rec.QInt := 9;
                end;
            }
            action(KeyOnly)
            {
                ApplicationArea = All;
                trigger OnAction()
                begin
                    Rec.Init();
                    Rec."Header No." := 'K';
                    Rec."Line No." := 5;
                    Rec.QTxt := 'keyonly';
                    Rec.QInt := 5;
                end;
            }
            action(InsertTwoFindLast)
            {
                ApplicationArea = All;
                trigger OnAction()
                begin
                    Rec.Init();
                    Rec."Header No." := 'T';
                    Rec."Line No." := 1;
                    Rec.QTxt := 'first';
                    Rec.QInt := 1;
                    Rec.Insert();
                    Rec.Init();
                    Rec."Header No." := 'T';
                    Rec."Line No." := 2;
                    Rec.QTxt := 'second';
                    Rec.QInt := 2;
                    Rec.Insert();
                    Rec.FindLast();
                end;
            }
        }
    }
}
