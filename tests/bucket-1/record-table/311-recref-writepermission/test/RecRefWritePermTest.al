codeunit 311003 "RecRef WritePerm Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure WritePermission_OpenRef_ReturnsTrue()
    // Proves WritePermission returns true (not a default false stub) when RecordRef is open
    var
        Helper: Codeunit "RecRef WritePerm Helper";
    begin
        Assert.IsTrue(Helper.TestWritePermission(), 'WritePermission should return true in standalone mode');
    end;

    [Test]
    procedure WritePermission_ClosedRef_ReturnsTrue()
    // Proves WritePermission returns true on a closed (uninitialized) RecordRef
    var
        Helper: Codeunit "RecRef WritePerm Helper";
    begin
        Assert.IsTrue(Helper.TestWritePermissionOnClosedRef(), 'WritePermission on closed RecordRef should return true');
    end;

    [Test]
    procedure WritePermission_AfterClose_ReturnsTrue()
    // Proves WritePermission returns true after the RecordRef is explicitly closed
    var
        Helper: Codeunit "RecRef WritePerm Helper";
    begin
        Assert.IsTrue(Helper.TestWritePermissionAfterClose(), 'WritePermission after Close() should return true');
    end;
}
