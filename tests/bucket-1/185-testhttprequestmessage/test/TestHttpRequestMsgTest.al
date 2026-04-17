/// Tests for TestHttpRequestMessage — issue #752.
codeunit 121001 "THRQM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── Path ──────────────────────────────────────────────────────

    [Test]
    procedure Path_Default_IsEmpty()
    var
        Req: TestHttpRequestMessage;
    begin
        Assert.AreEqual('', Req.Path,
            'Path must default to empty string');
    end;

    [Test]
    procedure Path_TwoInstances_AreEqual()
    var
        Req1: TestHttpRequestMessage;
        Req2: TestHttpRequestMessage;
    begin
        // Both must return the same empty default — not an instance-independent fault
        Assert.AreEqual(Req1.Path, Req2.Path,
            'Two fresh TestHttpRequestMessage instances must have the same Path default');
    end;

    // ── RequestType ───────────────────────────────────────────────

    [Test]
    procedure RequestType_Default_IsEmpty()
    var
        Req: TestHttpRequestMessage;
    begin
        Assert.AreEqual('', Req.RequestType,
            'RequestType must default to empty string');
    end;

    // ── HasSecretUri ──────────────────────────────────────────────

    [Test]
    procedure HasSecretUri_ReturnsFalse()
    var
        Req: TestHttpRequestMessage;
    begin
        Assert.IsFalse(Req.HasSecretUri(),
            'HasSecretUri must return false in standalone mode');
    end;

    [Test]
    procedure HasSecretUri_IsNotNoop()
    var
        Req: TestHttpRequestMessage;
    begin
        // Negative: must return false — not some other value
        Assert.AreNotEqual(true, Req.HasSecretUri(),
            'HasSecretUri must return false (not true)');
    end;
}
