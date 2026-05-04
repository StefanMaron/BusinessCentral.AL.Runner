codeunit 1260002 "Object NavValue Test"
{
    Subtype = Test;

    [Test]
    procedure SetRange_WithVariant_FiltersCorrectly()
    var
        Rec: Record "Object NavValue Table";
        Helper: Codeunit "Object NavValue Helper";
        V: Variant;
    begin
        // Setup
        Rec.Init();
        Rec."Code" := 'SR1';
        Rec.Description := 'Alpha';
        Rec.Insert();

        Rec.Init();
        Rec."Code" := 'SR2';
        Rec.Description := 'Beta';
        Rec.Insert();

        // Exercise: SetRange with Variant
        V := 'SR1';
        Helper.SetRangeFromVariant(Rec, V);

        // Verify
        Assert.AreEqual(1, Rec.Count(), 'SetRange should filter to one record');
        Rec.FindFirst();
        Assert.AreEqual('Alpha', Rec.Description, 'Should find the correct record');
    end;

    [Test]
    procedure SetRange_FromTo_WithVariant_FiltersCorrectly()
    var
        Rec: Record "Object NavValue Table";
        Helper: Codeunit "Object NavValue Helper";
        FromV: Variant;
        ToV: Variant;
    begin
        // Setup
        Rec.Init();
        Rec."Code" := 'FT1';
        Rec.Description := 'Alpha';
        Rec.Insert();

        Rec.Init();
        Rec."Code" := 'FT2';
        Rec.Description := 'Beta';
        Rec.Insert();

        Rec.Init();
        Rec."Code" := 'FT3';
        Rec.Description := 'Charlie';
        Rec.Insert();

        // Exercise: SetRange(from, to) with Variant
        FromV := 'FT1';
        ToV := 'FT2';
        Helper.SetRangeFromToVariant(Rec, FromV, ToV);

        // Verify
        Assert.AreEqual(2, Rec.Count(), 'SetRange from/to should filter to two records');
    end;

    [Test]
    procedure Validate_WithVariant_SetsField()
    var
        Rec: Record "Object NavValue Table";
        Helper: Codeunit "Object NavValue Helper";
        V: Variant;
    begin
        // Setup
        Rec.Init();
        Rec."Code" := 'VAL';
        Rec.Insert();

        // Exercise: Validate with Variant
        V := 'Updated';
        Helper.ValidateFromVariant(Rec, V);

        // Verify
        Assert.AreEqual('Updated', Rec.Description, 'Validate should set the field value');
    end;

    [Test]
    procedure Get_WithVariant_FindsRecord()
    var
        Rec: Record "Object NavValue Table";
        Helper: Codeunit "Object NavValue Helper";
        V: Variant;
    begin
        // Setup
        Rec.Init();
        Rec."Code" := 'KEY1';
        Rec.Description := 'Found';
        Rec.Insert();

        // Exercise: Get with Variant key
        V := 'KEY1';
        Rec.Init();
        Helper.GetViaVariantKey(Rec, V);

        // Verify
        Assert.AreEqual('Found', Rec.Description, 'Get should find the record by variant key');
    end;

    [Test]
    procedure Get_WithVariant_ErrorOnMissing()
    var
        Rec: Record "Object NavValue Table";
        Helper: Codeunit "Object NavValue Helper";
        V: Variant;
    begin
        // Exercise: Get with non-existent key
        V := 'NOEXIST';
        asserterror Helper.GetViaVariantKey(Rec, V);

        // Verify
        Assert.ExpectedError('does not exist');
    end;

    [Test]
    procedure SetFilter_WithVariant_FiltersCorrectly()
    var
        Rec: Record "Object NavValue Table";
        Helper: Codeunit "Object NavValue Helper";
        V: Variant;
    begin
        // Setup
        Rec.Init();
        Rec."Code" := 'FILT';
        Rec.Description := 'Filtered';
        Rec.Insert();

        Rec.Init();
        Rec."Code" := 'OTHER';
        Rec.Description := 'Other';
        Rec.Insert();

        // Exercise: SetFilter with Variant
        V := 'FILT';
        Helper.SetFilterFromVariant(Rec, V);

        // Verify
        Assert.AreEqual(1, Rec.Count(), 'SetFilter should filter to one record');
        Rec.FindFirst();
        Assert.AreEqual('Filtered', Rec.Description, 'Should find the correct record');
    end;

    [Test]
    procedure ModifyAll_WithVariant_UpdatesAll()
    var
        Rec: Record "Object NavValue Table";
        Helper: Codeunit "Object NavValue Helper";
        V: Variant;
    begin
        // Setup
        Rec.Init();
        Rec."Code" := 'MA1';
        Rec.Description := 'Old1';
        Rec.Insert();

        Rec.Init();
        Rec."Code" := 'MA2';
        Rec.Description := 'Old2';
        Rec.Insert();

        // Exercise: ModifyAll with Variant
        V := 'NewValue';
        Helper.ModifyAllFromVariant(Rec, V);

        // Verify
        Rec.FindSet();
        Assert.AreEqual('NewValue', Rec.Description, 'First record should be updated');
        Rec.Next();
        Assert.AreEqual('NewValue', Rec.Description, 'Second record should be updated');
    end;

    var
        Assert: Codeunit "Library Assert";
}
