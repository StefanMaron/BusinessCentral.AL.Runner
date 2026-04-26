/// Tests for Evaluate(var Version; Text) — verifies that the AL Evaluate() built-in
/// correctly parses dotted version strings into a Version variable.
/// This exercises the BC code path that emits ALSystemVariable.ALEvaluate<NavVersion>
/// which requires MockVersion to be a reference type (CS0452 gap).
codeunit 1297002 "Version Evaluate Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";
        Helper: Codeunit "Version Evaluate Helper";

    // ------------------------------------------------------------------
    // Positive: Evaluate() parses four-part version strings
    // ------------------------------------------------------------------

    [Test]
    procedure Evaluate_FourPart_ReturnsTrueAndSetsComponents()
    var
        Ver: Version;
        Ok: Boolean;
    begin
        // [GIVEN] A valid four-part version text
        // [WHEN] Evaluate is called
        Ok := Helper.TryParseVersion('25.1.30000.12345', Ver);
        // [THEN] Returns true
        Assert.IsTrue(Ok, 'Evaluate must return true for a valid version string');
        // [THEN] Each component is set correctly
        Assert.AreEqual(25,    Helper.GetMajor(Ver),    'Major must be 25');
        Assert.AreEqual(1,     Helper.GetMinor(Ver),    'Minor must be 1');
        Assert.AreEqual(30000, Helper.GetBuild(Ver),    'Build must be 30000');
        Assert.AreEqual(12345, Helper.GetRevision(Ver), 'Revision must be 12345');
    end;

    [Test]
    procedure Evaluate_PlatformVersion_ParsesCorrectly()
    var
        Ver: Version;
        Ok: Boolean;
    begin
        // [GIVEN] A realistic platform version string (pattern from telemetry)
        Ok := Helper.TryParseVersion('25.0.30000.0', Ver);
        Assert.IsTrue(Ok, 'Evaluate must succeed for platform version');
        Assert.AreEqual(25,    Helper.GetMajor(Ver),    'Major must be 25');
        Assert.AreEqual(0,     Helper.GetMinor(Ver),    'Minor must be 0');
        Assert.AreEqual(30000, Helper.GetBuild(Ver),    'Build must be 30000');
        Assert.AreEqual(0,     Helper.GetRevision(Ver), 'Revision must be 0');
    end;

    [Test]
    procedure Evaluate_TwoPart_ParsesMajorAndMinor()
    var
        Ver: Version;
        Ok: Boolean;
    begin
        // [GIVEN] A two-part version string
        Ok := Helper.TryParseVersion('3.14', Ver);
        Assert.IsTrue(Ok, 'Evaluate must succeed for two-part version');
        Assert.AreEqual(3,  Helper.GetMajor(Ver), 'Major must be 3');
        Assert.AreEqual(14, Helper.GetMinor(Ver), 'Minor must be 14');
        Assert.AreEqual(0,  Helper.GetBuild(Ver), 'Build defaults to 0');
    end;

    // ------------------------------------------------------------------
    // Negative: Evaluate() returns false for invalid input
    // ------------------------------------------------------------------

    [Test]
    procedure Evaluate_InvalidText_ReturnsFalse()
    var
        Ver: Version;
        Ok: Boolean;
    begin
        // [GIVEN] An obviously non-version string
        // [WHEN] Evaluate is called
        Ok := Helper.TryParseVersion('not-a-version', Ver);
        // [THEN] Returns false (parse failure)
        Assert.IsFalse(Ok, 'Evaluate must return false for a non-version string');
    end;

    [Test]
    procedure Evaluate_EmptyText_ReturnsFalse()
    var
        Ver: Version;
        Ok: Boolean;
    begin
        Ok := Helper.TryParseVersion('', Ver);
        Assert.IsFalse(Ok, 'Evaluate must return false for an empty string');
    end;
}
