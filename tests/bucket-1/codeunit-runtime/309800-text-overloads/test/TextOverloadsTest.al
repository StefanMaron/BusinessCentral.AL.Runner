codeunit 309801 "TextOvl Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "TextOvl Src";

    // ==================================================================
    // IncStr(Text, BigInteger) — 2-arg step form
    // ==================================================================

    [Test]
    procedure IncStr2Arg_PositiveStep()
    begin
        // 'INV-001' + 10 steps => 'INV-011'
        Assert.AreEqual('INV-011', Src.CallIncStrStep('INV-001', 10),
            'IncStr(s, 10) must increment by 10');
    end;

    [Test]
    procedure IncStr2Arg_StepOne()
    begin
        // Verify step=1 behaves identically to the 1-arg form
        Assert.AreEqual('A002', Src.CallIncStrStep('A001', 1),
            'IncStr(s, 1) must behave like IncStr(s)');
    end;

    [Test]
    procedure IncStr2Arg_LargeStep()
    begin
        // 'X099' + 5 => 'X104' (overflows digit width)
        Assert.AreEqual('X104', Src.CallIncStrStep('X099', 5),
            'IncStr(s, 5) must overflow correctly: 099+5=104');
    end;

    [Test]
    procedure IncStr2Arg_ZeroStep()
    begin
        // step=0 returns the original string unchanged
        Assert.AreEqual('DOC001', Src.CallIncStrStep('DOC001', 0),
            'IncStr(s, 0) must return the original string');
    end;

    // ==================================================================
    // MaxStrLen(Variant) — declared variable form
    // ==================================================================

    [Test]
    procedure MaxStrLen_Text42()
    begin
        Assert.AreEqual(42, Src.CallMaxStrLenVariant(),
            'MaxStrLen(Text[42] var) must return 42');
    end;

    [Test]
    procedure MaxStrLen_Code15()
    begin
        Assert.AreEqual(15, Src.CallMaxStrLenCode(),
            'MaxStrLen(Code[15] var) must return 15');
    end;

    // ==================================================================
    // Text.Split(Text) — multi-char separator
    // ==================================================================

    [Test]
    procedure TextSplitText_Count()
    begin
        Assert.AreEqual(3, Src.TextSplitTextCount('a::b::c', '::'),
            'Split on "::" must produce 3 parts');
    end;

    [Test]
    procedure TextSplitText_Element()
    begin
        Assert.AreEqual('b', Src.TextSplitTextNth('a::b::c', '::', 2),
            'Split on "::" second element must be "b"');
    end;

    [Test]
    procedure TextSplitText_Negative_NotSingleChar()
    begin
        // 'a:b::c'.Split('::') => 2 parts: ['a:b', 'c']  (not 4 like Split(':'))
        Assert.AreEqual(2, Src.TextSplitTextCount('a:b::c', '::'),
            'Split("::") must not degenerate into Split(":")');
    end;

    // ==================================================================
    // Text.IndexOfAny(Text, Integer) — 2-arg startIndex form
    // ==================================================================

    [Test]
    procedure TextIndexOfAnyStart_Found()
    begin
        // 'Hello World': IndexOfAny('lo', 4) — from pos 4, first 'l' or 'o': 'l' at 4
        Assert.AreEqual(4, Src.TextIndexOfAnyStart('Hello World', 'lo', 4),
            'IndexOfAny("lo", 4) must return 4');
    end;

    [Test]
    procedure TextIndexOfAnyStart_SkipsBefore()
    begin
        // 'Hello World': IndexOfAny('lo', 6) skips the earlier 'l'(3), 'l'(4), 'o'(5)
        // From pos 6: 'o' at pos 8 in "World"
        Assert.AreEqual(8, Src.TextIndexOfAnyStart('Hello World', 'lo', 6),
            'IndexOfAny("lo", 6) must skip earlier matches and return 8');
    end;

    [Test]
    procedure TextIndexOfAnyStart_NotFound()
    begin
        Assert.AreEqual(0, Src.TextIndexOfAnyStart('Hello World', 'xyz', 1),
            'IndexOfAny("xyz") must return 0 when not found');
    end;

    // ==================================================================
    // Label.Split(Text) — multi-char separator on Label ('one,two,three')
    // ==================================================================

    [Test]
    procedure LabelSplitText_Count()
    begin
        // LabelVal = 'one,two,three'; split on ',' => 3 parts
        Assert.AreEqual(3, Src.LabelSplitTextCount(','),
            'Label.Split(",") must give 3 parts');
    end;

    [Test]
    procedure LabelSplitText_Element()
    begin
        Assert.AreEqual('two', Src.LabelSplitTextNth(',', 2),
            'Label.Split(",") 2nd element must be "two"');
    end;

    [Test]
    procedure LabelSplitText_Negative_AbsentSep()
    begin
        // separator not present => 1 part (the whole string)
        Assert.AreEqual(1, Src.LabelSplitTextCount('::'),
            'Label.Split("::") absent separator must return 1 part');
    end;

    // ==================================================================
    // Label.IndexOfAny(Text, Integer) — 2-arg form on Label ('one,two,three')
    // ==================================================================

    [Test]
    procedure LabelIndexOfAnyStart_Found()
    begin
        // 'one,two,three'; IndexOfAny('nt', 5) — from pos 5: 't' at 5 in 'two'
        Assert.AreEqual(5, Src.LabelIndexOfAnyStart('nt', 5),
            'Label.IndexOfAny("nt", 5) must return 5');
    end;

    [Test]
    procedure LabelIndexOfAnyStart_NotFound()
    begin
        Assert.AreEqual(0, Src.LabelIndexOfAnyStart('xyz', 1),
            'Label.IndexOfAny("xyz", 1) must return 0');
    end;

    // ==================================================================
    // TextConst.Split(Text) — multi-char separator on TextConst ('hello world')
    // ==================================================================

    [Test]
    procedure TextConstSplitText_Count()
    begin
        // 'hello world'.Split(' ') => 2 parts
        Assert.AreEqual(2, Src.TextConstSplitTextCount(' '),
            'TextConst.Split(" ") must give 2 parts');
    end;

    [Test]
    procedure TextConstSplitText_AbsentSep()
    begin
        Assert.AreEqual(1, Src.TextConstSplitTextCount('::'),
            'TextConst.Split("::") absent separator must return 1 part');
    end;

    // ==================================================================
    // TextConst.IndexOfAny(Text, Integer) — 2-arg form on TextConst ('hello world')
    // ==================================================================

    [Test]
    procedure TextConstIndexOfAnyStart_Found()
    begin
        // 'hello world'.IndexOfAny('ow', 5) — from pos 5: 'o' at 5
        Assert.AreEqual(5, Src.TextConstIndexOfAnyStart('ow', 5),
            'TextConst.IndexOfAny("ow", 5) must return 5');
    end;

    [Test]
    procedure TextConstIndexOfAnyStart_NotFound()
    begin
        Assert.AreEqual(0, Src.TextConstIndexOfAnyStart('xyz', 1),
            'TextConst.IndexOfAny("xyz", 1) must return 0');
    end;
}
