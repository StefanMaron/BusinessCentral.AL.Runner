/// Tests for miscellaneous single-method gaps (issue #1382):
///   RecordRef.FieldExist(Text), RecordRef.FullyQualifiedName,
///   Codeunit.Run(Text,Table), DataTransfer.AddDestinationFilter,
///   Database.SelectLatestVersion(Integer), Session.LogMessage(9-arg),
///   Media.ImportStream(InStream,Text,Text,Text), Version.Create(4-arg).
codeunit 310490 "Misc Gaps Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        RecRefHelper: Codeunit "Misc Gaps RecordRef Helper";
        VersionHelper: Codeunit "Misc Gaps Version Helper";

    // ── RecordRef.FieldExist(Text) ─────────────────────────────────────────────

    [Test]
    procedure RecordRef_FieldExistByName_ExistingField_ReturnsTrue()
    begin
        // [GIVEN] Table 310400 has field named "No."
        // [WHEN] FieldExist is called with "No."
        // [THEN] Returns true
        Assert.IsTrue(RecRefHelper.FieldExistByName(310400, 'No.'), 'FieldExist(''No.'') must return true');
    end;

    [Test]
    procedure RecordRef_FieldExistByName_AnotherExistingField_ReturnsTrue()
    begin
        // [GIVEN] Table 310400 has field named "Name"
        // [WHEN] FieldExist is called with "Name"
        // [THEN] Returns true
        Assert.IsTrue(RecRefHelper.FieldExistByName(310400, 'Name'), 'FieldExist(''Name'') must return true');
    end;

    [Test]
    procedure RecordRef_FieldExistByName_NonExistingField_ReturnsFalse()
    begin
        // [GIVEN] Table 310400 does not have field named "DoesNotExist"
        // [WHEN] FieldExist is called with "DoesNotExist"
        // [THEN] Returns false
        Assert.IsFalse(RecRefHelper.FieldExistByName(310400, 'DoesNotExist'), 'FieldExist(''DoesNotExist'') must return false');
    end;

    // ── RecordRef.FullyQualifiedName ───────────────────────────────────────────

    [Test]
    procedure RecordRef_FullyQualifiedName_ReturnsCompanyDollarTable()
    var
        FQN: Text;
    begin
        // [GIVEN] RecordRef opened on table 310400 (name "Misc Gaps Table")
        // [WHEN] FullyQualifiedName is called
        FQN := RecRefHelper.GetFullyQualifiedName(310400);
        // [THEN] Returns "CRONUS$Misc Gaps Table"
        Assert.AreEqual('CRONUS$Misc Gaps Table', FQN, 'FullyQualifiedName must return CompanyName$TableName');
    end;

    // ── Version.Create(Integer, Integer, Integer, Integer) ────────────────────

    [Test]
    procedure Version_Create4Arg_StoresAllComponents()
    var
        Ver: Version;
    begin
        // [GIVEN] Version.Create called with (1, 2, 3, 4)
        Ver := VersionHelper.CreateVersion(1, 2, 3, 4);
        // [THEN] Each component is correct
        Assert.AreEqual(1, Ver.Major(), 'Major must be 1');
        Assert.AreEqual(2, Ver.Minor(), 'Minor must be 2');
        Assert.AreEqual(3, Ver.Build(), 'Build must be 3');
        Assert.AreEqual(4, Ver.Revision(), 'Revision must be 4');
    end;

    [Test]
    procedure Version_Create4Arg_ZeroComponents()
    var
        Ver: Version;
    begin
        // [GIVEN] Version.Create called with all zeros
        Ver := VersionHelper.CreateVersion(0, 0, 0, 0);
        // [THEN] All components are 0
        Assert.AreEqual(0, Ver.Major(), 'Major must be 0');
        Assert.AreEqual(0, Ver.Minor(), 'Minor must be 0');
        Assert.AreEqual(0, Ver.Build(), 'Build must be 0');
        Assert.AreEqual(0, Ver.Revision(), 'Revision must be 0');
    end;

    // ── Codeunit.Run(Text, Table) ──────────────────────────────────────────────

    [Test]
    procedure Codeunit_Run_ByTextName_WithRecord_Succeeds()
    var
        Rec: Record "Misc Gaps Table";
        RunHelper: Codeunit "Misc Gaps Codeunit Run Helper";
    begin
        // [GIVEN] A record in the table
        Rec."No." := 'RUN001';
        Rec.Amount := 5;
        Rec.Insert();
        // [WHEN] RunByTextName is called (uses Codeunit.Run(Text, Rec))
        RunHelper.RunByTextName(Rec);
        // [THEN] OnRun incremented Amount to 6
        Rec.Get('RUN001');
        Assert.AreEqual(6, Rec.Amount, 'Codeunit.Run(Text,Rec) must have incremented Amount');
    end;

    // ── Media.ImportStream(InStream, Text, Text, Text) ────────────────────────

    [Test]
    procedure Media_ImportStream_4Arg_SetsHasValue()
    var
        Rec: Record "Misc Gaps Table";
        MediaHelper: Codeunit "Misc Gaps Media Helper";
        BlobRec: Record "Misc Gaps Blob Table" temporary;
        InStr: InStream;
    begin
        // [GIVEN] A record and an InStream from a temporary blob
        Rec."No." := 'MEDIA001';
        Rec.Insert();
        MediaHelper.MakeBlobInStream(BlobRec, InStr);
        // [WHEN] ImportStream with 4 args is called on the Media field
        MediaHelper.ImportStream4Arg(Rec, InStr, 'file.png', 'image/png', 'Test Image');
        // [THEN] HasValue is true after the import
        Assert.IsTrue(MediaHelper.HasValue(Rec), 'Media.HasValue must be true after ImportStream(4-arg)');
    end;

    // ── DataTransfer.AddDestinationFilter(Integer, Text, Joker) ──────────────

    [Test]
    procedure DataTransfer_AddDestinationFilter_3Arg_NoThrow()
    var
        DTHelper: Codeunit "Misc Gaps DataTransfer Helper";
    begin
        // [GIVEN/WHEN] AddDestinationFilter with 3 args is called
        // [THEN] Does not throw
        DTHelper.AddDestinationFilterVariant(310400, 310400);
    end;

    // ── Database.SelectLatestVersion(Integer) ────────────────────────────────

    [Test]
    procedure Database_SelectLatestVersion_WithCompanyId_NoThrow()
    var
        DBHelper: Codeunit "Misc Gaps Database Helper";
    begin
        // [GIVEN/WHEN] SelectLatestVersion(42) is called
        // [THEN] Does not throw (no-op stub)
        DBHelper.CallSelectLatestVersionWithCompany(42);
    end;

    // ── Session.LogMessage(9-arg) ─────────────────────────────────────────────

    [Test]
    procedure Session_LogMessage_9Arg_NoThrow()
    var
        SessionHelper: Codeunit "Misc Gaps Session Helper";
    begin
        // [GIVEN/WHEN] LogMessage with 9 args is called
        // [THEN] Does not throw (telemetry is a no-op in standalone mode)
        SessionHelper.LogMessage9Arg(
            'TEST0001',
            'Test telemetry message',
            Verbosity::Normal,
            DataClassification::SystemMetadata,
            TelemetryScope::ExtensionPublisher,
            'Dim1', 'Val1', 'Dim2', 'Val2');
    end;
}
