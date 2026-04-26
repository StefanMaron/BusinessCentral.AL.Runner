/// Helper codeunit for HttpContent.WriteFrom(Text/SecretText) round-trip
/// and HttpHeaders.GetSecretValues(Text, List) — issue #1381.
codeunit 310300 "HWF Src"
{
    // ── HttpContent.WriteFrom(Text) ──────────────────────────────────────────

    /// WriteFrom(Text) then ReadAs(Text) round-trip.
    procedure WriteFromTextRoundTrip(body: Text): Text
    var
        content: HttpContent;
        result: Text;
    begin
        content.WriteFrom(body);
        content.ReadAs(result);
        exit(result);
    end;

    // ── HttpContent.WriteFrom(SecretText) ────────────────────────────────────

    /// WriteFrom(SecretText) then ReadAs(Text) round-trip.
    /// The runner treats SecretText as plain text internally.
    procedure WriteFromSecretRoundTrip(plainValue: Text): Text
    var
        content: HttpContent;
        secret: SecretText;
        result: Text;
    begin
        secret := plainValue;
        content.WriteFrom(secret);
        content.ReadAs(result);
        exit(result);
    end;

    // ── HttpHeaders.GetSecretValues(Text, List of [SecretText]) ──────────────

    /// Adds a plain-text header then retrieves it via GetSecretValues.
    /// Returns the count of values in the secrets list.
    procedure GetSecretValuesCount(headerName: Text; headerValue: Text): Integer
    var
        req: HttpRequestMessage;
        headers: HttpHeaders;
        secrets: List of [SecretText];
    begin
        req.GetHeaders(headers);
        headers.Add(headerName, headerValue);
        headers.GetSecretValues(headerName, secrets);
        exit(secrets.Count());
    end;

    /// Returns the first secret value as plain text (SecretText unwrap pattern).
    procedure GetSecretValuesFirst(headerName: Text; headerValue: Text): Text
    var
        req: HttpRequestMessage;
        headers: HttpHeaders;
        secrets: List of [SecretText];
        secret: SecretText;
    begin
        req.GetHeaders(headers);
        headers.Add(headerName, headerValue);
        headers.GetSecretValues(headerName, secrets);
        if secrets.Count() > 0 then begin
            secrets.Get(1, secret);
            exit(secret.Unwrap());
        end;
        exit('');
    end;

    /// Returns false when GetSecretValues is called for a missing key.
    procedure GetSecretValuesMissingKey(headerName: Text): Boolean
    var
        req: HttpRequestMessage;
        headers: HttpHeaders;
        secrets: List of [SecretText];
    begin
        req.GetHeaders(headers);
        exit(headers.GetSecretValues(headerName, secrets));
    end;
}
