codeunit 56903 TestBlob
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit BlobHelper;

    [Test]
    procedure WriteAndReadText()
    var
        Rec: Record BlobTable;
    begin
        // Positive: write text to BLOB, read it back, verify match
        Rec.Id := 1;
        Rec.Insert(false);

        Helper.WriteText(Rec, 'Hello BLOB');
        Assert.AreEqual('Hello BLOB', Helper.ReadText(Rec), 'Written text should match read text');
    end;

    [Test]
    procedure ReadFromEmptyBlob()
    var
        Rec: Record BlobTable;
    begin
        // Positive: reading from empty BLOB returns empty string
        Rec.Id := 2;
        Rec.Insert(false);

        Assert.AreEqual('', Helper.ReadText(Rec), 'Empty BLOB should return empty string');
    end;

    [Test]
    procedure HasValueReturnsFalseForEmptyBlob()
    var
        Rec: Record BlobTable;
    begin
        // Positive: HasValue is false for empty BLOB
        Rec.Id := 3;
        Rec.Insert(false);

        Assert.AreEqual(false, Helper.HasValue(Rec), 'Empty BLOB should not have value');
    end;

    [Test]
    procedure HasValueReturnsTrueAfterWrite()
    var
        Rec: Record BlobTable;
    begin
        // Positive: HasValue is true after writing
        Rec.Id := 4;
        Rec.Insert(false);

        Helper.WriteText(Rec, 'data');
        Assert.AreEqual(true, Helper.HasValue(Rec), 'BLOB should have value after write');
    end;

    [Test]
    procedure OverwriteBlobData()
    var
        Rec: Record BlobTable;
    begin
        // Positive: overwriting BLOB replaces old data
        Rec.Id := 5;
        Rec.Insert(false);

        Helper.WriteText(Rec, 'first');
        Helper.WriteText(Rec, 'second');
        Assert.AreEqual('second', Helper.ReadText(Rec), 'Overwritten BLOB should return new data');
    end;

    [Test]
    procedure WrittenTextDiffersFromWrongValue()
    var
        Rec: Record BlobTable;
    begin
        // Negative: written text does not match a wrong value
        Rec.Id := 6;
        Rec.Insert(false);

        Helper.WriteText(Rec, 'correct');
        Assert.AreNotEqual('wrong', Helper.ReadText(Rec), 'Read text should not equal wrong value');
    end;
}
