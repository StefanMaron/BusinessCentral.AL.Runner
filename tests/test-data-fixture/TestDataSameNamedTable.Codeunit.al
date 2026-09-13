/// <summary>
/// End-to-end proof for issue #2264: a table whose AL name another installed app also declares
/// in the same company is hydrated from the physical table owned by the app this run resolved.
///
/// "Dimension Set Entry" is the shipped case. The W1 CRONUS backup holds two physical tables of that
/// name in one company: Base Application's (table 480, 89 rows) and Power BI Report embeddings'
/// (0 rows). This fixture's closure does not include Power BI, so the catalog resolves only 480 and
/// leaves the sibling unresolved, yet the reader still refuses the bare name
/// (`ambiguous table ... $437dbf0e... | ...$e4e86220...`). So this proves the one-resolvable-candidate
/// case: 89 rows instead of the refused-and-empty 0. Choosing between TWO resolvable candidates is
/// proved by AlRunner.Tests/TestDataSameNamedTablesTests.cs.
///
/// NOT RUN BY CI — see README.md in this directory.
/// </summary>
codeunit 64409 "Test Data Same-Named Table"
{
    Subtype = Test;

    var
        Assert: Codeunit "TDF Assert";

    [Test]
    procedure DimensionSetEntry_IsHydratedFromTheBaseApplicationTable()
    var
        DimensionSetEntry: Record "Dimension Set Entry";
    begin
        Assert.AreEqual(89, DimensionSetEntry.Count(), 'every Base Application Dimension Set Entry row in the CRONUS backup');

        Assert.IsTrue(DimensionSetEntry.Get(2, 'DEPARTMENT'), 'Dimension Set 2 / DEPARTMENT must exist');
        Assert.AreEqual('SALES', DimensionSetEntry."Dimension Value Code", 'set 2 DEPARTMENT value');
        Assert.AreEqual(20, DimensionSetEntry."Dimension Value ID", 'set 2 DEPARTMENT value id');

        Assert.IsFalse(DimensionSetEntry.Get(2, 'NOT-A-DIMENSION'), 'hydration must not invent rows');
    end;
}
