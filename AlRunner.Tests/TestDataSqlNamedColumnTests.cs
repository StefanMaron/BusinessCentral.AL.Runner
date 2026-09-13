// #2273: the backup reader names a same-app tableextension field's column by its SQL name
// (`Routing No_` for Item."Routing No."). That field is in the run's metatable, so dropping the
// column blanks a value AL reads. Measured on BC 28.1 W1 CRONUS: 11 such columns across Item,
// Purchase Line, Inventory Setup, Cash Flow Setup and No. Series Line, e.g. item SP-BOM2000's
// Routing No. read '' while the backup stores 'SP-BOM2000'. The end-to-end proof is
// tests/test-data-fixture (needs the backup, not run by CI); these pin the mapping itself.
using AlRunner.Patches;
using Xunit;

public class TestDataSqlNamedColumnTests
{
    static IReadOnlySet<string> Fields(params string[] names)
        => new HashSet<string>(names, StringComparer.Ordinal);

    static IReadOnlyDictionary<string, string> Aliases(params (string Field, string Sql)[] fields)
        => RecordPatches.BuildTestDataSqlColumnAliases(
            fields.Select(f => (f.Field, (Func<string?>)(() => f.Sql))));

    [Fact]
    public void ASqlNamedColumnOfAFieldTheTableHasIsMappedOntoThatField()
    {
        var aliases = Aliases(("No.", "No_"), ("Description", "Description"), ("Routing No.", "Routing No_"));
        var plan = RecordPatches.PlanTestDataColumns(
            Fields("No.", "Description", "Routing No."),
            new[] { "No.", "Description", "Routing No_" },
            aliases);

        Assert.Equal(new[] { "No.", "Description" }, plan.Mapped);
        Assert.Equal("Routing No.", Assert.Single(plan.MappedBySqlName, kv => kv.Key == "Routing No_").Value);
        Assert.Empty(plan.NotInThisBuild);
    }

    [Fact]
    public void AColumnMatchingNoFieldsSqlNameIsStillDroppedAndCounted()
    {
        var plan = RecordPatches.PlanTestDataColumns(
            Fields("No.", "Routing No."),
            new[] { "No.", "Gone Field_" },
            Aliases(("No.", "No_"), ("Routing No.", "Routing No_")));

        Assert.Empty(plan.MappedBySqlName);
        Assert.Equal(new[] { "Gone Field_" }, plan.NotInThisBuild);
    }

    [Fact]
    public void AnAlNamedColumnWinsOverTheSqlNamedDuplicate()
    {
        // Both spellings for one field: the AL name is the reader's resolved answer, so the SQL
        // one must not overwrite it, and it is counted rather than silently discarded.
        var plan = RecordPatches.PlanTestDataColumns(
            Fields("Routing No."),
            new[] { "Routing No.", "Routing No_" },
            Aliases(("Routing No.", "Routing No_")));

        Assert.Equal(new[] { "Routing No." }, plan.Mapped);
        Assert.Empty(plan.MappedBySqlName);
        Assert.Equal(new[] { "Routing No_" }, plan.NotInThisBuild);
    }

    [Fact]
    public void ASqlNameTwoFieldsClaimIsNotAssignedToEither()
    {
        var aliases = Aliases(("A.B", "A_B"), ("A/B", "A_B"), ("Routing No.", "Routing No_"));

        Assert.False(aliases.ContainsKey("A_B"));
        Assert.Equal("Routing No.", aliases["Routing No_"]);

        var plan = RecordPatches.PlanTestDataColumns(Fields("A.B", "A/B", "X"), new[] { "X", "A_B" }, aliases);
        Assert.Empty(plan.MappedBySqlName);
        Assert.Equal(new[] { "A_B" }, plan.NotInThisBuild);
    }

    [Fact]
    public void AFieldWhoseSqlNameCannotBeReadContributesNoAlias()
    {
        var aliases = RecordPatches.BuildTestDataSqlColumnAliases(new (string, Func<string?>)[]
        {
            ("Broken.", () => throw new NullReferenceException("parent")),
            ("Routing No.", () => "Routing No_"),
        });

        Assert.Equal(new[] { "Routing No_" }, aliases.Keys);
    }

    [Fact]
    public void ARowOfOnlySqlNamedColumnsCanHydrate()
    {
        var plan = RecordPatches.PlanTestDataColumns(
            Fields("Routing No."), new[] { "Routing No_" }, Aliases(("Routing No.", "Routing No_")));

        Assert.True(plan.CanHydrate);
    }
}
