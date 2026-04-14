codeunit 50914 "InStream Method Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestInStreamLength()
    var
        Helper: Codeunit "InStream Methods";
    begin
        // InStream.Length should return the byte length of the content
        Assert.AreEqual(11, Helper.TestLength(), 'Length of "Hello World" should be 11');
    end;

    [Test]
    procedure TestInStreamPosition()
    var
        Helper: Codeunit "InStream Methods";
    begin
        // After reading 5 bytes, position should be 5
        Assert.AreEqual(5, Helper.TestPosition(), 'Position after reading 5 chars should be 5');
    end;

    [Test]
    procedure TestInStreamResetPosition()
    var
        Helper: Codeunit "InStream Methods";
    begin
        // After reset, reading from start should return full content
        Assert.AreEqual('Hello World', Helper.TestResetPosition(), 'After ResetPosition, full text should be readable');
    end;

    [Test]
    procedure TestInStreamLengthNotZero()
    var
        Helper: Codeunit "InStream Methods";
    begin
        // Negative: length should not be zero for non-empty content
        Assert.AreNotEqual(0, Helper.TestLength(), 'Length must not be zero for non-empty stream');
    end;
}
