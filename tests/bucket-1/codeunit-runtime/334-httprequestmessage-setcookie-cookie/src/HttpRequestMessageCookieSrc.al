codeunit 1320502 "HRM Cookie Src"
{
    procedure SetCookieObjectReturnsTrue(): Boolean
    var
        req: HttpRequestMessage;
        cookie: Cookie;
    begin
        cookie.Name := 'SessionId';
        cookie.Value := 'abc-123';
        exit(req.SetCookie(cookie));
    end;

    procedure SetCookieObjectRoundTrip(): Text
    var
        req: HttpRequestMessage;
        cookieIn: Cookie;
        cookieOut: Cookie;
    begin
        cookieIn.Name := 'SessionId';
        cookieIn.Value := 'abc-123';
        req.SetCookie(cookieIn);
        if req.GetCookie('SessionId', cookieOut) then
            exit(cookieOut.Value);
        exit('');
    end;

    procedure GetCookieMissingReturnsFalse(): Boolean
    var
        req: HttpRequestMessage;
        cookieOut: Cookie;
    begin
        exit(req.GetCookie('MissingCookie', cookieOut));
    end;
}
