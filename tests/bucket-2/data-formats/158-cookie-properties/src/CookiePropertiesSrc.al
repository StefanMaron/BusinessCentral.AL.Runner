/// Helper codeunit exercising Cookie property get/set (Name, Value) and read-only properties (Domain, Path, Secure, HttpOnly, Expires).
codeunit 61830 "CP Helper"
{
    /// Sets Name and Value (writable) and reads back both.
    procedure CreateCookieWithNameValue(
        cookieName: Text;
        cookieValue: Text
    ): Text
    var
        Cookie: Cookie;
    begin
        Cookie.Name := cookieName;
        Cookie.Value := cookieValue;
        exit(Cookie.Name + '|' + Cookie.Value);
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

    procedure DefaultDomain(): Text
    var
        Cookie: Cookie;
    begin
        exit(Cookie.Domain);
    end;

    procedure DefaultPath(): Text
    var
        Cookie: Cookie;
    begin
        exit(Cookie.Path);
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
