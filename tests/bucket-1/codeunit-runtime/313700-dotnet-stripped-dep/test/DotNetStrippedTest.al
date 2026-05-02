codeunit 1313701 "DotNet Stripped Dep Test"
{
    Subtype = Test;

    /// <summary>
    /// Positive: the non-DotNet procedure in a stripped codeunit returns the expected value.
    /// </summary>
    [Test]
    procedure StrippedDep_NormalProcedure_ReturnsExpectedValue()
    var
        Helper: Codeunit "DotNet Stripped Dep Helper";
        Assert: Codeunit "Library Assert";
        Version: Text;
    begin
        // [GIVEN] A codeunit that had its DotNet procedures stripped by extract-deps
        // [WHEN] Call a normal (non-DotNet) procedure
        Version := Helper.GetVersion();
        // [THEN] The non-DotNet procedure runs correctly and returns the expected value
        Assert.AreEqual('2.0', Version, 'Normal procedure in a DotNet-stripped dep must return the expected value');
    end;

    /// <summary>
    /// Negative: calling a stripped DotNet procedure raises an Error with the runner message.
    /// </summary>
    [Test]
    procedure StrippedDep_DotNetProcedure_RaisesError()
    var
        Helper: Codeunit "DotNet Stripped Dep Helper";
        Assert: Codeunit "Library Assert";
    begin
        // [GIVEN] A codeunit that had its DotNet procedure ParseXml stripped by extract-deps
        // [WHEN] Call the stripped DotNet procedure
        // [THEN] An error is raised with the AL Runner stub message
        asserterror Helper.ParseXml('<?xml version="1.0"?>');
        Assert.ExpectedError('AL Runner:');
    end;
}
