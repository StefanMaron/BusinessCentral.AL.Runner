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

    procedure SendRequestFails()
    var
        Client: HttpClient;
        Request: HttpRequestMessage;
        Response: HttpResponseMessage;
    begin
        Client.Send(Request, Response);
    end;

    procedure BuildRequestGetMethod(): Text
    var
        Request: HttpRequestMessage;
        Content: HttpContent;
    begin
        Request.Method('POST');
        Request.SetRequestUri('https://example.com/api');
        Content.WriteFrom('test body');
        Request.Content(Content);
        exit(Request.Method());
    end;

    procedure BuildRequestReadContent(): Text
    var
        Request: HttpRequestMessage;
        Content: HttpContent;
        Result: Text;
    begin
        Request.Method('POST');
        Content.WriteFrom('test body');
        Request.Content(Content);
        Content.ReadAs(Result);
        exit(Result);
    end;

    procedure StreamRoundTrip(Input: Text): Text
    var
        BlobRec: Record "Http Test Blob";
        Content: HttpContent;
        OutStr: OutStream;
        InStr: InStream;
        Result: Text;
    begin
        BlobRec.Data.CreateOutStream(OutStr);
        OutStr.WriteText(Input);
        BlobRec.Data.CreateInStream(InStr);
        Content.WriteFrom(InStr);
        Content.ReadAs(Result);
        exit(Result);
    end;

    procedure GetRequestFails()
    var
        Client: HttpClient;
        Response: HttpResponseMessage;
    begin
        Client.Get('https://example.com', Response);
    end;
}
