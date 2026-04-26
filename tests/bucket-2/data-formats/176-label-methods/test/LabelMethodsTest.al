codeunit 60011 "LBM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "LBM Src";

    [Test]
    procedure Label_Contains()
    begin
        Assert.IsTrue(Src.LabelContains('World'), 'Label.Contains("World") must be true');
        Assert.IsTrue(Src.LabelContains('Hello'), 'Label.Contains("Hello") must be true');
        Assert.IsFalse(Src.LabelContains('xyz'), 'Label.Contains("xyz") must be false');
    end;

    [Test]
    procedure Label_StartsWith()
    begin
        Assert.IsTrue(Src.LabelStartsWith('Hello'), 'Label.StartsWith("Hello") must be true');
        Assert.IsFalse(Src.LabelStartsWith('World'), 'Label.StartsWith("World") must be false');
    end;

    [Test]
    procedure Label_EndsWith()
    begin
        Assert.IsTrue(Src.LabelEndsWith('World'), 'Label.EndsWith("World") must be true');
        Assert.IsFalse(Src.LabelEndsWith('Hello'), 'Label.EndsWith("Hello") must be false');
    end;

    [Test]
    procedure Label_ToLower()
    begin
        Assert.AreEqual('hello world', Src.LabelToLower(),
            'Label.ToLower() on "HELLO World" must be "hello world"');
    end;

    [Test]
    procedure Label_ToUpper()
    begin
        Assert.AreEqual('HELLO WORLD', Src.LabelToUpper(),
            'Label.ToUpper() on "Hello world" must be "HELLO WORLD"');
    end;

    [Test]
    procedure Label_Trim()
    begin
        Assert.AreEqual('padded', Src.LabelTrim(),
            'Label.Trim() on "  padded  " must be "padded"');
    end;

    [Test]
    procedure Label_Replace()
    begin
        Assert.AreEqual('Hello Earth', Src.LabelReplace('World', 'Earth'),
            'Label.Replace("World", "Earth") must produce "Hello Earth"');
    end;

    [Test]
    procedure Label_Replace_NotFound_Unchanged()
    begin
        // Replacing a non-existent substring must leave the original intact.
        Assert.AreEqual('Hello World', Src.LabelReplace('xyz', 'Earth'),
            'Label.Replace of non-existent substring must leave text unchanged');
    end;

    [Test]
    procedure Label_IndexOf()
    begin
        // 'Hello World': H=1, e=2, l=3, l=4, o=5, (space)=6, W=7, o=8, r=9, l=10, d=11
        Assert.AreEqual(7, Src.LabelIndexOf('World'), 'IndexOf("World") in "Hello World" must be 7');
        Assert.AreEqual(1, Src.LabelIndexOf('Hello'), 'IndexOf("Hello") must be 1 (1-based)');
        Assert.AreEqual(0, Src.LabelIndexOf('xyz'), 'IndexOf of non-existent substring must be 0');
    end;

    [Test]
    procedure Label_ToLower_NotIdentity_NegativeTrap()
    begin
        // Negative: guard against ToLower() returning the raw text (no casing).
        Assert.AreNotEqual('HELLO World', Src.LabelToLower(),
            'Label.ToLower() must lower-case, not return raw');
    end;
}
