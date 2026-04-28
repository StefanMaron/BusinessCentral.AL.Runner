codeunit 1320406 "HRM GetHeaders Bool Src"
{
    /// <summary>
    /// Exercises HttpRequestMessage.GetHeaders() in a boolean context.
    /// </summary>
    procedure GetHeadersInIf(): Boolean
    var
        Request: HttpRequestMessage;
        Headers: HttpHeaders;
    begin
        if not Request.GetHeaders(Headers) then
            exit(false);
        exit(true);
    end;
}
