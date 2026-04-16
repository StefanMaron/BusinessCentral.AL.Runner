/// Tests for the Version built-in type: Create, Major, Minor, Build, Revision, ToText.
codeunit 99101 "VER Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "VER Source";

    // ------------------------------------------------------------------
    // Version.Create — stores components
    // ------------------------------------------------------------------

    [Test]
    procedure Create_StoresComponents()
    var
        Ver: Version;
    begin
        // [GIVEN] Create is called with specific components
        Ver := Src.CreateVersion(2, 3, 4, 5);
        // [THEN] Major returns 2
        Assert.AreEqual(2, Src.GetMajor(Ver), 'Major must be 2');
        // [THEN] Minor returns 3
        Assert.AreEqual(3, Src.GetMinor(Ver), 'Minor must be 3');
        // [THEN] Build returns 4
        Assert.AreEqual(4, Src.GetBuild(Ver), 'Build must be 4');
        // [THEN] Revision returns 5
        Assert.AreEqual(5, Src.GetRevision(Ver), 'Revision must be 5');
    end;

    // ------------------------------------------------------------------
    // Major
    // ------------------------------------------------------------------

    [Test]
    procedure Major_ReturnsStoredValue()
    var
        Ver: Version;
    begin
        Ver := Src.CreateVersion(10, 0, 0, 0);
        Assert.AreEqual(10, Src.GetMajor(Ver), 'Major must return 10');
    end;

    [Test]
    procedure Major_Zero()
    var
        Ver: Version;
    begin
        Ver := Src.CreateVersion(0, 0, 0, 0);
        Assert.AreEqual(0, Src.GetMajor(Ver), 'Major must return 0');
    end;

    // ------------------------------------------------------------------
    // Minor
    // ------------------------------------------------------------------

    [Test]
    procedure Minor_ReturnsStoredValue()
    var
        Ver: Version;
    begin
        Ver := Src.CreateVersion(1, 7, 0, 0);
        Assert.AreEqual(7, Src.GetMinor(Ver), 'Minor must return 7');
    end;

    // ------------------------------------------------------------------
    // Build
    // ------------------------------------------------------------------

    [Test]
    procedure Build_ReturnsStoredValue()
    var
        Ver: Version;
    begin
        Ver := Src.CreateVersion(1, 0, 42, 0);
        Assert.AreEqual(42, Src.GetBuild(Ver), 'Build must return 42');
    end;

    // ------------------------------------------------------------------
    // Revision
    // ------------------------------------------------------------------

    [Test]
    procedure Revision_ReturnsStoredValue()
    var
        Ver: Version;
    begin
        Ver := Src.CreateVersion(1, 0, 0, 99);
        Assert.AreEqual(99, Src.GetRevision(Ver), 'Revision must return 99');
    end;

    // ------------------------------------------------------------------
    // ToText
    // ------------------------------------------------------------------

    [Test]
    procedure ToText_ReturnsDottedFormat()
    var
        Ver: Version;
        T: Text;
    begin
        // [GIVEN] Version 2.1.0.0
        Ver := Src.CreateVersion(2, 1, 0, 0);
        // [WHEN] ToText is called
        T := Src.GetToText(Ver);
        // [THEN] Returns "2.1.0.0"
        Assert.AreEqual('2.1.0.0', T, 'ToText must return "2.1.0.0"');
    end;

    [Test]
    procedure ToText_AllComponents()
    var
        Ver: Version;
        T: Text;
    begin
        Ver := Src.CreateVersion(3, 14, 159, 26);
        T := Src.GetToText(Ver);
        Assert.AreEqual('3.14.159.26', T, 'ToText must include all four components');
    end;
}
