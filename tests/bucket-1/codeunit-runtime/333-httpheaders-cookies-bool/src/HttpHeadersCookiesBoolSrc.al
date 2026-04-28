codeunit 1320500 "HH Cookie Bool Src"
{
    procedure AddHeaderReturnsTrue(): Boolean
    var
        req: HttpRequestMessage;
        headers: HttpHeaders;
    begin
        req.GetHeaders(headers);
        exit(headers.Add('X-Test', 'value'));
    end;

    procedure SetCookieReturnsTrue(): Boolean
    var
        req: HttpRequestMessage;
    begin
        exit(req.SetCookie('Session', 'abc'));
    end;

    procedure RemoveCookieReturnsTrue(): Boolean
    var
        req: HttpRequestMessage;
    begin
        req.SetCookie('Session', 'abc');
        exit(req.RemoveCookie('Session'));
    end;

    procedure RemoveCookieMissingReturnsFalse(): Boolean
    var
        req: HttpRequestMessage;
    begin
        exit(req.RemoveCookie('Missing'));
    end;
}
