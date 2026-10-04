report 73001 "OT Report"
{
    ProcessingOnly = true;
    dataset
    {
        dataitem(Row; "OT Rec")
        {
            trigger OnPreDataItem() var T: Codeunit "OT Target"; R: Integer; begin R := T.MRepPreData(1); end;
            trigger OnAfterGetRecord() var T: Codeunit "OT Target"; R: Integer; begin R := T.MRepAfterGet(1); end;
            trigger OnPostDataItem() var T: Codeunit "OT Target"; R: Integer; begin R := T.MRepPostData(1); end;
        }
    }
    requestpage
    {
        layout { area(content) { field(Opt; Opt) { ApplicationArea = All; trigger OnValidate() var T: Codeunit "OT Target"; R: Integer; begin R := T.MRepReqValidate(1); end; } } }
        trigger OnOpenPage() var T: Codeunit "OT Target"; R: Integer; begin R := T.MRepReqOpen(1); end;
        trigger OnQueryClosePage(CloseAction: Action): Boolean var T: Codeunit "OT Target"; R: Integer; begin R := T.MRepReqQuery(1); exit(true); end;
        trigger OnClosePage() var T: Codeunit "OT Target"; R: Integer; begin R := T.MRepReqClose(1); end;
    }
    var Opt: Integer;
    trigger OnInitReport() var T: Codeunit "OT Target"; R: Integer; begin R := T.MRepInit(1); end;
    trigger OnPreReport() var T: Codeunit "OT Target"; R: Integer; begin R := T.MRepPre(1); end;
    trigger OnPostReport() var T: Codeunit "OT Target"; R: Integer; begin R := T.MRepPost(1); end;
}

reportextension 73003 "OT Report Ext" extends "OT Report"
{
    dataset
    {
        modify(Row)
        {
            trigger OnBeforeAfterGetRecord() var T: Codeunit "OT Target"; R: Integer; begin R := T.MExtBeforeAfterGet(1); end;
            trigger OnAfterAfterGetRecord() var T: Codeunit "OT Target"; R: Integer; begin R := T.MExtAfterAfterGet(1); end;
        }
        addlast(Row)
        {
            dataitem(ExtRow; "OT Rec")
            {
                trigger OnPreDataItem() var T: Codeunit "OT Target"; R: Integer; begin R := T.MExtRowPre(1); end;
                trigger OnAfterGetRecord() var T: Codeunit "OT Target"; R: Integer; begin R := T.MExtRowGet(1); end;
                trigger OnPostDataItem() var T: Codeunit "OT Target"; R: Integer; begin R := T.MExtRowPost(1); end;
            }
        }
    }
    requestpage
    {
        layout { addlast(content) { field(ExtOpt; ExtOpt) { ApplicationArea = All; trigger OnValidate() var T: Codeunit "OT Target"; R: Integer; begin R := T.MExtReqValidate(1); end; } } }
    }
    var ExtOpt: Integer;
    trigger OnPreReport() var T: Codeunit "OT Target"; R: Integer; begin R := T.MExtPre(1); end;
    trigger OnPostReport() var T: Codeunit "OT Target"; R: Integer; begin R := T.MExtPost(1); end;
}

// A second report, so a run of the first is shown not to start it, and a third whose triggers call nothing.
report 73006 "OT Other Report"
{
    ProcessingOnly = true;
    UseRequestPage = false;
    trigger OnPreReport() var T: Codeunit "OT Target"; R: Integer; begin R := T.MOtherPre(1); end;
}

report 73009 "OT Quiet Report"
{
    ProcessingOnly = true;
    UseRequestPage = false;
    trigger OnPreReport() begin end;
}
