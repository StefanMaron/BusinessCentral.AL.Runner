codeunit 56254 "XmlPort Query Diag Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Logic: Codeunit "Diag Logic";

    // ==================================================================
    // XmlPort lifecycle — must NOT throw
    // ==================================================================

    [Test]
    procedure XmlPortDeclarationCompiles()
    begin
        // [GIVEN] A codeunit that declares an XmlPort variable
        // [WHEN]  We call a procedure that doesn't invoke Import/Export
        // [THEN]  It returns the expected value
        Assert.AreEqual('xmlport-ok', Logic.XmlPortDeclareAndReturn(),
            'XmlPort declaration and non-I/O logic should work');
    end;

    [Test]
    procedure XmlPortInvokeDoesNotCrash()
    begin
        // [GIVEN] A codeunit with an XmlPort variable
        // [WHEN]  Invoke() is called (custom trigger dispatch)
        // [THEN]  No error — Invoke returns null silently
        Logic.XmlPortInvoke();
    end;

    // ==================================================================
    // XmlPort error messages — must mention "interface injection"
    // ==================================================================

    [Test]
    procedure XmlPortImportErrorMentionsInterfaceInjection()
    var
        InStr: InStream;
    begin
        // [GIVEN] An XmlPort variable
        // [WHEN]  Import() is called
        // [THEN]  Error message contains "interface injection"
        asserterror Logic.TryXmlPortImport(InStr);
        Assert.ExpectedMessage('interface injection', GetLastErrorText);
    end;

    [Test]
    procedure XmlPortExportErrorMentionsInterfaceInjection()
    var
        OutStr: OutStream;
    begin
        // [GIVEN] An XmlPort variable
        // [WHEN]  Export() is called
        // [THEN]  Error message contains "interface injection"
        asserterror Logic.TryXmlPortExport(OutStr);
        Assert.ExpectedMessage('interface injection', GetLastErrorText);
    end;

    [Test]
    procedure StaticXmlPortImportErrorMentionsInterfaceInjection()
    var
        InStr: InStream;
    begin
        // [GIVEN] A static XmlPort.Import call
        // [WHEN]  XmlPort.Import(portId, InStr) is called
        // [THEN]  Error message contains "interface injection"
        asserterror Logic.TryStaticXmlPortImport(InStr);
        Assert.ExpectedMessage('interface injection', GetLastErrorText);
    end;

    [Test]
    procedure StaticXmlPortExportErrorMentionsInterfaceInjection()
    var
        OutStr: OutStream;
    begin
        // [GIVEN] A static XmlPort.Export call
        // [WHEN]  XmlPort.Export(portId, OutStr) is called
        // [THEN]  Error message contains "interface injection"
        asserterror Logic.TryStaticXmlPortExport(OutStr);
        Assert.ExpectedMessage('interface injection', GetLastErrorText);
    end;

    [Test]
    procedure XmlPortImportErrorMentionsServiceTier()
    var
        InStr: InStream;
    begin
        // [GIVEN] An XmlPort variable
        // [WHEN]  Import() is called
        // [THEN]  Error message mentions "BC service tier"
        asserterror Logic.TryXmlPortImport(InStr);
        Assert.ExpectedMessage('BC service tier', GetLastErrorText);
    end;

    // ==================================================================
    // Query lifecycle — must NOT throw
    // ==================================================================

    [Test]
    procedure QueryDeclarationCompiles()
    begin
        // [GIVEN] A codeunit that declares a Query variable
        // [WHEN]  We call a procedure that doesn't invoke Open/Read
        // [THEN]  It returns the expected value
        Assert.AreEqual('query-ok', Logic.QueryDeclareAndReturn(),
            'Query declaration and non-data logic should work');
    end;

    [Test]
    procedure QuerySetRangeAndCloseDoNotThrow()
    begin
        // [GIVEN] A Query variable
        // [WHEN]  SetRange + Close are called
        // [THEN]  No error — both are no-ops
        Logic.QuerySetRangeAndClose();
    end;

    [Test]
    procedure QuerySetFilterAndCloseDoNotThrow()
    begin
        // [GIVEN] A Query variable
        // [WHEN]  SetFilter + TopNumberOfRows + Close are called
        // [THEN]  No error — all are no-ops
        Logic.QuerySetFilterAndClose();
    end;

    // ==================================================================
    // Query error messages — must mention "interface"
    // ==================================================================

    [Test]
    procedure QueryOpenErrorMentionsInterface()
    begin
        // [GIVEN] A Query variable
        // [WHEN]  Open() is called
        // [THEN]  Error message contains "interface"
        asserterror Logic.TryQueryOpen();
        Assert.ExpectedMessage('interface', GetLastErrorText);
    end;

    [Test]
    procedure QueryReadErrorMentionsInterface()
    begin
        // [GIVEN] A Query variable
        // [WHEN]  Read() is called
        // [THEN]  Error message contains "interface"
        asserterror Logic.TryQueryRead();
        Assert.ExpectedMessage('interface', GetLastErrorText);
    end;

    [Test]
    procedure QueryOpenErrorMentionsServiceTier()
    begin
        // [GIVEN] A Query variable
        // [WHEN]  Open() is called
        // [THEN]  Error message mentions "BC service tier"
        asserterror Logic.TryQueryOpen();
        Assert.ExpectedMessage('BC service tier', GetLastErrorText);
    end;

    [Test]
    procedure QueryOpenErrorMentionsRecord()
    begin
        // [GIVEN] A Query variable
        // [WHEN]  Open() is called
        // [THEN]  Error message suggests Record operations as alternative
        asserterror Logic.TryQueryOpen();
        Assert.ExpectedMessage('Record', GetLastErrorText);
    end;
}
