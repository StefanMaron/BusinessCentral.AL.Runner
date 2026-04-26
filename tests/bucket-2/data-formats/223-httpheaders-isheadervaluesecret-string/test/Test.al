// Tests for issue #xxx — HttpHeaders.IsHeaderValueSecret with string literal argument.
//
// BC emits ALIsHeaderValueSecret("Content-Type") with a raw string literal.
// The mock must accept a string key, not just NavText.
codeunit 62231 "HHSecTest"
{
    Subtype = Test;

    var Assert: Codeunit Assert;

    // Positive: IsHeaderValueSecret always returns false in standalone mode.
    // Proves the string-key overload resolves and returns the expected non-default value (false).
    [Test]
    procedure IsHeaderValueSecret_StringKey_ReturnsFalse()
    var
        Src: Codeunit HHSecSrc;
    begin
        Assert.IsFalse(Src.IsSecretHeader('Authorization'), 'Non-secret header must return false');

    end;

    // Positive: the literal-argument pattern from FinApiConnector compiles and returns true
    // (header was added successfully).
    [Test]
    procedure SetContentTypeHeader_LiteralArg_HeaderAdded()
    var
        Src: Codeunit HHSecSrc;
    begin
        Assert.IsTrue(
            Src.SetContentTypeHeader('application/json'),
            'Content-Type header should be present after add');
    end;

    // Negative: a different header key also returns false for IsHeaderValueSecret.
    [Test]
    procedure IsHeaderValueSecret_AnotherKey_ReturnsFalse()
    var
        Src: Codeunit HHSecSrc;
    begin
        Assert.IsFalse(Src.IsSecretHeader('X-Custom-Header'), 'Custom header must not be secret');
    end;
}
