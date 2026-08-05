/// <summary>
/// Split out of tests/runner-extras/microsoft-dependencies.
///
/// Microsoft/Application Test Library is the one dependency in runner-extras that ships as an
/// R2R app — a precompiled DLL with no `src/`, so invoking it exercises the Tier-1 load path
/// against unmodified MS-compiled code. That is precisely what makes it worth testing, and
/// precisely why nothing in the 27.x test-apps artifact can stand in for it: those 102 apps are
/// all source-only (0 DLLs), so swapping one in would quietly downgrade this to a Tier-3
/// source-compile and the test would keep passing while proving something weaker.
///
/// The app does not exist before BC 28.0 (verified by enumerating the w1 artifact central
/// directory for 27.0 / 27.3 / 27.5). Isolating it here means the other 13 Microsoft-dependency
/// regressions no longer inherit a 28.0 floor they never needed.
/// </summary>
codeunit 62201 "Microsoft Test Library Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "MTL Assert";
        LibraryNoSeries: Codeunit "Library - No. Series";

    [Test]
    procedure BaseAppCodeunit_LibraryNoSeries_CreateNoSeriesLine_Completes()
    var
        NoSeries: Record "No. Series";
        NoSeriesLine: Record "No. Series Line";
    begin
        NoSeries.Code := 'ALRLIB';
        NoSeries.Insert();

        LibraryNoSeries.CreateNoSeriesLine('ALRLIB', 1, 'ALL0000001', 'ALL9999999');

        // Assert the values the MS library actually wrote, not merely that a row exists — a
        // runner that created an empty line would satisfy Get() alone.
        Assert.IsTrue(NoSeriesLine.Get('ALRLIB', 10000), 'Library - No. Series should create a No. Series Line.');
        Assert.IsTrue(NoSeriesLine."Starting No." = 'ALL0000001',
            'Library - No. Series must write the starting no. it was handed.');
        Assert.IsTrue(NoSeriesLine."Ending No." = 'ALL9999999',
            'Library - No. Series must write the ending no. it was handed.');
        Assert.AreEqual(1, NoSeriesLine."Increment-by No.",
            'Library - No. Series must write the increment it was handed.');
    end;

    [Test]
    procedure LibraryNoSeries_DoesNotCreateLinesItWasNotAsked_For()
    var
        NoSeriesLine: Record "No. Series Line";
    begin
        // Negative direction: without a CreateNoSeriesLine call there must be no line, so the
        // positive assertions above cannot be satisfied by a provider that fabricates rows.
        Assert.IsTrue(not NoSeriesLine.Get('ALRNONE', 10000),
            'No. Series Line must not exist for a series the test never created.');
    end;
}
