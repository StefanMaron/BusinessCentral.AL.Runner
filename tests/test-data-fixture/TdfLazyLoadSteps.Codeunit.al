/// <summary>
/// The shared body of issue #2262's boundary proof, called identically by BOTH test codeunits
/// (64404 and 64405). It is one procedure rather than a handful because the claim spans a
/// codeunit boundary and must not also depend on the order of tests INSIDE a codeunit.
///
/// WHY BOTH CODEUNITS RUN THE SAME STEPS
///   The runner does not run test codeunits in object-id order — measured: this bundle runs
///   64400, 64402, 64405, 64403, 64404. So a design where one codeunit dirties the database
///   and a "later" one checks the restore proves nothing, because the checker may run first
///   and pass trivially against a freshly loaded table.
///
///   Making the two codeunits symmetric removes the dependency entirely: each one first
///   ASSERTS the pristine state and then DIRTIES it. Whichever of the two runs second is, by
///   construction, asserting after a codeunit-boundary restore of the other's real damage —
///   a modified row, a deleted row and an inserted row — no matter which order they run in.
///
/// WHAT THE DAMAGE CATCHES
///   "Country/Region" (table 9) and "Shipping Agent" (table 291) are loaded ON DEMAND, in the
///   middle of a test body, long after CaptureInstallBaselineSnapshot() walked the store.
///   Nothing about them is in any snapshot unless the loader wrote it there
///   (RecordPatches.AppendBaselineTable). The three assertions fail differently:
///     - a baseline that never received the table          -> 0 rows, Get('GB') false
///     - a baseline that aliased the live rows             -> GB reads 'MUTATED BY THE TEST'
///     - a baseline that recorded the damaged rows, not the loaded ones -> US missing, ZZ present
///     - a baseline that recorded some rows but not all of them -> a sampled code is missing
///   No assertion names a row COUNT: the backup's count differs per build (139 on the 28.1-28.3
///   W1 backups, 247 on 28.4; #4772), and remembering the first codeunit's count for the second
///   needs SingleInstance state across test codeunits, which the runner resets (#4781).
/// </summary>
codeunit 64406 "TDF Lazy Load Steps"
{
    var
        Assert: Codeunit "TDF Assert";

    procedure AssertPristineThenDirty()
    begin
        AssertHydratedRowsArePristine();
        AssertOnDemandLoadingInventsNothing();
        AssertAWriteTriggersTheLoadToo();
        DirtyTheHydratedTables();
    end;

    /// <summary>
    /// Concrete values, not "some rows exist": an implementation that materialised blank
    /// rows, or that restored the table but not its contents, fails here. Run second (which
    /// one of the two calling codeunits always is), every one of these is an assertion about
    /// what the boundary restore put back.
    /// </summary>
    local procedure AssertHydratedRowsArePristine()
    var
        CountryRegion: Record "Country/Region";
        ShippingAgent: Record "Shipping Agent";
    begin
        Assert.IsTrue(CountryRegion.Get('GB'), 'Country/Region GB must be present');
        Assert.AreEqual('Great Britain', CountryRegion.Name, 'GB Name is the backup value, not a mutation');
        Assert.AreEqual('GB', CountryRegion."ISO Code", 'GB ISO Code');

        // A second row with a different Name, so a load that read one row and copied it
        // would still fail. It is also the row the other codeunit DELETES.
        Assert.IsTrue(CountryRegion.Get('US'), 'Country/Region US must be present');
        Assert.AreEqual('USA', CountryRegion.Name, 'US Name');

        // A third with a non-blank EU code, so "every field after the key is blank" fails too.
        Assert.IsTrue(CountryRegion.Get('DE'), 'Country/Region DE must be present');
        Assert.AreEqual('DE', CountryRegion."EU Country/Region Code", 'DE EU Country/Region Code');

        AssertSampledRowsArePresent();

        // A row a test INSERTED is not part of the install baseline and must not survive the
        // boundary, while the backup's own rows must. A restore that simply left the previous
        // codeunit's store alone would pass every assertion above and fail this one.
        Assert.IsFalse(ShippingAgent.Get('ZZTEST'), 'a row a previous test inserted must not survive the boundary');
        Assert.IsTrue(ShippingAgent.Get('FEDEX'), 'the backup rows must still be there');
        Assert.AreEqual('Federal Express Corporation', ShippingAgent.Name, 'FEDEX Name from the backup');
    end;

    /// <summary>
    /// Stands in for the exact row count, which differs per backup build (#4772): a baseline
    /// that restored only part of the table fails here unless the part it kept happens to hold
    /// every one of these. Codes spread across the key range, each present in the 27.5, 28.1
    /// and 28.4 W1 backups (read with `bcbak read --table "Country/Region"`).
    /// </summary>
    local procedure AssertSampledRowsArePresent()
    var
        CountryRegion: Record "Country/Region";
        Codes: List of [Code[10]];
        CountryCode: Code[10];
    begin
        Codes.AddRange('AT', 'BE', 'CA', 'CH', 'DK', 'ES', 'FI', 'FR', 'IT', 'JP', 'NL', 'NO', 'PL', 'SE', 'ZA');
        foreach CountryCode in Codes do
            Assert.IsTrue(CountryRegion.Get(CountryCode), StrSubstNo('Country/Region %1 must be present', CountryCode));
    end;

    /// <summary>The negative case: on-demand loading must not invent rows. Without it, an
    /// implementation that pre-created every key would satisfy the positives above.</summary>
    local procedure AssertOnDemandLoadingInventsNothing()
    var
        CountryRegion: Record "Country/Region";
    begin
        Assert.IsFalse(CountryRegion.Get('ZZ'), 'on-demand loading must not invent rows');

        // The raising form must still raise: a load that pre-created every key would satisfy
        // IsFalse above only by accident.
        asserterror CountryRegion.Get('ZZ', true);
        Assert.ExpectedError('Country/Region');
    end;

    /// <summary>
    /// THE case a load-on-read design gets silently wrong. On the codeunit that runs first,
    /// the very first thing this run ever does with "Shipping Agent" is an INSERT, and the
    /// primary key it inserts is one the backup already holds. Real BC raises a duplicate-key
    /// error, because the row is there. A load hooked only onto the read path would find an
    /// empty table, accept the Insert, and this would pass for the wrong reason with the
    /// backup's row silently gone.
    ///
    /// Hooking GetDataAccessForTableCore — the one place a table's storage is materialised —
    /// is what makes reads and writes equally covered.
    /// </summary>
    local procedure AssertAWriteTriggersTheLoadToo()
    var
        ShippingAgent: Record "Shipping Agent";
    begin
        ShippingAgent.Init();
        ShippingAgent.Code := 'DHL';
        ShippingAgent.Name := 'Not the backup value';
        asserterror ShippingAgent.Insert();
        Assert.ExpectedError('already exists');

        // The collision came from the BACKUP's row and not from some other cause: the row is
        // readable and carries the backup's value, not the one the failed Insert carried.
        Assert.IsTrue(ShippingAgent.Get('DHL'), 'the backup row is what the Insert collided with');
        Assert.AreEqual('DHL Systems, Inc.', ShippingAgent.Name, 'DHL Name from the backup');

        // The positive direction: a key the backup does NOT hold inserts fine, so the
        // collision above is about that key and not about the table refusing writes.
        ShippingAgent.Init();
        ShippingAgent.Code := 'ZZTEST';
        ShippingAgent.Name := 'Inserted by the test';
        ShippingAgent.Insert();
        Assert.IsTrue(ShippingAgent.Get('ZZTEST'), 'a key the backup does not hold must insert');
    end;

    /// <summary>
    /// Leave real damage for the other codeunit to find repaired. Asserting the writes took
    /// effect HERE is what makes that meaningful — a no-op Modify would leave the other
    /// codeunit reading a clean table and calling it a restore.
    /// </summary>
    local procedure DirtyTheHydratedTables()
    var
        CountryRegion: Record "Country/Region";
        LoadedCount: Integer;
    begin
        LoadedCount := CountryRegion.Count();

        CountryRegion.Get('GB');
        CountryRegion.Name := 'MUTATED BY THE TEST';
        CountryRegion.Modify();

        CountryRegion.Get('US');
        CountryRegion.Delete();

        Assert.IsTrue(CountryRegion.Get('GB'), 'GB still exists after the modify');
        Assert.AreEqual('MUTATED BY THE TEST', CountryRegion.Name, 'GB Name was really modified');
        Assert.IsFalse(CountryRegion.Get('US'), 'US was really deleted');
        Assert.AreEqual(LoadedCount - 1, CountryRegion.Count(), 'one row fewer after the delete');

        // A row the backup does not hold, on the lazily loaded table itself: the other
        // codeunit's AssertOnDemandLoadingInventsNothing then asserts the restore removed it.
        CountryRegion.Init();
        CountryRegion.Code := 'ZZ';
        CountryRegion.Name := 'Inserted by the test';
        CountryRegion.Insert();
        Assert.IsTrue(CountryRegion.Get('ZZ'), 'ZZ was really inserted');
        Assert.AreEqual(LoadedCount, CountryRegion.Count(), 'the insert brings the count back');
    end;
}
