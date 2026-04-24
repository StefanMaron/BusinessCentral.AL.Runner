codeunit 1296002 "Version Create Text Test"
{
    Subtype = Test;

    [Test]
    procedure CreateFromText_FourPart()
    var
        Helper: Codeunit "Version Create Text Helper";
    begin
        // Positive: Version.Create('25.1.30000.12345') parses all four components
        Assert.AreEqual(25, Helper.CreateAndCompareMajor('25.1.30000.12345'), 'Major should be 25');
        Assert.AreEqual(1, Helper.CreateAndCompareMinor('25.1.30000.12345'), 'Minor should be 1');
        Assert.AreEqual(30000, Helper.CreateAndCompareBuild('25.1.30000.12345'), 'Build should be 30000');
        Assert.AreEqual(12345, Helper.CreateAndCompareRevision('25.1.30000.12345'), 'Revision should be 12345');
    end;

    [Test]
    procedure CreateFromText_Comparison()
    var
        Helper: Codeunit "Version Create Text Helper";
    begin
        // Positive: Version created from text supports comparison operators
        Assert.IsTrue(Helper.CompareVersions('1.0.0.0', '2.0.0.0'), '1.0 < 2.0 should be true');
        Assert.IsFalse(Helper.CompareVersions('3.0.0.0', '2.0.0.0'), '3.0 < 2.0 should be false');
    end;

    [Test]
    procedure CreateFromText_PlatformStyleVersion()
    var
        Helper: Codeunit "Version Create Text Helper";
    begin
        // Positive: realistic platform version string (the actual pattern from the issue)
        Assert.AreEqual(25, Helper.CreateAndCompareMajor('25.0.30000.0'), 'Platform major');
        Assert.AreEqual(0, Helper.CreateAndCompareMinor('25.0.30000.0'), 'Platform minor');
        Assert.AreEqual(30000, Helper.CreateAndCompareBuild('25.0.30000.0'), 'Platform build');
        Assert.AreEqual(0, Helper.CreateAndCompareRevision('25.0.30000.0'), 'Platform revision');
    end;

    var
        Assert: Codeunit "Library Assert";
}
