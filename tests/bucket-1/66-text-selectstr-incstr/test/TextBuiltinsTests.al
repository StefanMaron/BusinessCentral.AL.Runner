codeunit 58101 "TBI Text Builtins Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "TBI Text Builtins Helper";

    // -----------------------------------------------------------------------
    // SelectStr
    // -----------------------------------------------------------------------

    [Test]
    procedure SelectStr_FirstToken_ReturnsFirst()
    begin
        // [GIVEN] Comma-separated string 'Alpha,Beta,Gamma'
        // [WHEN] SelectStr(1, ...) called
        // [THEN] Returns 'Alpha'
        Assert.AreEqual('Alpha', Helper.SelectStrToken(1, 'Alpha,Beta,Gamma'), 'SelectStr(1) must return Alpha');
    end;

    [Test]
    procedure SelectStr_SecondToken_ReturnsSecond()
    begin
        Assert.AreEqual('Beta', Helper.SelectStrToken(2, 'Alpha,Beta,Gamma'), 'SelectStr(2) must return Beta');
    end;

    [Test]
    procedure SelectStr_LastToken_ReturnsLast()
    begin
        Assert.AreEqual('Gamma', Helper.SelectStrToken(3, 'Alpha,Beta,Gamma'), 'SelectStr(3) must return Gamma');
    end;

    [Test]
    procedure SelectStr_OutOfRange_ThrowsError()
    begin
        // [GIVEN] 2-token string
        // [WHEN] index 3 requested
        // [THEN] BC error: "The SELECTSTR comma-string ... does not contain a value for index N."
        asserterror Helper.SelectStrToken(3, 'A,B');
        Assert.ExpectedError('does not contain a value for index');
    end;

    // -----------------------------------------------------------------------
    // IncStr
    // -----------------------------------------------------------------------

    [Test]
    procedure IncStr_TrailingDigits_Incremented()
    begin
        // [GIVEN] 'DOC001'
        // [WHEN] IncStr called
        // [THEN] Returns 'DOC002'
        Assert.AreEqual('DOC002', Helper.IncStrValue('DOC001'), 'IncStr must increment trailing digits');
    end;

    [Test]
    procedure IncStr_PreservesLeadingZeros()
    begin
        // [GIVEN] 'A099' → 'A100' (width preserved)
        Assert.AreEqual('A100', Helper.IncStrValue('A099'), 'IncStr must handle carry with leading zeros');
    end;

    [Test]
    procedure IncStr_NoDigits_ReturnsUnchanged()
    begin
        // [GIVEN] 'ABC' (no digits)
        // [WHEN] IncStr called
        // [THEN] Returns 'ABC' unchanged
        Assert.AreEqual('ABC', Helper.IncStrValue('ABC'), 'IncStr with no digits must return unchanged string');
    end;

    // -----------------------------------------------------------------------
    // ConvertStr
    // -----------------------------------------------------------------------

    [Test]
    procedure ConvertStr_SingleChar_Replaced()
    begin
        // [GIVEN] 'Hello', from='H', to='J'
        // [WHEN] ConvertStr called
        // [THEN] Returns 'Jello'
        Assert.AreEqual('Jello', Helper.ConvertStrValue('Hello', 'H', 'J'), 'ConvertStr must replace H with J');
    end;

    [Test]
    procedure ConvertStr_MultipleChars_AllReplaced()
    begin
        // [GIVEN] 'abc', from='ac', to='AC'
        // [WHEN] ConvertStr called
        // [THEN] Returns 'AbC'
        Assert.AreEqual('AbC', Helper.ConvertStrValue('abc', 'ac', 'AC'), 'ConvertStr must replace all matching chars');
    end;

    [Test]
    procedure ConvertStr_NoMatch_Unchanged()
    begin
        // [GIVEN] 'Hello', from='xyz', to='XYZ'
        // [WHEN] ConvertStr called
        // [THEN] Returns 'Hello' unchanged
        Assert.AreEqual('Hello', Helper.ConvertStrValue('Hello', 'xyz', 'XYZ'), 'ConvertStr with no match must return unchanged');
    end;

    // -----------------------------------------------------------------------
    // CopyStr — 2-param (position to end)
    // -----------------------------------------------------------------------

    [Test]
    procedure CopyStr_FromPosition_ReturnsRemainder()
    begin
        // [GIVEN] 'ABCDEF', position=3
        // [WHEN] CopyStr(s, 3) called
        // [THEN] Returns 'CDEF'
        Assert.AreEqual('CDEF', Helper.CopyStrFromPos('ABCDEF', 3), 'CopyStr(s,3) must return from position 3 to end');
    end;

    [Test]
    procedure CopyStr_FromPosition1_ReturnsWhole()
    begin
        // [GIVEN] 'Hello', position=1
        // [THEN] Returns 'Hello'
        Assert.AreEqual('Hello', Helper.CopyStrFromPos('Hello', 1), 'CopyStr(s,1) must return full string');
    end;

    // -----------------------------------------------------------------------
    // CopyStr — 3-param (position + length)
    // -----------------------------------------------------------------------

    [Test]
    procedure CopyStr_ThreeParam_ReturnsSubstring()
    begin
        // [GIVEN] 'ABCDEF', position=2, length=3
        // [WHEN] CopyStr(s, 2, 3) called
        // [THEN] Returns 'BCD'
        Assert.AreEqual('BCD', Helper.CopyStrSubstring('ABCDEF', 2, 3), 'CopyStr(s,2,3) must return 3 chars from position 2');
    end;

    [Test]
    procedure CopyStr_ThreeParam_LengthBeyondEnd_ReturnsToEnd()
    begin
        // [GIVEN] 'Hello', position=3, length=100
        // [WHEN] CopyStr called
        // [THEN] Returns 'llo' (truncated at end)
        Assert.AreEqual('llo', Helper.CopyStrSubstring('Hello', 3, 100), 'CopyStr with oversized length must return to end');
    end;
}
