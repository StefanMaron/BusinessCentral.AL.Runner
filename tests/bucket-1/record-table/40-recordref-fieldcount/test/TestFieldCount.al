codeunit 54300 "Test RecordRef FieldCount"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure FieldCountReturnsSchemaCountForEmptyRecord()
    var
        Rec: Record "FC Three";
        RecRef: RecordRef;
    begin
        // Positive: RecordRef on a freshly-opened table reports schema field count,
        // even though no fields have been assigned values.
        RecRef.Open(Database::"FC Three");
        Assert.AreEqual(3, RecRef.FieldCount,
            'Empty RecordRef should report 3 schema fields for FC Three');
        RecRef.Close();
    end;

    [Test]
    procedure FieldCountUnchangedAfterSettingFields()
    var
        Rec: Record "FC Three";
        RecRef: RecordRef;
        Count: Integer;
    begin
        // Negative/invariance: setting field values must NOT change FieldCount.
        // (In BC, FieldCount is a schema property, not a runtime write count.)
        Rec.Init();
        Rec."Id" := 1;
        Rec.Name := 'Alpha';
        Rec.Amount := 99.5;
        Rec.Insert(true);

        RecRef.GetTable(Rec);
        Count := RecRef.FieldCount;
        Assert.AreEqual(3, Count,
            'FieldCount should still be 3 after populating all fields');
        RecRef.Close();
    end;

    [Test]
    procedure FieldCountReflectsDifferentTableSchema()
    var
        RecRef: RecordRef;
    begin
        // Positive: a 5-field table reports 5, proving the count is per-table schema.
        RecRef.Open(Database::"FC Five");
        Assert.AreEqual(5, RecRef.FieldCount,
            'FC Five has 5 schema fields');
        RecRef.Close();
    end;

    [Test]
    procedure FieldCountIsNotWriteCount()
    var
        Rec: Record "FC Five";
        RecRef: RecordRef;
    begin
        // Negative: assigning only a subset must not lower FieldCount to that subset.
        Rec.Init();
        Rec.Code := 'X';
        Rec.Description := 'only two fields set';
        Rec.Insert(true);

        RecRef.GetTable(Rec);
        Assert.AreNotEqual(2, RecRef.FieldCount,
            'FieldCount must not reflect the number of fields written (only 2 were set)');
        Assert.AreEqual(5, RecRef.FieldCount,
            'FieldCount must be the schema count (5) regardless of writes');
        RecRef.Close();
    end;
}
