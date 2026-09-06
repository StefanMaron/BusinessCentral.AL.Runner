// ExpectationMatchAuditTests — an expectations entry that matches NO test must be
// reportable (issue #3123).
//
// tests/expectations/ was loud in both directions for a test the manifest MATCHED: a
// test that passes against a known-gap entry fails the run with "Remove the entry", and
// a test that raises an undeclared out-of-scope signal fails with "Add an entry". An
// entry that matched nothing was the one hole. ExpectationManifest.Lookup returns null
// for a name it does not hold, ExpectationClassifier takes its `entry == null` branch,
// and the result is a plain pass or a plain fail — so one wrong letter in CodeunitName
// or Method silently converted a declared, tracked gap into an undeclared one, and the
// run went red in a way indistinguishable from a gap nobody had declared.
//
// Measured before the fix, against AlRunner.Tests/Fixtures/ExpectationsBundle (codeunit
// 60810 "Expct Fixture Tests") on BC 28.1, one entry per run, only the quoted field
// differing:
//
//   "CodeunitName": "Expct Fixture Tests"   → PASS (known-gap), pass-known-gap: 1, exit 0
//   "CodeunitName": "Expct Fixture Test"    → FAIL, fail: 1,                      exit 1
//   "Method": "GreenPath_KnownGapDeclare"   → FAIL, fail: 1,                      exit 1
//
// All three printed "[expectations] loaded 1 entry from <dir>" first. `loaded` was true
// and said nothing about `matched`; the two failing runs produced no warning and no
// mention of the entry anywhere.
//
// These are the pure tests over the audit itself. The end-to-end proof that the audit
// reaches the exit code lives in ExpectationManifestWiringTests.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ExpectationMatchAuditTests
{
    private static ExpectationManifest LoadOneEntryManifest(
        string codeunitName, string method, int codeunitId = 60810)
    {
        var dir = TestScratch.Dir("al-runner-match-audit");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "known-gaps-fixture.json"), $$"""
        [
          {
            "codeunitId": {{codeunitId}},
            "CodeunitName": "{{codeunitName}}",
            "Method": "{{method}}",
            "Mode": "expect-fail-known-gap",
            "Issue": "https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3123"
          }
        ]
        """);
        try { return ExpectationManifest.LoadFromDirectory(dir); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static DiscoveredTestCodeunit TheFixtureCodeunit() =>
        new(60810, "Expct Fixture Tests", "Codeunit60810",
            new[] { "GreenPath_PlainPass", "GreenPath_KnownGapDeclared", "Drift_OosEntryButPasses" });

    [Fact]
    public void ExactEntry_MatchesTheDiscoveredCodeunit()
    {
        var manifest = LoadOneEntryManifest("Expct Fixture Tests", "GreenPath_KnownGapDeclared");
        manifest.NoteDiscoveredTestCodeunit(TheFixtureCodeunit());

        Assert.Empty(manifest.FindUnmatchedEntries());
    }

    [Fact]
    public void OneWrongLetterInCodeunitName_IsReported_AndNamesTheIdItFoundInstead()
    {
        // The exact shape from the measurement above: id 60810 is right, the name is
        // one letter short.
        var manifest = LoadOneEntryManifest("Expct Fixture Test", "GreenPath_KnownGapDeclared");
        manifest.NoteDiscoveredTestCodeunit(TheFixtureCodeunit());

        var unmatched = Assert.Single(manifest.FindUnmatchedEntries());
        Assert.Equal("Expct Fixture Test", unmatched.Entry.CodeunitName);
        // The diagnostic must give the reader the correction, not just the complaint:
        // the id WAS loaded, under this other name.
        Assert.Contains("object id 60810 was loaded as \"Expct Fixture Tests\"",
            unmatched.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("Check CodeunitName for a typo", unmatched.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void OneWrongLetterInMethod_IsReported_AndListsTheMethodsThatDoExist()
    {
        var manifest = LoadOneEntryManifest("Expct Fixture Tests", "GreenPath_KnownGapDeclare");
        manifest.NoteDiscoveredTestCodeunit(TheFixtureCodeunit());

        var unmatched = Assert.Single(manifest.FindUnmatchedEntries());
        Assert.Contains("declares no test method 'GreenPath_KnownGapDeclare'",
            unmatched.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("GreenPath_KnownGapDeclared", unmatched.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeunitNotLoadedAtAll_IsReportedAsNotLoaded_NotAsATypo()
    {
        // The legitimate case: an entry naming a corpus codeunit during a run over a
        // different bundle. It is still "unmatched" — deciding what that MEANS is the
        // caller's job (--expectations-require-match), which is why the diagnostic must
        // not accuse anyone of a typo here.
        var manifest = LoadOneEntryManifest("Some Other Suite", "SomeTest", codeunitId: 60999);
        manifest.NoteDiscoveredTestCodeunit(TheFixtureCodeunit());

        var unmatched = Assert.Single(manifest.FindUnmatchedEntries());
        Assert.Equal(
            "no codeunit named \"Some Other Suite\" (id 60999) was loaded in this run",
            unmatched.Diagnostic);
        Assert.DoesNotContain("typo", unmatched.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingDiscoveredAtAll_LeavesEveryEntryUnmatched()
    {
        // Guards the direction that would make the audit decoration: with no discovery
        // recorded, an entry must NOT read as matched.
        var manifest = LoadOneEntryManifest("Expct Fixture Tests", "GreenPath_KnownGapDeclared");

        Assert.Single(manifest.FindUnmatchedEntries());
    }

    [Fact]
    public void WildcardEntry_MatchesWhenTheCodeunitHasAnyTestMethod()
    {
        var manifest = LoadOneEntryManifest("Expct Fixture Tests", "*");
        manifest.NoteDiscoveredTestCodeunit(TheFixtureCodeunit());

        Assert.Empty(manifest.FindUnmatchedEntries());
    }

    [Fact]
    public void WildcardEntry_OnAMisspelledCodeunit_IsStillReported()
    {
        // "*" must widen the METHOD, never the codeunit — otherwise a wildcard entry
        // would be exempt from the whole audit.
        var manifest = LoadOneEntryManifest("Expct Fixture Test", "*");
        manifest.NoteDiscoveredTestCodeunit(TheFixtureCodeunit());

        Assert.Single(manifest.FindUnmatchedEntries());
    }

    [Fact]
    public void EntryWrittenAgainstTheClrTypeName_Matches()
    {
        // TestExecutor.LookupExpectation honours entries written against either the AL
        // object name or the CLR type name, so the audit must too — otherwise it would
        // report a working entry as orphaned.
        var manifest = LoadOneEntryManifest("Codeunit60810", "GreenPath_KnownGapDeclared");
        manifest.NoteDiscoveredTestCodeunit(TheFixtureCodeunit());

        Assert.Empty(manifest.FindUnmatchedEntries());
    }

    [Fact]
    public void ADiscoveryFromAnyBundle_Counts()
    {
        // The manifest instance is shared across every bundle in a process, so an entry
        // matched by the second bundle must not be reported because the first did not
        // have it.
        var manifest = LoadOneEntryManifest("Expct Fixture Tests", "GreenPath_KnownGapDeclared");
        manifest.NoteDiscoveredTestCodeunit(
            new DiscoveredTestCodeunit(60455, "Other Suite", "Codeunit60455", new[] { "Whatever" }));
        manifest.NoteDiscoveredTestCodeunit(TheFixtureCodeunit());

        Assert.Empty(manifest.FindUnmatchedEntries());
    }

    [Theory]
    [InlineData("Codeunit60810", 60810)]
    [InlineData("Codeunit6020", 6020)]
    [InlineData("Codeunit", null)]
    [InlineData("CodeunitAbc", null)]
    [InlineData("SomethingElse60810", null)]
    public void ParseAlObjectId_ReadsTheIdOffTheEmittedTypeName(string typeName, int? expected)
    {
        Assert.Equal(expected, AlRunner.TestExecutor.ParseAlObjectId(typeName));
    }
}
