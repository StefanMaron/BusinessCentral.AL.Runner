codeunit 64001 "XPA Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "XPA Helper";

    // ------------------------------------------------------------------
    // Positive: XmlPort with textattribute and fieldattribute schema nodes
    // compiles, and codeunits in the same compilation unit execute correctly.
    // ------------------------------------------------------------------

    [Test]
    procedure XmlPortWithAttributes_CompilesAndHelperRuns()
    begin
        // [GIVEN] A codeunit in the same compilation unit as an XmlPort
        //         that uses textattribute and fieldattribute schema elements
        // [WHEN]  We call a helper procedure that doesn't invoke Import/Export
        // [THEN]  It returns the expected value — proves compilation succeeded
        Assert.AreEqual('ok', Helper.GetStatus(), 'Helper must compile and return ok');
    end;

    [Test]
    procedure XmlPortWithAttributes_PortIdCorrect()
    begin
        // [GIVEN] A helper that declares an XmlPort variable (attribute schema)
        // [WHEN]  We call GetPortId
        // [THEN]  It returns the XmlPort object ID — proves the variable declaration compiled
        Assert.AreEqual(64000, Helper.GetPortId(), 'XmlPort ID must be 64000');
    end;
}
