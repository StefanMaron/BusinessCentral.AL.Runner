/// <summary>
/// End-to-end proof for issue #2264: a table whose AL name another installed app also declares
/// in the same company is hydrated from the physical table owned by the app this run resolved.
///
/// "Dimension Set Entry" is the shipped case: Base Application declares table 480 and Power BI
/// Report embeddings declares table 36950 under the same name. In the W1 CRONUS backup the Base
/// Application one holds 89 rows and the Power BI one none, so the count below tells the two
/// apart as well as the refused-and-empty state this replaced.
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
