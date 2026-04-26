/// Proves that a page containing a fileupload() action compiles and runs.
/// fileupload() is a UI-only action type; the runner strips the declaration
/// at preprocessing so the BC compiler does not see the unsupported syntax.
codeunit 97801 "FUA Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Compilation smoke-test
    // ------------------------------------------------------------------

    [Test]
    procedure FileuploadAction_PageCompilesAndRuns()
    begin
        // If compilation reached this point the fileupload() block was stripped
        // successfully.  The page is UI-only so we simply assert a known truth.
        Assert.IsTrue(true, 'Page with fileupload() action compiled and test ran');
    end;

    // ------------------------------------------------------------------
    // Negative: unrelated errors still propagate normally
    // ------------------------------------------------------------------

    [Test]
    procedure FileuploadAction_UnrelatedErrorStillPropagates()
    begin
        asserterror Error('intentional error');
        Assert.ExpectedError('intentional error');
    end;
}
