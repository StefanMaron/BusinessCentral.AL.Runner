codeunit 55201 "Test CopyFilters"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // CopyFilters — positive tests
    // -----------------------------------------------------------------------

    [Test]
    procedure CopyFilters_TransfersSetRangeFilter()
    var
        Source: Record "CopyFilters Probe";
        Target: Record "CopyFilters Probe";
    begin
        // [GIVEN] Source record has a SetRange filter on Status
        Source.SetRange(Status, 2, 5);

        // [WHEN] CopyFilters is called
        Target.CopyFilters(Source);

        // [THEN] Target has the same filter
        Assert.IsTrue(Target.HasFilter, 'Target should have filter after CopyFilters');
        Assert.AreEqual(Source.GetFilters(), Target.GetFilters(), 'Target filters should match source filters');
    end;

    [Test]
    procedure CopyFilters_TransfersSetFilterExpression()
    var
        Source: Record "CopyFilters Probe";
        Target: Record "CopyFilters Probe";
    begin
        // [GIVEN] Source record has a SetFilter expression on Status
        Source.SetFilter(Status, '>3');

        // [WHEN] CopyFilters is called
        Target.CopyFilters(Source);

        // [THEN] Target GetFilters matches source
        Assert.IsTrue(Target.HasFilter, 'Target should have filter after CopyFilters');
        Assert.AreEqual(Source.GetFilters(), Target.GetFilters(), 'Target filters should match source SetFilter expression');
    end;

    [Test]
    procedure CopyFilters_TransfersMultipleFieldFilters()
    var
        Source: Record "CopyFilters Probe";
        Target: Record "CopyFilters Probe";
    begin
        // [GIVEN] Source has filters on two different fields
        Source.SetRange(Status, 1);
        Source.SetFilter(Category, 'A*');

        // [WHEN] CopyFilters is called
        Target.CopyFilters(Source);

        // [THEN] Target GetFilters matches source exactly
        Assert.AreEqual(Source.GetFilters(), Target.GetFilters(), 'Multi-field filters should be copied exactly');
    end;

    [Test]
    procedure CopyFilters_OverwritesExistingTargetFilters()
    var
        Source: Record "CopyFilters Probe";
        Target: Record "CopyFilters Probe";
    begin
        // [GIVEN] Target already has a filter on a different field
        Target.SetRange(Status, 99);

        // [GIVEN] Source has a filter on Status with a different value
        Source.SetRange(Status, 7);

        // [WHEN] CopyFilters is called
        Target.CopyFilters(Source);

        // [THEN] Target now has source's filter, not its own prior filter
        Assert.AreEqual(Source.GetFilters(), Target.GetFilters(), 'CopyFilters should overwrite target existing filters');
    end;

    [Test]
    procedure CopyFilters_AffectsCount()
    var
        Source: Record "CopyFilters Probe";
        Target: Record "CopyFilters Probe";
    begin
        // [GIVEN] Records inserted: Status 1, 2, 3
        InsertProbe('A', 1, 'X');
        InsertProbe('B', 2, 'X');
        InsertProbe('C', 3, 'X');

        // [GIVEN] Source filters to Status = 2
        Source.SetRange(Status, 2);

        // [WHEN] CopyFilters to Target then count
        Target.CopyFilters(Source);

        // [THEN] Target.Count should reflect the filter (only 1 record matches)
        Assert.AreEqual(1, Target.Count, 'CopyFilters should make Count respect copied filter');
    end;

    // -----------------------------------------------------------------------
    // CopyFilters — negative tests
    // -----------------------------------------------------------------------

    [Test]
    procedure CopyFilters_FromEmptySourceClearsTargetFilters()
    var
        Source: Record "CopyFilters Probe";
        Target: Record "CopyFilters Probe";
    begin
        // [GIVEN] Target has a filter, Source has no filters
        Target.SetRange(Status, 5);
        // Source is fresh — no filters

        // [WHEN] CopyFilters from empty source
        Target.CopyFilters(Source);

        // [THEN] Target filters are cleared
        Assert.IsFalse(Target.HasFilter, 'CopyFilters from empty source should clear target filters');
        Assert.AreEqual('', Target.GetFilters(), 'Target GetFilters should be empty after copy from unfilterd source');
    end;

    [Test]
    procedure CopyFilters_DoesNotAffectSourceFilters()
    var
        Source: Record "CopyFilters Probe";
        Target: Record "CopyFilters Probe";
        SourceFiltersBeforeCopy: Text;
        SourceFiltersAfterCopy: Text;
    begin
        // [GIVEN] Source has a filter
        Source.SetRange(Status, 3);
        SourceFiltersBeforeCopy := Source.GetFilters();

        // [WHEN] CopyFilters to Target
        Target.CopyFilters(Source);

        // [THEN] Source filters are unchanged
        SourceFiltersAfterCopy := Source.GetFilters();
        Assert.AreEqual(SourceFiltersBeforeCopy, SourceFiltersAfterCopy, 'CopyFilters should not modify source filters');
    end;

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    local procedure InsertProbe(NewCode: Code[20]; NewStatus: Integer; NewCategory: Text[50])
    var
        Rec: Record "CopyFilters Probe";
    begin
        Rec.Init();
        Rec.Code := NewCode;
        Rec.Status := NewStatus;
        Rec.Category := NewCategory;
        Rec.Insert();
    end;
}
