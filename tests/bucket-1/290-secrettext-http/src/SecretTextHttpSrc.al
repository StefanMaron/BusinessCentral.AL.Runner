/// Helper codeunit exercising SecretText in HTTP patterns — issues #1086, #1091.
///
/// Exercises:
///   HttpHeaders.Add(name, SecretText)           — generates ALAdd(DataError, string, NavSecretText)
///   HttpHeaders.TryAddWithoutValidation(lit, st) — generates ALTryAddWithoutValidation(DataError, string, NavSecretText)
///   HttpContent.WriteFrom(SecretText)            — generates AlCompat.HttpContentLoadFrom(content, NavSecretText)
codeunit 165001 "STH Src"
{
    /// Add a SecretText header using HttpHeaders.Add.
    /// BC emits: headers.ALAdd(DataError, string, NavSecretText)
    procedure AddSecretHeader(name: Text; plainValue: Text)
    var
        headers: HttpHeaders;
        req: HttpRequestMessage;
        secretVal: SecretText;
    begin
        secretVal := plainValue;
        req.GetHeaders(headers);
        headers.Add(name, secretVal);
    end;

    /// TryAddWithoutValidation with a literal name and SecretText value.
    /// BC emits: ALTryAddWithoutValidation(DataError, "Authorization", NavSecretText)
    /// — string → NavText (#1091) and NavSecretText → NavText gaps.
    procedure TryAddSecretHeader(plainValue: Text): Boolean
    var
        headers: HttpHeaders;
        req: HttpRequestMessage;
        secretVal: SecretText;
    begin
        secretVal := plainValue;
        req.GetHeaders(headers);
        exit(headers.TryAddWithoutValidation('Authorization', secretVal));
    end;

    /// WriteFrom with a SecretText value.
    /// BC emits: AlCompat.HttpContentLoadFrom(content, NavSecretText)
    /// — NavSecretText → MockInStream gap (#1086).
    procedure WriteSecretContent(plainValue: Text)
    var
        content: HttpContent;
        secretVal: SecretText;
    begin
        secretVal := plainValue;
        content.WriteFrom(secretVal);
    end;
}
