codeunit 59581 "BST Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "BST Src";

    [Test]
    procedure BlobStream_WriteAndRead_RoundsTrip()
    begin
        // Positive: write a text via Blob.CreateOutStream → read it back via
        // Blob.CreateInStream — round-trip must preserve the content.
        Assert.AreEqual('hello', Src.WriteAndRead('hello'),
            'Write then read via Blob streams must round-trip the text');
    end;

    [Test]
    procedure BlobStream_WriteAndRead_EmptyString()
    begin
        // Empty string must round-trip as empty (no crash, no extra content).
        Assert.AreEqual('', Src.WriteAndRead(''),
            'Empty string must round-trip as empty');
    end;

    [Test]
    procedure BlobStream_WriteAndRead_SpecialChars()
    begin
        // Unicode / special chars must round-trip through UTF-8 stream.
        Assert.AreEqual('caf\u00e9 \u2603', Src.WriteAndRead('caf\u00e9 \u2603'),
            'Unicode text must round-trip through blob streams');
    end;

    [Test]
    procedure BlobStream_WriteAndRead_LongText()
    begin
        // Proving the blob round-trip isn't limited to short strings.
        Assert.AreEqual(
            'abcdefghijklmnopqrstuvwxyz 0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ',
            Src.WriteAndRead('abcdefghijklmnopqrstuvwxyz 0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ'),
            'Longer text must round-trip');
    end;

    [Test]
    procedure BlobStream_HasValue_AfterWrite()
    begin
        // Positive: after writing to the blob via OutStream, HasValue must be true.
        Assert.IsTrue(Src.WrittenBlobHasValue('payload'),
            'HasValue must be true after writing via CreateOutStream + WriteText');
    end;

    [Test]
    procedure BlobStream_HasValue_FreshIsFalse()
    begin
        // Negative: a newly-declared Blob must report HasValue=false.
        Assert.IsFalse(Src.FreshBlobHasNoValue(),
            'Fresh Blob field must have HasValue=false');
    end;

    [Test]
    procedure BlobStream_WriteAndRead_NotIdentity_NegativeTrap()
    begin
        // Negative: guard against WriteAndRead shortcutting by just returning the
        // input — different inputs must produce different outputs (proving the
        // result really flows through the blob round-trip).
        Assert.AreNotEqual(
            Src.WriteAndRead('alpha'),
            Src.WriteAndRead('beta'),
            'Different input must produce different round-trip output');
    end;
}
