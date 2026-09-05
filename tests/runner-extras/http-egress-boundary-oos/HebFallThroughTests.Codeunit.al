// Issue #2547 — the other half of the handler's return value, and here for the same reason as
// "Heb Mock Tests": [HttpClientHandler] is OnPrem-scoped and cannot be declared in the
// Cloud-target al-language corpus (error AL0296).
//
// A handler returning TRUE asks for the request to be sent for real. Under
// BlockOutboundRequests that is refused by the platform — and it must be the PLATFORM's
// refusal, not the runner's, because the request never reached the egress boundary.
codeunit 64554 "Heb FallThrough Tests"
{
    Subtype = Test;
    TestHttpRequestPolicy = BlockOutboundRequests;

    var
        Assert: Codeunit "Heb Assert";

    [Test]
    [HandlerFunctions('FallThroughHandler')]
    procedure HandlerReturningTrue_UnderBlock_RaisesTheFallThroughError()
    var
        Client: HttpClient;
        Response: HttpResponseMessage;
    begin
        asserterror Client.Get('https://unreachable.invalid/fallthrough', Response);

        Assert.ExpectedError('BlockOutboundRequests');
        Assert.NotExpectedError('out-of-scope:');
    end;

    [HttpClientHandler]
    procedure FallThroughHandler(Request: TestHttpRequestMessage; var Response: TestHttpResponseMessage): Boolean
    begin
        exit(true);
    end;
}
