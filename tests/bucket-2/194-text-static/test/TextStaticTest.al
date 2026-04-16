codeunit 60201 "TXST Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "TXST Src";

    // --- DelChr ---

    [Test]
    procedure DelChr_RemovesAll()
    begin
        // where='=' means "all occurrences anywhere".
        Assert.AreEqual('hello', Src.DelChrIt('h e l l o', '=', ' '),
            'Text.DelChr must remove every space');
    end;

    [Test]
    procedure DelChr_Leading()
    begin
        Assert.AreEqual('hello', Src.DelChrIt('   hello', '<', ' '),
            'Text.DelChr(<,'' '') must remove only leading spaces');
    end;

    [Test]
    procedure DelChr_Trailing()
    begin
        Assert.AreEqual('hello', Src.DelChrIt('hello   ', '>', ' '),
            'Text.DelChr(>,'' '') must remove only trailing spaces');
    end;

    // --- DelStr ---

    [Test]
    procedure DelStr_Range()
    begin
        // 1-based: DelStr('hello', 2, 3) removes chars 2..4 ("ell"), leaving "ho".
        Assert.AreEqual('ho', Src.DelStrIt('hello', 2, 3),
            'Text.DelStr(v,2,3) must remove three chars starting at position 2');
    end;

    [Test]
    procedure DelStr_ToEnd()
    begin
        // 2-arg DelStr: delete from pos to end.
        Assert.AreEqual('hel', Src.DelStrToEnd('hello', 4),
            'Text.DelStr(v,4) must remove from pos 4 to end');
    end;

    // --- InsStr ---

    [Test]
    procedure InsStr_AtStart()
    begin
        Assert.AreEqual('!hello', Src.InsStrIt('hello', '!', 1),
            'Text.InsStr(v,''!'',1) must insert at position 1');
    end;

    [Test]
    procedure InsStr_InMiddle()
    begin
        Assert.AreEqual('heXXXllo', Src.InsStrIt('hello', 'XXX', 3),
            'Text.InsStr(v,''XXX'',3) must insert three chars at position 3');
    end;

    // --- LowerCase / UpperCase ---

    [Test]
    procedure LowerCase()
    begin
        Assert.AreEqual('abc xyz', Src.LowerIt('ABC xyz'),
            'Text.LowerCase must return all lower-case');
    end;

    [Test]
    procedure UpperCase()
    begin
        Assert.AreEqual('ABC XYZ', Src.UpperIt('abc XYZ'),
            'Text.UpperCase must return all upper-case');
    end;

    [Test]
    procedure Lower_And_Upper_DifferOnMixed_NegativeTrap()
    begin
        Assert.AreNotEqual(Src.LowerIt('AaA'), Src.UpperIt('AaA'),
            'Text.LowerCase and Text.UpperCase must produce different results on mixed input');
    end;

    // --- MaxStrLen ---

    [Test]
    procedure MaxStrLen_ReturnsDeclaredLength()
    begin
        // Declared as Text[50].
        Assert.AreEqual(50, Src.MaxStrLenIt(),
            'Text.MaxStrLen(s) must return the declared Text[N] length');
    end;

    // --- StrLen ---

    [Test]
    procedure StrLen_OfHello_Is5()
    begin
        Assert.AreEqual(5, Src.StrLenIt('hello'),
            'Text.StrLen(''hello'') must return 5');
    end;

    [Test]
    procedure StrLen_OfEmpty_Is0()
    begin
        Assert.AreEqual(0, Src.StrLenIt(''),
            'Text.StrLen('''') must return 0');
    end;

    // --- StrSubstNo ---

    [Test]
    procedure StrSubstNo_NoPlaceholders()
    begin
        // 1-arg form: returns the format string unchanged.
        Assert.AreEqual('hello', Src.StrSubstNoOneArg('hello'),
            'Text.StrSubstNo with no placeholders must return the format unchanged');
    end;

    [Test]
    procedure StrSubstNo_OneArg()
    begin
        Assert.AreEqual('Hello, World', Src.StrSubstNoTwoArg('Hello, %1', 'World'),
            'Text.StrSubstNo must substitute %1');
    end;

    [Test]
    procedure StrSubstNo_TwoArgs_DifferentTypes()
    begin
        Assert.AreEqual('Alice is 30', Src.StrSubstNoThreeArg('%1 is %2', 'Alice', 30),
            'Text.StrSubstNo must substitute %1 (Text) and %2 (Integer)');
    end;
}
