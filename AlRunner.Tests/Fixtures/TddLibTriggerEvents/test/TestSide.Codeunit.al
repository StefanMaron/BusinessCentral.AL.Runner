/// <summary>
/// #5286: subscribers in the test bundle itself. "HandleBeforeDelete" reaches its stub directly. The other two
/// need a second round: the stub is called by "HandleChainValidate", which is started by the Validate in "Touch",
/// which "HandleChainInsert" calls, and that one is started by an insert into the app's "TLib Chain Rec".
/// </summary>
codeunit 72420 "TLib Test Subscriber"
{
    [EventSubscriber(ObjectType::Table, Database::"TLib Rec", 'OnBeforeDeleteEvent', '', false, false)]
    local procedure HandleBeforeDelete(var Rec: Record "TLib Rec"; RunTrigger: Boolean)
    var
        Target: Codeunit "TLib Target";
        Result: Integer;
    begin
        Result := Target.MissingTestDelete(1);
    end;

    [EventSubscriber(ObjectType::Table, Database::"TLib Chain Rec", 'OnAfterInsertEvent', '', false, false)]
    local procedure HandleChainInsert(var Rec: Record "TLib Chain Rec"; RunTrigger: Boolean)
    begin
        Touch();
    end;

    [EventSubscriber(ObjectType::Table, Database::"TLib Rec", 'OnAfterValidateEvent', 'Qty', false, false)]
    local procedure HandleChainValidate(var Rec: Record "TLib Rec"; var xRec: Record "TLib Rec"; CurrFieldNo: Integer)
    var
        Target: Codeunit "TLib Target";
        Result: Integer;
    begin
        Result := Target.MissingChain(1);
    end;

    local procedure Touch()
    var
        Rec: Record "TLib Rec";
    begin
        Rec.Validate(Qty, 1);
    end;
}
