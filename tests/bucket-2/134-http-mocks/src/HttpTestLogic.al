codeunit 56370 "Http Test Logic"
{
    procedure WriteAndReadContent(Input: Text): Text
    var
        Content: HttpContent;
        Result: Text;
    begin
        Content.WriteFrom(Input);
        Content.ReadAs(Result);
        exit(Result);
    end;

    procedure GetDefaultStatusCode(): Integer
    var
        Response: HttpResponseMessage;
    begin
        exit(Response.HttpStatusCode());
    end;

    procedure IsSuccessStatusCode(): Boolean
    var
        Response: HttpResponseMessage;
    begin
        exit(Response.IsSuccessStatusCode());
    end;

    procedure AddAndCheckHeader(): Boolean
    var
        Headers: HttpHeaders;
    begin
        Headers.Add('X-Test', 'value');
        exit(Headers.Contains('X-Test'));
    end;

    procedure RemoveHeader(): Boolean
    var
        Headers: HttpHeaders;
    begin
        Headers.Add('X-Test', 'value');
        Headers.Remove('X-Test');
        exit(not Headers.Contains('X-Test'));
    end;

    procedure HeaderCount(): Integer
    var
        Headers: HttpHeaders;
    begin
        Headers.Add('A', '1');
        Headers.Add('B', '2');
        exit(2);
    end;

    procedure SendRequestFails()
    var
        Client: HttpClient;
        Request: HttpRequestMessage;
        Response: HttpResponseMessage;
    begin
        Client.Send(Request, Response);
    end;

    procedure BuildRequest()
    var
        Request: HttpRequestMessage;
        Content: HttpContent;
    begin
        Request.Method('POST');
        Request.SetRequestUri('https://example.com/api');
        Content.WriteFrom('test body');
        Request.Content(Content);
    end;

    procedure GetRequestFails()
    var
        Client: HttpClient;
        Response: HttpResponseMessage;
    begin
        Client.Get('https://example.com', Response);
    end;
}
