// Placeholder test — won't actually run because AL compilation should fail.
codeunit 310502 "Same Ext ProfileExt Dup Tests"
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
