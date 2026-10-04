page 72803 "PT Card"
{
    PageType = Card;
    SourceTable = "PT Rec";
    layout
    {
        area(Content)
        {
            field(PK; Rec.PK) { }
            field(Qty; Rec.Qty)
            {
                trigger OnValidate() var T: Codeunit "PT Target"; R: Integer; begin R := T.MQtyValidate(1); end;
                trigger OnLookup(var Text: Text): Boolean var T: Codeunit "PT Target"; R: Integer; begin R := T.MQtyLookup(1); end;
                trigger OnDrillDown() var T: Codeunit "PT Target"; R: Integer; begin R := T.MQtyDrill(1); end;
                trigger OnAssistEdit() var T: Codeunit "PT Target"; R: Integer; begin R := T.MQtyAssist(1); end;
            }
            field(Note; Rec.Note)
            {
                trigger OnValidate() var T: Codeunit "PT Target"; R: Integer; begin R := T.MNoteValidate(1); end;
            }
            field(Ref; Rec.Ref) { }
        }
    }
    actions
    {
        area(Processing)
        {
            action(Go) { trigger OnAction() var T: Codeunit "PT Target"; R: Integer; begin R := T.MGo(1); end; }
            // What a page's code asks of its own page: a save runs the page's modify trigger, a close its close triggers.
            action(SaveRow) { trigger OnAction() begin Rec.Qty := 9; CurrPage.SaveRecord(); end; }
            action(UpdateRow) { trigger OnAction() begin Rec.Qty := 9; CurrPage.Update(true); end; }
            action(CloseIt) { trigger OnAction() begin CurrPage.Close(); end; }
            action(RecModify) { trigger OnAction() begin Rec.Qty := 9; Rec.Modify(true); end; }
            action(Other) { trigger OnAction() var T: Codeunit "PT Target"; R: Integer; begin R := T.MOther(1); end; }
        }
    }
    trigger OnInit() var T: Codeunit "PT Target"; R: Integer; begin R := T.MInit(1); end;
    trigger OnOpenPage() var T: Codeunit "PT Target"; R: Integer; begin R := T.MOpen(1); end;
    trigger OnClosePage() var T: Codeunit "PT Target"; R: Integer; begin R := T.MClose(1); end;
    trigger OnQueryClosePage(CloseAction: Action): Boolean var T: Codeunit "PT Target"; R: Integer; begin R := T.MQueryClose(1); exit(true); end;
    trigger OnNewRecord(BelowxRec: Boolean) var T: Codeunit "PT Target"; R: Integer; begin R := T.MNew(1); end;
    trigger OnInsertRecord(BelowxRec: Boolean): Boolean var T: Codeunit "PT Target"; R: Integer; begin R := T.MInsert(1); exit(true); end;
    trigger OnModifyRecord(): Boolean var T: Codeunit "PT Target"; R: Integer; begin R := T.MModify(1); exit(true); end;
    trigger OnDeleteRecord(): Boolean var T: Codeunit "PT Target"; R: Integer; begin R := T.MDelete(1); exit(true); end;
    trigger OnAfterGetRecord() var T: Codeunit "PT Target"; R: Integer; begin R := T.MAfterGet(1); end;
    trigger OnAfterGetCurrRecord() var T: Codeunit "PT Target"; R: Integer; begin R := T.MAfterGetCurr(1); end;
    trigger OnFindRecord(Which: Text): Boolean var T: Codeunit "PT Target"; R: Integer; begin R := T.MFind(1); exit(Rec.Find(Which)); end;
    trigger OnNextRecord(Steps: Integer): Integer var T: Codeunit "PT Target"; R: Integer; begin R := T.MNext(1); exit(Rec.Next(Steps)); end;
    trigger OnPageBackgroundTaskCompleted(TaskId: Integer; Results: Dictionary of [Text, Text]) var T: Codeunit "PT Target"; R: Integer; begin R := T.MBackground(1); end;
}

// What a page extension adds: triggers of its own, a field with a trigger, an action.
pageextension 72804 "PT Card Ext" extends "PT Card"
{
    layout
    {
        addlast(Content)
        {
            field(Extra; Rec.Extra)
            {
                trigger OnValidate() var T: Codeunit "PT Target"; R: Integer; begin R := T.MExtValidate(1); end;
            }
        }
    }
    actions
    {
        addlast(Processing)
        {
            action(ExtGo) { trigger OnAction() var T: Codeunit "PT Target"; R: Integer; begin R := T.MExtAction(1); end; }
        }
    }
    trigger OnOpenPage() var T: Codeunit "PT Target"; R: Integer; begin R := T.MExtOpen(1); end;
    trigger OnAfterGetRecord() var T: Codeunit "PT Target"; R: Integer; begin R := T.MExtAfterGet(1); end;
}

// A page with no code at all: opening it reaches nothing.
page 72805 "PT Quiet Card"
{
    PageType = Card;
    SourceTable = "PT Quiet";
    layout { area(Content) { field(K; Rec.K) { } } }
}
