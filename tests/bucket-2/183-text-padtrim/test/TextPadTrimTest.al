codeunit 60081 "TPT Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "TPT Src";

    // --- PadLeft ---

    [Test]
    procedure PadLeft_AddsPadChar()
    begin
        Assert.AreEqual('0000042', Src.PadLeftIt('42', 7, '0'),
            'PadLeft must pad the left side with the given char to the total length');
    end;

    [Test]
    procedure PadLeft_Default_PadsWithSpace()
    begin
        Assert.AreEqual('     42', Src.PadLeft_SpaceDefault('42', 7),
            'PadLeft with no char argument must pad with spaces');
    end;

    [Test]
    procedure PadLeft_AlreadyLongerThanLength_Unchanged()
    begin
        // .NET semantics: if the source is already longer, PadLeft returns it unchanged.
        Assert.AreEqual('hello', Src.PadLeftIt('hello', 3, '*'),
            'PadLeft must not truncate when source >= target length');
    end;

    // --- PadRight ---

    [Test]
    procedure PadRight_AddsPadChar()
    begin
        Assert.AreEqual('42*****', Src.PadRightIt('42', 7, '*'),
            'PadRight must pad the right side with the given char');
    end;

    [Test]
    procedure PadRight_Default_PadsWithSpace()
    begin
        Assert.AreEqual('42     ', Src.PadRight_SpaceDefault('42', 7),
            'PadRight with no char argument must pad with spaces');
    end;

    [Test]
    procedure PadRight_DifferentFromPadLeft_NegativeTrap()
    begin
        // Negative trap: PadLeft and PadRight must produce different results
        // when padding a short string.
        Assert.AreNotEqual(Src.PadLeftIt('x', 3, '_'), Src.PadRightIt('x', 3, '_'),
            'PadLeft and PadRight must produce different strings');
    end;

    // --- Remove ---

    [Test]
    procedure Remove_FromIndex_ToEnd()
    begin
        // AL 1-based: Remove(3) deletes from position 3 to the end.
        Assert.AreEqual('Hi', Src.RemoveFromStart('Hi World', 3),
            'Remove(index) deletes from the 1-based index to the end');
    end;

    [Test]
    procedure Remove_Count()
    begin
        // AL 1-based: Remove(3, 4) deletes 4 chars starting at index 3.
        // "Hello World" -> remove positions 3..6 ("llo ") -> "HeWorld"
        Assert.AreEqual('HeWorld', Src.RemoveCount('Hello World', 3, 4),
            'Remove(index, count) removes count chars starting at the index');
    end;

    // --- Replace ---

    [Test]
    procedure Replace_SingleChar()
    begin
        Assert.AreEqual('H.llo', Src.ReplaceChars('Hello', 'e', '.'),
            'Replace(char, char) must substitute every occurrence');
    end;

    [Test]
    procedure Replace_String()
    begin
        Assert.AreEqual('Hello there', Src.ReplaceStrings('Hello world', 'world', 'there'),
            'Replace(text, text) must substitute the needle');
    end;

    [Test]
    procedure Replace_NoMatch_Unchanged()
    begin
        // When the needle is absent the string must be returned unchanged.
        Assert.AreEqual('Hello', Src.ReplaceStrings('Hello', 'xyz', 'abc'),
            'Replace returns the source unchanged when the needle is absent');
    end;

    // --- Trim ---

    [Test]
    procedure Trim_StripsBothSides()
    begin
        Assert.AreEqual('hello', Src.TrimIt('   hello   '),
            'Trim must strip whitespace on both sides');
    end;

    [Test]
    procedure Trim_NoWhitespace_Unchanged()
    begin
        Assert.AreEqual('hello', Src.TrimIt('hello'),
            'Trim on a non-whitespace string must return the string unchanged');
    end;

    [Test]
    procedure TrimStart_StripsLeading()
    begin
        Assert.AreEqual('hello   ', Src.TrimStartIt('   hello   '),
            'TrimStart must strip leading whitespace only');
    end;

    [Test]
    procedure TrimEnd_StripsTrailing()
    begin
        Assert.AreEqual('   hello', Src.TrimEndIt('   hello   '),
            'TrimEnd must strip trailing whitespace only');
    end;

    [Test]
    procedure TrimStart_DiffersFromTrimEnd_NegativeTrap()
    begin
        Assert.AreNotEqual(Src.TrimStartIt('   x   '), Src.TrimEndIt('   x   '),
            'TrimStart and TrimEnd must produce different results on a two-sided string');
    end;
}
