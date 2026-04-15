// Placeholder test — won't actually run because AL compilation should fail.
codeunit 56350 "Same Ext Dup Tests"
{
    Subtype = Test;

    [Test]
    procedure Placeholder()
    var
        Assert: Codeunit Assert;
    begin
        Assert.IsTrue(true, 'Should not reach here');
    end;
}
