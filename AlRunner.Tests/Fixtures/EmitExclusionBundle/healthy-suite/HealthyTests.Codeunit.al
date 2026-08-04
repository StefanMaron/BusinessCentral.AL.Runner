/// <summary>
/// Binds and runs normally, so the bundle still reports a PASSING test while its broken
/// sibling suite is dropped wholesale. That surviving pass is the whole point of the
/// fixture — it is what kept the bundle non-empty and the exclusion out of the exit code.
/// </summary>
codeunit 60770 "Emit Excl Bundle Healthy"
{
    Subtype = Test;

    var
        Assert: Codeunit "Emit Excl Bundle Assert";

    [Test]
    procedure BundleHealthy_Addition_StillRuns()
    begin
        Assert.AreEqual(3, 1 + 2, 'the healthy sibling suite must compile and run');
    end;
}
