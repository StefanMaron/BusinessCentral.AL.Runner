codeunit 50401 SecretStoreTest
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestStoreAndRetrieveSecret()
    var
        Helper: Codeunit SecretStoreHelper;
        Retrieved: SecretText;
    begin
        // [SCENARIO] Store a secret text and retrieve it.
        // [GIVEN] A secret value stored via StoreFromText (exercises ToSecretText rewrite).
        Helper.StoreFromText('TestKey', 'my-secret-value');

        // [WHEN] We retrieve the secret.
        Assert.IsTrue(Helper.GetSecret('TestKey', Retrieved), 'GetSecret should return true.');

        // [THEN] The retrieved value is not empty.
        Assert.IsFalse(Retrieved.IsEmpty(), 'Retrieved secret should not be empty.');
    end;

    [Test]
    procedure TestSecretNotFoundReturnsEmpty()
    var
        Helper: Codeunit SecretStoreHelper;
        Retrieved: SecretText;
    begin
        // [SCENARIO] Retrieving a non-existent key returns false and empty secret.
        // [GIVEN] No value stored for 'MissingKey'.
        Helper.RemoveSecret('MissingKey');

        // [WHEN] We attempt to retrieve it.
        // [THEN] GetSecret returns false.
        Assert.IsFalse(Helper.GetSecret('MissingKey', Retrieved), 'GetSecret should return false for missing key.');

        // [THEN] The retrieved value is empty.
        Assert.IsTrue(Retrieved.IsEmpty(), 'Retrieved secret should be empty for missing key.');
    end;

    [Test]
    procedure TestHasSecretAfterStore()
    var
        Helper: Codeunit SecretStoreHelper;
    begin
        // [SCENARIO] HasSecret returns true after storing a value.
        Helper.RemoveSecret('CheckKey');
        Assert.IsFalse(Helper.HasSecret('CheckKey'), 'HasSecret should be false before storing.');

        Helper.StoreFromText('CheckKey', 'value123');
        Assert.IsTrue(Helper.HasSecret('CheckKey'), 'HasSecret should be true after storing.');

        Helper.RemoveSecret('CheckKey');
        Assert.IsFalse(Helper.HasSecret('CheckKey'), 'HasSecret should be false after removing.');
    end;
}
