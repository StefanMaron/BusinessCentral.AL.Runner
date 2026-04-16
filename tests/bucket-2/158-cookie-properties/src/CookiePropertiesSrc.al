/// Helper codeunit exercising Cookie property get/set (Name, Value, Domain, Path, Secure, HttpOnly, Expires).
codeunit 61800 "CP Helper"
{
    /// Sets Name, Value, Domain, Path and reads back all four.
    procedure CreateCookieWithProperties(
        cookieName: Text;
        cookieValue: Text;
        cookieDomain: Text;
        cookiePath: Text
    ): Text
    var
        Cookie: Cookie;
    begin
        Cookie.Name := cookieName;
        Cookie.Value := cookieValue;
        Cookie.Domain := cookieDomain;
        Cookie.Path := cookiePath;
        exit(Cookie.Name + '|' + Cookie.Value + '|' + Cookie.Domain + '|' + Cookie.Path);
    end;

    procedure GetCookieName(Cookie: Cookie): Text
    begin
        exit(Cookie.Name);
    end;

    procedure GetCookieValue(Cookie: Cookie): Text
    begin
        exit(Cookie.Value);
    end;

    procedure GetCookieDomain(Cookie: Cookie): Text
    begin
        exit(Cookie.Domain);
    end;

    procedure GetCookiePath(Cookie: Cookie): Text
    begin
        exit(Cookie.Path);
    end;

    procedure GetCookieSecure(Cookie: Cookie): Boolean
    begin
        exit(Cookie.Secure);
    end;

    procedure GetCookieHttpOnly(Cookie: Cookie): Boolean
    begin
        exit(Cookie.HttpOnly);
    end;

    procedure GetCookieExpires(Cookie: Cookie): DateTime
    begin
        exit(Cookie.Expires);
    end;

    procedure DefaultExpires(): DateTime
    var
        Cookie: Cookie;
    begin
        exit(Cookie.Expires);
    end;

    procedure DefaultSecure(): Boolean
    var
        Cookie: Cookie;
    begin
        exit(Cookie.Secure);
    end;

    procedure DefaultHttpOnly(): Boolean
    var
        Cookie: Cookie;
    begin
        exit(Cookie.HttpOnly);
    end;
}
