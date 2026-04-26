/// Helper codeunit exercising HttpHeaders gap methods — issue #746.
codeunit 97000 "HHM Src"
{
    // TryAddWithoutValidation — adds header, returns true
    procedure TryAddWithoutValidation(name: Text; value: Text): Boolean
    var
        req: HttpRequestMessage;
        headers: HttpHeaders;
    begin
        req.GetHeaders(headers);
        exit(headers.TryAddWithoutValidation(name, value));
    end;

    // TryAddWithoutValidation then Contains — header is present
    procedure TryAddThenContains(name: Text; value: Text): Boolean
    var
        req: HttpRequestMessage;
        headers: HttpHeaders;
    begin
        req.GetHeaders(headers);
        headers.TryAddWithoutValidation(name, value);
        exit(headers.Contains(name));
    end;

    // Clear — removes all headers
    procedure ClearRemovesHeaders(name: Text; value: Text): Boolean
    var
        req: HttpRequestMessage;
        headers: HttpHeaders;
    begin
        req.GetHeaders(headers);
        headers.Add(name, value);
        headers.Clear();
        exit(headers.Contains(name));
    end;

    // Keys — returns names of added headers
    procedure KeysContainsAdded(name: Text; value: Text): Boolean
    var
        req: HttpRequestMessage;
        headers: HttpHeaders;
        keys: List of [Text];
    begin
        req.GetHeaders(headers);
        headers.Add(name, value);
        keys := headers.Keys();
        exit(keys.Contains(name));
    end;

    // ContainsSecret — returns false for plain headers
    procedure ContainsSecretFalseForPlain(name: Text; value: Text): Boolean
    var
        req: HttpRequestMessage;
        headers: HttpHeaders;
    begin
        req.GetHeaders(headers);
        headers.Add(name, value);
        exit(headers.ContainsSecret(name));
    end;

    // GetSecretValues — runner treats all headers as plain text internally;
    // values are returned wrapped as SecretText regardless of how they were added.
    procedure GetSecretValuesCount(name: Text; value: Text): Integer
    var
        req: HttpRequestMessage;
        headers: HttpHeaders;
        secrets: List of [SecretText];
    begin
        req.GetHeaders(headers);
        headers.Add(name, value);
        headers.GetSecretValues(name, secrets);
        exit(secrets.Count());
    end;
}
