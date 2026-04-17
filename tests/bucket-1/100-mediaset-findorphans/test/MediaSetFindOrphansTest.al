codeunit 117002 "MSF Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "MSF Src";

    [Test]
    procedure MediaSetFindOrphans_ReturnsEmptyList()
    var
        Orphans: List of [Guid];
    begin
        // [WHEN] MediaSet.FindOrphans() is called in standalone runner (no real media storage)
        Orphans := Src.GetOrphans();

        // [THEN] Returns an empty list — no orphaned media in test environment
        Assert.AreEqual(0, Orphans.Count(), 'FindOrphans must return empty list in standalone runner');
    end;

    [Test]
    procedure MediaSetFindOrphans_DoesNotCrash()
    begin
        // [WHEN] FindOrphans is called (positive: must not throw)
        Src.GetOrphans();

        // [THEN] No exception — the stub executes cleanly
        Assert.IsTrue(true, 'FindOrphans must not throw in standalone runner');
    end;

    [Test]
    procedure MediaSetFindOrphans_ReturnedListIsEmpty_NotZeroCount()
    var
        Orphans: List of [Guid];
        IsEmpty: Boolean;
    begin
        // [WHEN]
        Orphans := Src.GetOrphans();

        // [THEN] Count is exactly 0 (not just truthy-empty)
        IsEmpty := Orphans.Count() = 0;
        Assert.IsTrue(IsEmpty, 'FindOrphans result count must be exactly 0');
    end;
}
