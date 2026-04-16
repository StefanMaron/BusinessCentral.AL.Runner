codeunit 97001 "EI PageNo Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EI PageNo Src";

    [Test]
    procedure PageNo_SetAndGet_RoundTrip()
    begin
        Assert.AreEqual(21, Src.SetAndGet(), 'PageNo round-trip must return 21');
    end;

    [Test]
    procedure PageNo_DefaultIsZero()
    begin
        Assert.AreEqual(0, Src.DefaultPageNo(), 'Default PageNo must be 0');
    end;

    [Test]
    procedure PageNo_OverwriteReturnsLatest()
    begin
        Assert.AreEqual(99, Src.OverwritePageNo(), 'Overwrite PageNo must return last value set');
    end;

    [Test]
    procedure PageNo_InlineSetGet()
    var
        EI: ErrorInfo;
    begin
        EI.PageNo(42);
        Assert.AreEqual(42, EI.PageNo(), 'Inline PageNo round-trip must return 42');
    end;

    [Test]
    procedure PageNo_DefaultNotNonZero()
    var
        EI: ErrorInfo;
    begin
        Assert.AreNotEqual(1, EI.PageNo(), 'Default PageNo must not be 1');
    end;
}
