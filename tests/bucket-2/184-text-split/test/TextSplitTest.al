codeunit 60091 "TXSP Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "TXSP Src";

    // --- Split(Char) ---

    [Test]
    procedure SplitChar_CountIsThree()
    begin
        Assert.AreEqual(3, Src.SplitCharCount('one,two,three', ','),
            'Split on "," must produce three parts');
    end;

    [Test]
    procedure SplitChar_FirstElement()
    begin
        Assert.AreEqual('one', Src.SplitCharNth('one,two,three', ',', 1),
            'First element of Split must be "one"');
    end;

    [Test]
    procedure SplitChar_LastElement()
    begin
        Assert.AreEqual('three', Src.SplitCharNth('one,two,three', ',', 3),
            'Last element of Split must be "three"');
    end;

    [Test]
    procedure SplitChar_IncludesEmptyEntries()
    begin
        // 'a,,b' -> ['a', '', 'b'] with .NET default semantics.
        Assert.AreEqual(3, Src.SplitCharCount('a,,b', ','),
            'Split must preserve empty entries between consecutive separators');
    end;

    [Test]
    procedure SplitChar_NoSeparator_SingleEntry()
    begin
        // If the separator is absent the whole string is one element.
        Assert.AreEqual(1, Src.SplitCharCount('no-comma', ','),
            'Split with no separator match returns a single-element list');
    end;

    [Test]
    procedure SplitChar_NoSeparator_EntryIsFullInput()
    begin
        Assert.AreEqual('no-comma', Src.SplitCharNth('no-comma', ',', 1),
            'Single-element Split result must equal the original input');
    end;

    [Test]
    procedure SplitChar_JoinReconstructs()
    begin
        // Positive sanity check: joined back with a delimiter it reconstructs.
        Assert.AreEqual('one|two|three|', Src.SplitCharJoin('one,two,three', ','),
            'Round-trip through Split must not lose characters');
    end;

    // --- Split(Text) ---

    [Test]
    procedure SplitText_MultiCharSeparator_Count()
    begin
        Assert.AreEqual(3, Src.SplitTextCount('one::two::three', '::'),
            'Split on "::" must produce three parts');
    end;

    [Test]
    procedure SplitText_MultiCharSeparator_Element()
    begin
        Assert.AreEqual('two', Src.SplitTextNth('one::two::three', '::', 2),
            'Split on multi-char separator must return correct element');
    end;

    [Test]
    procedure SplitText_DiffersFromSingleChar_NegativeTrap()
    begin
        // Negative trap: Split("::") must NOT degenerate into Split(':').
        // Split('a:b::c', ':')  -> 4 parts
        // Split('a:b::c', '::') -> 2 parts ['a:b', 'c']
        Assert.AreEqual(2, Src.SplitTextCount('a:b::c', '::'),
            'Split(text) must treat the full string as one separator');
    end;

    // --- Split(List of [Char]) ---

    [Test]
    procedure SplitMultipleSeps_CountIsThree()
    begin
        // 'a,b;c' with seps {',', ';'} splits into ['a', 'b', 'c'].
        Assert.AreEqual(3, Src.SplitMultipleSepsCount('a,b;c'),
            'Split(List of [Char]) must split on any of the supplied characters');
    end;

    [Test]
    procedure SplitMultipleSeps_SingleSeparator_OnlyOneKind()
    begin
        // Using the same input with only one of the two separators should still split
        // at both ',' and ';' positions when the list contains both.
        Assert.AreEqual(2, Src.SplitMultipleSepsCount('only,comma'),
            'Split(List of [Char]) must find commas even when semicolons are absent');
    end;
}
