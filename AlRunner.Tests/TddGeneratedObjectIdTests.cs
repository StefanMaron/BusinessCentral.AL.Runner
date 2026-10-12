// #5498: every object --tdd generates (a codeunit, a table, an enum, a stub codeunit beside a package's codeunit, a tableextension and an
// enumextension beside a package's table and enum) takes the first id of the app's idRanges that NO module of the compile uses for that kind of
// object, not only the first one the app's own files leave free. Here a package holds one object of every one of those kinds on 65400, the first id
// of the test app's range (an app and its test app sharing a range, the PTE case). Runner-specific (a --tdd choice of id), so it lives here, not in
// the al-language corpus. The design: docs/tdd-missing-object.md and docs/tdd-precompiled.md.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TddGeneratedObjectIdTests : IDisposable
{
    private static readonly string FixtureRoot = Path.Combine(TddMissingObjectTests.FixtureRoot, "..", "TddGeneratedObjectId");
    private const string PkgAppId = "7b2c3d4e-a516-40c7-b138-f294a5b6c267";

    private const string PkgSymbols = """
        {"Codeunits":[{"Id":65400,"Name":"Idp Calc","Methods":[
          {"Id":1,"Name":"Existing","ReturnTypeDefinition":{"Name":"Integer"},"Properties":[]}
        ],"ReferenceSourceFileName":"Idp.al","Properties":[]}],
        "Tables":[{"Id":65400,"Name":"Idp Entry","Fields":[
          {"Id":1,"Name":"Id","TypeDefinition":{"Name":"Integer"},"Properties":[]}
        ],"Keys":[{"Name":"PK","FieldNames":["Id"],"Properties":[{"Name":"Clustered","Value":"1"}]}],
        "ReferenceSourceFileName":"Idp.al","Properties":[]}],
        "EnumTypes":[{"Id":65400,"Name":"Idp Tier","Values":[{"Ordinal":0,"Name":"Bronze","Properties":[]}],
        "ReferenceSourceFileName":"Idp.al","Properties":[{"Name":"Extensible","Value":"true"}]}],
        "TableExtensions":[{"Id":65400,"Name":"Idp Entry Ext","TargetObject":"Idp Entry","Fields":[
          {"Id":65400,"Name":"Idp Ext Field","TypeDefinition":{"Name":"Integer"},"Properties":[]}
        ],"Properties":[]}],
        "EnumExtensionTypes":[{"Id":65400,"Name":"Idp Tier Ext","TargetObject":"Idp Tier","Values":[
          {"Ordinal":65400,"Name":"Idp Ext Value","Properties":[]}
        ],"Properties":[]}],
        "AppId":"@APPID@","Name":"Tdd Generated Object Id Pkg","Publisher":"AL Runner Fixtures","Version":"1.0.0.0"}
        """;

    private readonly string _scratch;

    public TddGeneratedObjectIdTests()
    {
        _scratch = TestScratch.Dir("al-runner-tdd-generated-object-id");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>
    /// Six tests each need one generated object, and the package holds that kind's id 65400: each object takes 65401 instead (the test app's own
    /// codeunits hold 65410-65415 and are skipped as before), the compile accepts it (no AL0264), and every test runs against it and names it.
    /// </summary>
    [SkippableFact]
    public void GeneratedObjects_SkipTheIdsAPackageHolds()
    {
        TestArtifacts.SkipIfMissing();
        var packages = Path.Combine(_scratch, "packages");
        Directory.CreateDirectory(packages);
        File.WriteAllBytes(Path.Combine(packages, "AL_Runner_Fixtures_Tdd_Generated_Object_Id_Pkg_1.0.0.0.app"),
            TddPackageMemberTests.BuildApp(PkgAppId, "Tdd Generated Object Id Pkg", 65400, 65404, PkgSymbols, Path.Combine(FixtureRoot, "pkgsrc")));
        var test = TddMissingObjectTests.CopyFolder(Path.Combine(FixtureRoot, "test"), Path.Combine(_scratch, "test"));

        var (stdout, stderr, exit) = TddMissingObjectTests.RunRunner(null, "--tdd", $"--cache \"{Path.Combine(_scratch, "cache")}\"",
            "--output-json", $"--package-cache \"{packages}\"", $"\"{test}\"");

        Assert.DoesNotContain("AL0264", stdout + stderr);
        Assert.True(exit == 0, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();
        Assert.Equal(6, tests.Count);
        JsonElement Find(string name) => tests.Single(t => t.GetProperty("name").GetString()!.EndsWith("." + name));
        void AssertRuns(string name, params string[] stubs)
        {
            var t = Find(name);
            Assert.True(t.GetProperty("status").GetString() == "pass", $"{name}: {t}");
            Assert.Equal(stubs.OrderBy(x => x, StringComparer.Ordinal), TddMissingObjectTests.StubsOf(t).OrderBy(x => x, StringComparer.Ordinal));
        }
        AssertRuns("MissingCodeunit_TakesAFreeId", "Idp Missing Codeunit: codeunit 65401", "Idp Missing Codeunit: procedure \"Calc\"(Arg1: Integer): Integer");
        AssertRuns("MissingTable_TakesAFreeId", "Idp Missing Table: table 65401");
        AssertRuns("MissingEnum_TakesAFreeId", "Idp Missing Enum: enum 65401");
        AssertRuns("StubBesidePackageCodeunit_TakesAFreeId", "Idp Calc: procedure \"Missing\"(Arg1: Integer): Integer");
        AssertRuns("FieldBesidePackageTable_TakesAFreeId", "Idp Entry: field \"Points\": Integer");
        AssertRuns("ValueBesidePackageEnum_TakesAFreeId", "Idp Tier: enum value \"Gold\" = 65401");

        var lines = stderr.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains(lines, l => l.StartsWith("--tdd: stub codeunit \"TDD Stub 65402\" beside precompiled Idp Calc"));
        Assert.Contains(lines, l => l.StartsWith("--tdd: table extension \"TDD Table Extension 65401\" (id 65401) beside package table Idp Entry"));
        Assert.Contains(lines, l => l.StartsWith("--tdd: enum extension \"TDD Enum Extension 65401\" (id 65401) beside package enum Idp Tier"));
        Assert.Contains(lines, l => l.StartsWith("--tdd: generated codeunit \"Idp Missing Codeunit\" (id 65401)"));
        Assert.Contains(lines, l => l.StartsWith("--tdd: generated table \"Idp Missing Table\" (id 65401)"));
        Assert.Contains(lines, l => l.StartsWith("--tdd: generated enum \"Idp Missing Enum\" (id 65401)"));
    }
}
