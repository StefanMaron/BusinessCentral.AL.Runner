codeunit 163103 "BSC TestBlobStreamChained"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        ChainedSrc: Codeunit "BSC ChainedStreamSrc";

    [Test]
    procedure ChainedOutStream_WriteText_And_ReadBack()
    var
        TempBlob: Codeunit "BSC TempBlobLike";
    begin
        // Positive: write text via chained CreateOutStream().WriteText(), read back via chained CreateInStream()
        ChainedSrc.WriteTextChained(TempBlob, 'Hello Chained');
        Assert.AreEqual('Hello Chained', ChainedSrc.ReadTextChained(TempBlob), 'Chained write/read should round-trip');
    end;

    [Test]
    procedure ChainedOutStream_HasValue_AfterWrite()
    var
        TempBlob: Codeunit "BSC TempBlobLike";
    begin
        // Positive: HasValue is true after chained write
        ChainedSrc.WriteTextChained(TempBlob, 'data');
        Assert.IsTrue(TempBlob.HasValue(), 'TempBlob must have value after chained write');
    end;

    [Test]
    procedure ChainedOutStream_OverwriteReplaces()
    var
        TempBlob: Codeunit "BSC TempBlobLike";
    begin
        // Positive: second chained write replaces first
        ChainedSrc.WriteTextChained(TempBlob, 'First');
        ChainedSrc.WriteTextChained(TempBlob, 'Second');
        Assert.AreEqual('Second', ChainedSrc.ReadTextChained(TempBlob), 'Second write should replace first');
    end;

    [Test]
    procedure ChainedOutStream_WrongValueFails()
    var
        TempBlob: Codeunit "BSC TempBlobLike";
    begin
        // Negative: chained write does not produce wrong value
        ChainedSrc.WriteTextChained(TempBlob, 'Expected');
        Assert.AreNotEqual('NotExpected', ChainedSrc.ReadTextChained(TempBlob), 'Chained write should not produce wrong value');
    end;
}
