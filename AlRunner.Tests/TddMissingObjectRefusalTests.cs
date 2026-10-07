// #5431: the cases where --tdd must NOT add an empty codeunit, because one of that name exists (an empty one would
// shadow it) or because no id is free. Each is a measured refusal: the test is reported FAILED naming the unresolved
// type, nothing is generated, and the run says why. The design: docs/tdd-missing-object.md.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TddMissingObjectRefusalTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string Refusal = Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TddMissingObjectRefusal");

    private readonly string _scratch;

    public TddMissingObjectRefusalTests()
    {
        _scratch = TestScratch.Dir("al-runner-tdd-missing-object-refusal");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private static void AssertRefused((string StdOut, string StdErr, int Exit) run, string reason, string type)
    {
        var (stdout, stderr, exit) = run;
        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();
        var t = Assert.Single(tests);
        Assert.Equal("fail", t.GetProperty("status").GetString());
        Assert.Contains($"error AL0185: Codeunit '{type}' is missing", t.GetProperty("message").GetString());
        Assert.Empty(TddMissingObjectTests.StubsOf(t));
        Assert.Contains(reason, stderr);
        Assert.DoesNotContain("--tdd: generated codeunit", stderr);
        Assert.Contains("no members were generated", stdout + stderr);
    }

    /// <summary>"Namespaced Elsewhere" exists in a namespace the test file does not use: unresolved here, but an empty
    /// global codeunit of that name would shadow the real one, so it is refused.</summary>
    [SkippableFact]
    public void ObjectInAnotherNamespace_IsNotShadowed()
    {
        TestArtifacts.SkipIfMissing();
        var app = TddMissingObjectTests.CopyFolder(Path.Combine(Refusal, "namespace"), Path.Combine(_scratch, "app"));
        var before = TddMissingObjectTests.HashTree(app);

        var run = TddMissingObjectTests.RunRunner(null, "--tdd", $"--cache \"{Path.Combine(_scratch, "cache")}\"", "--output-json", $"\"{app}\"");

        AssertRefused(run, "--tdd: codeunit \"Namespaced Elsewhere\" not generated - a codeunit of that name exists in a module or namespace this code does not reach", "Namespaced Elsewhere");
        Assert.Equal(before, TddMissingObjectTests.HashTree(app));
    }

    /// <summary>"Sibling Helper" is a real codeunit of the other bundle of the run, which the test app forgot to
    /// declare a dependency on: unresolved, and not to be hidden by an empty codeunit that makes the test pass.</summary>
    [SkippableFact]
    public void ObjectOfASiblingBundleTheAppDoesNotDeclare_IsNotShadowed()
    {
        TestArtifacts.SkipIfMissing();
        var dep = TddMissingObjectTests.CopyFolder(Path.Combine(Refusal, "undeclared", "dep"), Path.Combine(_scratch, "dep"));
        var test = TddMissingObjectTests.CopyFolder(Path.Combine(Refusal, "undeclared", "test"), Path.Combine(_scratch, "test"));

        var run = TddMissingObjectTests.RunRunner(null, "--tdd", $"--cache \"{Path.Combine(_scratch, "cache")}\"", "--output-json", $"\"{dep}\"", $"\"{test}\"");

        AssertRefused(run, "--tdd: codeunit \"Sibling Helper\" not generated - another bundle of the run declares it (SiblingHelper.Codeunit.al)", "Sibling Helper");
    }

    /// <summary>An app whose idRanges hold no free codeunit id: nothing is generated, the test is FAILED, the run says
    /// the id was the reason.</summary>
    [SkippableFact]
    public void NoFreeCodeunitId_RefusesAndSaysSo()
    {
        TestArtifacts.SkipIfMissing();
        var test = TddMissingObjectTests.CopyFolder(Path.Combine(Refusal, "undeclared", "test"), Path.Combine(_scratch, "test"));
        var manifest = Path.Combine(test, "app.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"to\": 65379", "\"to\": 65360"));

        var run = TddMissingObjectTests.RunRunner(null, "--tdd", $"--cache \"{Path.Combine(_scratch, "cache")}\"", "--output-json", $"\"{test}\"");

        AssertRefused(run, "--tdd: codeunit \"Sibling Helper\" not generated - no free codeunit id in the app's idRanges for it", "Sibling Helper");
    }
}
