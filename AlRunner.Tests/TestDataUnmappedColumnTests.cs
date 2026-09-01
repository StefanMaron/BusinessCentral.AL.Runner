// A backup column that names no field of the target table must cost that COLUMN, not the
// whole table (#2273 / #2301).
//
// Measured on BC 28.1 W1 CRONUS, running Microsoft's Tests-SINGLESERVER app: table 309
// "No. Series Line" refused over `Allow Gaps in Nos.`, so the runner saw ZERO No. Series
// Line rows for every series, and ~220 of the bucket's tests failed with "You cannot assign
// new numbers from the number series <X>" — an error AL raises when a series has no lines at
// all. The real backup's CONT line is CT000001..CT100000 with 23 used.
//
// `Allow Gaps in Nos.` is ObsoleteState = Removed / ObsoleteTag '27.0' (Business Foundation's
// NoSeriesLineObsolete.TableExt.al, behind `#if not CLEANSCHEMA27`). The shipped
// SymbolReference.json still declares it, so the reader names it, while the compiled app this
// runner loads has no such field. A column absent from the target NCLMetaTable cannot be read
// by ANY AL code in this run — it is not addressable — so dropping it hides nothing a test
// could observe, while refusing the table hands AL an empty table it silently believes.
using AlRunner.Patches;
using Xunit;

public class TestDataUnmappedColumnTests
{
    static IReadOnlySet<string> Fields(params string[] names)
        => new HashSet<string>(names, StringComparer.Ordinal);

    [Fact]
    public void ColumnsThatAreFieldsAreMapped()
    {
        var plan = RecordPatches.PlanTestDataColumns(
            Fields("Series Code", "Line No.", "Starting No."),
            new[] { "Series Code", "Line No.", "Starting No." });

        Assert.Equal(new[] { "Series Code", "Line No.", "Starting No." }, plan.Mapped);
        Assert.Empty(plan.NotInThisBuild);
        Assert.Empty(plan.FromUninstalledApps);
    }

    [Fact]
    public void AColumnThisBuildHasNoFieldForIsDroppedNotRefused()
    {
        var plan = RecordPatches.PlanTestDataColumns(
            Fields("Series Code", "Line No.", "Starting No."),
            new[] { "Series Code", "Line No.", "Starting No.", "Allow Gaps in Nos." });

        // The point of the test: the other three still hydrate.
        Assert.Equal(new[] { "Series Code", "Line No.", "Starting No." }, plan.Mapped);
        Assert.Equal(new[] { "Allow Gaps in Nos." }, plan.NotInThisBuild);
        Assert.True(plan.CanHydrate);
    }

    [Fact]
    public void ACompanionColumnOfAnUninstalledAppIsCountedSeparately()
    {
        // The pre-existing case (#2261): BC's raw storage form for an app outside this run's
        // closure. It stays a distinct count because it means something different — the app
        // is not here — rather than "this build's table has no such field".
        var plan = RecordPatches.PlanTestDataColumns(
            Fields("No."),
            new[] { "No.", "Sust_ Cert_ No_$b3780cd9-f8f8-4a83-a4d5-0c2ad87b28af" });

        Assert.Equal(new[] { "No." }, plan.Mapped);
        Assert.Equal(new[] { "Sust_ Cert_ No_$b3780cd9-f8f8-4a83-a4d5-0c2ad87b28af" },
            plan.FromUninstalledApps);
        Assert.Empty(plan.NotInThisBuild);
    }

    [Fact]
    public void ARowSharingNoColumnWithTheTableStillRefuses()
    {
        // The guard the old refusal existed for: a row whose shape has nothing to do with
        // this table is a mismatch, not a dropped field, and hydrating it would fabricate
        // rows of defaults. Dropping every column would do exactly that silently.
        var plan = RecordPatches.PlanTestDataColumns(
            Fields("Series Code", "Line No."),
            new[] { "Customer No.", "Posting Date" });

        Assert.Empty(plan.Mapped);
        Assert.False(plan.CanHydrate);
    }

    [Fact]
    public void NoColumnsAtAllIsNotAMismatch()
    {
        // An empty column list is "nothing to do", not a shape mismatch — it must not be
        // reported as one.
        var plan = RecordPatches.PlanTestDataColumns(Fields("Series Code"), Array.Empty<string>());

        Assert.Empty(plan.Mapped);
        Assert.True(plan.CanHydrate);
    }
}
