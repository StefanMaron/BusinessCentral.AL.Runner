codeunit 1320402 "HRM SetRequestUri Bool Src"
{
    /// <summary>
    /// Exercises SetRequestUri() in a boolean context.
    /// </summary>
    procedure SetRequestUriInIf(Uri: Text): Boolean
    var
        Request: HttpRequestMessage;
    begin
        if not Request.SetRequestUri(Uri) then
            exit(false);
        exit(true);
    end;
}
