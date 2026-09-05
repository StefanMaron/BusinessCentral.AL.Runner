/// <summary>
/// Runs and passes, keeping the bucket's Stage at Ran while its sibling suite is dropped.
/// </summary>
codeunit 61360 "Emit Excl Part H Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Emit Excl Part H Assert";

    [Test]
    procedure PartialBundleHealthy_StillRuns()
    begin
        Assert.AreEqual(3, 1 + 2, 'the healthy sibling suite must compile and run');
    end;
}
