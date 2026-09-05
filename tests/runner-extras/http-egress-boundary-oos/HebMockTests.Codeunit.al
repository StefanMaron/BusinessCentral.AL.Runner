// Issue #2547 — AL's HTTP mocking, which is in scope precisely because nothing leaves the
// process. Would normally live upstream: this is plain BC behaviour, not a runner claim.
//
// It cannot. The al-language corpus app targets Cloud, and [HttpClientHandler] has scope
// OnPrem, so the AL compiler rejects it there outright:
//
//   error AL0296: The application object or method 'HttpClientHandler' has scope 'OnPrem'
//   and cannot be used for 'Cloud' development.
//
// Measured, not assumed — an earlier draft of the corpus PR carried these two tests and would
// not compile. The half the corpus CAN express is upstream as codeunit 60318.
codeunit 64553 "Heb Mock Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Heb Assert";

    // The URL is deliberately unreachable: a response arriving at all proves the request was
    // served in-process rather than by anything on the network.
    [Test]
    [HandlerFunctions('MockOkHandler')]
    procedure HttpClientHandler_ReturningFalse_ServesTheRequest()
    var
        Client: HttpClient;
        Response: HttpResponseMessage;
        Body: Text;
    begin
        if not Client.Get('https://unreachable.invalid/mocked', Response) then
            Error('a mocked GET must report success');
        if not Response.IsSuccessStatusCode() then
            Error('the handler set status 200, got %1', Response.HttpStatusCode());

        Response.Content().ReadAs(Body);
        if Body <> 'MOCKED' then
            Error('the body must be the one the handler wrote, got ''%1''', Body);
    end;

    [HttpClientHandler]
    procedure MockOkHandler(Request: TestHttpRequestMessage; var Response: TestHttpResponseMessage): Boolean
    begin
        Response.HttpStatusCode := 200;
        Response.ReasonPhrase := 'OK';
        Response.Content.WriteFrom('MOCKED');
        // FALSE means "I have answered this request". TRUE would mean "send it for real" —
        // the return value reads backwards, which is what the fall-through test below pins.
        exit(false);
    end;
}
