/// Tests for TestPage.RunPageBackgroundTask and TestHttpRequestMessage.QueryParameters — issue #950.
codeunit 128001 "TIS Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── TestPage.RunPageBackgroundTask ────────────────────────────────────────

    [Test]
    procedure RunPageBackgroundTask_ReturnsEmptyDict()
    var
        P: TestPage "TIS Card Page";
        Result: Dictionary of [Text, Text];
    begin
        // Positive: RunPageBackgroundTask returns empty dictionary in runner (no-op stub).
        P.OpenView();
        Result := P.RunPageBackgroundTask(1);
        Assert.AreEqual(0, Result.Count(), 'RunPageBackgroundTask must return empty dictionary in runner');
        P.Close();
    end;

    [Test]
    procedure RunPageBackgroundTask_CountIsNotPositive()
    var
        P: TestPage "TIS Card Page";
        Result: Dictionary of [Text, Text];
    begin
        // Negative: result dict must be empty (no background task runs in standalone).
        P.OpenView();
        Result := P.RunPageBackgroundTask(1);
        Assert.IsFalse(Result.Count() > 0, 'RunPageBackgroundTask result must have 0 entries in runner');
        P.Close();
    end;

    [Test]
    procedure RunPageBackgroundTask_DifferentTaskIds_BothEmpty()
    var
        P: TestPage "TIS Card Page";
        R1: Dictionary of [Text, Text];
        R2: Dictionary of [Text, Text];
    begin
        // Positive: different task IDs both return empty dicts.
        P.OpenView();
        R1 := P.RunPageBackgroundTask(1);
        R2 := P.RunPageBackgroundTask(99);
        Assert.AreEqual(0, R1.Count(), 'Task 1 must return empty dict');
        Assert.AreEqual(0, R2.Count(), 'Task 99 must return empty dict');
        P.Close();
    end;

    // ── TestHttpRequestMessage.QueryParameters ────────────────────────────────

    [Test]
    procedure QueryParameters_FreshRequest_IsEmpty()
    var
        Req: TestHttpRequestMessage;
        Params: Dictionary of [Text, Text];
    begin
        // Positive: fresh request has no query parameters.
        Params := Req.QueryParameters();
        Assert.AreEqual(0, Params.Count(), 'Fresh TestHttpRequestMessage must have 0 query parameters');
    end;

    [Test]
    procedure QueryParameters_CountIsNotPositive()
    var
        Req: TestHttpRequestMessage;
        Params: Dictionary of [Text, Text];
    begin
        // Negative: count must not be positive (no params in runner stub).
        Params := Req.QueryParameters();
        Assert.IsFalse(Params.Count() > 0, 'QueryParameters count must not be positive on a fresh request');
    end;

}
