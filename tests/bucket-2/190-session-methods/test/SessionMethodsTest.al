codeunit 60161 "SES Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "SES Src";

    [Test]
    procedure CurrentClientType_IsBackground()
    begin
        // Standalone contract — no interactive session, so client type is Background.
        Assert.AreEqual(ClientType::Background, Src.GetClientType(),
            'Session.CurrentClientType must report Background in standalone mode');
    end;

    [Test]
    procedure CurrentExecutionMode_IsStandard()
    begin
        // Standalone contract — ExecutionMode::Standard (no debugger attached).
        Assert.AreEqual(ExecutionMode::Standard, Src.GetExecutionMode(),
            'Session.CurrentExecutionMode must report Standard in standalone mode');
    end;

    [Test]
    procedure DefaultClientType_IsBackground()
    begin
        Assert.AreEqual(ClientType::Background, Src.GetDefaultClientType(),
            'Session.DefaultClientType must report Background in standalone mode');
    end;

    [Test]
    procedure LogMessage_DoesNotThrow()
    begin
        // Positive: LogMessage with a dictionary is a no-op standalone but must
        // not throw — the runner silently drops telemetry.
        Assert.IsTrue(Src.LogMessageDoesNotThrow(),
            'Session.LogMessage must complete without throwing');
    end;

    [Test]
    procedure LogAuditMessage_DoesNotThrow()
    begin
        Assert.IsTrue(Src.LogAuditMessageDoesNotThrow(),
            'Session.LogAuditMessage must complete without throwing');
    end;

    [Test]
    procedure ClientType_NotWebClient_NegativeTrap()
    begin
        // Negative trap: standalone must not report an interactive client type.
        Assert.AreNotEqual(ClientType::Web, Src.GetClientType(),
            'Session.CurrentClientType must not report Web in standalone mode');
    end;
}
