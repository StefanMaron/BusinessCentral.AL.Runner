/// Exercises HttpContent.GetHeaders() — must be invocable as a method.
codeunit 60440 "HCGH Src"
{
    procedure GetContentHeaders_DoesNotThrow(): Boolean
    var
        content: HttpContent;
        headers: HttpHeaders;
    begin
        content.WriteFrom('test body');
        content.GetHeaders(headers);
        exit(true);
    end;

    procedure GetContentHeaders_ThenAdd(): Boolean
    var
        content: HttpContent;
        headers: HttpHeaders;
    begin
        content.WriteFrom('hello');
        content.GetHeaders(headers);
        headers.Add('Content-Type', 'text/plain');
        exit(headers.Contains('Content-Type'));
    end;
}
