codeunit 83701 "EI Verbosity Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EI Verbosity Src";

    [Test]
    procedure Verbosity_SetError_GetIsError()
    begin
        Assert.IsTrue(Src.SetError_GetIsError(), 'Verbosity set to Error must get as Error');
    end;

    [Test]
    procedure Verbosity_SetWarning_GetIsWarning()
    begin
        Assert.IsTrue(Src.SetWarning_GetIsWarning(), 'Verbosity set to Warning must get as Warning');
    end;

    [Test]
    procedure Verbosity_OverwriteReturnsLatest()
    begin
        Assert.IsTrue(Src.OverwriteReturnsLatest(), 'Overwrite Warning->Error must return Error');
    end;

    [Test]
    procedure Verbosity_InlineSetGet()
    var
        EI: ErrorInfo;
    begin
        EI.Verbosity(Verbosity::Warning);
        Assert.IsTrue(EI.Verbosity() = Verbosity::Warning, 'Inline set Warning must get as Warning');
    end;

    [Test]
    procedure Verbosity_SetError_IsNotWarning()
    begin
        Assert.IsTrue(Src.SetError_GetIsNotWarning(), 'Verbosity Error must not equal Warning');
    end;
}
