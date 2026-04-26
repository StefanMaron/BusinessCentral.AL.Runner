codeunit 30002 "Record Format Test"
{
    Subtype = Test;
    var Assert: Codeunit Assert;

    /// <summary>
    /// Format(Record) must not throw IConvertible; result must be non-empty.
    /// </summary>
    [Test]
    procedure FormatDefaultRecord_ReturnsNonEmpty()
    var
        Rec: Record "Record Format Table";
        Src: Codeunit "Record Format Src";
        Result: Text;
    begin
        // Positive: Format on a default (empty cursor) Record returns a string without crashing
        Result := Src.FormatRecord(Rec);
        Assert.IsTrue(Result <> '', 'Format(Record) must return a non-empty string');
    end;

    /// <summary>
    /// Format(Record) where the record has a populated key — result must contain the key value.
    /// </summary>
    [Test]
    procedure FormatPopulatedRecord_ContainsKeyValue()
    var
        Src: Codeunit "Record Format Src";
        Result: Text;
    begin
        // Positive: Format of a populated record returns the position string containing key=42
        Result := Src.FormatPopulatedRecord();
        Assert.IsTrue(Result <> '', 'Format(populated Record) must return a non-empty string');
        Assert.IsTrue(StrPos(Result, '42') > 0, 'Format result must contain the key value 42');
    end;

    /// <summary>
    /// Passing a Record through a Format() call to an AcceptText method must not crash.
    /// </summary>
    [Test]
    procedure PassRecordAsText_DoesNotThrow()
    var
        Rec: Record "Record Format Table";
        Src: Codeunit "Record Format Src";
        Result: Text;
    begin
        // Positive: passing a formatted record string through a Text param chain must succeed
        Result := Src.PassRecordAsText(Rec);
        Assert.IsTrue(Result <> '', 'PassRecordAsText must return a non-empty string');
    end;

    /// <summary>
    /// Negative: Format of a cleared Record still returns a string (position string with zero key), not an error.
    /// </summary>
    [Test]
    procedure FormatRecord_AfterClear_ReturnsNonEmpty()
    var
        Rec: Record "Record Format Table";
        Src: Codeunit "Record Format Src";
        Result: Text;
    begin
        // Negative: even a cleared/default record produces a deterministic, non-empty string
        Clear(Rec);
        Result := Src.FormatRecord(Rec);
        Assert.IsTrue(Result <> '', 'Format(cleared Record) must return a non-empty string, not crash');
    end;
}
