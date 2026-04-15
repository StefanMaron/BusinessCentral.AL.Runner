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
        // Table 56260 has 6 declared fields
        Assert.AreEqual(6, Probe.GetRecRefFieldCount(56260), 'FieldCount should return number of declared fields');
    end;

    [Test]
    procedure CaptionWithEmbeddedApostropheIsUnescaped()
    begin
        // field 6 has Caption = 'Vendor''s Name' — the doubled apostrophe should be unescaped
        Assert.AreEqual('Vendor''s Name', Probe.GetFieldCaption(56260, 6), 'Embedded apostrophe should be unescaped');
    end;

    [Test]
    procedure UnknownFieldFallsBackToFieldNNCaption()
    begin
        // Field 999 is not declared in table 56260 — caption should fall back to 'Field999'
        Assert.AreEqual('Field999', Probe.GetFieldCaption(56260, 999), 'Unknown field should fall back to FieldNN');
    end;

    [Test]
    procedure UnknownTableFallsBackToTableNNName()
    begin
        // Table 99999 is not registered — RecRef.Name should fall back to 'Table99999'
        Assert.AreEqual('Table99999', Probe.GetTableName(99999), 'Unknown table should fall back to TableNN');
    end;

    [Test]
    procedure IntegerFieldTypeIsInteger()
    begin
        // field 1 (Entry No.) is Integer
        Assert.AreEqual('Integer', Probe.GetFieldType(56260, 1), 'Integer field should report type Integer');
    end;

    [Test]
    procedure DecimalFieldTypeIsDecimal()
    begin
        // field 3 (Amount) is Decimal
        Assert.AreEqual('Decimal', Probe.GetFieldType(56260, 3), 'Decimal field should report type Decimal');
    end;

    [Test]
    procedure BooleanFieldTypeIsBoolean()
    begin
        // field 5 (Active) is Boolean
        Assert.AreEqual('Boolean', Probe.GetFieldType(56260, 5), 'Boolean field should report type Boolean');
    end;

    [Test]
    procedure TextFieldTypeIsText()
    begin
        // field 2 (Description) is Text[100]
        Assert.AreEqual('Text', Probe.GetFieldType(56260, 2), 'Text field should report type Text');
    end;

    [Test]
    procedure CodeFieldTypeIsCode()
    begin
        // field 4 (Item Code) is Code[20]
        Assert.AreEqual('Code', Probe.GetFieldType(56260, 4), 'Code field should report type Code');
    end;
}
