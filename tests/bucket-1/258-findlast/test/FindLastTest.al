codeunit 50258 "FindLast Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    local procedure Seed()
    var
        Letter: Record "FL Letter";
    begin
        Letter.DeleteAll();
        Letter.Init(); Letter.Code := 'A'; Letter.Category := 1; Letter.Weight := 10; Letter.Insert();
        Letter.Init(); Letter.Code := 'M'; Letter.Category := 2; Letter.Weight := 20; Letter.Insert();
        Letter.Init(); Letter.Code := 'Z'; Letter.Category := 1; Letter.Weight := 30; Letter.Insert();
    end;

    [Test]
    procedure FindLast_Unfiltered_PositionsToLastByPK()
    var
        Letter: Record "FL Letter";
    begin
        Seed();
        Assert.IsTrue(Letter.FindLast(), 'FindLast must return true when the table has rows');
        Assert.AreEqual('Z', Letter.Code, 'FindLast on unfiltered table must position to Code = Z (last by PK)');
        Assert.AreEqual(30, Letter.Weight, 'FindLast must load the full record — Weight must be 30, not default');
    end;

    [Test]
    procedure FindLast_EmptyTable_ReturnsFalse()
    var
        Letter: Record "FL Letter";
    begin
        Letter.DeleteAll();
        Assert.IsFalse(Letter.FindLast(), 'FindLast on empty table must return false');
    end;

    [Test]
    procedure FindLast_WithFilter_PositionsToFilteredLast()
    var
        Letter: Record "FL Letter";
    begin
        Seed();
        Letter.SetRange(Category, 1); // filter: Category = 1 → matches A and Z
        Assert.IsTrue(Letter.FindLast(), 'FindLast on filtered set with matches must return true');
        Assert.AreEqual('Z', Letter.Code, 'FindLast within Category=1 must return Z (last matching PK)');
    end;

    [Test]
    procedure FindLast_WithFilterNoMatch_ReturnsFalse()
    var
        Letter: Record "FL Letter";
    begin
        Seed();
        Letter.SetRange(Category, 99); // no matches
        Assert.IsFalse(Letter.FindLast(), 'FindLast with filter that matches nothing must return false');
    end;

    [Test]
    procedure FindLast_RespectsFilter_NotFullTable()
    var
        Letter: Record "FL Letter";
    begin
        Seed();
        // [GIVEN] filter out Z so the filtered last is M (not Z)
        Letter.SetFilter(Code, '<>Z');
        Assert.IsTrue(Letter.FindLast(), 'FindLast must return true');
        // [THEN] proves filtering is applied — if FindLast ignored the filter it would return Z
        Assert.AreEqual('M', Letter.Code, 'FindLast must honour filter and return M, not Z');
    end;

    [Test]
    procedure FindLast_DifferentFromFindFirst()
    var
        First: Record "FL Letter";
        Last: Record "FL Letter";
    begin
        Seed();
        Assert.IsTrue(First.FindFirst(), 'FindFirst must succeed');
        Assert.IsTrue(Last.FindLast(), 'FindLast must succeed');
        // Proves FindLast isn't aliased to FindFirst
        Assert.AreEqual('A', First.Code, 'FindFirst returns A');
        Assert.AreEqual('Z', Last.Code, 'FindLast returns Z');
        Assert.AreNotEqual(First.Code, Last.Code, 'FindFirst and FindLast must return different records');
    end;
}
