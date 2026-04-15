codeunit 56261 "Metadata Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;
        Probe: Codeunit "Metadata Probe";

    [Test]
    procedure FieldCaptionReturnsExplicitCaption()
    begin
        // field 1 has Caption = 'Entry Number'
        Assert.AreEqual('Entry Number', Probe.GetFieldCaption(56260, 1), 'Should return explicit field caption');
    end;

    [Test]
    procedure FieldCaptionFallsBackToFieldName()
    begin
        // field 3 (Amount) has no Caption property — should return field name
        Assert.AreEqual('Amount', Probe.GetFieldCaption(56260, 3), 'Should fall back to field name when no caption');
    end;

    [Test]
    procedure FieldNameReturnsCorrectName()
    begin
        Assert.AreEqual('Entry No.', Probe.GetFieldName(56260, 1), 'Should return field name');
    end;

    [Test]
    procedure FieldNameReturnsQuotedFieldName()
    begin
        Assert.AreEqual('Item Code', Probe.GetFieldName(56260, 4), 'Should return quoted field name');
    end;

    [Test]
    procedure TableNameReturnsRealName()
    begin
        Assert.AreEqual('Metadata Test Item', Probe.GetTableName(56260), 'Should return real table name');
    end;

    [Test]
    procedure TableCaptionReturnsExplicitCaption()
    begin
        Assert.AreEqual('Test Item', Probe.GetRecordTableCaption(), 'Should return explicit table caption');
    end;

    [Test]
    procedure RecordTableNameReturnsRealName()
    begin
        Assert.AreEqual('Metadata Test Item', Probe.GetRecordTableName(), 'Should return real table name via Record');
    end;

    [Test]
    procedure FieldCaptionFromRecordReturnsCaption()
    var
        Item: Record "Metadata Test Item";
    begin
        // FieldCaption("Entry No.") should return 'Entry Number'
        Assert.AreEqual('Entry Number', Item.FieldCaption("Entry No."), 'Record.FieldCaption should return caption');
    end;

    [Test]
    procedure FieldCaptionNoExplicitReturnsFallback()
    var
        Item: Record "Metadata Test Item";
    begin
        // Amount has no Caption property, so should return field name 'Amount'
        Assert.AreEqual('Amount', Item.FieldCaption(Amount), 'Record.FieldCaption should fall back to field name');
    end;

    [Test]
    procedure TextFieldLengthReturnsCorrectValue()
    begin
        // field 2 (Description) is Text[100] — length should be 100
        Assert.AreEqual(100, Probe.GetFieldLength(56260, 2), 'Text[100] should return length 100');
    end;

    [Test]
    procedure CodeFieldLengthReturnsCorrectValue()
    begin
        // field 4 (Item Code) is Code[20] — length should be 20
        Assert.AreEqual(20, Probe.GetFieldLength(56260, 4), 'Code[20] should return length 20');
    end;

    [Test]
    procedure RecRefFieldCountReturnsSchemaCount()
    begin
        // Table 56260 has 5 declared fields
        Assert.AreEqual(5, Probe.GetRecRefFieldCount(56260), 'FieldCount should return number of declared fields');
    end;
}
