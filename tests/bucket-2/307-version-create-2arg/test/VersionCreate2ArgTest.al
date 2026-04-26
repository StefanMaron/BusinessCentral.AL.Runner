codeunit 307001 "Version Create 2Arg Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";

    [Test]
    procedure Create2Arg_MajorMinor_PopulatesCorrectly()
    var
        Helper: Codeunit "Version Create 2Arg Helper";
        Ver: Version;
    begin
        // Positive: Version.Create(major, minor) sets major/minor and zeroes build/revision
        Ver := Helper.CreateMajorMinor(5, 3);
        Assert.AreEqual(5, Ver.Major(), 'Major must be 5');
        Assert.AreEqual(3, Ver.Minor(), 'Minor must be 3');
        Assert.AreEqual(0, Ver.Build(), 'Build must default to 0');
        Assert.AreEqual(0, Ver.Revision(), 'Revision must default to 0');
    end;

    [Test]
    procedure Create2Arg_NonDefaultValues_NotZero()
    var
        Helper: Codeunit "Version Create 2Arg Helper";
        Ver: Version;
    begin
        // Positive: non-zero values are stored — proves the mock is not a no-op
        Ver := Helper.CreateMajorMinor(17, 42);
        Assert.AreEqual(17, Ver.Major(), 'Major must be 17, not zero-default');
        Assert.AreEqual(42, Ver.Minor(), 'Minor must be 42, not zero-default');
    end;

    [Test]
    procedure Create3Arg_MajorMinorBuild_PopulatesCorrectly()
    var
        Helper: Codeunit "Version Create 2Arg Helper";
        Ver: Version;
    begin
        // Positive: Version.Create(major, minor, build) sets all three; revision defaults to 0
        Ver := Helper.CreateMajorMinorBuild(2, 1, 500);
        Assert.AreEqual(2, Ver.Major(), 'Major must be 2');
        Assert.AreEqual(1, Ver.Minor(), 'Minor must be 1');
        Assert.AreEqual(500, Ver.Build(), 'Build must be 500');
        Assert.AreEqual(0, Ver.Revision(), 'Revision must default to 0');
    end;

    [Test]
    procedure Create4Arg_AllComponents_PopulatesCorrectly()
    var
        Helper: Codeunit "Version Create 2Arg Helper";
        Ver: Version;
    begin
        // Positive: Version.Create(major, minor, build, revision) sets all four components
        Ver := Helper.CreateMajorMinorBuildRevision(7, 3, 100, 42);
        Assert.AreEqual(7, Ver.Major(), 'Major must be 7');
        Assert.AreEqual(3, Ver.Minor(), 'Minor must be 3');
        Assert.AreEqual(100, Ver.Build(), 'Build must be 100');
        Assert.AreEqual(42, Ver.Revision(), 'Revision must be 42');
    end;
}
