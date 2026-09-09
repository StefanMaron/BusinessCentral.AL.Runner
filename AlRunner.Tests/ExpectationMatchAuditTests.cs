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

    // ── Suite scoping (#3347) ────────────────────────────────────────────────────
    //
    // The audit's claim is "this invocation was expected to discover a test for every
    // entry". That held only while every entry named a corpus test: the manifest
    // directory is shared by every invocation in this repo, and only the full-corpus CI
    // step passes --expectations-require-match. The first entry naming a
    // tests/runner-extras/ codeunit made the claim false — the corpus step can never
    // load that codeunit, so it reported a correct entry as matching nothing and failed
    // the leg with exit 5 (measured on PR #3711, all three BC legs).
    //
    // The optional "Suites" field is the entry saying which suite roots can cover it.
    // Absent = audited by every invocation, which is what every pre-existing entry
    // wants and gets. Present = audited only by an invocation that ran a matching root.

    private static ExpectationManifest LoadOneEntryManifestWithSuites(
        string codeunitName, string method, string suitesJson)
    {
        var dir = TestScratch.Dir("al-runner-match-audit-suites");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "known-gaps-fixture.json"), $$"""
        [
          {
            "codeunitId": 60810,
            "CodeunitName": "{{codeunitName}}",
            "Method": "{{method}}",
            "Mode": "expect-fail-known-gap",
            "Issue": "https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3123",
            "Suites": {{suitesJson}}
          }
        ]
        """);
        try { return ExpectationManifest.LoadFromDirectory(dir); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void AnEntryScopedToAnotherSuite_IsNotAuditedByThisRun()
    {
        // The #3711 shape exactly: the entry names a runner-extras codeunit and the run
        // is the corpus. Nothing here could have loaded it, so reporting it as unmatched
        // accuses a correct entry of a typo.
        var manifest = LoadOneEntryManifestWithSuites(
            "Precompiled Implicit Return", "PrecompiledBooleanMethodWithNoExit_ReturnsFalse",
            "[\"tests/runner-extras\"]");
        manifest.NoteDiscoveredTestCodeunit(TheFixtureCodeunit());

        Assert.Empty(manifest.FindUnmatchedEntries(new[] { "tests/al-language/tests/al-language" }));
    }

    [Fact]
    public void AnEntryScopedToTheSuiteThisRunCovers_IsStillAudited()
    {
        // The negative that makes the test above mean something: scoping must not become
        // a blanket exemption. The run DID cover this suite, so a wrong method name is
        // still reported — the whole point of the audit.
        var manifest = LoadOneEntryManifestWithSuites(
            "Expct Fixture Tests", "GreenPath_KnownGapDeclare",
            "[\"tests/runner-extras\"]");
        manifest.NoteDiscoveredTestCodeunit(TheFixtureCodeunit());

        var unmatched = manifest.FindUnmatchedEntries(
            new[] { "tests/runner-extras/precompiled-implicit-return-3347" });

        var u = Assert.Single(unmatched);
        Assert.Contains("declares no test method", u.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryScopedToTheSuiteThisRunCovers_AndCorrect_Matches()
    {
        // The control for the pair above: same scope, correct entry, no report.
        var manifest = LoadOneEntryManifestWithSuites(
            "Expct Fixture Tests", "GreenPath_KnownGapDeclared",
            "[\"tests/runner-extras\"]");
        manifest.NoteDiscoveredTestCodeunit(TheFixtureCodeunit());

        Assert.Empty(manifest.FindUnmatchedEntries(
            new[] { "tests/runner-extras/precompiled-implicit-return-3347" }));
    }

    [Fact]
    public void AnUnscopedEntry_IsAuditedByEveryRun()
    {
        // Back-compat, and the reason the field is optional: every entry that existed
        // before #3347 carries no Suites and must keep being audited exactly as before.
        var manifest = LoadOneEntryManifest("Expct Fixture Test", "GreenPath_KnownGapDeclared");
        manifest.NoteDiscoveredTestCodeunit(TheFixtureCodeunit());

        var unmatched = manifest.FindUnmatchedEntries(new[] { "tests/al-language/tests/al-language" });

        var u = Assert.Single(unmatched);
        Assert.Contains("Check CodeunitName for a typo", u.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void SuiteScoping_MatchesOnAPathSegment_NotASubstring()
    {
        // "tests/runner-extras" must not be satisfied by a root that merely CONTAINS
        // those characters in a longer segment. Otherwise a typo'd scope silently
        // exempts an entry from every run, which is the failure this whole audit exists
        // to prevent, reintroduced through its own escape hatch.
        var manifest = LoadOneEntryManifestWithSuites(
            "Expct Fixture Tests", "GreenPath_KnownGapDeclare",
            "[\"tests/runner-extras\"]");
        manifest.NoteDiscoveredTestCodeunit(TheFixtureCodeunit());

        // Covered: the root IS the scope, and a root BELOW it.
        Assert.Single(manifest.FindUnmatchedEntries(new[] { "tests/runner-extras" }));
        Assert.Single(manifest.FindUnmatchedEntries(new[] { "tests/runner-extras/some-bundle" }));

        // Not covered: a sibling whose name merely starts with the scope's last segment.
        Assert.Empty(manifest.FindUnmatchedEntries(new[] { "tests/runner-extras-isolation-disabled" }));
    }

    [Fact]
    public void NoRootsGiven_AuditsEveryEntry_ScopedOrNot()
    {
        // A caller that cannot say which roots it ran must not get a quieter audit than
        // one that can. The parameterless overload keeps the pre-#3347 behaviour.
        var manifest = LoadOneEntryManifestWithSuites(
            "Precompiled Implicit Return", "PrecompiledBooleanMethodWithNoExit_ReturnsFalse",
            "[\"tests/runner-extras\"]");
        manifest.NoteDiscoveredTestCodeunit(TheFixtureCodeunit());

        Assert.Single(manifest.FindUnmatchedEntries());
    }

    // ── Carried resume attempts (#3168) ──────────────────────────────────────────
    //
    // NoteDiscoveredTestCodeunit is called by the executor IN THIS PROCESS. A resumed
    // run (#2280) is several processes, and the final one is handed the earlier
    // attempts' results (--merge-results). An entry naming a test that ran in an earlier
    // attempt was reported as matching nothing — "check CodeunitName for a typo" about a
    // correct entry, which is the one distinction the audit exists to draw.

    private static TestResult CarriedTest(
        string typeName, string method, string? displayName) =>
        new(typeName, method, TestOutcome.Pass, null, null, TimeSpan.FromMilliseconds(1),
            CodeunitDisplayName: displayName);

    [Fact]
    public void AnEntryMatchedOnlyByACarriedAttempt_IsNotReportedAsUnmatched()
    {
        var manifest = LoadOneEntryManifest("Expct Fixture Tests", "GreenPath_KnownGapDeclared");
        // This process discovered a DIFFERENT codeunit — the entry's own is reachable
        // only through the carry, so nothing here can pass vacuously.
        manifest.NoteDiscoveredTestCodeunit(
            new DiscoveredTestCodeunit(60455, "Other Suite", "Codeunit60455", new[] { "Whatever" }));
        Assert.Single(manifest.FindUnmatchedEntries());   // RED state, asserted in place

        manifest.NoteDiscoveredFromCarriedResults(new[]
        {
            CarriedTest("Codeunit60810", "GreenPath_KnownGapDeclared", "Expct Fixture Tests"),
        });

        Assert.Empty(manifest.FindUnmatchedEntries());
    }

    [Fact]
    public void ACarriedAttempt_AlsoMatchesAnEntryWrittenAgainstTheClrTypeName()
    {
        // The carry records both names, so both spellings an entry may use must work —
        // matching TestExecutor.LookupExpectation, exactly as in-process discovery does.
        var manifest = LoadOneEntryManifest("Codeunit60810", "GreenPath_KnownGapDeclared");
        manifest.NoteDiscoveredFromCarriedResults(new[]
        {
            CarriedTest("Codeunit60810", "GreenPath_KnownGapDeclared", "Expct Fixture Tests"),
        });

        Assert.Empty(manifest.FindUnmatchedEntries());
    }

    [Fact]
    public void ACarriedAttempt_DoesNotMatchAMethodItNeverRan_AndSaysSoHonestly()
    {
        // The audit must not become a rubber stamp: a carried codeunit satisfies only the
        // methods that actually ran. And the diagnostic must not claim the codeunit
        // "declares no test method X" — a carry file records what RAN, which is the full
        // declared set only when nothing filtered it.
        var manifest = LoadOneEntryManifest("Expct Fixture Tests", "GreenPath_PlainPass");
        manifest.NoteDiscoveredFromCarriedResults(new[]
        {
            CarriedTest("Codeunit60810", "GreenPath_KnownGapDeclared", "Expct Fixture Tests"),
        });

        var unmatched = Assert.Single(manifest.FindUnmatchedEntries());
        Assert.Contains("reached only by an earlier resume attempt", unmatched.Diagnostic,
            StringComparison.Ordinal);
        Assert.Contains("ran no test method 'GreenPath_PlainPass'", unmatched.Diagnostic,
            StringComparison.Ordinal);
        Assert.Contains("The methods it ran: GreenPath_KnownGapDeclared", unmatched.Diagnostic,
            StringComparison.Ordinal);
        Assert.DoesNotContain("declares no test method", unmatched.Diagnostic,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InProcessDiscovery_KeepsTheDeclaresWording_EvenAlongsideACarriedAttempt()
    {
        // The counterpart to the test above: when the codeunit was really loaded here,
        // its method list IS the declared set, so the sharper wording must survive.
        var manifest = LoadOneEntryManifest("Expct Fixture Tests", "GreenPath_PlainPas");
        manifest.NoteDiscoveredTestCodeunit(TheFixtureCodeunit());
        manifest.NoteDiscoveredFromCarriedResults(new[]
        {
            CarriedTest("Codeunit60810", "GreenPath_KnownGapDeclared", "Expct Fixture Tests"),
        });

        var unmatched = Assert.Single(manifest.FindUnmatchedEntries());
        Assert.Contains("declares no test method 'GreenPath_PlainPas'", unmatched.Diagnostic,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ACarriedCtorPlaceholder_IsNotTreatedAsAMethodName()
    {
        // TestExecutor records "<ctor>" for a codeunit that would not construct. No AL
        // author can write that name, so an entry must never match it.
        var manifest = LoadOneEntryManifest("Expct Fixture Tests", "<ctor>");
        manifest.NoteDiscoveredFromCarriedResults(new[]
        {
            CarriedTest("Codeunit60810", "<ctor>", "Expct Fixture Tests"),
        });

        Assert.Single(manifest.FindUnmatchedEntries());
    }

    [Fact]
    public void ACarriedResultWithNoDisplayName_StillMatchesOnTheTypeName()
    {
        // CodeunitDisplayName is nullable in the carry format; the type name is not.
        var manifest = LoadOneEntryManifest("Codeunit60810", "GreenPath_KnownGapDeclared");
        manifest.NoteDiscoveredFromCarriedResults(new[]
        {
            CarriedTest("Codeunit60810", "GreenPath_KnownGapDeclared", null),
        });

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
