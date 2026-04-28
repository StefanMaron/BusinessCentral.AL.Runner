codeunit 1320409 "File ViewFromStream Bool Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ViewFromStream_ReturnsTrue()
    var
        Helper: Codeunit "File ViewFromStream Bool Src";
    begin
        Assert.AreEqual(true, Helper.ViewFromStreamInIf(),
            'File.ViewFromStream (2-arg) must return true in bool context');
        Assert.AreEqual(true, Helper.ViewFromStreamEditableInIf(),
            'File.ViewFromStream (3-arg) must return true in bool context');
    end;

    [Test]
    procedure ViewFromStream_WrongExpectationFails()
    var
        Helper: Codeunit "File ViewFromStream Bool Src";
    begin
        asserterror Assert.AreEqual(false, Helper.ViewFromStreamInIf(),
            'ViewFromStream should not return false');
        Assert.ExpectedError('AreEqual');
    end;
}
