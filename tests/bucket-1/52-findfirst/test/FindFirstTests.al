codeunit 55901 "FindFirst Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    local procedure Seed()
    var
        Letter: Record "FF Letter";
    begin
        Letter.DeleteAll();
        Letter.Init(); Letter.Code := 'A'; Letter.Category := 1; Letter.Weight := 10; Letter.Insert();
        Letter.Init(); Letter.Code := 'M'; Letter.Category := 2; Letter.Weight := 20; Letter.Insert();
        Letter.Init(); Letter.Code := 'Z'; Letter.Category := 1; Letter.Weight := 30; Letter.Insert();
    end;

    // -----------------------------------------------------------------------
    // FindFirst — positive tests
    // -----------------------------------------------------------------------

    [Test]
    procedure FindFirst_Unfiltered_PositionsToFirstByPK()
    var
        Letter: Record "FF Letter";
    begin
        // [GIVEN] Three records inserted in non-alphabetical order (M first, then A, then Z)
        Letter.Init(); Letter.Code := 'M'; Letter.Category := 2; Letter.Weight := 20; Letter.Insert();
        Letter.Init(); Letter.Code := 'A'; Letter.Category := 1; Letter.Weight := 10; Letter.Insert();
        Letter.Init(); Letter.Code := 'Z'; Letter.Category := 1; Letter.Weight := 30; Letter.Insert();

        // [WHEN] FindFirst with no filter
        Assert.IsTrue(Letter.FindFirst(), 'FindFirst must return true when table has rows');

        // [THEN] Positioned to Code = A (first by PK ascending)
        Assert.AreEqual('A', Letter.Code, 'FindFirst on unfiltered table must position to Code = A (first PK)');
        Assert.AreEqual(10, Letter.Weight, 'FindFirst must load the full record — Weight must be 10');
    end;

    [Test]
    procedure FindFirst_WithSetRange_ReturnsFirstMatchingRecord()
    var
        Letter: Record "FF Letter";
    begin
        // [GIVEN] Three records: A (Cat=1), M (Cat=2), Z (Cat=1)
        Seed();

        // [WHEN] Filter to Category=1, then FindFirst
        Letter.SetRange(Category, 1);
        Assert.IsTrue(Letter.FindFirst(), 'FindFirst with matching filter must return true');

        // [THEN] First matching record in PK order is A
        Assert.AreEqual('A', Letter.Code, 'FindFirst with Category=1 must return A (first matching PK)');
        Assert.AreEqual(10, Letter.Weight, 'Weight of A must be 10');
    end;

    [Test]
    procedure FindFirst_WithSetCurrentKey_RespectsAlternateKey()
    var
        Letter: Record "FF Letter";
    begin
        // [GIVEN] Three records with different categories
        Seed();

        // [WHEN] SetCurrentKey to Category, then FindFirst (Category=2 comes before ... but we test PK within same category)
        Letter.SetCurrentKey(Category);
        Letter.SetRange(Category, 1);
        Assert.IsTrue(Letter.FindFirst(), 'FindFirst with SetCurrentKey and SetRange must return true');

        // [THEN] First Category=1 record in key order: A
        Assert.AreEqual('A', Letter.Code, 'FindFirst with Category key and Category=1 filter must return A');
    end;

    [Test]
    procedure FindFirst_SingleRecord_ReturnsThatRecord()
    var
        Letter: Record "FF Letter";
    begin
        // [GIVEN] Exactly one record
        Letter.Init(); Letter.Code := 'ONLY'; Letter.Category := 5; Letter.Weight := 99; Letter.Insert();

        // [WHEN] FindFirst
        Assert.IsTrue(Letter.FindFirst(), 'FindFirst on a single record must return true');

        // [THEN] That record is returned
        Assert.AreEqual('ONLY', Letter.Code, 'FindFirst on single record must return it');
        Assert.AreEqual(99, Letter.Weight, 'FindFirst must load Weight = 99');
    end;

    // -----------------------------------------------------------------------
    // FindFirst — negative tests
    // -----------------------------------------------------------------------

    [Test]
    procedure FindFirst_EmptyTable_ReturnsFalse()
    var
        Letter: Record "FF Letter";
    begin
        // [GIVEN] Empty table
        Letter.DeleteAll();

        // [WHEN/THEN] FindFirst returns false (not throw) when called without error level
        Assert.IsFalse(Letter.FindFirst(), 'FindFirst on empty table must return false');
    end;

    [Test]
    procedure FindFirst_FilterMatchesNothing_ReturnsFalse()
    var
        Letter: Record "FF Letter";
    begin
        // [GIVEN] Records exist but none match Category=999
        Seed();

        // [WHEN] SetRange to a non-existent category
        Letter.SetRange(Category, 999);

        // [THEN] FindFirst returns false
        Assert.IsFalse(Letter.FindFirst(), 'FindFirst with no matching records must return false');
    end;

    [Test]
    procedure FindFirst_DoesNotMutateNonMatchingRecords()
    var
        Letter: Record "FF Letter";
    begin
        // [GIVEN] Two categories of records
        Seed();

        // [WHEN] FindFirst returns first Category=1 record
        Letter.SetRange(Category, 1);
        Letter.FindFirst();

        // [THEN] Category=2 record (M) is unchanged — verify by direct Get
        Letter.Reset();
        Letter.Get('M');
        Assert.AreEqual(2, Letter.Category, 'FindFirst must not modify non-targeted records');
        Assert.AreEqual(20, Letter.Weight, 'FindFirst must not change Weight of untouched records');
    end;
}
