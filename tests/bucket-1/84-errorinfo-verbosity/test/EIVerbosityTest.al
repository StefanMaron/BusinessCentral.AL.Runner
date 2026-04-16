codeunit 83601 "EI Verbosity Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EI Verbosity Src";

    [Test]
    procedure Verbosity_SetAndGet_RoundTrip()
    begin
        Assert.AreEqual(3, Src.SetAndGet(), 'Verbosity::Error AsInteger must be 3');
    end;

    [Test]
    procedure Verbosity_DefaultIsZero()
    begin
        Assert.AreEqual(0, Src.DefaultVerbosity(), 'Default Verbosity AsInteger must be 0');
    end;

    [Test]
    procedure Verbosity_OverwriteReturnsLatest()
    begin
        // Verbosity::Normal = 1
        Assert.AreEqual(1, Src.OverwriteVerbosity(), 'Overwrite Verbosity must return last value set');
    end;

    [Test]
    procedure Verbosity_InlineSetGet()
    var
        EI: ErrorInfo;
    begin
        EI.Verbosity(Verbosity::Warning);
        Assert.AreEqual(2, EI.Verbosity().AsInteger(), 'Verbosity::Warning AsInteger must be 2');
    end;

    [Test]
    procedure Verbosity_DefaultNotError()
    var
        EI: ErrorInfo;
    begin
        Assert.AreNotEqual(3, EI.Verbosity().AsInteger(), 'Default Verbosity must not be Error (3)');
    end;
}
