codeunit 60071 "TXS Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "TXS Src";

    // --- Contains ---

    [Test]
    procedure Contains_Positive_FindsSubstring()
    begin
        Assert.IsTrue(Src.Contains_Positive('Hello World', 'World'),
            'Contains must find a substring');
    end;

    [Test]
    procedure Contains_Negative_NotFound()
    begin
        Assert.IsFalse(Src.Contains_Positive('Hello World', 'xyz'),
            'Contains must return false when the substring is absent');
    end;

    [Test]
    procedure Contains_CaseSensitive()
    begin
        // AL Text.Contains is case-sensitive.
        Assert.IsFalse(Src.Contains_Positive('Hello World', 'world'),
            'Contains is case-sensitive — lowercase "world" must not match');
    end;

    [Test]
    procedure Contains_EmptyNeedle_IsTrue()
    begin
        // .NET semantics: an empty needle is always contained.
        Assert.IsTrue(Src.Contains_Positive('Hello', ''),
            'Contains with empty needle returns true');
    end;

    // --- StartsWith ---

    [Test]
    procedure StartsWith_Positive()
    begin
        Assert.IsTrue(Src.StartsWith_It('Hello World', 'Hello'),
            'StartsWith must recognise the prefix');
    end;

    [Test]
    procedure StartsWith_Negative()
    begin
        Assert.IsFalse(Src.StartsWith_It('Hello World', 'World'),
            'StartsWith must reject non-prefix');
    end;

    [Test]
    procedure StartsWith_CaseSensitive()
    begin
        Assert.IsFalse(Src.StartsWith_It('Hello World', 'hello'),
            'StartsWith is case-sensitive');
    end;

    // --- EndsWith ---

    [Test]
    procedure EndsWith_Positive()
    begin
        Assert.IsTrue(Src.EndsWith_It('Hello World', 'World'),
            'EndsWith must recognise the suffix');
    end;

    [Test]
    procedure EndsWith_Negative()
    begin
        Assert.IsFalse(Src.EndsWith_It('Hello World', 'Hello'),
            'EndsWith must reject non-suffix');
    end;

    // --- IndexOf ---

    [Test]
    procedure IndexOf_Found_Is1Based()
    begin
        // AL returns 1-based index; 'World' in 'Hello World' starts at position 7.
        Assert.AreEqual(7, Src.IndexOfIt('Hello World', 'World'),
            'IndexOf must return 1-based position of the first match');
    end;

    [Test]
    procedure IndexOf_NotFound_Returns0()
    begin
        Assert.AreEqual(0, Src.IndexOfIt('Hello World', 'xyz'),
            'IndexOf must return 0 when the needle is absent (AL convention)');
    end;

    [Test]
    procedure IndexOf_FirstOccurrence_NotLast()
    begin
        // 'o' appears at positions 5 and 8 in 'Hello World'; IndexOf returns 5.
        Assert.AreEqual(5, Src.IndexOfIt('Hello World', 'o'),
            'IndexOf returns the first occurrence, not the last');
    end;

    // --- LastIndexOf ---

    [Test]
    procedure LastIndexOf_Found_Is1Based()
    begin
        // 'o' appears at positions 5 and 8; LastIndexOf returns 8.
        Assert.AreEqual(8, Src.LastIndexOfIt('Hello World', 'o'),
            'LastIndexOf must return the 1-based position of the last occurrence');
    end;

    [Test]
    procedure LastIndexOf_NotFound_Returns0()
    begin
        Assert.AreEqual(0, Src.LastIndexOfIt('Hello World', 'xyz'),
            'LastIndexOf must return 0 when the needle is absent');
    end;

    [Test]
    procedure LastIndexOf_DiffersFromIndexOf()
    begin
        // Negative trap: if LastIndexOf were implemented as IndexOf the two
        // would return the same thing for strings with multiple matches.
        Assert.AreNotEqual(Src.IndexOfIt('Hello World', 'o'),
            Src.LastIndexOfIt('Hello World', 'o'),
            'LastIndexOf must not be the same as IndexOf for multi-match strings');
    end;
}
