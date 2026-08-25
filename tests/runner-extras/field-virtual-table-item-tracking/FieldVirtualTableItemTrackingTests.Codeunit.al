// Reproduces #2008: the virtual Field system table (2000000041) provider must
// initialize successfully during a standard item-tracking / reservation lookup
// against Base App's "Reservation Entry" table (337), not only for a bundle's
// own private table (which tests/runner-extras/standalone-suites/field-virtual-table
// already proves works).
//
// RED (before the fix): Reservation Entry.SetSourceFilter(...) -> IsEmpty() threw
// RunnerOutOfScopeException("Field (virtual table 2000000041)") from
// AlRunner.Patches.RecordPatches.BuildManagedFieldDataProvider, even though
// AlRunner.BcRuntime.EnsureMetadataProviderSeeded() had already run one frame up.
//
// GREEN (after the fix): the lookup completes and returns a truthful, empty
// result set (no reservation entries seeded), proving the Field virtual-table
// provider initializes on this Base-App-backed path.
codeunit 61101 "FVTIT Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "FVTIT Assert";

    // Minimal isolation reproducer from #2008: a direct Reservation Entry.SetSourceFilter
    // call (the same pattern ItemTrackingManagement.ItemTrackingExistsOnDocumentLine uses
    // internally) must not throw while initializing the Field virtual table.
    [Test]
    procedure ReservationEntrySetSourceFilter_NoTrackingSeeded_IsEmpty()
    var
        ReservEntry: Record "Reservation Entry";
    begin
        ReservEntry.SetSourceFilter(Database::"Purchase Line", 1, '', -1, true);
        ReservEntry.SetSourceFilter('', 0);

        Assert.IsTrue(ReservEntry.IsEmpty(), 'No Reservation Entry rows were seeded for this source filter; IsEmpty must report true');
    end;

    // Positive companion: once a matching Reservation Entry row exists, the same
    // lookup path must find it — proves the Field virtual table initialization
    // this fix restores does not just avoid throwing, it lets the real lookup work.
    [Test]
    procedure ReservationEntrySetSourceFilter_MatchingEntrySeeded_IsFound()
    var
        ReservEntry: Record "Reservation Entry";
    begin
        ReservEntry.Init();
        ReservEntry."Entry No." := 1;
        ReservEntry."Source Type" := Database::"Purchase Line";
        ReservEntry."Source Subtype" := 1;
        ReservEntry."Source ID" := '';
        ReservEntry."Source Ref. No." := 10000;
        ReservEntry.Insert();

        ReservEntry.Reset();
        ReservEntry.SetSourceFilter(Database::"Purchase Line", 1, '', 10000, true);
        ReservEntry.SetSourceFilter('', 0);

        Assert.IsFalse(ReservEntry.IsEmpty(), 'The seeded Reservation Entry must be found by SetSourceFilter');
        Assert.IsTrue(ReservEntry.FindFirst(), 'FindFirst must locate the seeded Reservation Entry');
        Assert.AreEqual(1, ReservEntry."Entry No.", 'Entry No. of the found row must match the seeded row');
    end;

    // Item-tracking-lookup surface: the exact standard procedure the original report
    // failed inside. Proves the fix at the level the issue was actually reported.
    [Test]
    procedure ItemTrackingExistsOnDocumentLine_NoTrackingSeeded_ReturnsFalse()
    var
        ItemTrackingManagement: Codeunit "Item Tracking Management";
        Exists: Boolean;
    begin
        Exists := ItemTrackingManagement.ItemTrackingExistsOnDocumentLine(Database::"Purchase Line", 1, '', -1);

        Assert.IsFalse(Exists, 'No item tracking was seeded for this document line; ItemTrackingExistsOnDocumentLine must return false');
    end;
}
