// #5485: a test reaches a generated OBJECT (a codeunit, a table or an enum no app declares) not only where it names the object, but through
// any object whose declaration names it: a table whose field is typed with a generated enum, a codeunit whose global is a generated codeunit.
// Such a test compiles only because the object was generated, so it carries it in generatedStubs. Runner-specific (the annotation --tdd puts on
// a result), so it lives here, not in the al-language corpus. The design: docs/tdd-missing-object.md.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TddGeneratedObjectReachTests : IDisposable
{
    private static readonly string FixtureRoot = Path.Combine(TddMissingObjectTests.FixtureRoot, "..", "TddGeneratedObjectReach");
    private readonly string _scratch;

    public TddGeneratedObjectReachTests()
    {
        _scratch = TestScratch.Dir("al-runner-tdd-generated-object-reach");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>
    /// "Doc Status" is named only by a field of the table "Probe Doc", and "Missing Collab" only by a global of the codeunit "Probe Helper" (which
    /// one procedure mentions), and "Missing Sub" only by a global of a subscriber no test names. The test that declares a record of the table,
    /// the test that calls a procedure of the codeunit, and the test that raises the event the subscriber handles pass only because the objects
    /// were generated, and name them. The test that reaches none of the three declaring objects names nothing.
    /// </summary>
    [SkippableFact]
    public void TestsReachingAnObjectThroughAnotherObjectsDeclaration_NameIt()
    {
        TestArtifacts.SkipIfMissing();
        var app = TddMissingObjectTests.CopyFolder(FixtureRoot, Path.Combine(_scratch, "app"));

        var (stdout, stderr, exit) = TddMissingObjectTests.RunRunner(null, "--tdd", $"--cache \"{Path.Combine(_scratch, "cache")}\"", "--output-json", $"\"{app}\"");

        Assert.True(exit == 0, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();
        Assert.Equal(4, tests.Count);
        JsonElement Find(string name) => tests.Single(t => t.GetProperty("name").GetString()!.EndsWith("." + name));
        void AssertRuns(string name, params string[] stubs)
        {
            var t = Find(name);
            Assert.True(t.GetProperty("status").GetString() == "pass", $"{name}: {t}");
            Assert.Equal(stubs.OrderBy(x => x, StringComparer.Ordinal), TddMissingObjectTests.StubsOf(t).OrderBy(x => x, StringComparer.Ordinal));
        }
        AssertRuns("ReadsFieldOfGeneratedEnum_NamesNoValue", "Doc Status: enum 70000");
        AssertRuns("CallsHelperNotUsingMissingCodeunit", "Missing Collab: codeunit 70001");
        AssertRuns("RaisesEventOfSubscriberWithMissingGlobal", "Missing Sub: codeunit 70002");
        AssertRuns("CallsAnotherHelper_NamesNoStub");
        Assert.Contains("--tdd: 3 test(s) reach generated stubs this run:", stderr);
    }
}
