codeunit 50922 "Record Persistence Tests"
{
    Subtype = Test;

    var
        Helper: Codeunit "Persistence Helper";
        Assert: Codeunit Assert;

    [Test]
    procedure TestRecordsPersistAcrossFunctions()
    begin
        // [GIVEN] Records inserted by SetupData in one function call
        Helper.SetupData();

        // [WHEN] Reading records in a different function call
        // [THEN] All 3 records should be visible
        Assert.AreEqual(3, Helper.GetRecordCount(), 'Records should persist across function calls');
    end;

    [Test]
    procedure TestTotalAmountAcrossFunctions()
    begin
        // [GIVEN] Records inserted by SetupData
        Helper.SetupData();

        // [WHEN] Summing amounts from a different function
        // [THEN] Total should be 100 + 250 + 50 = 400
        Assert.AreEqual(400, Helper.GetTotalAmount(), 'Total amount should sum all persisted records');
    end;

    [Test]
    procedure TestIndividualRecordPersistence()
    begin
        // [GIVEN] Records inserted by SetupData
        Helper.SetupData();

        // [WHEN] Reading individual records from a different function
        // [THEN] Each record should have the correct description
        Assert.AreEqual('First Entry', Helper.GetDescription(1), 'First record should persist');
        Assert.AreEqual('Second Entry', Helper.GetDescription(2), 'Second record should persist');
        Assert.AreEqual('Third Entry', Helper.GetDescription(3), 'Third record should persist');
    end;

    [Test]
    procedure TestGetNonExistentRecordFails()
    begin
        // [GIVEN] Records inserted by SetupData
        Helper.SetupData();

        // [WHEN] Trying to get a record that was not inserted
        asserterror Helper.GetDescription(99);

        // [THEN] Should error (record not found)
        Assert.ExpectedError('');
    end;

    [Test]
    procedure TestEmptyTableBeforeSetup()
    begin
        // [WHEN] No records have been inserted yet
        // [THEN] Count should be zero
        Assert.AreEqual(0, Helper.GetRecordCount(), 'Table should be empty before setup');
        Assert.AreEqual(0, Helper.GetTotalAmount(), 'Total should be zero on empty table');
    end;
}
