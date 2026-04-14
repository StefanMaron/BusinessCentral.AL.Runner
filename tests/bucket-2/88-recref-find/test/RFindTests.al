codeunit 56881 "RF Find Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    // --- RecRef.Find() with no argument ---

    [Test]
    procedure FindOnNonEmptyTableReturnsTrue()
    var
        R: Record "RF Find Item";
        RecRef: RecordRef;
        Found: Boolean;
    begin
        // [GIVEN] A record exists in the table
        R.Id := 1;
        R.Name := 'Alpha';
        R.Insert();

        // [WHEN] RecRef.Find() is called and result stored in a Boolean
        RecRef.Open(Database::"RF Find Item");
        Found := RecRef.Find();
        RecRef.Close();

        // [THEN] The result passed to Assert.IsTrue compiles and succeeds
        Assert.IsTrue(Found, 'RecRef.Find() must return true when records exist');
    end;

    [Test]
    procedure FindOnEmptyTableReturnsFalse()
    var
        RecRef: RecordRef;
        Found: Boolean;
    begin
        // [GIVEN] No records exist
        // [WHEN] RecRef.Find() is called on empty table
        RecRef.Open(Database::"RF Find Item");
        Found := RecRef.Find();
        RecRef.Close();

        // [THEN] Result is false
        Assert.IsFalse(Found, 'RecRef.Find() must return false on empty table');
    end;

    [Test]
    procedure FindDirectlyInAssertIsTrue()
    var
        R: Record "RF Find Item";
        RecRef: RecordRef;
    begin
        // [GIVEN] A record exists
        R.Id := 2;
        R.Name := 'Bravo';
        R.Insert();

        // [WHEN/THEN] RecRef.Find() result used directly in Assert.IsTrue
        RecRef.Open(Database::"RF Find Item");
        Assert.IsTrue(RecRef.Find(), 'RecRef.Find() direct in Assert.IsTrue must compile and succeed');
        RecRef.Close();
    end;

    [Test]
    procedure FindDirectlyInAssertIsFalse()
    var
        RecRef: RecordRef;
    begin
        // [GIVEN] No records
        // [WHEN/THEN] RecRef.Find() used directly in Assert.IsFalse on empty table
        RecRef.Open(Database::"RF Find Item");
        Assert.IsFalse(RecRef.Find(), 'RecRef.Find() direct in Assert.IsFalse must compile and return false');
        RecRef.Close();
    end;

    [Test]
    procedure FindViaProbeReturnsTrue()
    var
        R: Record "RF Find Item";
        Probe: Codeunit "RF Find Probe";
    begin
        // [GIVEN] A record exists
        R.Id := 3;
        R.Name := 'Charlie';
        R.Insert();

        // [WHEN] Find via probe codeunit (tests cross-codeunit RecRef.Find usage)
        // [THEN] Returns true
        Assert.IsTrue(Probe.FindRecordViaRecRef(Database::"RF Find Item"), 'Probe RecRef.Find must return true');
    end;

    [Test]
    procedure FindViaProbeReturnsFalse()
    var
        Probe: Codeunit "RF Find Probe";
    begin
        // [GIVEN] No records
        // [WHEN] Find via probe
        // [THEN] Returns false
        Assert.IsFalse(Probe.FindRecordViaRecRef(Database::"RF Find Item"), 'Probe RecRef.Find must return false on empty table');
    end;
}
