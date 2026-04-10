codeunit 50916 "Iso Storage Tests"
{
    Subtype = Test;

    var
        IsoStorage: Codeunit "Iso Storage Wrapper";
        Assert: Codeunit Assert;

    [Test]
    procedure TestSetAndGet()
    var
        Result: Text;
        Found: Boolean;
    begin
        // [GIVEN] A key-value pair is stored
        IsoStorage.SetValue('mykey', 'hello world');

        // [WHEN] Retrieving the value
        Found := IsoStorage.GetValue('mykey', Result);

        // [THEN] The value should be returned
        Assert.IsTrue(Found, 'Get should return true for existing key');
        Assert.AreEqual('hello world', Result, 'Value should match what was set');
    end;

    [Test]
    procedure TestContainsAfterSet()
    var
        Result: Boolean;
    begin
        // [GIVEN] A key is stored
        IsoStorage.SetValue('exists-key', 'value');

        // [WHEN] Checking if it exists
        Result := IsoStorage.HasKey('exists-key');

        // [THEN] Contains should return true
        Assert.IsTrue(Result, 'Contains should return true after Set');
    end;

    [Test]
    procedure TestContainsMissingKey()
    var
        Result: Boolean;
    begin
        // [WHEN] Checking a key that was never set
        Result := IsoStorage.HasKey('no-such-key');

        // [THEN] Contains should return false
        Assert.IsFalse(Result, 'Contains should return false for missing key');
    end;

    [Test]
    procedure TestDeleteRemovesKey()
    var
        Deleted: Boolean;
        Found: Boolean;
        Val: Text;
    begin
        // [GIVEN] A key is stored
        IsoStorage.SetValue('del-key', 'to-delete');

        // [WHEN] Deleting it
        Deleted := IsoStorage.RemoveKey('del-key');

        // [THEN] Delete returns true, and Get returns false
        Assert.IsTrue(Deleted, 'Delete should return true for existing key');
        Found := IsoStorage.GetValue('del-key', Val);
        Assert.IsFalse(Found, 'Get should return false after deletion');
    end;

    [Test]
    procedure TestOverwriteValue()
    var
        Result: Text;
    begin
        // [GIVEN] A key with an initial value
        IsoStorage.SetValue('overwrite', 'first');

        // [WHEN] Setting the same key to a new value
        IsoStorage.SetValue('overwrite', 'second');

        // [THEN] The new value should be returned
        IsoStorage.GetValue('overwrite', Result);
        Assert.AreEqual('second', Result, 'Value should be overwritten');
    end;
}
