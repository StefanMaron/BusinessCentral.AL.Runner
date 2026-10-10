// #5445: --tdd with a test that names a TABLE no app declares (`T: Record "No Such Table"`, AL0185). The table is added
// with one placeholder primary key field, and the existing member generation adds the fields the tests assign.
// Runner-specific (--tdd turning a compile error into a generated object the tests run against), so it lives here,
// not in the al-language corpus. The design: docs/tdd-missing-object.md.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TddMissingTableTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string Fixtures = Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures");

    private readonly string _scratch;

    public TddMissingTableTests()
    {
        _scratch = TestScratch.Dir("al-runner-tdd-missing-table");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private const string GeneratedNote = "no app of the run and no package it can read declares it; its only field is the placeholder primary key \"TDD Key\": Integer (AutoIncrement)";

    private (string StdOut, string StdErr, int Exit) RunTdd(string app, (string, string)? env = null) =>
        TddMissingObjectTests.RunRunner(env, "--tdd", $"--cache \"{Path.Combine(_scratch, "cache")}\"", "--output-json", $"\"{app}\"");

    private static List<JsonElement> TestsOf(JsonDocument doc) => doc.RootElement.GetProperty("tests").EnumerateArray().ToList();

    /// <summary>
    /// A table no app declares is added to the file that names it first, with ids from the app's own range that no TABLE
    /// uses (a codeunit may hold the same number), and the fields the tests assign are added next to the placeholder
    /// key; a table that exists keeps its real shape; what cannot be generated is FAILED without taking the other files
    /// down; nothing is written.
    /// </summary>
    [SkippableFact]
    public void MissingTable_RunsToGeneratedTable_AndNothingIsWritten()
    {
        TestArtifacts.SkipIfMissing();
        var app = TddMissingObjectTests.CopyFolder(Path.Combine(Fixtures, "TddMissingTable"), Path.Combine(_scratch, "app"));
        var before = TddMissingObjectTests.HashTree(app);

        var (stdout, stderr, exit) = RunTdd(app);

        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = TestsOf(doc);
        JsonElement Find(string name) => tests.Single(t => t.GetProperty("name").GetString()!.EndsWith("." + name));
        void AssertPasses(string name, params string[] stubs)
        {
            var t = Find(name);
            Assert.True(t.GetProperty("status").GetString() == "pass", $"{name}: {t}");
            Assert.Equal(stubs, TddMissingObjectTests.StubsOf(t));
        }

        // Table ids 65320 is the app's own table; codeunits hold 65320-65322, which tables do not care about.
        const string NoSuch = "No Such Table: table 65322";
        AssertPasses("InitAndInsert_RunToTheGeneratedTable", NoSuch);
        AssertPasses("Get_FindsTheInsertedRow", NoSuch);
        AssertPasses("AssignedField_RoundTripsThroughTheTable", NoSuch, "No Such Table: field \"Amount\": Integer");
        AssertPasses("GlobalVariable_RunsToTheGeneratedTable", "Another Missing Table: table 65321", "Another Missing Table: field \"Quantity\": Integer");
        AssertPasses("SameTable_InASecondFile", NoSuch, "No Such Table: field \"Amount\": Integer");
        // A codeunit and a table sharing a name are two objects, each in its own id space, and the field goes to the table.
        AssertPasses("TableAndCodeunit_ShareOneName", "Twin: codeunit 65323", "Twin: table 65324", "Twin: field \"Weight\": Integer",
            "Twin: procedure \"Calc\"(Arg1: Integer): Integer");
        AssertPasses("ExistingTable_KeepsItsRealShape");
        AssertPasses("Unrelated_NamesNoStub");

        // A field that is only read, and a Text assignment (no length to infer), are not generated: the file is FAILED
        // naming the member, with no stub.
        foreach (var name in new[] { "ReadOfAFieldNothingAssigns_IsRefused", "TextAssignment_IsRefused" })
        {
            var t = Find(name);
            Assert.Equal("fail", t.GetProperty("status").GetString());
            Assert.Contains("error AL0132: 'Record \"Refused Table\"' does not contain a definition for 'Qty'", t.GetProperty("message").GetString());
            Assert.Empty(TddMissingObjectTests.StubsOf(t));
        }

        Assert.Equal(10, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(8, doc.RootElement.GetProperty("passed").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("failed").GetInt32());

        var lines = stderr.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal(new[]
        {
            "--tdd: generated codeunit \"Twin\" (id 65323) in A2SecondFileTests.Codeunit.al: no app of the run and no package it can read declares it",
            $"--tdd: generated table \"Another Missing Table\" (id 65321) in A1TableTests.Codeunit.al: {GeneratedNote}",
            $"--tdd: generated table \"No Such Table\" (id 65322) in A1TableTests.Codeunit.al: {GeneratedNote}",
            $"--tdd: generated table \"Refused Table\" (id 65323) in A4RefusedTests.Codeunit.al: {GeneratedNote}",
            $"--tdd: generated table \"Twin\" (id 65324) in A2SecondFileTests.Codeunit.al: {GeneratedNote}",
        }, lines.Where(l => l.StartsWith("--tdd: generated ") && l.Contains(" (id ")).ToList());
        Assert.DoesNotContain("no members were generated", stdout + stderr);
        Assert.Equal(before, TddMissingObjectTests.HashTree(app));
    }

    /// <summary>
    /// Room for ONE more table id (65320 is the app's own table, the range ends at 65321), with the diagnostics fed to
    /// the member generation backwards (the compile reports them in an order that changes between runs): the first
    /// table by name takes the id, every other table and the codeunit (no codeunit id is free either) is refused and says
    /// the id was the reason.
    /// </summary>
    [SkippableFact]
    public void OneFreeTableId_ReversedFeed_GoesToTheFirstName_TheOthersAreRefusedAndNamed()
    {
        TestArtifacts.SkipIfMissing();
        var app = TddMissingObjectTests.CopyFolder(Path.Combine(Fixtures, "TddMissingTable"), Path.Combine(_scratch, "app"));
        var manifest = Path.Combine(app, "app.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"to\": 65339", "\"to\": 65321"));

        var (stdout, stderr, exit) = RunTdd(app, ("AL_RUNNER_TDD_DIAG_ORDER", "reverse"));

        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        Assert.Contains($"--tdd: generated table \"Another Missing Table\" (id 65321) in A1TableTests.Codeunit.al: {GeneratedNote}", stderr);
        foreach (var name in new[] { "No Such Table", "Refused Table", "Twin" })
        {
            Assert.Contains($"--tdd: table \"{name}\" not generated - no free table id in the app's idRanges for it", stderr);
            Assert.DoesNotContain($"--tdd: generated table \"{name}\"", stderr);
        }
        Assert.Contains("--tdd: codeunit \"Twin\" not generated - no free codeunit id in the app's idRanges for it", stderr);
        using var doc = JsonDocument.Parse(stdout.Trim());
        // Every file names a table that was refused, so none of them compiles.
        Assert.All(TestsOf(doc), t => Assert.Equal("fail", t.GetProperty("status").GetString()));
        Assert.Contains(TestsOf(doc), t => t.GetProperty("message").GetString()!.Contains("AL0185"));
    }

    /// <summary>
    /// A package of the folders declares "Package Only Ledger" and the app does not depend on it: an empty table of that
    /// name would shadow it, so it is refused with the package named and the test FAILED on the AL0185; the refusal uses
    /// up no id, so "Zulu Nowhere Ledger" (declared by nobody, sorted after it) takes the first free one.
    /// </summary>
    [SkippableFact]
    public void TableOfAPackageTheAppDoesNotDeclare_IsNotShadowed_AndUsesUpNoId()
    {
        TestArtifacts.SkipIfMissing();
        var app = TddMissingObjectTests.CopyFolder(Path.Combine(Fixtures, "TddMissingTablePackage"), Path.Combine(_scratch, "app"));
        Directory.CreateDirectory(Path.Combine(app, ".alpackages"));
        File.WriteAllBytes(Path.Combine(app, ".alpackages", "AL_Runner_Fixtures_Tdd_Package_Only_1.0.0.0.app"),
            TddMissingObjectPackageTests.BuildPackage(LedgerSymbols));
        var before = TddMissingObjectTests.HashTree(app);

        var (stdout, stderr, exit) = RunTdd(app);

        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = TestsOf(doc);
        var refused = tests.Single(t => t.GetProperty("name").GetString()!.EndsWith(".UndeclaredPackageTable_IsNotShadowed"));
        Assert.Equal("fail", refused.GetProperty("status").GetString());
        Assert.Contains("error AL0185: Table 'Package Only Ledger' is missing", refused.GetProperty("message").GetString());
        Assert.Empty(TddMissingObjectTests.StubsOf(refused));
        var control = tests.Single(t => t.GetProperty("name").GetString()!.EndsWith(".MissingEverywhere_IsGenerated"));
        Assert.Equal("pass", control.GetProperty("status").GetString());
        Assert.Equal(new[] { "Zulu Nowhere Ledger: table 65360" }, TddMissingObjectTests.StubsOf(control));
        Assert.Contains("--tdd: table \"Package Only Ledger\" not generated - the package Tdd Package Only 1.0.0.0 (AL_Runner_Fixtures_Tdd_Package_Only_1.0.0.0.app) declares it - if the test means that table, add the dependency on it to app.json; if it means a new one, give it another name; an empty table would shadow it", stderr);
        Assert.DoesNotContain("--tdd: generated table \"Package Only Ledger\"", stderr);
        Assert.Equal(before, TddMissingObjectTests.HashTree(app));
    }

    /// <summary>
    /// A field the test assigns on a table that bundle "a" declares beside a codeunit of the same name (one file): the
    /// field is generated into bundle "a"'s TABLE and the test runs to its value. The cross-bundle twin of the
    /// name-and-type match of <c>TddGeneration.TryGenerate</c>; found by name alone, the codeunit was picked and the
    /// field refused (AL0132).
    /// </summary>
    [SkippableFact]
    public void CrossBundle_TableSharingANameWithACodeunit_GetsTheFieldOnTheTable()
    {
        TestArtifacts.SkipIfMissing();
        var root = Path.Combine(Fixtures, "TddMissingTableTwinBundles");
        var a = TddMissingObjectTests.CopyFolder(Path.Combine(root, "a"), Path.Combine(_scratch, "a"));
        var b = TddMissingObjectTests.CopyFolder(Path.Combine(root, "b"), Path.Combine(_scratch, "b"));
        var before = TddMissingObjectTests.HashTree(a);

        var (stdout, stderr, exit) = TddMissingObjectTests.RunRunner(null, "--tdd",
            $"--cache \"{Path.Combine(_scratch, "cache")}\"", "--output-json", $"\"{a}\"", $"\"{b}\"");

        Assert.True(exit == 0, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var t = Assert.Single(TestsOf(doc));
        Assert.Equal("pass", t.GetProperty("status").GetString());
        Assert.Equal(new[] { "Twin: field \"Weight\": Integer" }, TddMissingObjectTests.StubsOf(t));
        Assert.DoesNotContain("AL0132", stdout + stderr);
        Assert.Equal(before, TddMissingObjectTests.HashTree(a)); // the generation is an in-memory overlay
    }

    private const string LedgerSymbols = """
        "Tables":[{"Id":65400,"Name":"Package Only Ledger","Fields":[{"Id":1,"Name":"Entry No.","TypeDefinition":{"Name":"Integer"}}],
          "Keys":[{"Name":"PK","FieldNames":["Entry No."],"Properties":[]}],"Properties":[]}]
        """;
}
