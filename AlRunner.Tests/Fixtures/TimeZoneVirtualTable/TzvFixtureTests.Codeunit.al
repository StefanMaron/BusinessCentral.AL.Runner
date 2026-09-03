// Fixture suite for TimeZoneVirtualTableTests.cs (#2584).
//
// Every assertion is about SHAPE, never about a specific time zone id. The row set is
// whatever the host operating system reports — that is what BC's own TimeZoneDataProvider
// does, and asserting a Windows id here would fail on this Linux host for a reason that is
// documented behavior, not a bug.
codeunit 60781 "TZV Fixture Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "TZV Assert";

    [Test]
    procedure TimeZone_IsNotEmpty()
    var
        TimeZone: Record "Time Zone";
    begin
        // Before the fix this raised "There is no Time Zone within the filter."
        Assert.IsTrue(TimeZone.FindSet(), 'Time Zone must answer at least one row.');
        Assert.IsTrue(TimeZone.Count() > 1, 'A host reports more than one time zone.');
    end;

    [Test]
    procedure TimeZone_NumbersStartAtOneAndIncrementWithNoGaps()
    var
        TimeZone: Record "Time Zone";
        Expected: Integer;
    begin
        // "No." is a sequence over the host's list, so a provider that inserted rows without
        // numbering them, or numbered them from 0, fails here.
        Expected := 0;
        Assert.IsTrue(TimeZone.FindSet(), 'Time Zone must answer at least one row.');
        repeat
            Expected += 1;
            Assert.AreEqual(Expected, TimeZone."No.", 'Time Zone "No." must be 1..N with no gaps.');
        until TimeZone.Next() = 0;
    end;

    [Test]
    procedure TimeZone_EveryRowHasANonBlankId()
    var
        TimeZone: Record "Time Zone";
    begin
        // The negative control for a provider that inserted N blank rows: the count and the
        // numbering above would both pass, and this would not.
        Assert.IsTrue(TimeZone.FindSet(), 'Time Zone must answer at least one row.');
        repeat
            Assert.IsFalse(TimeZone.ID = '', 'Every Time Zone row must carry a non-blank ID.');
        until TimeZone.Next() = 0;
    end;

    [Test]
    procedure TimeZone_GetOne_AgreesWithTheFirstRowOfFindSet()
    var
        ByGet: Record "Time Zone";
        ByFind: Record "Time Zone";
    begin
        Assert.IsTrue(ByFind.FindSet(), 'Time Zone must answer at least one row.');
        Assert.IsTrue(ByGet.Get(1), 'Get(1) must find the first time zone.');
        Assert.AreEqual(ByFind.ID, ByGet.ID, 'Get(1) and the first FindSet row must be the same zone.');
        Assert.AreEqual(ByFind."Display Name", ByGet."Display Name", 'Get(1) must carry the same Display Name.');
    end;

    [Test]
    procedure TimeZone_GetOnANumberPastTheEnd_ReturnsFalse()
    var
        TimeZone: Record "Time Zone";
    begin
        // A provider answering every Get with a row would pass everything above.
        Assert.IsFalse(TimeZone.Get(999999), 'Time Zone must not have a row numbered 999999.');
    end;

    [Test]
    procedure TimeZone_FilterOnNumber_DiscriminatesBetweenRows()
    var
        TimeZone: Record "Time Zone";
    begin
        TimeZone.SetRange("No.", 1);
        Assert.AreEqual(1, TimeZone.Count(), 'A filter on one existing number must select one row.');

        TimeZone.SetRange("No.", 999999);
        Assert.AreEqual(0, TimeZone.Count(), 'A filter on an unused number must select no rows.');
        Assert.IsTrue(TimeZone.IsEmpty(), 'IsEmpty must be true for a filter naming no time zone.');
    end;
}
