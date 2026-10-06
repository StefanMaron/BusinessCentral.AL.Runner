// Fixture for codeunit 73400 "BNR Blank Part Tests": a top-level list over the same table.
page 73402 "BNR Lines List"
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
                field(QCd; Rec.QCd) { ApplicationArea = All; }
                field(QInt; Rec.QInt) { ApplicationArea = All; }
                field(QDec; Rec.QDec) { ApplicationArea = All; }
                field(QBool; Rec.QBool) { ApplicationArea = All; }
                field(QOpt; Rec.QOpt) { ApplicationArea = All; }
                field(QDt; Rec.QDt) { ApplicationArea = All; }
                field(QTm; Rec.QTm) { ApplicationArea = All; }
                field(QDtTm; Rec.QDtTm) { ApplicationArea = All; }
                field(QEn; Rec.QEn) { ApplicationArea = All; }
                field(QBig; Rec.QBig) { ApplicationArea = All; }
                field(QGd; Rec.QGd) { ApplicationArea = All; }
                field(QDur; Rec.QDur) { ApplicationArea = All; }
                field(QIntInit; Rec.QIntInit) { ApplicationArea = All; }
                field(QDecInit; Rec.QDecInit) { ApplicationArea = All; }
                field(QBoolInit; Rec.QBoolInit) { ApplicationArea = All; }
            }
        }
    }
}
