codeunit 1320504 "HC ReadAs Secret Src"
{
    procedure ReadAsSecretRoundTrip(body: Text): Text
    var
        content: HttpContent;
        secret: SecretText;
    begin
        content.WriteFrom(body);
        content.ReadAs(secret);
        exit(secret.Unwrap());
    end;

    procedure ReadAsSecretReturnsTrue(): Boolean
    var
        content: HttpContent;
        secret: SecretText;
    begin
        content.WriteFrom('payload');
        exit(content.ReadAs(secret));
    end;

    procedure ReadAsSecretIsEmpty(body: Text): Boolean
    var
        content: HttpContent;
        secret: SecretText;
    begin
        content.WriteFrom(body);
        content.ReadAs(secret);
        exit(secret.Unwrap() = '');
    end;
}
