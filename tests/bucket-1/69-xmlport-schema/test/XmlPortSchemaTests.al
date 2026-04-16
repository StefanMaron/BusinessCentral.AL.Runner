codeunit 58701 "XPS XmlPort Schema Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "XPS Schema Helper";

    // -----------------------------------------------------------------------
    // Positive: XmlPort with schema section compiles and code around it runs
    // -----------------------------------------------------------------------

    [Test]
    procedure SchemaSection_XmlPortCompiles_StatusReturned()
    begin
        // [GIVEN] A codeunit that declares an XmlPort variable (with schema section)
        // [WHEN]  A procedure that does not call Import/Export is invoked
        // [THEN]  The codeunit runs normally — schema section does not block compilation
        Assert.AreEqual('ok', Helper.GetStatus(), 'XmlPort with schema section must compile and run');
    end;

    [Test]
    procedure SchemaSection_XmlPortId_CorrectlyRegistered()
    begin
        // [GIVEN] An XmlPort object defined with a schema section
        // [WHEN]  The declared ID is returned via a helper
        // [THEN]  The ID matches the XmlPort definition (58700)
        Assert.AreEqual(58700, Helper.GetExportPortId(), 'XmlPort schema section: object ID must be 58700');
    end;

    [Test]
    procedure SchemaSection_SecondXmlPort_CompilesIndependently()
    begin
        // [GIVEN] Two XmlPort objects each with their own schema section
        // [WHEN]  The second port's ID is returned
        // [THEN]  Both ports compiled successfully — ID 58701 is correct
        Assert.AreEqual(58701, Helper.GetHeaderPortId(), 'Second XmlPort with schema section must also compile');
    end;

    [Test]
    procedure SchemaSection_TableElement_FieldElementNodes_Compile()
    begin
        // [GIVEN] An XmlPort with textelement > tableelement > fieldelement nesting
        // [WHEN]  We call a procedure that declares that XmlPort as a variable
        // [THEN]  The schema node hierarchy does not block compilation
        Assert.AreEqual('ok', Helper.GetStatus(), 'textelement/tableelement/fieldelement nesting must compile');
    end;

    // -----------------------------------------------------------------------
    // Export() on an XmlPort with schema section is a no-op in al-runner
    // (requires the BC service tier for real data; no error is expected)
    // -----------------------------------------------------------------------

    [Test]
    procedure SchemaSection_Export_IsNoOp()
    var
        OutStr: OutStream;
    begin
        // [GIVEN] An XmlPort with a schema section
        // [WHEN]  SetDestination + Export() are called
        // [THEN]  No error is raised — Export is a no-op in standalone mode
        Helper.TryExport(OutStr);
    end;
}
