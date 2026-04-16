/// Helper codeunit exercising Cookie property get/set (Name, Value, Domain, Path, Secure, HttpOnly, Expires).
codeunit 61800 "CP Helper"
{
    procedure CreateCookieWithProperties(
        cookieName: Text;
        cookieValue: Text;
        cookieDomain: Text;
        cookiePath: Text;
        cookieSecure: Boolean;
        cookieHttpOnly: Boolean
    ): Text
    var
        Cookie: Cookie;
    begin
        Cookie.Name := cookieName;
        Cookie.Value := cookieValue;
        Cookie.Domain := cookieDomain;
        Cookie.Path := cookiePath;
        Cookie.Secure := cookieSecure;
        Cookie.HttpOnly := cookieHttpOnly;
        exit(Cookie.Name + '|' + Cookie.Value + '|' + Cookie.Domain + '|' +
             Cookie.Path + '|' + Format(Cookie.Secure) + '|' + Format(Cookie.HttpOnly));
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

    procedure SetAndGetExpires(expiresDateTime: DateTime): DateTime
    var
        Cookie: Cookie;
    begin
        Cookie.Expires := expiresDateTime;
        exit(Cookie.Expires);
    end;
}
