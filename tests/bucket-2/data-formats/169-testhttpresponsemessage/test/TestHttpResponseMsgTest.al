/// Tests for TestHttpResponseMessage — issue #739.
codeunit 96001 "THRM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── HttpStatusCode ───────────────────────────────────────────

    [Test]
    procedure TestHttpResponseMsg_HttpStatusCode_GetSet()
    var
        Resp: TestHttpResponseMessage;
    begin
        Resp.HttpStatusCode := 404;
        Assert.AreEqual(404, Resp.HttpStatusCode,
            'HttpStatusCode must round-trip via get/set');
    end;

    [Test]
    procedure TestHttpResponseMsg_HttpStatusCode_DefaultIs200()
    var
        Resp: TestHttpResponseMessage;
    begin
        Assert.AreEqual(200, Resp.HttpStatusCode,
            'HttpStatusCode default must be 200');
    end;

    // ── IsSuccessfulRequest ──────────────────────────────────────

    [Test]
    procedure TestHttpResponseMsg_IsSuccessfulRequest_TrueFor200()
    var
        Resp: TestHttpResponseMessage;
    begin
        Resp.HttpStatusCode := 200;
        Assert.IsTrue(Resp.IsSuccessfulRequest(),
            'Status 200 must be a successful request');
    end;

    [Test]
    procedure TestHttpResponseMsg_IsSuccessfulRequest_TrueFor201()
    var
        Resp: TestHttpResponseMessage;
    begin
        Resp.HttpStatusCode := 201;
        Assert.IsTrue(Resp.IsSuccessfulRequest(),
            'Status 201 must be a successful request');
    end;

    [Test]
    procedure TestHttpResponseMsg_IsSuccessfulRequest_FalseFor400()
    var
        Resp: TestHttpResponseMessage;
    begin
        Resp.HttpStatusCode := 400;
        Assert.IsFalse(Resp.IsSuccessfulRequest(),
            'Status 400 must not be a successful request');
    end;

    [Test]
    procedure TestHttpResponseMsg_IsSuccessfulRequest_FalseFor500()
    var
        Resp: TestHttpResponseMessage;
    begin
        Resp.HttpStatusCode := 500;
        Assert.IsFalse(Resp.IsSuccessfulRequest(),
            'Status 500 must not be a successful request');
    end;

    // ── ReasonPhrase ─────────────────────────────────────────────

    [Test]
    procedure TestHttpResponseMsg_ReasonPhrase_GetSet()
    var
        Resp: TestHttpResponseMessage;
    begin
        Resp.ReasonPhrase := 'Not Found';
        Assert.AreEqual('Not Found', Resp.ReasonPhrase,
            'ReasonPhrase must round-trip via get/set');
    end;

    // ── Content ──────────────────────────────────────────────────

    [Test]
    procedure TestHttpResponseMsg_Content_IsNonNull()
    var
        Resp: TestHttpResponseMessage;
        Body: Text;
    begin
        Resp.Content.WriteFrom('hello');
        Resp.Content.ReadAs(Body);
        Assert.AreEqual('hello', Body,
            'Content must allow writing and reading back');
    end;

    // ── Headers ──────────────────────────────────────────────────

    [Test]
    procedure TestHttpResponseMsg_Headers_CanAdd()
    var
        Resp: TestHttpResponseMessage;
        vals: array[1] of Text;
    begin
        Resp.Headers.Add('X-Test', 'abc');
        Resp.Headers.GetValues('X-Test', vals);
        Assert.AreEqual('abc', vals[1],
            'Headers must store and retrieve added header values');
    end;

    // ── IsBlockedByEnvironment ───────────────────────────────────

    [Test]
    procedure TestHttpResponseMsg_IsBlockedByEnvironment_ReturnsFalse()
    var
        Resp: TestHttpResponseMessage;
    begin
        Assert.IsFalse(Resp.IsBlockedByEnvironment(),
            'IsBlockedByEnvironment must return false in test mode');
    end;
}
