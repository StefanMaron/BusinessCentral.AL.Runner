codeunit 100006 "Assert 130002 Test"
{
    Subtype = Test;

    [Test]
    procedure AreEqual_ViaCodeunit130002_RoutesToMockAssert()
    var
        Assert130002: Codeunit "Library Assert 130002";
        Assert: Codeunit "Library Assert";
    begin
        // [GIVEN] Codeunit 130002 ("Library Assert" real BC ID)
        // [WHEN] Call AreEqual via the 130002 codeunit with matching values
        // [THEN] It routes to MockAssert and passes (proving the routing works)
        Assert130002.AreEqual(42, 42, 'AreEqual via codeunit 130002 should route to MockAssert');

        // Also verify a non-default value to prove it's not a no-op
        Assert130002.AreEqual('hello', 'hello', 'String comparison should also work via 130002');
    end;

    [Test]
    procedure AreEqual_ViaCodeunit130002_FailsOnMismatch()
    var
        Assert130002: Codeunit "Library Assert 130002";
        Assert: Codeunit "Library Assert";
    begin
        // [GIVEN] Codeunit 130002
        // [WHEN] Call AreEqual with mismatched values
        // [THEN] It should raise an error (proving it actually calls MockAssert, not a no-op)
        asserterror Assert130002.AreEqual(1, 2, 'Values should not match');
        Assert.ExpectedError('Assert.AreEqual failed');
    end;
}
