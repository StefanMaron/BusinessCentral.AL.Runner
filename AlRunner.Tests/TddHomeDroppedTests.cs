// #5493: the closing list of a --tdd run (`--tdd: generated N member(s) this run:`) names only members whose home file the compile kept.
// A member generated into a file the compile then drops is declared by nothing, and the list must not claim it. Three shapes, each a
// different path to the same list: a procedure in a source codeunit, a package table's field in a bundle other bundles depend on,
// and a package table's field in a test app whose only file is dropped. Runner-specific (the report --tdd prints), so it lives here,
// not in the al-language corpus. The design: docs/tdd-missing-object.md and docs/tdd-precompiled.md.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TddHomeDroppedTests : IDisposable
{
    private static readonly string FixtureRoot = Path.Combine(TddMissingObjectTests.FixtureRoot, "..", "TddHomeDropped");
    private readonly string _scratch;

    public TddHomeDroppedTests()
    {
        _scratch = TestScratch.Dir("al-runner-tdd-home-dropped");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private const string PkgAppId = "5f1a2b3c-8394-4ea5-9f16-d0728293a045";

    private const string PkgSymbols = """
        {"Codeunits":[{"Id":65390,"Name":"Pkg Calc","Methods":[
          {"Id":1,"Name":"Existing","ReturnTypeDefinition":{"Name":"Integer"},"Properties":[]}
        ],"ReferenceSourceFileName":"PkgCalc.Codeunit.al","Properties":[]}],
        "AppId":"@APPID@","Name":"Tdd Home Dropped Pkg","Publisher":"AL Runner Fixtures","Version":"1.0.0.0"}
        """;

    /// <summary>The packages the shapes read: table 65380 "Loyalty Entry" with the fields "Id" and "Existing Points" (the field shapes), and
    /// codeunit 65390 "Pkg Calc" with the procedure "Existing" (the stub shape), each carrying its own source.</summary>
    private string PackageCache()
    {
        var packages = Path.Combine(_scratch, "packages");
        Directory.CreateDirectory(packages);
        File.WriteAllBytes(Path.Combine(packages, TddPackageMemberTests.DepFileStem + ".app"),
            TddPackageMemberTests.BuildApp(TddPackageMemberTests.DepAppId, "Tdd Package Members Dep", 65380, 65389,
                TddPackageMemberTests.DepSymbols, Path.Combine(TddPackageMemberTests.FixtureRoot, "dep")));
        File.WriteAllBytes(Path.Combine(packages, "AL_Runner_Fixtures_Tdd_Home_Dropped_Pkg_1.0.0.0.app"),
            TddPackageMemberTests.BuildApp(PkgAppId, "Tdd Home Dropped Pkg", 65390, 65394, PkgSymbols, Path.Combine(FixtureRoot, "pkgsrc")));
        return packages;
    }

    private (string StdOut, string StdErr, int Exit) Run(string name, params string[] bundles)
    {
        var copies = bundles.Select(b => $"\"{TddMissingObjectTests.CopyFolder(Path.Combine(FixtureRoot, b), Path.Combine(_scratch, name + "-" + b))}\"");
        return TddMissingObjectTests.RunRunner(null, "--tdd", $"--cache \"{Path.Combine(_scratch, "cache-" + name)}\"", "--output-json",
            $"--package-cache \"{PackageCache()}\"", string.Join(" ", copies));
    }

    /// <summary>
    /// Three members are generated into files the compile then drops (each holds an unrelated error): "Calc" in the source codeunit "Lib", the
    /// codeunit "Nothing There" and its "Total" in a test file, and "Missing" in a stub beside the package's "Pkg Calc". Nothing declares any
    /// of them, so the run says so for each, and neither the closing list nor a test names one.
    /// </summary>
    [SkippableFact]
    public void MembersOfDroppedFiles_AreNotReportedAsGenerated()
    {
        TestArtifacts.SkipIfMissing();

        var (stdout, stderr, exit) = Run("source", "source");

        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();
        Assert.Contains("TDD-EXCLUDED", stdout + stderr);
        Assert.All(tests, t =>
        {
            Assert.Equal("fail", t.GetProperty("status").GetString());
            Assert.Empty(TddMissingObjectTests.StubsOf(t));
        });
        foreach (var member in new[]
        {
            "Lib: procedure \"Calc\"(Arg1: Integer): Integer",
            "Nothing There: codeunit 65400",
            "Nothing There: procedure \"Total\"(Arg1: Integer): Integer",
            "Pkg Calc: procedure \"Missing\"(Arg1: Integer): Integer",
        })
            Assert.Contains($"--tdd: not reported as generated: {member} - the compile did not keep", stderr);
        Assert.DoesNotContain("member(s) this run:", stderr);
        Assert.Contains("--tdd: no members were generated this run", stderr);
    }

    /// <summary>
    /// The test app is one file, and the file is dropped: no tableextension is declared, so the field "Points" it was generated in is not
    /// reported, and the record engine was not told of it either. The compile settles on the trees it kept (none), not on the trees it was handed.
    /// </summary>
    [SkippableFact]
    public void FieldInAnAppWhoseOnlyFileIsDropped_IsNotReportedAsGenerated()
    {
        TestArtifacts.SkipIfMissing();

        var (stdout, stderr, exit) = Run("onefile", "onefile");

        Assert.NotEqual(0, exit);
        Assert.Contains("--tdd: dropped with its file: table extension", stderr);
        Assert.DoesNotContain("member(s) this run:", stderr);
        Assert.DoesNotContain("Loyalty Entry: field \"Points\"", stdout + stderr);
        Assert.Contains("--tdd: no members were generated this run", stderr);
    }

    /// <summary>
    /// The app is a source bundle the test bundle depends on, so what it generates is kept for the run's list before its compile settles. Its file
    /// is then dropped, with the tableextension, and the list does not name the field: the store is settled with the compile. The test bundle
    /// reads the table's field count and sees the package's two.
    /// </summary>
    [SkippableFact]
    public void FieldInADroppedBundleFile_IsNotReportedAsGenerated_AndTheRecordEngineIsNotToldOfIt()
    {
        TestArtifacts.SkipIfMissing();

        var (stdout, stderr, exit) = Run("bundle", "bundle", "tests");

        Assert.Contains("--tdd: dropped with its file: table extension", stderr);
        using var doc = JsonDocument.Parse(stdout.Trim());
        var test = doc.RootElement.GetProperty("tests").EnumerateArray().Single(t => t.GetProperty("name").GetString()!.EndsWith(".FieldCount_IsTheTablesOwn"));
        Assert.True(test.GetProperty("status").GetString() == "pass", $"exit {exit}\n{test}\n{stderr}");
        Assert.DoesNotContain("member(s) this run:", stderr);
        Assert.Contains("--tdd: no members were generated this run", stderr);
    }
}
