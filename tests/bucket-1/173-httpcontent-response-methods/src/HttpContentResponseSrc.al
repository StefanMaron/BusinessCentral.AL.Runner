/// Helper codeunit for HttpContent.Clear / WriteFrom / IsSecretContent
/// and HttpResponseMessage.GetCookie / GetCookieNames / IsBlockedByEnvironment.
codeunit 99200 "HCR Helper"
{
    // ── HttpContent ───────────────────────────────────────────────────────────

    /// WriteFrom then ReadAs round-trip.
    procedure ContentWriteFromReadAs(text: Text): Text
    var
        content: HttpContent;
        result: Text;
    begin
        content.WriteFrom(text);
        content.ReadAs(result);
        exit(result);
    end;

    /// WriteFrom sets content; Clear empties it.
    procedure ContentClearResult(): Text
    var
        content: HttpContent;
        result: Text;
    begin
        content.WriteFrom('hello');
        content.Clear();
        content.ReadAs(result);
        exit(result);
    end;

    /// IsSecretContent returns false (runner has no secret content support).
    procedure ContentIsSecretContent(): Boolean
    var
        content: HttpContent;
    begin
        exit(content.IsSecretContent());
    end;

    // ── HttpResponseMessage ───────────────────────────────────────────────────

    /// IsBlockedByEnvironment returns false in the runner.
    procedure ResponseIsBlocked(): Boolean
    var
        response: HttpResponseMessage;
    begin
        exit(response.IsBlockedByEnvironment());
    end;

    /// GetCookie returns false (no cookies in runner).
    procedure ResponseGetCookieResult(cookieName: Text): Boolean
    var
        response: HttpResponseMessage;
        cookie: Cookie;
    begin
        exit(response.GetCookie(cookieName, cookie));
    end;

    /// GetCookieNames returns an empty list.
    procedure ResponseGetCookieNamesCount(): Integer
    var
        response: HttpResponseMessage;
        names: List of [Text];
    begin
        response.GetCookieNames(names);
        exit(names.Count());
    end;
}
