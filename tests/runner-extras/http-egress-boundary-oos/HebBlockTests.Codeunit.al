// Issue #2547 — the scoping control for "Heb Allow Tests".
//
// The refusal must fire ONLY where a socket would actually open. Under BlockOutboundRequests
// BC refuses the request itself, and that error is real BC behaviour the runner must leave
// alone: replacing it with the runner's own out-of-scope throw would be a divergence, and one
// that would quietly hide which of the two things went wrong.
//
// Without this test, an over-broad fix that refused every HttpClient call by name would pass
// the sibling suite and look correct.
codeunit 64552 "Heb Block Tests"
{
    Subtype = Test;
    TestHttpRequestPolicy = BlockOutboundRequests;

    var
        Assert: Codeunit "Heb Assert";

    [Test]
    procedure UnhandledRequest_UnderBlock_GetsBcsOwnErrorNotTheRunners()
    var
        Client: HttpClient;
        Response: HttpResponseMessage;
    begin
        asserterror Client.Get('https://example.invalid/runner-extras', Response);

        // BC's own wording, from NavNCLTestCodeunitUnhandledHttpRequestException.
        Assert.ExpectedError('BlockOutboundRequests');
        // ...and specifically NOT the runner's refusal: this request never reached the
        // egress boundary, so claiming external-http here would name the wrong cause.
        Assert.NotExpectedError('out-of-scope:');
    end;
}
