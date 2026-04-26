// Tests for Library - Utility (codeunit 131003) stub.
// Exercises GenerateGUID, GenerateRandomCode, GenerateRandomCode20, and GenerateRandomText.
codeunit 301001 "Library - Utility Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        LibUtil: Codeunit "Library - Utility";

    [Test]
    procedure GenerateGUID_Returns36CharString()
    var
        G: Text;
    begin
        // [WHEN] GenerateGUID is called
        G := LibUtil.GenerateGUID();
        // [THEN] Result is non-empty and exactly 36 characters (8-4-4-4-12 GUID format)
        Assert.AreNotEqual('', G, 'GenerateGUID must not return empty string');
        Assert.AreEqual(36, StrLen(G), 'GenerateGUID must return a 36-character GUID string');
    end;

    [Test]
    procedure GenerateGUID_ReturnsDifferentValuesEachCall()
    var
        G1: Text;
        G2: Text;
    begin
        // [WHEN] GenerateGUID is called twice
        G1 := LibUtil.GenerateGUID();
        G2 := LibUtil.GenerateGUID();
        // [THEN] The two values differ (proves each call generates a unique GUID)
        Assert.AreNotEqual(G1, G2, 'GenerateGUID should return a unique value each call');
    end;

    [Test]
    procedure GenerateRandomCode_ReturnsNonEmptyAtMost10Chars()
    var
        C: Code[10];
    begin
        // [WHEN] GenerateRandomCode is called (field 1, table 18)
        C := LibUtil.GenerateRandomCode(1, 18);
        // [THEN] Result is non-empty and fits within 10 chars
        Assert.AreNotEqual('', C, 'GenerateRandomCode must not return empty string');
        Assert.IsTrue(StrLen(C) <= 10, 'GenerateRandomCode result must not exceed 10 characters');
        Assert.IsTrue(StrLen(C) > 0, 'GenerateRandomCode result must be non-empty');
    end;

    [Test]
    procedure GenerateRandomCode20_ReturnsNonEmptyAtMost20Chars()
    var
        C: Code[20];
    begin
        // [WHEN] GenerateRandomCode20 is called (field 1, table 18)
        C := LibUtil.GenerateRandomCode20(1, 18);
        // [THEN] Result is non-empty and fits within 20 chars
        Assert.AreNotEqual('', C, 'GenerateRandomCode20 must not return empty string');
        Assert.IsTrue(StrLen(C) <= 20, 'GenerateRandomCode20 result must not exceed 20 characters');
        Assert.IsTrue(StrLen(C) > 0, 'GenerateRandomCode20 result must be non-empty');
    end;

    [Test]
    procedure GenerateRandomText_ReturnsNonEmptyResult()
    var
        T: Text;
    begin
        // [WHEN] GenerateRandomText is called with max length 50
        T := LibUtil.GenerateRandomText(50);
        // [THEN] Result is non-empty
        Assert.AreNotEqual('', T, 'GenerateRandomText must not return empty string');
        Assert.IsTrue(StrLen(T) > 0, 'GenerateRandomText must return text with at least one character');
    end;

    [Test]
    procedure GenerateRandomText_LengthWithinBounds()
    var
        T: Text;
    begin
        // [WHEN] GenerateRandomText is called with max length 20
        T := LibUtil.GenerateRandomText(20);
        // [THEN] Result length is within the max
        Assert.IsTrue(StrLen(T) <= 20, 'GenerateRandomText result must not exceed MaxLength');
    end;
}
