// CompanyRenameCascadeBindingTests — issue #5071.
//
// A RUNNER-MECHANISM test. The BC-observable claim (renaming a Company re-keys a
// TableRelation = Company.Name field in a per-company table, and leaves a row naming another
// company alone) is measured upstream by corpus codeunit 67959 on a real service tier.
//
// What is pinned HERE is the runner's wiring:
//   - the two Ncl edits that let BC's company token table hand out a token for a second company
//     (the tenant-database guard and the SQL spelling lookup);
//   - that the per-company rename update builds its record the way BC does. Until #5349 it went
//     through a scope that granted every Company-row company for the length of the construction,
//     because the record store was not partitioned per company; every record on another company
//     now reads that company's own store (RecordPatches.CompanyStores.cs), so the scope is gone
//     and the validator grants the Company-row companies everywhere.
using AlRunner.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class CompanyRenameCascadeBindingTests
{
    private const string CompanyTokensType = "Microsoft.Dynamics.Nav.Runtime.CompanyTokens";

    private static string RewrittenNclPath => Path.Combine(
        Path.GetDirectoryName(typeof(CompanyRenameCascadeBindingTests).Assembly.Location)
            ?? AppContext.BaseDirectory,
        "Microsoft.Dynamics.Nav.Ncl.dll");

    private static ModuleDefinition OpenNcl()
    {
        Skip.IfNot(File.Exists(RewrittenNclPath), $"the Ncl is not present at '{RewrittenNclPath}'.");
        var module = ModuleDefinition.ReadModule(RewrittenNclPath);
        NclRewriteMarker.SkipUnlessRewritten(module, RewrittenNclPath);
        return module;
    }

    private static List<string> Called(MethodDefinition method)
        => method.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => (i.Operand as MethodReference)?.FullName ?? string.Empty)
            .ToList();

    [SkippableFact]
    public void CompanyTokensGet_AsksTheRunnerForTheDatabaseType_NotTheUninitialisedSkeleton()
    {
        using var module = OpenNcl();
        var get = module.GetType(CompanyTokensType)!.Methods.Single(m =>
            m.Name == "Get" && m.HasThis && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.String");
        var called = Called(get);

        Assert.Single(called, n => n.Contains("CompanyAccessPatches::TenantDatabaseType"));
        Assert.DoesNotContain(called, n => n.Contains("NavDatabase::get_DatabaseType"));
    }

    [SkippableFact]
    public void CompanyTokensSpellingLookup_IsTheRunnersHelper_NotASqlQuery()
    {
        using var module = OpenNcl();
        var lookup = module.GetType(CompanyTokensType)!.Methods.Single(m => m.Name == "GetCollationAwareCompanyName");
        var called = Called(lookup);

        Assert.Equal(
            new[] { "System.String AlRunner.Patches.CompanyAccessPatches::CompanyTokens_GetCollationAwareCompanyName(System.Object,System.String)" },
            called);
    }

    [SkippableFact]
    public void PerCompanyRenameUpdate_BuildsItsRecordWithBcsOwnConstruction()
    {
        using var module = OpenNcl();
        var machine = module.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord")!.NestedTypes.Single(t =>
            t.Name.StartsWith("<UpdateReferencingTableOnRenameAsync>d__") && t.Fields.Any(f => f.Name == "companyName"));
        var called = Called(machine.Methods.Single(m => m.Name == "MoveNext"));

        Assert.Single(called, n => n.Contains("NCLMetaTable::CreateObjectInstance"));
        Assert.DoesNotContain(called, n => n.Contains("CompanyAccessPatches"));
    }

    // The session company and the blank name are answered without looking for a Company table: the
    // fake session has none behind it, so a path that went looking for one would throw. The grant of
    // a company with a Company row needs BC's own GetAllCompaniesAsync over a real session, and is
    // measured end to end by corpus codeunits 69970 to 69975 and tests/runner-extras/company-store-isolation.
    [Fact]
    public void Validate_TheSessionCompanyAndTheBlankName_AreAnsweredWithoutACompanyTable()
    {
        var session = new FakeSession("My Company");

        Assert.True(CompanyAccessPatches.CompanyHelper_ValidateUserHasAccessToCompany(session, "My Company", out var own));
        Assert.Equal(string.Empty, own);

        Assert.True(CompanyAccessPatches.CompanyHelper_ValidateUserHasAccessToCompany(session, "my company", out var ownLower));
        Assert.Equal(string.Empty, ownLower);

        Assert.True(CompanyAccessPatches.CompanyHelper_ValidateUserHasAccessToCompany(session, "", out var empty));
        Assert.Equal(string.Empty, empty);
    }

    [Fact]
    public void TokenHelpers_AnswerWhatTheTokenTableNeeds()
    {
        Assert.Equal(Microsoft.Dynamics.Nav.Runtime.NavDatabaseType.Tenant, CompanyAccessPatches.TenantDatabaseType(null));
        Assert.Equal("ALT RENCASC CO2", CompanyAccessPatches.CompanyTokens_GetCollationAwareCompanyName(new object(), "ALT RENCASC CO2"));
    }

    private sealed class FakeSession
    {
        public FakeSession(string companyName) => Company = new FakeCompany(companyName);
        public FakeCompany Company { get; }
    }

    private sealed class FakeCompany
    {
#pragma warning disable CS0169, CS0649, IDE0044
        private readonly string companyName;
#pragma warning restore CS0169, CS0649, IDE0044
        public FakeCompany(string name) => companyName = name;
    }
}
