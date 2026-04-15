codeunit 50930 "BigText Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "BigText Helper";

    [Test]
    procedure TestAddAndRead()
    begin
        // [WHEN] Adding text and reading it back
        // [THEN] The result should match the input
        Assert.AreEqual('Hello World', Helper.AddAndRead('Hello World'), 'AddText + GetSubText should round-trip');
    end;

    [Test]
    procedure TestLength()
    begin
        // [WHEN] Adding text and checking length
        // [THEN] Length should match the input length
        Assert.AreEqual(5, Helper.GetLength('Hello'), 'Length should be 5');
    end;

    [Test]
    procedure TestEmptyLength()
    begin
        // [WHEN] No text is added
        // [THEN] Length should be 0
        Assert.AreEqual(0, Helper.GetLength(''), 'Empty BigText should have length 0');
    end;

    [Test]
    procedure TestTextPosFound()
    begin
        // [WHEN] Searching for a substring that exists
        // [THEN] TextPos should return the 1-based position
        Assert.AreEqual(7, Helper.FindPosition('Hello World', 'World'), 'World starts at position 7');
    end;

    [Test]
    procedure TestTextPosNotFound()
    begin
        // [WHEN] Searching for a substring that does not exist
        // [THEN] TextPos should return 0
        Assert.AreEqual(0, Helper.FindPosition('Hello World', 'Missing'), 'Missing text should return 0');
    end;

    [Test]
    procedure TestGetSubText()
    begin
        // [WHEN] Extracting a substring
        // [THEN] The extracted text should match
        Assert.AreEqual('World', Helper.GetSubstring('Hello World', 7, 5), 'Substring at pos 7, length 5');
    end;

    [Test]
    procedure TestConcatenate()
    begin
        // [WHEN] Adding two texts
        // [THEN] They should be concatenated
        Assert.AreEqual('FooBar', Helper.ConcatenateTexts('Foo', 'Bar'), 'Two AddText calls should concatenate');
    end;

    [Test]
    procedure TestTextPosNegative()
    begin
        // [WHEN] Searching for an empty needle
        // [THEN] TextPos should return 0 for empty search
        Assert.AreEqual(0, Helper.FindPosition('Some Text', ''), 'Empty needle should return 0');
    end;
}
