/// <summary>
/// #5286: every test passes (the stubs return the default and nothing asserts on them). The name says which
/// path reaches a stub; the "IsNotAnnotated" ones reach none.
/// </summary>
codeunit 72310 "Trg Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure InsertTrue_RunsTheOnInsertTrigger()
    var
        Rec: Record "Trg Rec";
    begin
        Rec.PK := 'K01';
        Rec.Insert(true);
    end;

    [Test]
    procedure InsertWithoutRunTrigger_IsNotAnnotated()
    var
        Rec: Record "Trg Rec";
    begin
        Rec.PK := 'K02';
        Rec.Insert();
    end;

    [Test]
    procedure InsertFalse_IsNotAnnotated()
    var
        Rec: Record "Trg Rec";
    begin
        Rec.PK := 'K03';
        Rec.Insert(false);
    end;

    [Test]
    procedure ModifyTrue_RunsTheOnModifyTrigger()
    var
        Rec: Record "Trg Rec";
    begin
        Rec.PK := 'K04';
        Rec.Insert();
        Rec.Qty := 2;
        Rec.Modify(true);
    end;

    [Test]
    procedure DeleteTrue_RunsTheOnDeleteTrigger()
    var
        Rec: Record "Trg Rec";
    begin
        Rec.PK := 'K05';
        Rec.Insert();
        Rec.Delete(true);
    end;

    [Test]
    procedure RenameRunsTheOnRenameTrigger()
    var
        Rec: Record "Trg Rec";
    begin
        Rec.PK := 'K06';
        Rec.Insert();
        Rec.Rename(Rec.PK + 'R');
    end;

    [Test]
    procedure ValidateRunsTheFieldOnValidateTrigger()
    var
        Rec: Record "Trg Rec";
    begin
        Rec.PK := 'K07';
        Rec.Validate(Qty, 3);
    end;

    [Test]
    procedure InsertTrue_RaisesTheEventItsTriggerRaises()
    var
        Rec: Record "Trg Event Rec";
    begin
        Rec.PK := 'K08';
        Rec.Insert(true);
    end;

    [Test]
    procedure Insert_RaisesOnBeforeInsertEvent()
    var
        Rec: Record "Trg Db Rec";
    begin
        Rec.PK := 'K09';
        Rec.Insert();
    end;

    [Test]
    procedure Modify_RaisesOnAfterModifyEvent()
    var
        Rec: Record "Trg Db Rec";
    begin
        Rec.PK := 'K10';
        Rec.Insert();
        Rec.Qty := 2;
        Rec.Modify();
    end;

    [Test]
    procedure Delete_RaisesOnAfterDeleteEvent()
    var
        Rec: Record "Trg Db Rec";
    begin
        Rec.PK := 'K11';
        Rec.Insert();
        Rec.Delete();
    end;

    [Test]
    procedure Rename_RaisesOnAfterRenameEvent()
    var
        Rec: Record "Trg Db Rec";
    begin
        Rec.PK := 'K12';
        Rec.Insert();
        Rec.Rename(Rec.PK + 'R');
    end;

    [Test]
    procedure Validate_RaisesOnAfterValidateEvent()
    var
        Rec: Record "Trg Db Rec";
    begin
        Rec.PK := 'K13';
        Rec.Validate(Qty, 3);
    end;

    [Test]
    procedure InsertIntoAQuietTable_IsNotAnnotated()
    var
        Rec: Record "Trg Quiet Rec";
    begin
        Rec.PK := 'K14';
        Rec.Insert(true);
        Rec.Modify(true);
        Rec.Delete(true);
    end;

    [Test]
    procedure CodeunitRunByName_RunsOnRun()
    begin
        Codeunit.Run(Codeunit::"Trg Runner");
    end;

    [Test]
    procedure CodeunitRunOfAVariable_RunsOnRun()
    var
        Runner: Codeunit "Trg Runner";
    begin
        Runner.Run();
    end;

    [Test]
    procedure CodeunitRunWithARecord_RunsOnRun()
    var
        Rec: Record "Trg Quiet Rec";
    begin
        Rec.PK := 'K15';
        Codeunit.Run(Codeunit::"Trg Rec Runner", Rec);
    end;

    [Test]
    procedure CodeunitRun_RaisesTheEventItsOnRunRaises()
    begin
        Codeunit.Run(Codeunit::"Trg Event Runner");
    end;

    [Test]
    procedure CodeunitRunOfAQuietCodeunit_IsNotAnnotated()
    begin
        Codeunit.Run(Codeunit::"Trg Quiet Runner");
    end;

    [Test]
    procedure Quiet_IsNotAnnotated()
    begin
    end;

    [Test]
    procedure BareInsertInsideTheTable_RunsTheOnInsertTrigger()
    var
        Rec: Record "Trg Rec";
    begin
        Rec.PK := 'K30';
        Rec.InsertSelf();
    end;

    [Test]
    procedure DeleteAllTrue_RunsTheOnDeleteTrigger()
    var
        Rec: Record "Trg Rec";
    begin
        Rec.DeleteAll(true);
    end;

    [Test]
    procedure ModifyAllTrue_RunsTheOnModifyTrigger()
    var
        Rec: Record "Trg Rec";
    begin
        Rec.ModifyAll(Qty, 4, true);
    end;

    [Test]
    procedure ModifyAllWithoutRunTrigger_IsNotAnnotated()
    var
        Rec: Record "Trg Rec";
    begin
        Rec.ModifyAll(Qty, 4);
    end;

    [Test]
    procedure RecordRefInsertTrue_StartsTheTriggerOfAnyTable()
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(Database::"Trg Rec");
        RecRef.Field(1).Value := 'K31';
        RecRef.Insert(true);
    end;

    [Test]
    procedure FieldRefValidate_StartsTheFieldTriggerOfAnyTable()
    var
        RecRef: RecordRef;
        Field: FieldRef;
    begin
        RecRef.Open(Database::"Trg Rec");
        Field := RecRef.Field(2);
        Field.Validate(3);
    end;

    [Test]
    procedure InsertIntoTheChainTable_NeedsASecondRound()
    var
        Rec: Record "Trg Chain Rec";
    begin
        Rec.PK := 'K32';
        Rec.Insert();
    end;

    [Test]
    procedure ListInsert_IsNotARecordOperation_IsNotAnnotated()
    var
        Values: List of [Text];
    begin
        Values.Add('a');
        Values.Insert(1, 'b');
    end;

    [Test]
    procedure CodeunitRunOfAQuietVariable_IsNotAnnotated()
    var
        Runner: Codeunit "Trg Quiet Runner";
    begin
        Runner.Run();
    end;
}
