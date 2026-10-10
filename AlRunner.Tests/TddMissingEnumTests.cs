// #5445: --tdd with a test that names an ENUM no app declares (`K: Enum "No Such Enum"`, AL0185). The enum is added
// empty, and the existing member generation adds the values the tests name (`K::Open`, `Enum::"X"::Open`).
// Runner-specific (--tdd turning a compile error into a generated object the tests run against), so it lives here,
// not in the al-language corpus. The design: docs/tdd-missing-object.md.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TddMissingEnumTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string Fixtures = Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures");

    private readonly string _scratch;

    public TddMissingEnumTests()
    {
        _scratch = TestScratch.Dir("al-runner-tdd-missing-enum");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private const string GeneratedNote = "no app of the run and no package it can read declares it";

    private (string StdOut, string StdErr, int Exit) RunTdd(string app, (string, string)? env = null) =>
        TddMissingObjectTests.RunRunner(env, "--tdd", $"--cache \"{Path.Combine(_scratch, "cache")}\"", "--output-json", $"\"{app}\"");

    private static List<JsonElement> TestsOf(JsonDocument doc) => doc.RootElement.GetProperty("tests").EnumerateArray().ToList();

    /// <summary>
    /// An enum no app declares is added, empty, to the file that names it first, with an id from the app's own range that
    /// no ENUM uses (a codeunit may hold the same number), and the values the tests name are added in the order the
    /// code names them; an enum that exists keeps its real values; nothing is written.
    /// </summary>
    [SkippableFact]
    public void MissingEnum_RunsToGeneratedEnum_AndNothingIsWritten()
    {
        TestArtifacts.SkipIfMissing();
        var app = TddMissingObjectTests.CopyFolder(Path.Combine(Fixtures, "TddMissingEnum"), Path.Combine(_scratch, "app"));
        var before = TddMissingObjectTests.HashTree(app);

        var (stdout, stderr, exit) = RunTdd(app);

        Assert.True(exit == 0, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = TestsOf(doc);
        JsonElement Find(string name) => tests.Single(t => t.GetProperty("name").GetString()!.EndsWith("." + name));
        void AssertPasses(string name, params string[] stubs)
        {
            var t = Find(name);
            Assert.True(t.GetProperty("status").GetString() == "pass", $"{name}: {t}");
            Assert.Equal(stubs, TddMissingObjectTests.StubsOf(t));
        }

        // Enum id 65320 is the app's own enum; the codeunits hold 65320-65321 (and the generated Twin 65322), which
        // enums do not care about: "Another Missing Enum" takes 65321 although a codeunit has it.
        const string NoSuch = "No Such Enum: enum 65322";
        AssertPasses("VariableValues_RunToTheGeneratedEnum", NoSuch, "No Such Enum: enum value \"Open\" = 0", "No Such Enum: enum value \"Closed\" = 1");
        AssertPasses("LiteralValue_RunsToTheGeneratedEnum", NoSuch, "No Such Enum: enum value \"Pending\" = 2");
        AssertPasses("GlobalVariable_RunsToTheGeneratedEnum", "Another Missing Enum: enum 65321", "Another Missing Enum: enum value \"Left\" = 0");
        AssertPasses("SameEnum_InASecondFile", NoSuch, "No Such Enum: enum value \"Closed\" = 1");
        // A codeunit and an enum sharing a name are two objects, each in its own id space, and the value goes to the enum.
        AssertPasses("EnumAndCodeunit_ShareOneName", "Twin: codeunit 65322", "Twin: enum 65323", "Twin: enum value \"Alpha\" = 0",
            "Twin: procedure \"Calc\"(Arg1: Integer): Integer");
        AssertPasses("ExistingEnum_KeepsItsRealValues");
        AssertPasses("Unrelated_NamesNoStub");

        Assert.Equal(7, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(7, doc.RootElement.GetProperty("passed").GetInt32());

        var lines = stderr.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal(new[]
        {
            $"--tdd: generated codeunit \"Twin\" (id 65322) in A2SecondFileTests.Codeunit.al: {GeneratedNote}",
            $"--tdd: generated enum \"Another Missing Enum\" (id 65321) in A1EnumTests.Codeunit.al: {GeneratedNote}",
            $"--tdd: generated enum \"No Such Enum\" (id 65322) in A1EnumTests.Codeunit.al: {GeneratedNote}",
            $"--tdd: generated enum \"Twin\" (id 65323) in A2SecondFileTests.Codeunit.al: {GeneratedNote}",
        }, lines.Where(l => l.StartsWith("--tdd: generated ") && l.Contains(" (id ")).ToList());
        Assert.DoesNotContain("no members were generated", stdout + stderr);
        Assert.Equal(before, TddMissingObjectTests.HashTree(app));
    }

    /// <summary>
    /// Room for ONE more enum id (65320 is the app's own enum, the range ends at 65321, and both codeunit ids are taken), with
    /// the diagnostics fed to the member generation backwards (the compile reports them in an order that changes between
    /// runs): the first enum by name takes the id, every other enum and the codeunit is refused and says the id was the reason.
    /// </summary>
    [SkippableFact]
    public void OneFreeEnumId_ReversedFeed_GoesToTheFirstName_TheOthersAreRefusedAndNamed()
    {
        TestArtifacts.SkipIfMissing();
        var app = TddMissingObjectTests.CopyFolder(Path.Combine(Fixtures, "TddMissingEnum"), Path.Combine(_scratch, "app"));
        var manifest = Path.Combine(app, "app.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"to\": 65339", "\"to\": 65321"));

        var (stdout, stderr, exit) = RunTdd(app, ("AL_RUNNER_TDD_DIAG_ORDER", "reverse"));

        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        Assert.Contains($"--tdd: generated enum \"Another Missing Enum\" (id 65321) in A1EnumTests.Codeunit.al: {GeneratedNote}", stderr);
        foreach (var name in new[] { "No Such Enum", "Twin" })
        {
            Assert.Contains($"--tdd: enum \"{name}\" not generated - no free enum id in the app's idRanges for it", stderr);
            Assert.DoesNotContain($"--tdd: generated enum \"{name}\"", stderr);
        }
        Assert.Contains("--tdd: codeunit \"Twin\" not generated - no free codeunit id in the app's idRanges for it", stderr);
        using var doc = JsonDocument.Parse(stdout.Trim());
        // Both files name an enum that was refused, so neither compiles.
        Assert.All(TestsOf(doc), t => Assert.Equal("fail", t.GetProperty("status").GetString()));
        Assert.Contains(TestsOf(doc), t => t.GetProperty("message").GetString()!.Contains("AL0185"));
    }

    /// <summary>
    /// A package of the folders declares "Package Only Status" and the app does not depend on it: an empty enum of that
    /// name would shadow it, so it is refused with the package named and the test FAILED on the AL0185; the refusal uses
    /// up no id, so "Zulu Nowhere Status" (declared by nobody, sorted after it) takes the first free one.
    /// </summary>
    [SkippableFact]
    public void EnumOfAPackageTheAppDoesNotDeclare_IsNotShadowed_AndUsesUpNoId()
    {
        TestArtifacts.SkipIfMissing();
        var app = TddMissingObjectTests.CopyFolder(Path.Combine(Fixtures, "TddMissingEnumPackage"), Path.Combine(_scratch, "app"));
        Directory.CreateDirectory(Path.Combine(app, ".alpackages"));
        File.WriteAllBytes(Path.Combine(app, ".alpackages", "AL_Runner_Fixtures_Tdd_Package_Only_1.0.0.0.app"),
            TddMissingObjectPackageTests.BuildPackage(StatusSymbols));
        var before = TddMissingObjectTests.HashTree(app);

        var (stdout, stderr, exit) = RunTdd(app);

        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = TestsOf(doc);
        var refused = tests.Single(t => t.GetProperty("name").GetString()!.EndsWith(".UndeclaredPackageEnum_IsNotShadowed"));
        Assert.Equal("fail", refused.GetProperty("status").GetString());
        Assert.Contains("error AL0185: Enum 'Package Only Status' is missing", refused.GetProperty("message").GetString());
        Assert.Empty(TddMissingObjectTests.StubsOf(refused));
        var control = tests.Single(t => t.GetProperty("name").GetString()!.EndsWith(".MissingEverywhere_IsGenerated"));
        Assert.Equal("pass", control.GetProperty("status").GetString());
        Assert.Equal(new[] { "Zulu Nowhere Status: enum 65360" }, TddMissingObjectTests.StubsOf(control));
        Assert.Contains("--tdd: enum \"Package Only Status\" not generated - the package Tdd Package Only 1.0.0.0 (AL_Runner_Fixtures_Tdd_Package_Only_1.0.0.0.app) declares it - if the test means that enum, add the dependency on it to app.json; if it means a new one, give it another name; an empty enum would shadow it", stderr);
        Assert.DoesNotContain("--tdd: generated enum \"Package Only Status\"", stderr);
        Assert.Equal(before, TddMissingObjectTests.HashTree(app));
    }

    private const string StatusSymbols = """
        "EnumTypes":[{"Id":65400,"Name":"Package Only Status","Values":[{"Ordinal":0,"Name":"Open"}],"Properties":[]}]
        """;
}
