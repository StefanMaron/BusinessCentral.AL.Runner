codeunit 58401 "XmlPort Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Logic: Codeunit "XmlPort Logic";

    // ------------------------------------------------------------------
    // Positive: codeunit with XmlPort variables compiles and business
    // logic that does not call Import/Export runs correctly.
    // ------------------------------------------------------------------

    [Test]
    procedure XmlPortDeclarationCompiles()
    begin
        // [GIVEN] A codeunit that declares an XmlPort variable
        // [WHEN]  We call a procedure that doesn't invoke Import/Export
        // [THEN]  It returns the expected value — proves compilation succeeded
        Assert.AreEqual('ready', Logic.GetStatus(), 'GetStatus should return ready');
    end;

    [Test]
    procedure XmlPortIdConstantCorrect()
    begin
        // [GIVEN] A procedure that returns the XmlPort object ID
        // [WHEN]  We call it
        // [THEN]  The ID matches the XmlPort definition
        Assert.AreEqual(58400, Logic.GetXmlPortId(), 'XmlPort ID should be 58400');
    end;

    // ------------------------------------------------------------------
    // Negative: calling Import/Export must throw a NotSupportedException
    // with 'XmlPort' in the message so the developer gets a clear hint.
    // ------------------------------------------------------------------

    [Test]
    procedure InstanceImportThrowsNotSupported()
    var
        InStr: InStream;
    begin
        // [GIVEN] An uninitialized InStream (import throws before reading it)
        // [WHEN]  We call the instance form XP.Import()
        // [THEN]  A clear 'XmlPort' error is raised
        asserterror Logic.TryInstanceImport(InStr);
        Assert.ExpectedError('XmlPort');
    end;

    [Test]
    procedure InstanceExportThrowsNotSupported()
    var
        OutStr: OutStream;
    begin
        // [GIVEN] An uninitialized OutStream (export throws before writing to it)
        // [WHEN]  We call the instance form XP.Export()
        // [THEN]  A clear 'XmlPort' error is raised
        asserterror Logic.TryInstanceExport(OutStr);
        Assert.ExpectedError('XmlPort');
    end;

    [Test]
    procedure StaticImportThrowsNotSupported()
    var
        InStr: InStream;
    begin
        // [GIVEN] An uninitialized InStream
        // [WHEN]  We call the static form XmlPort.Import(portId, InStr)
        // [THEN]  A clear 'XmlPort' error is raised
        asserterror Logic.TryStaticImport(InStr);
        Assert.ExpectedError('XmlPort');
    end;

    [Test]
    procedure StaticExportThrowsNotSupported()
    var
        OutStr: OutStream;
    begin
        // [GIVEN] An uninitialized OutStream
        // [WHEN]  We call the static form XmlPort.Export(portId, OutStr)
        // [THEN]  A clear 'XmlPort' error is raised
        asserterror Logic.TryStaticExport(OutStr);
        Assert.ExpectedError('XmlPort');
    end;
}
