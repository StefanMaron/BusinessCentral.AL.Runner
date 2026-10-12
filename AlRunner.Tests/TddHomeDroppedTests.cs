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

    /// <summary>The package every shape but the first generates a field of: table 65380 "Loyalty Entry" with the fields "Id" and "Existing Points".</summary>
    private string PackageCache()
    {
        var packages = Path.Combine(_scratch, "packages");
        Directory.CreateDirectory(packages);
        File.WriteAllBytes(Path.Combine(packages, TddPackageMemberTests.DepFileStem + ".app"),
            TddPackageMemberTests.BuildApp(TddPackageMemberTests.DepAppId, "Tdd Package Members Dep", 65380, 65389,
                TddPackageMemberTests.DepSymbols, Path.Combine(TddPackageMemberTests.FixtureRoot, "dep")));
        return packages;
    }

    private (string StdOut, string StdErr, int Exit) Run(string name, params string[] bundles)
    {
        var copies = bundles.Select(b => $"\"{TddMissingObjectTests.CopyFolder(Path.Combine(FixtureRoot, b), Path.Combine(_scratch, name + "-" + b))}\"");
        return TddMissingObjectTests.RunRunner(null, "--tdd", $"--cache \"{Path.Combine(_scratch, "cache-" + name)}\"", "--output-json",
            $"--package-cache \"{PackageCache()}\"", string.Join(" ", copies));
    }

    /// <summary>
    /// "Calc" is added to the codeunit "Lib", whose file holds an unrelated error: the compile drops the file, so nothing declares "Calc".
    /// The run says the file was dropped, and neither the closing list nor the test names the member.
    /// </summary>
    [SkippableFact]
    public void ProcedureInADroppedSourceCodeunit_IsNotReportedAsGenerated()
    {
        TestArtifacts.SkipIfMissing();

        var (stdout, stderr, exit) = Run("source", "source");

        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var test = doc.RootElement.GetProperty("tests").EnumerateArray().Single();
        Assert.Equal("fail", test.GetProperty("status").GetString());
        Assert.Empty(TddMissingObjectTests.StubsOf(test));
        Assert.Contains("TDD-EXCLUDED", stdout + stderr);
        Assert.Contains("--tdd: not reported as generated: Lib: procedure \"Calc\"(Arg1: Integer): Integer", stderr);
        Assert.DoesNotContain("--tdd: generated ", stderr);
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
        Assert.DoesNotContain("--tdd: generated ", stderr);
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
        Assert.DoesNotContain("--tdd: generated ", stderr);
        Assert.Contains("--tdd: no members were generated this run", stderr);
    }
}
