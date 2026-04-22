// Source helpers for issue #xxx — HttpHeaders.ContainsSecret called with a literal key.
//
// BC emits headers.ALIsHeaderValueSecret("Content-Type") where the header name is a
// raw C# string literal (not NavText). The existing mock only has ALIsHeaderValueSecret(NavText),
// causing CS1503 on compile. The fix adds a string overload.
//
// AL: Headers.ContainsSecret('Content-Type') → C#: headers.ALIsHeaderValueSecret("Content-Type")
codeunit 62230 "HHSecSrc"
{
    /// Returns whether the header key is a secret header.
    /// Always false in standalone mode.
    procedure IsSecretHeader(HeaderKey: Text): Boolean
    var
        Headers: HttpHeaders;
    begin
        exit(Headers.ContainsSecret(HeaderKey));
    end;

    /// Mimics the exact pattern from FinApiConnector/BizBankApi:
    ///   if Headers.ContainsSecret('Content-Type') then Headers.Remove('Content-Type');
    ///   Headers.Add('Content-Type', 'application/x-www-form-urlencoded');
    /// BC emits ALIsHeaderValueSecret with the string literal "Content-Type".
    procedure SetContentTypeHeader(ContentType: Text): Boolean
    var
        Headers: HttpHeaders;
    begin
        if not Headers.ContainsSecret('Content-Type') then begin
            Headers.Add('Content-Type', ContentType);
            exit(Headers.Contains('Content-Type'));
        end;
        exit(false);
    end;
}
