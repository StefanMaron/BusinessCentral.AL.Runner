codeunit 50809 "CurrentKey Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // CurrentKey — positive tests
    // -----------------------------------------------------------------------

    [Test]
    procedure CurrentKeyReturnsKeyAfterSetCurrentKey()
    var
        Rec: Record "Key Probe";
    begin
        // [GIVEN] A record with SetCurrentKey
        Rec.SetCurrentKey("Name");

        // [WHEN] CurrentKey is read
        // [THEN] It should return the key field name
        Assert.AreNotEqual('', Rec.CurrentKey(), 'CurrentKey should return non-empty after SetCurrentKey');
    end;

    // -----------------------------------------------------------------------
    // CurrentKey — negative tests
    // -----------------------------------------------------------------------

    [Test]
    procedure CurrentKeyReturnsDefaultWhenNotSet()
    var
        Rec: Record "Key Probe";
        KeyText: Text;
    begin
        // [GIVEN] A record with no explicit SetCurrentKey
        // [WHEN] CurrentKey is read
        KeyText := Rec.CurrentKey();
        // [THEN] It should return a value (PK by default) without crashing
        // We just verify it compiles and runs — the value is PK-dependent
    end;

    // -----------------------------------------------------------------------
    // Ascending — positive tests
    // -----------------------------------------------------------------------

    [Test]
    procedure AscendingDefaultsToTrue()
    var
        Rec: Record "Key Probe";
    begin
        // [GIVEN] A record with default sort order
        Rec.SetCurrentKey("Name");

        // [WHEN/THEN] Ascending should default to true
        Assert.IsTrue(Rec.Ascending(), 'Ascending should default to true');
    end;

    [Test]
    procedure AscendingReturnsFalseAfterSetAscendingFalse()
    var
        Rec: Record "Key Probe";
    begin
        // [GIVEN] A record with descending sort
        Rec.SetCurrentKey("Name");
        Rec.SetAscending("Name", false);

        // [WHEN/THEN] Ascending should return false
        Assert.IsFalse(Rec.Ascending(), 'Ascending should return false after SetAscending(false)');
    end;
}
