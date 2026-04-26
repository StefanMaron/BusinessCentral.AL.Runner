codeunit 59381 "RV Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "RV Src";

    [Test]
    procedure LastUsedRowVersion_IsNonNegative()
    begin
        // Positive: without a real DB the stub must still produce a non-negative
        // BigInteger — a negative value would indicate an unwired return.
        Assert.IsTrue(Src.LastUsedNonNegative(),
            'Database.LastUsedRowVersion must be non-negative');
    end;

    [Test]
    procedure MinimumActiveRowVersion_IsNonNegative()
    begin
        Assert.IsTrue(Src.MinActiveNonNegative(),
            'Database.MinimumActiveRowVersion must be non-negative');
    end;

    [Test]
    procedure LastUsedRowVersion_ReadCompletes()
    var
        rv: BigInteger;
    begin
        // Negative: guard against a throwing stub — just reading into a variable
        // must complete successfully.
        rv := Src.GetLastUsedRowVersion();
        Assert.IsTrue(rv >= 0, 'Read must complete and return a non-negative BigInteger');
    end;

    [Test]
    procedure MinimumActiveRowVersion_ReadCompletes()
    var
        rv: BigInteger;
    begin
        rv := Src.GetMinimumActiveRowVersion();
        Assert.IsTrue(rv >= 0, 'Read must complete and return a non-negative BigInteger');
    end;

    [Test]
    procedure RowVersions_InvariantRelationship()
    var
        lastUsed: BigInteger;
        minActive: BigInteger;
    begin
        // In BC, MinimumActiveRowVersion <= LastUsedRowVersion is the real invariant
        // (min active version can never exceed the highest version ever used).
        // Under stubs both are zero so the relation holds trivially; this test
        // locks the invariant in case future stubs return non-zero values.
        lastUsed := Src.GetLastUsedRowVersion();
        minActive := Src.GetMinimumActiveRowVersion();
        Assert.IsTrue(minActive <= lastUsed,
            'MinimumActiveRowVersion must be <= LastUsedRowVersion');
    end;
}
