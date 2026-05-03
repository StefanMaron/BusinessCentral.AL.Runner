// Tests for issue #1587: CLEANSCHEMA symbols derive from BC version.
// Verifies that the default CLEANSCHEMA set (1..25) is applied correctly
// so code guarded by active symbols compiles and runs as expected.
codeunit 1587101 "CS Version Helper Test"
{
    Subtype = Test;

    var
        Helper: Codeunit "CS Version Helper";
        Assert: Codeunit Assert;

    // Positive: unguarded procedure is always reachable regardless of CLEANSCHEMA set.
    [Test]
    procedure AlwaysPresent_Returns100()
    begin
        Assert.AreEqual(100, Helper.AlwaysPresent(), 'AlwaysPresent must return 100');
    end;

    // Positive: CLEANSCHEMA1 is in the default active set (1..25).
    // #if CLEANSCHEMA1 block must be compiled in, so WhenCS1Active is reachable.
    [Test]
    procedure WhenCS1Active_Returns1()
    begin
        Assert.AreEqual(1, Helper.WhenCS1Active(), 'CLEANSCHEMA1 must be active — returns 1');
    end;

    // Positive: CLEANSCHEMA25 is at the top of the default set.
    // #if CLEANSCHEMA25 block must be compiled in.
    [Test]
    procedure WhenCS25Active_Returns25()
    begin
        Assert.AreEqual(25, Helper.WhenCS25Active(), 'CLEANSCHEMA25 must be active — returns 25');
    end;
}
