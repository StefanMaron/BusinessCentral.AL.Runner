codeunit 60201 "XmlPort Run Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Logic: Codeunit "XmlPort Run Logic";

    // ------------------------------------------------------------------
    // Positive: codeunit that references XmlPort.Run compiles correctly.
    // ------------------------------------------------------------------

    [Test]
    procedure XmlPortRunCompiles()
    begin
        // [GIVEN] A codeunit with XmlPort.Run calls
        // [WHEN]  We call a procedure that doesn't invoke XmlPort.Run
        // [THEN]  It returns the expected value — proves compilation succeeded
        Assert.AreEqual('ready', Logic.GetStatus(), 'GetStatus should return ready');
    end;

    // ------------------------------------------------------------------
    // Negative: all XmlPort.Run overloads must throw a 'XmlPort' error.
    // ------------------------------------------------------------------

    [Test]
    procedure StaticRunThrowsNotSupported()
    begin
        // [GIVEN] A static XmlPort.Run(portId) call
        // [WHEN]  We invoke it
        // [THEN]  A clear 'XmlPort' error is raised (service tier required)
        asserterror Logic.TryStaticRun();
        Assert.ExpectedError('XmlPort');
    end;

    [Test]
    procedure StaticRunWithDirectionThrowsNotSupported()
    begin
        // [GIVEN] A static XmlPort.Run(portId, isImport) call with direction=true
        // [WHEN]  We invoke it
        // [THEN]  A clear 'XmlPort' error is raised
        asserterror Logic.TryStaticRunWithDirection(true);
        Assert.ExpectedError('XmlPort');
    end;

    [Test]
    procedure StaticRunWithRecordThrowsNotSupported()
    var
        Item: Record "XmlPort Run Item";
    begin
        // [GIVEN] A static XmlPort.Run(portId, isImport, rec) call with a record
        // [WHEN]  We invoke it
        // [THEN]  A clear 'XmlPort' error is raised
        asserterror Logic.TryStaticRunWithRecord(true, Item);
        Assert.ExpectedError('XmlPort');
    end;
}
