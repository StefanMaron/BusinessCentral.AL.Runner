/// <summary>
/// #5286: subscribers of database events (they have no publisher procedure in any bundle) and of the
/// event "Trg Event Rec" raises from its trigger. Each calls its own missing member.
/// </summary>
codeunit 72303 "Trg Db Subscriber"
{
    [EventSubscriber(ObjectType::Table, Database::"Trg Db Rec", 'OnBeforeInsertEvent', '', false, false)]
    local procedure HandleBeforeInsert(var Rec: Record "Trg Db Rec"; RunTrigger: Boolean)
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingBeforeInsert(1);
    end;

    [EventSubscriber(ObjectType::Table, Database::"Trg Db Rec", 'OnAfterModifyEvent', '', false, false)]
    local procedure HandleAfterModify(var Rec: Record "Trg Db Rec"; var xRec: Record "Trg Db Rec"; RunTrigger: Boolean)
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingAfterModify(1);
    end;

    [EventSubscriber(ObjectType::Table, Database::"Trg Db Rec", 'OnAfterDeleteEvent', '', false, false)]
    local procedure HandleAfterDelete(var Rec: Record "Trg Db Rec"; RunTrigger: Boolean)
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingAfterDelete(1);
    end;

    [EventSubscriber(ObjectType::Table, Database::"Trg Db Rec", 'OnAfterRenameEvent', '', false, false)]
    local procedure HandleAfterRename(var Rec: Record "Trg Db Rec"; var xRec: Record "Trg Db Rec"; RunTrigger: Boolean)
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingAfterRename(1);
    end;

    [EventSubscriber(ObjectType::Table, Database::"Trg Db Rec", 'OnAfterValidateEvent', 'Qty', false, false)]
    local procedure HandleAfterValidate(var Rec: Record "Trg Db Rec"; var xRec: Record "Trg Db Rec"; CurrFieldNo: Integer)
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingAfterValidate(1);
    end;

    [EventSubscriber(ObjectType::Table, Database::"Trg Event Rec", 'OnTrig', '', false, false)]
    local procedure HandleTrig()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingTrigEvent(1);
    end;

    [EventSubscriber(ObjectType::Table, Database::"Trg Chain Rec", 'OnAfterInsertEvent', '', false, false)]
    local procedure HandleChainInsert(var Rec: Record "Trg Chain Rec"; RunTrigger: Boolean)
    begin
        ModifyOther();
    end;

    [EventSubscriber(ObjectType::Table, Database::"Trg Chain Rec 2", 'OnAfterModifyEvent', '', false, false)]
    local procedure HandleChainModify(var Rec: Record "Trg Chain Rec 2"; var xRec: Record "Trg Chain Rec 2"; RunTrigger: Boolean)
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingChain(1);
    end;

    local procedure ModifyOther()
    var
        Other: Record "Trg Chain Rec 2";
    begin
        Other.PK := 'C';
        Other.Insert();
        Other.Modify();
    end;
}
