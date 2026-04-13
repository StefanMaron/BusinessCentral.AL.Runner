/// <summary>
/// Probe codeunit for test 89 — BC Nav* types whose constructors accept ITreeObject.
/// When used as local variables in AL, the BC compiler emits `new NavHttpClient(this)`
/// etc. in the generated scope constructor. Before the fix the scope type (which extends
/// AlScope, not ITreeObject) caused CS1503 and the file was excluded as a CompilationGap.
///
/// SendRequest() is the method that declares HTTP variables (NavHttpClient, etc.) in its
/// scope. It would fail at runtime because HttpClient requires a BC service tier, but it
/// is never invoked by the tests — it is here purely to exercise the compilation fix.
///
/// IsValidUrl() is a pure-logic helper with no HTTP variables; it is called by tests.
/// </summary>
codeunit 56900 "HTTP Probe"
{
    /// <summary>
    /// Method that declares HttpClient, HttpRequestMessage, HttpResponseMessage, and
    /// HttpContent as local variables. The BC compiler generates
    ///   new NavHttpClient(this)   new NavHttpContent(this)
    ///   new NavHttpRequestMessage(this)   new NavHttpResponseMessage(this)
    /// in the scope constructor — the exact pattern that produced CS1503 before the fix.
    /// This method is never called by the test codeunit; its mere presence verifies
    /// that the file compiles without CS1503.
    /// </summary>
    procedure SendRequest(Url: Text): Text
    var
        Client: HttpClient;
        Content: HttpContent;
        Request: HttpRequestMessage;
        Response: HttpResponseMessage;
    begin
        // Would call Client.Send(Request, Response) in production, but
        // HTTP is out of scope for al-runner. Never invoked by tests.
        exit('');
    end;

    /// <summary>
    /// Pure-logic URL validator.  No HTTP variables → no Nav* constructor issue.
    /// This is what the tests actually call.
    /// </summary>
    procedure IsValidUrl(Url: Text): Boolean
    begin
        exit(Url.StartsWith('http://') or Url.StartsWith('https://'));
    end;
}
