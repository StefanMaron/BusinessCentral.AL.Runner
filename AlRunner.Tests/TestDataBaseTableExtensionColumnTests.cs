// #5385: a raw `<sql>$<app id>` column is judged against the run's closure. BC 29 stores
// table-extension fields as columns of the BASE table under that name (BC 28: in the `$ext`
// companion), so it turns up for apps that ARE installed — Base Application's own extensions of
// Business Foundation / System tables — whenever the reader did not map it to an AL name (reader
// v0.1.2 never did on a BC 29 backup). Counting those as "an app this run does not install"
// dropped every table-extension value and said the app was missing.
//
// Measured on the BC 29.0.54011.55816 W1 backup: reader 0.1.2 returns
// `Default Location Code$437dbf0e-84ff-417a-965d-ed2bb9650972` for Return Reason, and
// `Sales$437dbf0e-…` for Source Code Setup (the end-to-end proof is
// tests/test-data-fixture/TestDataExtensionFields, which needs the backup and is not run by CI).
// These pin the plan itself.
using AlRunner.Patches;
using Xunit;

public class TestDataBaseTableExtensionColumnTests
{
    const string BaseApp = "437dbf0e-84ff-417a-965d-ed2bb9650972";
    const string Withholding = "c31ee575-3fc7-4388-98ee-d75aa2fc5f87";

    static IReadOnlySet<string> Fields(params string[] names)
        => new HashSet<string>(names, StringComparer.Ordinal);

    static IReadOnlySet<Guid> Installed(params string[] ids)
        => new HashSet<Guid>(ids.Select(Guid.Parse));

    static IReadOnlyDictionary<string, string> Aliases(params (string Field, string Sql)[] fields)
        => RecordPatches.BuildTestDataSqlColumnAliases(
            fields.Select(f => (f.Field, (Func<string?>)(() => f.Sql))));

    [Fact]
    public void AnInstalledAppsColumnIsMappedOntoItsFieldNotCountedAsUninstalled()
    {
        var plan = RecordPatches.PlanTestDataColumns(
            Fields("Code", "Description", "Default Location Code", "Inventory Value Zero"),
            new[] { "Code", "Description", $"Default Location Code${BaseApp}", $"Inventory Value Zero${BaseApp}" },
            Aliases(),
            Installed(BaseApp));

        Assert.Equal(new[] { "Code", "Description" }, plan.Mapped);
        Assert.Empty(plan.FromUninstalledApps);
        Assert.Empty(plan.NotInThisBuild);
        // Each raw column lands on ITS OWN field, not on a neighbour's.
        Assert.Equal("Default Location Code", plan.MappedBySqlName[$"Default Location Code${BaseApp}"]);
        Assert.Equal("Inventory Value Zero", plan.MappedBySqlName[$"Inventory Value Zero${BaseApp}"]);
        Assert.Equal(2, plan.MappedBySqlName.Count);
    }

    [Fact]
    public void AnInstalledAppsColumnWhoseSqlNameDiffersFromTheFieldNameIsMappedByAlias()
    {
        var plan = RecordPatches.PlanTestDataColumns(
            Fields("No.", "Routing No."),
            new[] { "No.", $"Routing No_${BaseApp}" },
            Aliases(("Routing No.", "Routing No_")),
            Installed(BaseApp));

        Assert.Equal("Routing No.", plan.MappedBySqlName[$"Routing No_${BaseApp}"]);
        Assert.Empty(plan.FromUninstalledApps);
        Assert.Empty(plan.NotInThisBuild);
    }

    [Fact]
    public void AnInstalledAppsColumnTheTableHasNoFieldForIsAbsentFromThisBuildNotUninstalled()
    {
        var plan = RecordPatches.PlanTestDataColumns(
            Fields("Code"),
            new[] { "Code", $"Removed Field${BaseApp}" },
            Aliases(),
            Installed(BaseApp));

        Assert.Equal(new[] { $"Removed Field${BaseApp}" }, plan.NotInThisBuild);
        Assert.Empty(plan.FromUninstalledApps);
        Assert.Empty(plan.MappedBySqlName);
        Assert.True(plan.CanHydrate);
    }

    [Fact]
    public void AnUninstalledAppsColumnIsStillCountedAsUninstalledNextToAnInstalledOnes()
    {
        var plan = RecordPatches.PlanTestDataColumns(
            Fields("Code", "Sales"),
            new[] { "Code", $"Sales${BaseApp}", $"WHT Print Dialog${Withholding}" },
            Aliases(),
            Installed(BaseApp));

        Assert.Equal("Sales", plan.MappedBySqlName[$"Sales${BaseApp}"]);
        Assert.Equal(new[] { $"WHT Print Dialog${Withholding}" }, plan.FromUninstalledApps);
        Assert.Empty(plan.NotInThisBuild);
    }

    [Fact]
    public void AnUninstalledAppsColumnIsNotMappedEvenWhenAFieldOfThatNameExists()
    {
        // The app id decides, not the name: this field belongs to a table of the closure and the
        // column to an app outside it, so the column is not this field's value.
        var plan = RecordPatches.PlanTestDataColumns(
            Fields("Code", "Sales"),
            new[] { "Code", $"Sales${Withholding}" },
            Aliases(),
            Installed(BaseApp));

        Assert.Empty(plan.MappedBySqlName);
        Assert.Equal(new[] { $"Sales${Withholding}" }, plan.FromUninstalledApps);
    }

    [Fact]
    public void ARawColumnDoesNotOverwriteTheAlNamedColumnOfTheSameField()
    {
        var plan = RecordPatches.PlanTestDataColumns(
            Fields("Code", "Sales"),
            new[] { "Code", "Sales", $"Sales${BaseApp}" },
            Aliases(),
            Installed(BaseApp));

        Assert.Equal(new[] { "Code", "Sales" }, plan.Mapped);
        Assert.Empty(plan.MappedBySqlName);
        Assert.Equal(new[] { $"Sales${BaseApp}" }, plan.NotInThisBuild);
    }

    [Fact]
    public void AnUnknownClosureKeepsEveryRawColumnAsUninstalled()
    {
        // Nothing says which apps are installed, so nothing can be called installed: the
        // pre-#5385 answer, for the callers that pass no closure.
        var columns = new[] { "Code", $"Sales${BaseApp}" };
        foreach (var closure in new IReadOnlySet<Guid>?[] { null, Installed() })
        {
            var plan = RecordPatches.PlanTestDataColumns(
                Fields("Code", "Sales"), columns, Aliases(), closure);

            Assert.Empty(plan.MappedBySqlName);
            Assert.Equal(new[] { $"Sales${BaseApp}" }, plan.FromUninstalledApps);
        }
    }

    [Fact]
    public void ARowOfOnlyAnInstalledAppsRawColumnsCanHydrate()
    {
        var plan = RecordPatches.PlanTestDataColumns(
            Fields("Sales"), new[] { $"Sales${BaseApp}" }, Aliases(), Installed(BaseApp));

        Assert.True(plan.CanHydrate);
    }
}
