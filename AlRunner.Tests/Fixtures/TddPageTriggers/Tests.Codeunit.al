codeunit 72807 "PT Tests"
{
    Subtype = Test;

    local procedure Seed()
    var R: Record "PT Rec";
    begin
        R.DeleteAll();
        R.PK := 'A';
        R.Insert();
    end;

    // What a page opened on a record starts: its open triggers and the row's, and the events around them.
    [Test] procedure OpenView_StartsTheOpenAndRowTriggers() var P: TestPage "PT Card"; begin Seed(); P.OpenView(); end;
    [Test] procedure OpenNew_StartsTheOpenAndNewRecordTriggers() var P: TestPage "PT Card"; begin P.OpenNew(); end;
    [Test] procedure Close_AddsTheCloseTriggersAndTheSaveOfTheRow() var P: TestPage "PT Card"; begin Seed(); P.OpenView(); P.Close(); end;
    [Test] procedure GoToKey_AddsTheSaveOfTheRowWhenItLeaves() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); if P.GoToKey('A') then; end;
    [Test] procedure SetValue_OnAFieldWithAPageTrigger_StartsThatFieldsTriggerAndTheSave() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); P.Qty.SetValue(2); end;
    [Test] procedure ValueAssigned_OnAFieldWithAPageTrigger_StartsThatFieldsTrigger() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); P.Note.Value := 'x'; end;
    [Test] procedure SetValue_OnAFieldAPageExtensionAdds_StartsTheExtensionsFieldTrigger() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); P.Extra.SetValue(4); end;
    [Test] procedure Lookup_OnAFieldWithAPageLookup_StartsTheLookupAndValidates() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); P.Qty.Lookup(); end;
    [Test] procedure Lookup_OnAFieldWithOnlyATableLookup_StartsTheTablesLookup() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); P.Ref.Lookup(); end;
    [Test] procedure Drilldown_StartsTheFieldsDrillDownOnly() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); P.Qty.Drilldown(); end;
    [Test] procedure AssistEdit_StartsTheFieldsAssistEditOnly() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); P.Qty.AssistEdit(); end;
    [Test] procedure Invoke_OnAnAction_StartsThatActionsOnActionOnly() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); P.Go.Invoke(); end;
    [Test] procedure Invoke_OnAnotherAction_StartsThatActionsOnActionOnly() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); P.Other.Invoke(); end;
    [Test] procedure Invoke_OnAnActionAPageExtensionAdds_StartsItsOnAction() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); P.ExtGo.Invoke(); end;
    [Test] procedure Invoke_InAHelperTheTestCalls_StartsTheOnAction() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); InvokeGo(P); end;
    [Test] procedure Reading_AnOpenPage_StartsNoMoreThanTheOpen() var P: TestPage "PT Card"; X: Text; begin Seed(); P.OpenView(); X := P.Qty.Value; X := P.Qty.Caption; if P.Qty.Editable() then; end;
    [Test] procedure QuietPage_StartsOnlyItsOwnOpenEvent() var P: TestPage "PT Quiet Card"; begin P.OpenNew(); end;
    [Test] procedure OpenView_OnAParentPage_StartsThePartsOpenTriggers() var P: TestPage "PT Parent"; begin P.OpenView(); end;
    [Test] procedure First_OnAPart_AddsThePartsSaveOfTheRow() var P: TestPage "PT Parent"; begin P.OpenView(); if P.Lines.First() then; end;
    [Test] procedure Invoke_OnAnActionThatSavesThroughCurrPage_StartsTheModifyTrigger() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); P.SaveRow.Invoke(); end;
    [Test] procedure Invoke_OnAnActionThatUpdatesThroughCurrPage_StartsTheModifyTrigger() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); P.UpdateRow.Invoke(); end;
    [Test] procedure Invoke_OnAnActionThatClosesThroughCurrPage_StartsTheCloseTriggers() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); P.CloseIt.Invoke(); end;
    [Test] procedure Invoke_OnAnActionThatModifiesTheRecord_StartsNoPageSave() var P: TestPage "PT Card"; begin Seed(); P.OpenEdit(); P.RecModify.Invoke(); end;
    [Test] [HandlerFunctions('CardModalHandler')] procedure RunModalOfAPage_StartsEveryTriggerOfThePage() var R: Codeunit "PT Runner"; begin R.RunModalCard(); end;
    [Test] [HandlerFunctions('CardModalHandler')] procedure RunModalOnAPageVariable_StartsEveryTriggerOfThePage() var R: Codeunit "PT Runner"; begin R.RunCard(); end;
    [Test] procedure OK_OnAModalPage_StartsTheCloseTriggers() var P: TestPage "PT Dialog"; begin P.OpenNew(); P.OK().Invoke(); end;
    [Test] [HandlerFunctions('CardPageHandler')] procedure Edit_OnAListPage_StartsTheTriggersOfAPageTheCallCannotName() var P: TestPage "PT List"; begin Seed(); P.OpenView(); P.Edit().Invoke(); end;
    [Test] procedure NoPage_IsNotAnnotated() begin Seed(); end;

    [ModalPageHandler] procedure CardModalHandler(var P: TestPage "PT Card") begin end;
    [PageHandler] procedure CardPageHandler(var P: TestPage "PT Card") begin end;

    local procedure InvokeGo(var P: TestPage "PT Card")
    begin
        P.Go.Invoke();
    end;
}
