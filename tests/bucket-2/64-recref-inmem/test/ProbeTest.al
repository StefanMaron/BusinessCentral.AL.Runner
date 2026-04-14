codeunit 56641 "RR Mem Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure EmptyTableRecRefIsEmpty()
    var
        Probe: Codeunit "RR Mem Probe";
    begin
        // [GIVEN] No rows in the RR Mem Row table
        // [THEN] RecRef.IsEmpty() must be true — HasRows returns false
        Assert.IsFalse(Probe.HasRows(56640), 'RecRef over empty table must report empty');
    end;

    [Test]
    procedure SeededTableRecRefSeesRow()
    var
        R: Record "RR Mem Row";
        Probe: Codeunit "RR Mem Probe";
    begin
        // [GIVEN] A row inserted via a typed Record handle
        R.Id := 1;
        R.Name := 'hello';
        R.Insert();

        // [WHEN] RecRef opens the same table
        // [THEN] RecRef.IsEmpty() sees the row — HasRows returns true
        Assert.IsTrue(Probe.HasRows(56640), 'RecRef over seeded table must report non-empty');
    end;

    [Test]
    procedure ThreeArgOpenAlsoSeesRow()
    var
        R: Record "RR Mem Row";
        Probe: Codeunit "RR Mem Probe";
    begin
        R.Id := 2;
        R.Insert();

        Assert.IsTrue(Probe.HasRowsInCompany(56640, 'CRONUS'), 'Three-arg Open must also route to in-memory store');
    end;
}
