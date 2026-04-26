codeunit 61301 "DFI Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: DataFileInformation completes without error.
    // The stub is a no-op — var parameters keep their default values.
    // ------------------------------------------------------------------

    [Test]
    procedure DataFileInformation_ShowDialogFalse_NoError()
    var
        H: Codeunit "DFI Helper";
    begin
        H.CallDataFileInformation(false);
        Assert.IsTrue(true, 'DataFileInformation(false) must complete without error');
    end;

    [Test]
    procedure DataFileInformation_ShowDialogTrue_NoError()
    var
        H: Codeunit "DFI Helper";
    begin
        H.CallDataFileInformation(true);
        Assert.IsTrue(true, 'DataFileInformation(true) must complete without error');
    end;

    [Test]
    procedure DataFileInformation_ReturnFlagAfterCall_IsTrue()
    var
        H: Codeunit "DFI Helper";
    begin
        Assert.IsTrue(H.CallAndReturnFlag(false), 'Execution must continue after DataFileInformation no-op');
    end;

    // ------------------------------------------------------------------
    // Negative: error handling still works after DataFileInformation.
    // ------------------------------------------------------------------

    [Test]
    procedure DataFileInformation_ErrorAfterCall_CaughtCorrectly()
    var
        H: Codeunit "DFI Helper";
    begin
        H.CallDataFileInformation(false);
        asserterror Error('post-call error');
        Assert.ExpectedError('post-call error');
    end;
}
