codeunit 50811 "Record Stub Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    local procedure InsertRecord(EntryNo: Integer; Name: Text[100]; Amount: Decimal)
    var
        Rec: Record "Stub Probe";
    begin
        Rec.Init();
        Rec."Entry No." := EntryNo;
        Rec."Name" := Name;
        Rec."Amount" := Amount;
        Rec.Insert(true);
    end;

    // -----------------------------------------------------------------------
    // CountApprox — should return same as Count
    // -----------------------------------------------------------------------

    [Test]
    procedure CountApproxMatchesCount()
    var
        Rec: Record "Stub Probe";
    begin
        // [GIVEN] 3 records
        InsertRecord(1, 'A', 10);
        InsertRecord(2, 'B', 20);
        InsertRecord(3, 'C', 30);

        // [WHEN/THEN] CountApprox should equal Count
        Assert.AreEqual(Rec.Count(), Rec.CountApprox(), 'CountApprox should match Count');
    end;

    // -----------------------------------------------------------------------
    // Consistent — compiles and runs as no-op
    // -----------------------------------------------------------------------

    [Test]
    procedure ConsistentDoesNotError()
    var
        Rec: Record "Stub Probe";
    begin
        // [GIVEN] A record
        InsertRecord(1, 'A', 10);

        // [WHEN] Consistent is called (no-op in runner)
        Rec.Consistent(true);

        // [THEN] No error; record still accessible
        Rec.FindFirst();
        Assert.AreEqual(1, Rec."Entry No.", 'Record should still be accessible after Consistent');
    end;

    // -----------------------------------------------------------------------
    // FieldActive — returns true for existing fields
    // -----------------------------------------------------------------------

    [Test]
    procedure FieldActiveReturnsTrueForExistingField()
    var
        Rec: Record "Stub Probe";
    begin
        // [GIVEN] A record
        InsertRecord(1, 'A', 10);

        // [WHEN/THEN] FieldActive for a known field should return true
        Assert.IsTrue(Rec.FieldActive("Name"), 'FieldActive should return true for existing field');
    end;

    // -----------------------------------------------------------------------
    // AddLink / HasLinks / DeleteLinks
    // -----------------------------------------------------------------------

    [Test]
    procedure HasLinksReturnsFalseByDefault()
    var
        Rec: Record "Stub Probe";
    begin
        // [GIVEN] A record with no links
        InsertRecord(1, 'A', 10);
        Rec.FindFirst();

        // [WHEN/THEN] HasLinks should return false
        Assert.IsFalse(Rec.HasLinks(), 'HasLinks should be false when no links added');
    end;

    [Test]
    procedure AddLinkThenHasLinksReturnsTrue()
    var
        Rec: Record "Stub Probe";
    begin
        // [GIVEN] A record
        InsertRecord(1, 'A', 10);
        Rec.FindFirst();

        // [WHEN] A link is added
        Rec.AddLink('https://example.com');

        // [THEN] HasLinks should return true
        Assert.IsTrue(Rec.HasLinks(), 'HasLinks should be true after AddLink');
    end;

    [Test]
    procedure DeleteLinksThenHasLinksReturnsFalse()
    var
        Rec: Record "Stub Probe";
    begin
        // [GIVEN] A record with a link
        InsertRecord(1, 'A', 10);
        Rec.FindFirst();
        Rec.AddLink('https://example.com');

        // [WHEN] All links are deleted
        Rec.DeleteLinks();

        // [THEN] HasLinks should return false
        Assert.IsFalse(Rec.HasLinks(), 'HasLinks should be false after DeleteLinks');
    end;

    // -----------------------------------------------------------------------
    // WritePermission — stub returns true
    // -----------------------------------------------------------------------

    [Test]
    procedure WritePermissionReturnsTrue()
    var
        Rec: Record "Stub Probe";
    begin
        // [WHEN/THEN] WritePermission should return true (stub)
        Assert.IsTrue(Rec.WritePermission(), 'WritePermission should return true');
    end;

    // -----------------------------------------------------------------------
    // SetPermissionFilter — compiles and runs as no-op
    // -----------------------------------------------------------------------

    [Test]
    procedure SetPermissionFilterDoesNotError()
    var
        Rec: Record "Stub Probe";
    begin
        // [GIVEN] A record
        InsertRecord(1, 'A', 10);

        // [WHEN] SetPermissionFilter is called (no-op)
        Rec.SetPermissionFilter();

        // [THEN] Record is still accessible
        Assert.AreEqual(1, Rec.Count(), 'Records should remain after SetPermissionFilter');
    end;
}
