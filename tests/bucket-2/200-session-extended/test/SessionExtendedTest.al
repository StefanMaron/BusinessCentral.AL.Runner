codeunit 60271 "SXE Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "SXE Src";

    [Test]
    procedure ApplicationArea_ReturnsEmpty()
    begin
        // Standalone contract: no application area configured.
        Assert.AreEqual('', Src.GetAppArea(),
            'Session.ApplicationArea must return empty in standalone mode');
    end;

    [Test]
    procedure GetExecutionContext_ReturnsNormal()
    begin
        // AlCompat.GetExecutionContext returns Normal as the safe default.
        Assert.AreEqual(ExecutionContext::Normal, Src.GetExecContext(),
            'Session.GetExecutionContext must return Normal in standalone mode');
    end;

    [Test]
    procedure GetModuleExecutionContext_ReturnsNormal()
    begin
        Assert.AreEqual(ExecutionContext::Normal, Src.GetModuleExecContext(),
            'Session.GetModuleExecutionContext must return Normal');
    end;

    [Test]
    procedure GetCurrentModuleExecutionContext_ReturnsNormal()
    begin
        Assert.AreEqual(ExecutionContext::Normal, Src.GetCurrentModuleExecContext(),
            'Session.GetCurrentModuleExecutionContext must return Normal');
    end;

    [Test]
    procedure SendTraceTag_DoesNotThrow()
    begin
        Assert.IsTrue(Src.SendTraceTag_DoesNotThrow(),
            'Session.SendTraceTag must complete without error');
    end;

    [Test]
    procedure LogSecurityAudit_DoesNotThrow()
    begin
        Assert.IsTrue(Src.LogSecurityAudit_DoesNotThrow(),
            'Session.LogSecurityAudit must complete without error');
    end;

    [Test]
    procedure EnableVerboseTelemetry_DoesNotThrow()
    begin
        Assert.IsTrue(Src.EnableVerboseTelemetry_DoesNotThrow(),
            'Session.EnableVerboseTelemetry must complete without error');
    end;

    [Test]
    procedure ApplicationIdentifier_DoesNotThrow()
    var
        result: Text;
    begin
        // May return empty — just must not throw.
        result := Src.ApplicationIdentifier_DoesNotThrow();
        Assert.IsTrue(true, 'Session.ApplicationIdentifier must not throw');
    end;
}
