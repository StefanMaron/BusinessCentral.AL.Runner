/// Tests for Media.ImportStream (accepts InStream) and Media.ExportStream (accepts OutStream).
/// Verifies that stream-based overloads compile and execute without CS1503.
codeunit 303013 "MST Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "MST Helper";

    // ── ImportStream ──────────────────────────────────────────────────────────

    [Test]
    procedure ImportStream_SetsHasValueTrue()
    var
        Rec: Record "MST Media Table";
        BlobRec: Record "MST Blob Table" temporary;
        InStr: InStream;
    begin
        // Positive: after ImportStream, HasValue must be true.
        Rec.Init();
        Rec."No." := 'IMPORT-1';
        Rec.Insert();
        Helper.MakeBlobInStream(BlobRec, InStr);
        Assert.IsTrue(Helper.ImportFromStream(Rec, InStr), 'ImportStream must return true');
        Assert.IsTrue(Helper.HasValue(Rec), 'HasValue must be true after ImportStream');
    end;

    [Test]
    procedure ImportStream_ReturnsFalseWhenHasValueNotYetSet()
    var
        Rec: Record "MST Media Table";
    begin
        // Negative: a fresh record has no media — HasValue must be false.
        Rec.Init();
        Rec."No." := 'IMPORT-2';
        Rec.Insert();
        Assert.IsFalse(Helper.HasValue(Rec), 'HasValue must be false before any import');
    end;

    // ── ExportStream ──────────────────────────────────────────────────────────

    [Test]
    procedure ExportStream_ReturnsTrue()
    var
        Rec: Record "MST Media Table";
        BlobRec: Record "MST Blob Table" temporary;
        OutStr: OutStream;
    begin
        // Positive: ExportStream must return true (no crash) even with no stored data.
        Rec.Init();
        Rec."No." := 'EXPORT-1';
        Rec.Insert();
        Helper.MakeBlobOutStream(BlobRec, OutStr);
        Assert.IsTrue(Helper.ExportToStream(Rec, OutStr), 'ExportStream must return true');
    end;

    [Test]
    procedure ExportStream_DoesNotRaiseOnEmptyMedia()
    var
        Rec: Record "MST Media Table";
        BlobRec: Record "MST Blob Table" temporary;
        OutStr: OutStream;
    begin
        // Negative: ExportStream on a Media field with no content must not raise an error.
        Rec.Init();
        Rec."No." := 'EXPORT-2';
        Rec.Insert();
        Helper.MakeBlobOutStream(BlobRec, OutStr);
        // If this doesn't throw, the test passes.
        Helper.ExportToStream(Rec, OutStr);
    end;
}
