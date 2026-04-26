codeunit 56281 "Missing CU Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;
        Probe: Codeunit "Missing CU Probe";

    [Test]
    procedure ExistingCodeunit_RunSucceeds()
    begin
        // [WHEN]  A codeunit that does exist is called via Codeunit.Run
        // [THEN]  No error is raised
        Probe.CallExistingCodeunit();
    end;

    [Test]
    procedure MissingCodeunitErrorMentionsId()
    begin
        // [WHEN]  A codeunit that does not exist is called
        asserterror Probe.CallMissingUserCodeunit();
        // [THEN]  Error message contains the missing codeunit ID
        Assert.ExpectedMessage('59999', GetLastErrorText());
    end;

    [Test]
    procedure MissingCodeunitErrorMentionsStubsHint()
    begin
        // [WHEN]  A codeunit that does not exist is called
        asserterror Probe.CallMissingUserCodeunit();
        // [THEN]  Error message mentions --stubs as a resolution
        Assert.ExpectedMessage('--stubs', GetLastErrorText());
    end;

    [Test]
    procedure MissingCodeunitErrorMentionsGenerateStubs()
    begin
        // [WHEN]  A codeunit that does not exist is called
        asserterror Probe.CallMissingUserCodeunit();
        // [THEN]  Error message mentions --generate-stubs
        Assert.ExpectedMessage('--generate-stubs', GetLastErrorText());
    end;

    [Test]
    procedure MissingTestToolkitCodeunit_IsNoOp()
    begin
        // [WHEN]  A test-toolkit-range codeunit (130xxx-139999) not in the assembly is called
        // [THEN]  No error is raised — test toolkit codeunits are treated as no-ops
        Probe.CallMissingTestToolkitCodeunit();
    end;

    [Test]
    procedure LibraryERM_IsNoOp()
    begin
        // [WHEN]  Library - ERM (132217) not in the assembly is called via Codeunit.Run
        // [THEN]  No error is raised — test toolkit codeunits are treated as no-ops
        Probe.CallLibraryERM();
    end;

    [Test]
    procedure MissingSystemCodeunit_IsNoOp()
    begin
        // [WHEN]  A system-range codeunit (1-9999) that does not exist is called via Codeunit.Run
        // [THEN]  No error is raised — system codeunits are treated as no-ops
        Probe.CallMissingSystemCodeunit();
    end;

    [Test]
    procedure MissingCodeunitMentionsAvailableCodeunits()
    begin
        // [WHEN]  A codeunit that does not exist is called
        asserterror Probe.CallMissingUserCodeunit();
        // [THEN]  Error message reports what codeunits are available in the assembly
        Assert.ExpectedMessage('available', GetLastErrorText());
    end;
}
