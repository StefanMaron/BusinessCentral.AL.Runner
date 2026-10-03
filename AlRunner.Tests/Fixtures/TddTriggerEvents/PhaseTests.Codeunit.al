using TrgNs;

/// <summary>
/// #5286: every test passes. The extension's OnBefore/OnAfter triggers are started by the operation of the same
/// name, under the same RunTrigger rule as OnInsert; "AssignmentOnly" and the Insert() without RunTrigger start none.
/// </summary>
codeunit 72311 "Trg Phase Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure InsertTrue_RunsTheExtensionsBeforeAndAfterInsert()
    var
        Rec: Record "Trg Phase Rec";
    begin
        Rec.PK := 'P01';
        Rec.Insert(true);
    end;

    [Test]
    procedure ModifyTrue_RunsTheExtensionsBeforeAndAfterModify()
    var
        Rec: Record "Trg Phase Rec";
    begin
        Rec.PK := 'P02';
        Rec.Insert();
        Rec.Qty := 2;
        Rec.Modify(true);
    end;

    [Test]
    procedure DeleteTrue_RunsTheExtensionsBeforeAndAfterDelete()
    var
        Rec: Record "Trg Phase Rec";
    begin
        Rec.PK := 'P03';
        Rec.Insert();
        Rec.Delete(true);
    end;

    [Test]
    procedure Rename_RunsTheExtensionsBeforeAndAfterRename()
    var
        Rec: Record "Trg Phase Rec";
    begin
        Rec.PK := 'P04';
        Rec.Insert();
        Rec.Rename(Rec.PK + 'R');
    end;

    [Test]
    procedure Validate_RunsTheModifyBlocksBeforeAndAfterValidate()
    var
        Rec: Record "Trg Phase Rec";
    begin
        Rec.PK := 'P05';
        Rec.Validate(Qty, 3);
    end;

    [Test]
    procedure InsertOmittingRunTrigger_ExtensionTriggersDoNotRun_IsNotAnnotated()
    var
        Rec: Record "Trg Phase Rec";
    begin
        Rec.PK := 'P06';
        Rec.Insert();
    end;

    [Test]
    procedure AssignmentOnly_StartsNoTriggerAndNotTheLookup_IsNotAnnotated()
    var
        Rec: Record "Trg Phase Rec";
    begin
        Rec.PK := 'P07';
        Rec.Qty := 3;
        Rec.Lk := 'x';
    end;

    [Test]
    procedure InsertTrue_RunsTheOnAfterInsertOfAnExtensionNamingItsTableWithItsNamespace()
    var
        Rec: Record "Trg Ns Rec";
    begin
        Rec.PK := 'P08';
        Rec.Insert(true);
    end;
}
