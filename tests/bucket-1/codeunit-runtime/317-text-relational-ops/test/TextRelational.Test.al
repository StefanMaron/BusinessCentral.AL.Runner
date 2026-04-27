codeunit 1314004 "Text Relational Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "Text Relational Helper";

    [Test]
    procedure Text_GreaterThan_ReturnsTrue()
    begin
        // '5' > '3' — ordinal comparison should be true
        Assert.IsTrue(Helper.GreaterText('5', '3'), 'Text > Text must return true when left is greater');
    end;

    [Test]
    procedure Text_GreaterThan_ReturnsFalse()
    begin
        // '3' > '5' — ordinal comparison should be false
        Assert.IsFalse(Helper.GreaterText('3', '5'), 'Text > Text must return false when left is smaller');
    end;

    [Test]
    procedure Text_GreaterThan_EqualReturnsFalse()
    begin
        Assert.IsFalse(Helper.GreaterText('abc', 'abc'), 'Text > Text must return false when equal');
    end;

    [Test]
    procedure Text_LessThan_ReturnsTrue()
    begin
        Assert.IsTrue(Helper.LessText('apple', 'banana'), 'Text < Text must return true when left is less');
    end;

    [Test]
    procedure Text_LessThan_ReturnsFalse()
    begin
        Assert.IsFalse(Helper.LessText('banana', 'apple'), 'Text < Text must return false when left is greater');
    end;

    [Test]
    procedure Text_LessThan_EqualReturnsFalse()
    begin
        Assert.IsFalse(Helper.LessText('abc', 'abc'), 'Text < Text must return false when equal');
    end;

    [Test]
    procedure Text_GreaterOrEqual_TrueWhenGreater()
    begin
        Assert.IsTrue(Helper.GreaterOrEqualText('z', 'a'), 'Text >= Text must return true when left is greater');
    end;

    [Test]
    procedure Text_GreaterOrEqual_TrueWhenEqual()
    begin
        Assert.IsTrue(Helper.GreaterOrEqualText('abc', 'abc'), 'Text >= Text must return true when equal');
    end;

    [Test]
    procedure Text_GreaterOrEqual_FalseWhenLess()
    begin
        Assert.IsFalse(Helper.GreaterOrEqualText('a', 'z'), 'Text >= Text must return false when left is less');
    end;

    [Test]
    procedure Text_LessOrEqual_TrueWhenLess()
    begin
        Assert.IsTrue(Helper.LessOrEqualText('a', 'z'), 'Text <= Text must return true when left is less');
    end;

    [Test]
    procedure Text_LessOrEqual_TrueWhenEqual()
    begin
        Assert.IsTrue(Helper.LessOrEqualText('xyz', 'xyz'), 'Text <= Text must return true when equal');
    end;

    [Test]
    procedure Text_LessOrEqual_FalseWhenGreater()
    begin
        Assert.IsFalse(Helper.LessOrEqualText('z', 'a'), 'Text <= Text must return false when left is greater');
    end;

    [Test]
    procedure Text_NotEqual_ReturnsTrue()
    begin
        Assert.IsTrue(Helper.NotEqualText('5', '3'), 'Text <> Text must return true when different');
    end;

    [Test]
    procedure Text_NotEqual_ReturnsFalse()
    begin
        Assert.IsFalse(Helper.NotEqualText('abc', 'abc'), 'Text <> Text must return false when equal');
    end;
}
