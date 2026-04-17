/// Proves ErrorInfo.Create, ErrorInfo.Message (method form), and
/// ErrorInfo.ErrorType work in the runner.
codeunit 97901 "EIC Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EIC Src";

    // ------------------------------------------------------------------
    // ErrorInfo.Create(Message) — 1-arg overload
    // ------------------------------------------------------------------

    [Test]
    procedure Create_MessageIsPreserved()
    begin
        // [GIVEN] ErrorInfo.Create('hello')
        // [WHEN] Message is read back
        // [THEN] Returns 'hello'
        Assert.AreEqual('hello', Src.GetMessageFromCreated('hello'),
            'ErrorInfo.Create(msg) should set Message');
    end;

    [Test]
    procedure Create_EmptyMessage()
    begin
        // Empty string is valid
        Assert.AreEqual('', Src.GetMessageFromCreated(''),
            'ErrorInfo.Create empty message should be empty');
    end;

    // ------------------------------------------------------------------
    // ErrorInfo.Create(Message, ErrorType) — 2-arg overload
    // ------------------------------------------------------------------

    [Test]
    procedure CreateWithType_ReturnsErrorInfo()
    var
        ErrInfo: ErrorInfo;
    begin
        // Should not throw
        ErrInfo := Src.CreateWithMessageAndType('oops');
        Assert.AreEqual('oops', ErrInfo.Message,
            'Create(msg, ErrorType) should set Message');
    end;

    // ------------------------------------------------------------------
    // ErrorInfo.Message(text) — method-form setter
    // ------------------------------------------------------------------

    [Test]
    procedure MessageMethodSetter_ReturnsSetValue()
    begin
        Assert.AreEqual('world', Src.SetMessageViaMethod('world'),
            'Message(text) setter should be readable via Message getter');
    end;

    // ------------------------------------------------------------------
    // ErrorInfo.ErrorType — getter and setter
    // ------------------------------------------------------------------

    [Test]
    procedure ErrorType_ClientRoundtrip()
    begin
        Assert.AreEqual(ErrorType::Client, Src.SetErrorTypeAndRead(ErrorType::Client),
            'ErrorType set to Client should round-trip');
    end;

    [Test]
    procedure ErrorType_InternalRoundtrip()
    begin
        Assert.AreEqual(ErrorType::Internal, Src.SetErrorTypeAndRead(ErrorType::Internal),
            'ErrorType set to Internal should round-trip');
    end;
}
