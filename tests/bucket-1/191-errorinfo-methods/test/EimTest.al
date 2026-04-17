/// Tests for ErrorInfo.Create, Message, ErrorType — issue #215.
codeunit 129001 "EIM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EIM Src";

    // ── ErrorInfo.Create + Message ────────────────────────────────────────────

    [Test]
    procedure Create_SetsMessage()
    var
        ErrInfo: ErrorInfo;
    begin
        // Positive: ErrorInfo.Create(msg) captures the message.
        ErrInfo := Src.CreateWithMessage('Something went wrong');
        Assert.AreEqual('Something went wrong', Src.GetMessage(ErrInfo),
            'Message must match the value passed to Create');
    end;

    [Test]
    procedure Create_EmptyMessage_IsEmpty()
    var
        ErrInfo: ErrorInfo;
    begin
        // Positive: Create with empty string — Message returns empty string.
        ErrInfo := Src.CreateWithMessage('');
        Assert.AreEqual('', Src.GetMessage(ErrInfo), 'Message must be empty when created with empty string');
    end;

    [Test]
    procedure Message_DefaultErrorInfo_IsEmpty()
    var
        ErrInfo: ErrorInfo;
    begin
        // Negative: default-initialised ErrorInfo has empty message.
        Assert.AreEqual('', Src.GetMessage(ErrInfo), 'Default ErrorInfo message must be empty');
    end;

    // ── ErrorInfo.ErrorType ───────────────────────────────────────────────────

    [Test]
    procedure ErrorType_RoundTrips_Client()
    var
        ErrInfo: ErrorInfo;
    begin
        // Positive: ErrorType(Client) round-trips.
        Src.SetErrorTypeClient(ErrInfo);
        Assert.AreEqual(ErrorType::Client, Src.GetErrorType(ErrInfo),
            'ErrorType must round-trip Client');
    end;

    [Test]
    procedure ErrorType_Default_IsClient()
    var
        ErrInfo: ErrorInfo;
    begin
        // Negative: default ErrorType is Client (BC runtime default).
        Assert.AreEqual(ErrorType::Client, Src.GetErrorType(ErrInfo),
            'Default ErrorType must be Client');
    end;

}
