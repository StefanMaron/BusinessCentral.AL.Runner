codeunit 58500 "Test IsNullGuid"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure IsNullGuid_UninitializedGuid_ReturnsTrue()
    var
        G: Guid;
    begin
        // An uninitialized Guid is all zeros — IsNullGuid must return true
        Assert.IsTrue(IsNullGuid(G), 'IsNullGuid must return true for uninitialized (all-zeros) GUID');
    end;

    [Test]
    procedure IsNullGuid_AfterCreateGuid_ReturnsFalse()
    var
        G: Guid;
    begin
        // A GUID created by CreateGuid() is non-zero — IsNullGuid must return false
        G := CreateGuid();
        Assert.IsFalse(IsNullGuid(G), 'IsNullGuid must return false for a freshly created GUID');
    end;
}
