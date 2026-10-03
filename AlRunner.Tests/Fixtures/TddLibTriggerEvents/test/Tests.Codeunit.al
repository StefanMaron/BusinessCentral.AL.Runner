/// <summary>
/// #5286 across source bundles: the trigger, the subscriber or the OnRun is in a bundle other than the one
/// holding the operation that starts it. Every test passes against the stubs. The "IsNotAnnotated" ones start
/// nothing that reaches a stub.
/// </summary>
codeunit 72421 "TLib Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure InsertTrue_RunsTheTriggerOfTheAppsTable()
    var
        Rec: Record "TLib Trig Rec";
    begin
        Rec.PK := 'T01';
        Rec.Insert(true);
    end;

    [Test]
    procedure Insert_RaisesTheEventTheLibrarySubscribesTo()
    var
        Rec: Record "TLib Rec";
    begin
        Rec.PK := 'T02';
        Rec.Insert();
    end;

    [Test]
    procedure AppProcedureInserting_RaisesTheEventTheLibrarySubscribesTo()
    var
        Writer: Codeunit "TLib Writer";
    begin
        Writer.Write('T03');
    end;

    [Test]
    procedure ModifyTrue_RunsTheTriggerOfTheLibrarysTableExtension()
    var
        Rec: Record "TLib Rec";
    begin
        Rec.PK := 'T04';
        Rec.Insert();
        Rec.Qty := 5;
        Rec.Modify(true);
    end;

    [Test]
    procedure AppProcedureModifying_RunsTheTriggerOfTheLibrarysTableExtension()
    var
        Writer: Codeunit "TLib Writer";
    begin
        Writer.Write('T05');
        Writer.ModifyRec('T05');
    end;

    [Test]
    procedure AppProcedureDeleting_RaisesTheEventTheTestBundleSubscribesTo()
    var
        Writer: Codeunit "TLib Writer";
    begin
        Writer.Write('T06');
        Writer.Remove('T06');
    end;

    [Test]
    procedure CodeunitRunByName_RunsTheLibrarysOnRun()
    begin
        Codeunit.Run(Codeunit::"TLib Runner");
    end;

    [Test]
    procedure AppProcedureRunningById_RunsTheLibrarysOnRun()
    var
        Writer: Codeunit "TLib Writer";
    begin
        Writer.RunById(Codeunit::"TLib Runner");
    end;

    [Test]
    procedure AppProcedureInsertingThroughARecordRef_StartsTheTriggerOfAnyTable()
    var
        Writer: Codeunit "TLib Writer";
    begin
        Writer.InsertAny(Database::"TLib Trig Rec", 'T09');
    end;

    [Test]
    procedure AppProcedureInsertingThroughARecordRef_StartsTheTriggerOfALaterBundlesTable()
    var
        Writer: Codeunit "TLib Writer";
    begin
        Writer.InsertAny(Database::"TLib Lib Rec", 'T12');
    end;

    [Test]
    procedure RecordRefInsertInTheTestBundle_StartsTheTriggerOfTheAppsTable()
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(Database::"TLib Trig Rec");
        RecRef.Field(1).Value := 'T13';
        RecRef.Insert(true);
    end;

    [Test]
    procedure InsertIntoTheChainTable_NeedsASecondRound()
    var
        Rec: Record "TLib Chain Rec";
    begin
        Rec.PK := 'T10';
        Rec.Insert();
    end;

    [Test]
    procedure AppProcedureWritingToAQuietTable_IsNotAnnotated()
    var
        Writer: Codeunit "TLib Writer";
    begin
        Writer.WriteQuiet('T11');
    end;

    [Test]
    procedure Quiet_IsNotAnnotated()
    begin
    end;
}
