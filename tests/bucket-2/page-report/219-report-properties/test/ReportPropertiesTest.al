codeunit 60481 "RPR Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "RPR Src";

    [Test]
    procedure UseRequestPage_RoundTrips()
    begin
        Assert.IsFalse(Src.UseRequestPage_SetAndGet(),
            'Report.UseRequestPage setter + getter must round-trip false');
    end;

    [Test]
    procedure Language_RoundTrips()
    begin
        Assert.AreEqual(1033, Src.Language_SetAndGet(),
            'Report.Language setter + getter must round-trip 1033');
    end;

    [Test]
    procedure FormatRegion_RoundTrips()
    begin
        Assert.AreEqual('en-US', Src.FormatRegion_SetAndGet(),
            'Report.FormatRegion setter + getter must round-trip');
    end;
}
