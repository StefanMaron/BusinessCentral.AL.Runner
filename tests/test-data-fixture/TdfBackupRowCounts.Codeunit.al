/// <summary>
/// The Country/Region row count the backup holds, remembered across the two lazy-load test
/// codeunits (64404, 64405). The count is demo-data build state (139 on the 28.1 W1 backup,
/// 247 on 28.4; #4645), so it cannot be a literal. Whichever codeunit runs first reads the
/// freshly loaded table and records its count; the one that runs second, after the
/// codeunit-boundary restore, must see the same count.
///
/// SingleInstance, because the database is restored at the boundary and this value must not be.
/// </summary>
codeunit 64411 "TDF Backup Row Counts"
{
    SingleInstance = true;

    var
        Assert: Codeunit "TDF Assert";
        CountryRegionCount: Integer;

    /// <summary>Record the first count seen; every later one must equal it.</summary>
    procedure CheckCountryRegionCount(Actual: Integer)
    begin
        Assert.IsTrue(Actual > 0, 'the backup holds Country/Region rows, so the hydrated table must not be empty');
        if CountryRegionCount = 0 then begin
            CountryRegionCount := Actual;
            exit;
        end;
        Assert.AreEqual(CountryRegionCount, Actual,
            'every Country/Region row the backup holds, as counted on first load, must be back after the boundary restore');
    end;

    procedure RecordedCountryRegionCount(): Integer
    begin
        exit(CountryRegionCount);
    end;
}
