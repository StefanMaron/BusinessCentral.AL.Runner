codeunit 56281 "Missing CU Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;
        Probe: Codeunit "Missing CU Probe";

    [Test]
    procedure MissingCodeunitErrorMentionsId()
    begin
        // Positive: error message must contain the codeunit ID
        asserterror Probe.CallMissingUserCodeunit();
        Assert.ExpectedMessage('59999', GetLastErrorText());
    end;

    [Test]
    procedure MissingCodeunitErrorMentionsStubsHint()
    begin
        // Positive: error message must mention --stubs as a resolution
        asserterror Probe.CallMissingUserCodeunit();
        Assert.ExpectedMessage('--stubs', GetLastErrorText());
    end;

    [Test]
    procedure MissingCodeunitErrorMentionsGenerateStubs()
    begin
        // Positive: error message must mention --generate-stubs
        asserterror Probe.CallMissingUserCodeunit();
        Assert.ExpectedMessage('--generate-stubs', GetLastErrorText());
    end;

    [Test]
    procedure MissingTestToolkitCodeunitIdentifiesRange()
    begin
        // Positive: error for test-toolkit codeunit mentions "test toolkit"
        asserterror Probe.CallMissingTestToolkitCodeunit();
        Assert.ExpectedMessage('test toolkit', GetLastErrorText());
    end;

    [Test]
    procedure MissingSystemCodeunitIdentifiesRange()
    begin
        // Positive: error for system codeunit mentions "system"
        asserterror Probe.CallMissingSystemCodeunit();
        Assert.ExpectedMessage('system', GetLastErrorText());
    end;

    [Test]
    procedure MissingCodeunitListsAvailable()
    begin
        // Positive: error message lists available codeunits (our own IDs should appear)
        asserterror Probe.CallMissingUserCodeunit();
        Assert.ExpectedMessage('56280', GetLastErrorText());
    end;
}
