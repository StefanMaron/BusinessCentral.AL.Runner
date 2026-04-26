/// Helper codeunit: HttpHeaders.GetValues(key, List of [Text]) — issue #1080.
/// BC emits NavList<NavText> for List-typed var-parameter; the mock must
/// accept NavList<NavText> alongside the existing MockArray<NavText> overload.
codeunit 62210 "HH GetValues List Src"
{
    /// Adds a header then retrieves it via GetValues into a List of [Text].
    /// Returns the first value found (empty string if none).
    procedure GetFirstValue(name: Text; value: Text): Text
    var
        req: HttpRequestMessage;
        headers: HttpHeaders;
        values: List of [Text];
    begin
        req.GetHeaders(headers);
        headers.Add(name, value);
        headers.GetValues(name, values);
        if values.Count() > 0 then
            exit(values.Get(1));
        exit('');
    end;

    /// Returns the count of values retrieved via GetValues into a List of [Text].
    procedure GetValuesCount(name: Text; value: Text): Integer
    var
        req: HttpRequestMessage;
        headers: HttpHeaders;
        values: List of [Text];
    begin
        req.GetHeaders(headers);
        headers.Add(name, value);
        headers.GetValues(name, values);
        exit(values.Count());
    end;

    /// Returns false when GetValues is called for a key that does not exist.
    procedure GetValuesMissingKey(name: Text): Boolean
    var
        req: HttpRequestMessage;
        headers: HttpHeaders;
        values: List of [Text];
    begin
        req.GetHeaders(headers);
        exit(headers.GetValues(name, values));
    end;
}
