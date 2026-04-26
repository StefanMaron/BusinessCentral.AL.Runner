codeunit 60121 "LBS Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "LBS Src";

    [Test]
    procedure Label_Split_ByComma()
    begin
        Assert.AreEqual(3, Src.Split_ByComma_Count(),
            'Label.Split('','') on "a,b,c" must return 3 parts');
    end;

    [Test]
    procedure Label_Substring_FromIndex()
    begin
        // 1-based: position 7 in "Hello World" starts at "World".
        Assert.AreEqual('World', Src.Substring_From(7),
            'Label.Substring(7) on "Hello World" must return "World"');
    end;

    [Test]
    procedure Label_Substring_FromIndexLength()
    begin
        // Substring(1, 5) takes the first 5 chars.
        Assert.AreEqual('Hello', Src.Substring_FromLength(1, 5),
            'Label.Substring(1,5) on "Hello World" must return "Hello"');
    end;

    [Test]
    procedure Label_PadLeft_WithChar()
    begin
        Assert.AreEqual('0000042', Src.PadLeftIt(7, '0'),
            'Label.PadLeft(7, ''0'') on "42" must return "0000042"');
    end;

    [Test]
    procedure Label_PadRight_WithChar()
    begin
        Assert.AreEqual('42*****', Src.PadRightIt(7, '*'),
            'Label.PadRight(7, ''*'') on "42" must return "42*****"');
    end;

    [Test]
    procedure Label_Remove_FromIndex()
    begin
        // 1-based: Remove(3) on "Hello World" drops from position 3 to end.
        Assert.AreEqual('He', Src.RemoveFromStart(3),
            'Label.Remove(3) on "Hello World" must return "He"');
    end;

    [Test]
    procedure Label_TrimStart_StripsLeadingOnly()
    begin
        Assert.AreEqual('padded   ', Src.TrimStartIt(),
            'Label.TrimStart on "   padded   " must strip only the leading spaces');
    end;

    [Test]
    procedure Label_TrimEnd_StripsTrailingOnly()
    begin
        Assert.AreEqual('   padded', Src.TrimEndIt(),
            'Label.TrimEnd on "   padded   " must strip only the trailing spaces');
    end;

    [Test]
    procedure Label_LastIndexOf_Found()
    begin
        // 'o' appears at positions 5 and 8 in "Hello World"; LastIndexOf returns 8.
        Assert.AreEqual(8, Src.LastIndexOfIt('o'),
            'Label.LastIndexOf(''o'') on "Hello World" must return 8');
    end;

    [Test]
    procedure Label_LastIndexOf_NotFound()
    begin
        Assert.AreEqual(0, Src.LastIndexOfIt('xyz'),
            'Label.LastIndexOf must return 0 when the needle is absent');
    end;

    [Test]
    procedure Label_IndexOfAny_FindsFirstMatchingChar()
    begin
        // "Hello World" IndexOfAny "xyzW" — 'W' at position 7, earliest match.
        Assert.AreEqual(7, Src.IndexOfAnyIt('xyzW'),
            'Label.IndexOfAny must return the 1-based position of the earliest matching char');
    end;

    [Test]
    procedure Label_IndexOfAny_NotFound()
    begin
        Assert.AreEqual(0, Src.IndexOfAnyIt('xyz'),
            'Label.IndexOfAny must return 0 when no supplied char is in the Label');
    end;

    [Test]
    procedure Label_TrimStart_DiffersFromTrimEnd_NegativeTrap()
    begin
        // Negative trap: TrimStart and TrimEnd on a two-sided padded string
        // must not be implemented as the same method.
        Assert.AreNotEqual(Src.TrimStartIt(), Src.TrimEndIt(),
            'Label.TrimStart and Label.TrimEnd must produce different results');
    end;
}
