codeunit 59996 "ISE Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "ISE Src";

    [Test]
    procedure SetEncrypted_2Arg_NoOp()
    begin
        // Positive: 2-arg SetEncrypted(key, value) completes without throwing.
        Src.StoreEncrypted2Arg('token1', 'secret-value');
        Assert.IsTrue(true, 'SetEncrypted 2-arg must not throw');
    end;

    [Test]
    procedure SetEncrypted_3Arg_NoOp()
    begin
        // Positive: 3-arg SetEncrypted(key, value, datascope) completes.
        Src.StoreEncrypted3Arg('token2', 'secret-value', DataScope::Module);
        Assert.IsTrue(true, 'SetEncrypted 3-arg must not throw');
    end;

    [Test]
    procedure SetEncrypted_RoundTripsWithGet()
    begin
        // Store via SetEncrypted, retrieve via Get — value must round-trip.
        // In runner mode the "encryption" is transparent (no real crypto).
        Assert.AreEqual('my-secret-payload',
            Src.StoreAndRetrieve_2Arg('tok-k', 'my-secret-payload'),
            'SetEncrypted + Get must round-trip the value');
    end;

    [Test]
    procedure SetEncrypted_MarksContainsTrue()
    begin
        // After SetEncrypted, IsolatedStorage.Contains must return true.
        Assert.IsTrue(Src.StoreAndContains('tok-c', 'any-value'),
            'Contains must return true after SetEncrypted');
    end;

    [Test]
    procedure SetEncrypted_EmptyValue()
    begin
        // Edge: empty value must not crash the stub.
        Src.StoreEncrypted2Arg('tok-empty', '');
        Assert.IsTrue(true, 'SetEncrypted with empty value must not throw');
    end;

    [Test]
    procedure SetEncrypted_DifferentKeysDifferentValues_NegativeTrap()
    begin
        // Negative: guard against a stub that returns the same value for all keys.
        Assert.AreNotEqual(
            Src.StoreAndRetrieve_2Arg('tok-a', 'alpha'),
            Src.StoreAndRetrieve_2Arg('tok-b', 'beta'),
            'Different keys must store/retrieve different values');
    end;
}
