codeunit 58200 "STM Secret Text Helper"
{
    /// <summary>
    /// Returns true when the given SecretText holds no value.
    /// Exercises SecretText.IsEmpty().
    /// </summary>
    procedure IsSecretEmpty(secret: SecretText): Boolean
    begin
        exit(secret.IsEmpty());
    end;

    /// <summary>
    /// Assigns the given plain text to a SecretText and returns IsEmpty.
    /// Proves that a non-empty assignment is reflected by IsEmpty() = false.
    /// </summary>
    procedure IsAssignedSecretEmpty(plainText: Text): Boolean
    var
        secret: SecretText;
    begin
        secret := plainText;
        exit(secret.IsEmpty());
    end;

    /// <summary>
    /// Assigns the given plain text to a SecretText and returns Unwrap().
    /// Exercises SecretText.Unwrap() — the only AL-sanctioned way to get the raw value.
    /// </summary>
    procedure UnwrapSecret(plainText: Text): Text
    var
        secret: SecretText;
    begin
        secret := plainText;
        exit(secret.Unwrap());
    end;

    /// <summary>
    /// Builds a formatted SecretText message using SecretStrSubstNo.
    /// Exercises the global SecretStrSubstNo(format, arg) function.
    /// The result is a SecretText, unwrapped here for assertion.
    /// </summary>
    procedure BuildSecretMessage(fmt: Text; arg: Text): Text
    var
        result: SecretText;
    begin
        result := SecretStrSubstNo(fmt, arg);
        exit(result.Unwrap());
    end;
}
