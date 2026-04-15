codeunit 55902 "Record Copy Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Copy transfers field values
    // -----------------------------------------------------------------------

    [Test]
    procedure CopyTransfersFieldValues()
    var
        Source: Record "RC Test Table";
        Target: Record "RC Test Table";
    begin
        // [GIVEN] A record with specific field values
        Source.Init();
        Source."No." := 'A001';
        Source.Status := 2;
        Source.Amount := 99.50;

        // [WHEN] Copy into Target
        Target.Copy(Source);

        // [THEN] Target has the same field values
        Assert.AreEqual('A001', Target."No.", 'Copy must transfer No. field');
        Assert.AreEqual(2, Target.Status, 'Copy must transfer Status field');
        Assert.AreEqual(99.50, Target.Amount, 'Copy must transfer Amount field');
    end;

    // -----------------------------------------------------------------------
    // Copy always transfers filters (default ShareTable=false)
    // -----------------------------------------------------------------------

    [Test]
    procedure CopyTransfersFiltersWithShareTableFalse()
    var
        Source: Record "RC Test Table";
        Target: Record "RC Test Table";
    begin
        // [GIVEN] Source has a SetRange filter applied
        Source.SetRange(Status, 1, 3);

        // [WHEN] Copy without ShareTable (default=false)
        Target.Copy(Source);

        // [THEN] Target has the same filters
        Assert.AreEqual(Source.GetFilters(), Target.GetFilters(), 'Copy must transfer filters even without ShareTable');
    end;

    [Test]
    procedure CopyTransfersFiltersCount()
    var
        Source: Record "RC Test Table";
        Target: Record "RC Test Table";
    begin
        // [GIVEN] Three records, Source filtered to Status=1
        InsertRow('B01', 1, 10);
        InsertRow('B02', 2, 20);
        InsertRow('B03', 1, 30);
        Source.SetRange(Status, 1);

        // [WHEN] Copy into Target
        Target.Copy(Source);

        // [THEN] Target sees only 2 records (filtered), not 3
        Assert.AreEqual(2, Target.Count(), 'Copied filters must restrict count');
    end;

    // -----------------------------------------------------------------------
    // Copy with ShareTable=true — temp records share the same data
    // -----------------------------------------------------------------------

    [Test]
    procedure CopyShareTableTrueSharesTempData()
    var
        Source: Record "RC Test Table" temporary;
        Target: Record "RC Test Table" temporary;
    begin
        // [GIVEN] A temp record with one row
        Source.Init();
        Source."No." := 'T01';
        Source.Status := 1;
        Source.Insert();

        // [WHEN] Copy with ShareTable=true
        Target.Copy(Source, true);

        // [THEN] Target can see Source's row
        Assert.AreEqual(1, Target.Count(), 'ShareTable=true must share temp rows');
        Target.FindFirst();
        Assert.AreEqual('T01', Target."No.", 'ShareTable=true: shared row must be visible');
    end;

    [Test]
    procedure CopyShareTableTrueInsertVisibleInBoth()
    var
        Source: Record "RC Test Table" temporary;
        Target: Record "RC Test Table" temporary;
    begin
        // [GIVEN] Copy with ShareTable=true
        Source.Init();
        Source."No." := 'T10';
        Source.Insert();
        Target.Copy(Source, true);

        // [WHEN] Insert a new row via Target
        Target.Init();
        Target."No." := 'T11';
        Target.Insert();

        // [THEN] Source can also see the new row
        Assert.AreEqual(2, Source.Count(), 'Inserting via shared Target must be visible in Source');
    end;

    // -----------------------------------------------------------------------
    // Copy with ShareTable=false — temp records are independent
    // -----------------------------------------------------------------------

    [Test]
    procedure CopyShareTableFalseIndependentTempData()
    var
        Source: Record "RC Test Table" temporary;
        Target: Record "RC Test Table" temporary;
    begin
        // [GIVEN] Source temp has one row, copy with ShareTable=false
        Source.Init();
        Source."No." := 'U01';
        Source.Insert();
        Target.Copy(Source, false);

        // [WHEN] Insert another row via Target
        Target.Init();
        Target."No." := 'U02';
        Target.Insert();

        // [THEN] Source still only sees 1 row (independent)
        Assert.AreEqual(1, Source.Count(), 'ShareTable=false: Target inserts must not affect Source');
        Assert.AreEqual(2, Target.Count(), 'Target must see its own row');
    end;

    // -----------------------------------------------------------------------
    // Copy does not affect source filters
    // -----------------------------------------------------------------------

    [Test]
    procedure CopyDoesNotMutateSourceFilters()
    var
        Source: Record "RC Test Table";
        Target: Record "RC Test Table";
        OriginalFilter: Text;
    begin
        // [GIVEN] Source has a filter
        Source.SetRange(Status, 5);
        OriginalFilter := Source.GetFilters();

        // [WHEN] Copy into Target and modify Target's filters
        Target.Copy(Source);
        Target.SetRange(Status, 9);

        // [THEN] Source filters unchanged
        Assert.AreEqual(OriginalFilter, Source.GetFilters(), 'Copy must not allow Target filter changes to affect Source');
    end;

    local procedure InsertRow(No: Code[20]; Status: Integer; Amount: Decimal)
    var
        Rec: Record "RC Test Table";
    begin
        Rec.Init();
        Rec."No." := No;
        Rec.Status := Status;
        Rec.Amount := Amount;
        Rec.Insert();
    end;
}
