// Issue #2547 — the runner's OWN refusal, at the only point a real socket would open.
//
// TestHttpRequestPolicy = AllowAllOutboundRequests is load-bearing, not decoration. Under the
// default policy BC's dispatcher raises NavNCLTestCodeunitUnhandledHttpRequestException before
// the request ever leaves it, so the runner's egress boundary is unreachable and a test written
// without this property would assert against BC's error while claiming to test the runner's.
codeunit 64551 "Heb Allow Tests"
{
    Subtype = Test;
    TestHttpRequestPolicy = AllowAllOutboundRequests;

    var
        Assert: Codeunit "Heb Assert";

    [Test]
    procedure UnhandledRequest_UnderAllowAll_RefusedAtTheEgressBoundary()
    var
        Client: HttpClient;
        Response: HttpResponseMessage;
    begin
        // No [HttpClientHandler] anywhere in this codeunit, and the policy permits outbound
        // requests, so BC's dispatcher returns "not handled" and hands the request on to be
        // sent for real. On a service tier a socket opens here. In this runner it does not.
        asserterror Client.Get('https://example.invalid/runner-extras', Response);

        Assert.ExpectedError('out-of-scope:');
        // The VERB the AL author wrote, not a collapsed 'HttpClient.Send'. loud-failures.md
        // asks for the API that was touched, and the refusal recovers it from the request's
        // HTTP method rather than guessing — so a Post refused here would say HttpClient.Post.
        Assert.ExpectedError('HttpClient.Get');
        Assert.ExpectedError('external-http');
    end;
}
