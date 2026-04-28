codeunit 1320517 "HC Error Envelope Src"
{
    procedure SendTimeoutResponse()
    var
        Client: HttpClient;
        Request: HttpRequestMessage;
        Response: HttpResponseMessage;
        TestResponse: TestHttpResponseMessage;
    begin
        Request.Method('GET');
        Request.SetRequestUri('https://somevalidurl.com/SomePath');
        TestResponse.HttpStatusCode := 504;
        TestResponse.ReasonPhrase := 'Gateway Timeout';
        TestResponse.Content.WriteFrom('{"message":"Endpoint request timed out"}');
        Client.Send(Request, Response);
    end;

    procedure SendDefaultResponse()
    var
        Client: HttpClient;
        Request: HttpRequestMessage;
        Response: HttpResponseMessage;
    begin
        Request.Method('GET');
        Request.SetRequestUri('https://example.com/success');
        Client.Send(Request, Response);
    end;
}
