codeunit 84001 "InStream EOS Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: EOS() returns true when stream is empty or exhausted.
    // ------------------------------------------------------------------

    [Test]
    procedure EOS_EmptyStream_ReturnsTrue()
    var
        Src: Codeunit "InStream EOS Src";
    begin
        // [GIVEN] A blob with no content and an InStream created from it
        // [WHEN] EOS() is called immediately
        // [THEN] Returns true — stream has no data
        Assert.IsTrue(Src.EmptyStreamIsEOS(), 'EOS() must return true for an empty stream');
    end;

    [Test]
    procedure EOS_AfterReadingAll_ReturnsTrue()
    var
        Src: Codeunit "InStream EOS Src";
    begin
        // [GIVEN] A blob with content, fully read
        // [WHEN] EOS() is called after reading all data
        // [THEN] Returns true
        Assert.IsTrue(Src.StreamIsEOSAfterReadingAll(), 'EOS() must return true after reading all content');
    end;

    // ------------------------------------------------------------------
    // Negative: EOS() returns false when data remains.
    // ------------------------------------------------------------------

    [Test]
    procedure EOS_NonEmptyStreamAtStart_ReturnsFalse()
    var
        Src: Codeunit "InStream EOS Src";
    begin
        // [GIVEN] A blob with content, stream not yet read
        // [WHEN] EOS() is called at position 0
        // [THEN] Returns false — data remains
        Assert.IsTrue(Src.NonEmptyStreamIsNotEOSAtStart(), 'EOS() must return false at start of non-empty stream');
    end;

    // ------------------------------------------------------------------
    // Integration: EOS() drives a read loop correctly.
    // ------------------------------------------------------------------

    [Test]
    procedure EOS_DriveReadLoop_CountsLines()
    var
        Src: Codeunit "InStream EOS Src";
    begin
        // [GIVEN] A stream with 2 WriteText calls
        // [WHEN] Loop reads lines until EOS()
        // [THEN] Exactly 2 iterations
        Assert.AreEqual(2, Src.CountLinesUsingEOS(), 'EOS() loop must read exactly 2 lines from 2-line stream');
    end;
}
