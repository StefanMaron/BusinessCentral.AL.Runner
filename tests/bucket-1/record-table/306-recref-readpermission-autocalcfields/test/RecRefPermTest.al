codeunit 306022 "RecRef Perm Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ReadPermission_ReturnsTrue()
    // Proves ReadPermission returns true (not a default false stub)
    var
        Helper: Codeunit "RecRef Perm Helper";
    begin
        Assert.IsTrue(Helper.TestReadPermission(), 'ReadPermission should return true in standalone mode');
    end;

    [Test]
    procedure ReadPermission_OnClosedRef_ReturnsTrue()
    // Even on a closed RecordRef, ReadPermission should be true (no permission enforcement)
    var
        Helper: Codeunit "RecRef Perm Helper";
    begin
        Assert.IsTrue(Helper.TestReadPermissionOnClosedRef(), 'ReadPermission on closed RecordRef should return true');
    end;

    [Test]
    procedure SetAutoCalcFields_SingleField_NoThrow()
    // Proves SetAutoCalcFields(FldRef.Number) compiles and runs without error
    var
        Helper: Codeunit "RecRef Perm Helper";
    begin
        Assert.IsTrue(Helper.TestSetAutoCalcFieldsNoThrow(), 'SetAutoCalcFields with one field number should not throw');
    end;

    [Test]
    procedure SetAutoCalcFields_MultipleFields_NoThrow()
    // Proves SetAutoCalcFields(n, m) compiles and runs without error
    var
        Helper: Codeunit "RecRef Perm Helper";
    begin
        Assert.IsTrue(Helper.TestSetAutoCalcFieldsMultiple(), 'SetAutoCalcFields with multiple field numbers should not throw');
    end;
}
