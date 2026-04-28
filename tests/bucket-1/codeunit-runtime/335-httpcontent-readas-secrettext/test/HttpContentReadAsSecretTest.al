codeunit 1320505 "HC ReadAs Secret Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "HC ReadAs Secret Src";

    [Test]
    procedure ReadAsSecret_RoundTrip()
    begin
        Assert.AreEqual('secret-body', Src.ReadAsSecretRoundTrip('secret-body'),
            'HttpContent.ReadAs(SecretText) should return the stored body');
    end;

    [Test]
    procedure ReadAsSecret_ReturnsTrue()
    begin
        Assert.IsTrue(Src.ReadAsSecretReturnsTrue(),
            'HttpContent.ReadAs(SecretText) should return true');
    end;

    [Test]
    procedure ReadAsSecret_IsEmpty_FalseForNonEmpty()
    begin
        Assert.IsFalse(Src.ReadAsSecretIsEmpty('not-empty'),
            'ReadAs(SecretText) must not return empty for non-empty content');
    end;
}
