/// Tests for SecretText in HTTP patterns — issues #1086, #1091.
codeunit 165002 "STH Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";
        Helper: Codeunit "STH Src";

    /// HttpHeaders.Add(name, SecretText) must not throw.
    [Test]
    procedure AddSecretHeader_NoThrow()
    begin
        // [GIVEN] A non-empty secret value
        // [WHEN] Adding a secret header using HttpHeaders.Add(name, SecretText)
        Helper.AddSecretHeader('Authorization', 'bearer-token-abc');
        // [THEN] No error thrown — compilation and execution succeed
    end;

    /// HttpHeaders.TryAddWithoutValidation(literal, SecretText) must return true.
    /// Covers: string → NavText (#1091) and NavSecretText → NavText gaps.
    [Test]
    procedure TryAddSecretHeader_ReturnsTrue()
    var
        Result: Boolean;
    begin
        // [GIVEN] A non-empty secret value
        // [WHEN] TryAddWithoutValidation with literal name and SecretText value
        Result := Helper.TryAddSecretHeader('my-api-key-xyz');
        // [THEN] Returns true (always-succeed stub; non-default value proves mock works)
        Assert.IsTrue(Result, 'TryAddWithoutValidation with SecretText must return true');
    end;

    /// HttpContent.WriteFrom(SecretText) must not throw — covers #1086.
    [Test]
    procedure WriteSecretContent_NoThrow()
    begin
        // [GIVEN] A non-empty secret value
        // [WHEN] Writing SecretText to HttpContent
        Helper.WriteSecretContent('secret-body-content');
        // [THEN] No error thrown — NavSecretText → MockInStream gap resolved
    end;
}
