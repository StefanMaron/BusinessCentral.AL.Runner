codeunit 89001 "TCM Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "TCM Src";

    // ------------------------------------------------------------------
    // Contains
    // ------------------------------------------------------------------

    [Test]
    procedure TextConst_Contains_True()
    begin
        Assert.IsTrue(Src.LabelContains('World'), 'Label should contain World');
    end;

    [Test]
    procedure TextConst_Contains_False()
    begin
        Assert.IsFalse(Src.LabelContains('xyz'), 'Label should not contain xyz');
    end;

    // ------------------------------------------------------------------
    // StartsWith / EndsWith
    // ------------------------------------------------------------------

    [Test]
    procedure TextConst_StartsWith_True()
    begin
        Assert.IsTrue(Src.LabelStartsWith('Hello'), 'Label should start with Hello');
    end;

    [Test]
    procedure TextConst_StartsWith_False()
    begin
        Assert.IsFalse(Src.LabelStartsWith('World'), 'Label should not start with World');
    end;

    [Test]
    procedure TextConst_EndsWith_True()
    begin
        Assert.IsTrue(Src.LabelEndsWith('World!'), 'Label should end with World!');
    end;

    [Test]
    procedure TextConst_EndsWith_False()
    begin
        Assert.IsFalse(Src.LabelEndsWith('Hello'), 'Label should not end with Hello');
    end;

    // ------------------------------------------------------------------
    // IndexOf / LastIndexOf
    // ------------------------------------------------------------------

    [Test]
    procedure TextConst_IndexOf_Found()
    begin
        // 'Hello, World!' — 'o' first at position 5 (1-based in BC)
        Assert.AreEqual(5, Src.LabelIndexOf('o'), 'IndexOf o should return 5');
    end;

    [Test]
    procedure TextConst_IndexOf_NotFound()
    begin
        Assert.AreEqual(0, Src.LabelIndexOf('xyz'), 'IndexOf xyz should return 0');
    end;

    [Test]
    procedure TextConst_LastIndexOf_Found()
    begin
        // 'Hello, World!' — 'o' last at position 9 (1-based)
        Assert.AreEqual(9, Src.LabelLastIndexOf('o'), 'LastIndexOf o should return 9');
    end;

    // ------------------------------------------------------------------
    // Substring
    // ------------------------------------------------------------------

    [Test]
    procedure TextConst_Substring_WithLength()
    begin
        // 'Hello, World!' starting at 1 for 5 chars = 'Hello'
        Assert.AreEqual('Hello', Src.LabelSubstring(1, 5), 'Substring(1,5) should return Hello');
    end;

    [Test]
    procedure TextConst_Substring_FromStart()
    begin
        // 'Hello, World!' starting at 8 = 'World!'
        Assert.AreEqual('World!', Src.LabelSubstringFrom(8), 'Substring(8) should return World!');
    end;

    // ------------------------------------------------------------------
    // ToLower / ToUpper
    // ------------------------------------------------------------------

    [Test]
    procedure TextConst_ToLower()
    begin
        Assert.AreEqual('hello', Src.LabelToLower(), 'ToLower of HELLO should be hello');
    end;

    [Test]
    procedure TextConst_ToUpper()
    begin
        Assert.AreEqual('WORLD', Src.LabelToUpper(), 'ToUpper of world should be WORLD');
    end;

    // ------------------------------------------------------------------
    // Trim / TrimStart / TrimEnd
    // ------------------------------------------------------------------

    [Test]
    procedure TextConst_Trim()
    begin
        Assert.AreEqual('padded', Src.LabelTrim(), 'Trim should remove surrounding spaces');
    end;

    [Test]
    procedure TextConst_TrimStart()
    begin
        Assert.AreEqual('padded  ', Src.LabelTrimStart(), 'TrimStart should remove leading spaces');
    end;

    [Test]
    procedure TextConst_TrimEnd()
    begin
        Assert.AreEqual('  padded', Src.LabelTrimEnd(), 'TrimEnd should remove trailing spaces');
    end;

    // ------------------------------------------------------------------
    // PadLeft / PadRight
    // ------------------------------------------------------------------

    [Test]
    procedure TextConst_PadLeft()
    begin
        // 'world' padded to 8 chars = '   world'
        Assert.AreEqual('   world', Src.LabelPadLeft(8), 'PadLeft(8) should pad to width 8');
    end;

    [Test]
    procedure TextConst_PadRight()
    begin
        // 'world' padded to 8 chars = 'world   '
        Assert.AreEqual('world   ', Src.LabelPadRight(8), 'PadRight(8) should pad to width 8');
    end;

    // ------------------------------------------------------------------
    // Replace / Remove
    // ------------------------------------------------------------------

    [Test]
    procedure TextConst_Replace()
    begin
        Assert.AreEqual('Hello, AL!', Src.LabelReplace('World', 'AL'), 'Replace should work on Label');
    end;

    [Test]
    procedure TextConst_Remove()
    begin
        // 'Hello, World!' remove 2 chars at 1-based pos 6 removes ',' and ' ' = 'HelloWorld!'
        Assert.AreEqual('HelloWorld!', Src.LabelRemove(6, 2), 'Remove(6,2) should remove comma and space');
    end;

    // ------------------------------------------------------------------
    // Split
    // ------------------------------------------------------------------

    [Test]
    procedure TextConst_Split_Count()
    begin
        // 'one,two,three' split by ',' = 3 parts
        Assert.AreEqual(3, Src.LabelSplitCount(','), 'Split by comma should give 3 parts');
    end;
}
