// ExpectationBcMajorScopeTests — an entry may name the BC majors it describes (#5382).
//
// The manifest directory is shared by every BC leg. An entry true only against BC 29's engine
// (a SkiaSharp native library the 29 artifact does not ship for Linux, an interface id formula
// that changed) is wrong on every other leg: there the test passes and the entry reads "remove
// the entry". `BcMajors` scopes it; `ActiveBcMajor` is the run's own major.
//
// What these pin, each with the reading that would make it pass for the wrong reason:
//   * a scoped entry is consulted on its own major and ignored on another — an implementation
//     that dropped the scope check passes the first and fails the second;
//   * a run that does not know its major gets only the unscoped entries — an implementation
//     that treated "unknown" as "applies" passes both of the above and fails this one;
//   * the audit does not accuse a scoped-away entry of matching nothing;
//   * two entries for one test are legal only with disjoint scopes.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ExpectationBcMajorScopeTests
{
    private const string Issue = "https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/5382";

    private static ExpectationManifest Load(string json, string fileName = "known-gaps-fixture.json")
    {
        var dir = TestScratch.Dir("al-runner-bcmajor-scope");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), json);
        try { return ExpectationManifest.LoadFromDirectory(dir); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static string Entry(string method, string? bcMajors, string mode = "expect-fail-known-gap",
        string extra = $"\"Issue\": \"{Issue}\"") =>
        $$"""
          {
            "codeunitId": 60810,
            "CodeunitName": "Expct Fixture Tests",
            "Method": "{{method}}",
            "Mode": "{{mode}}",
            {{extra}}{{(bcMajors == null ? "" : $",\n    \"BcMajors\": {bcMajors}")}}
          }
          """;

    [Fact]
    public void ScopedEntry_IsConsultedOnItsOwnMajor()
    {
        var manifest = Load($"[{Entry("T1", "[29]")}]");
        manifest.ActiveBcMajor = 29;

        var entry = manifest.Lookup("Expct Fixture Tests", "T1");

        Assert.NotNull(entry);
        Assert.Equal(new[] { 29 }, entry!.BcMajors);
    }

    [Theory]
    [InlineData(27)]
    [InlineData(28)]
    public void ScopedEntry_IsIgnoredOnAnotherMajor(int major)
    {
        var manifest = Load($"[{Entry("T1", "[29]")}]");
        manifest.ActiveBcMajor = major;

        Assert.Null(manifest.Lookup("Expct Fixture Tests", "T1"));
    }

    [Fact]
    public void ScopedEntry_IsIgnoredWhenTheRunDoesNotKnowItsMajor()
    {
        var manifest = Load($"[{Entry("T1", "[29]")}]");

        Assert.Null(manifest.ActiveBcMajor);
        Assert.Null(manifest.Lookup("Expct Fixture Tests", "T1"));
    }

    [Fact]
    public void UnscopedEntry_AppliesOnEveryMajorAndWhenTheMajorIsUnknown()
    {
        var manifest = Load($"[{Entry("T1", null)}]");

        Assert.NotNull(manifest.Lookup("Expct Fixture Tests", "T1"));
        manifest.ActiveBcMajor = 27;
        Assert.NotNull(manifest.Lookup("Expct Fixture Tests", "T1"));
        manifest.ActiveBcMajor = 29;
        Assert.NotNull(manifest.Lookup("Expct Fixture Tests", "T1"));
    }

    [Fact]
    public void WildcardScopedToAnotherMajor_DoesNotShadowAnUnscopedExactEntryNorApply()
    {
        // An exact method entry applying on every major, and a wildcard that is only for 29.
        var manifest = Load($"[{Entry("T1", null)}, {Entry("*", "[29]")}]");
        manifest.ActiveBcMajor = 28;

        Assert.Equal("T1", manifest.Lookup("Expct Fixture Tests", "T1")!.Method);
        // The 29 wildcard must not answer for an unrelated method on 28.
        Assert.Null(manifest.Lookup("Expct Fixture Tests", "Other"));

        manifest.ActiveBcMajor = 29;
        Assert.Equal("*", manifest.Lookup("Expct Fixture Tests", "Other")!.Method);
    }

    [Fact]
    public void Audit_DoesNotReportAScopedAwayEntryAsMatchingNothing_AndNamesItAsNotAudited()
    {
        var manifest = Load($"[{Entry("T1", "[29]")}]");
        manifest.ActiveBcMajor = 28;
        manifest.NoteDiscoveredTestCodeunit(new DiscoveredTestCodeunit(
            60810, "Expct Fixture Tests", "Codeunit60810", new[] { "Other" }));

        Assert.Empty(manifest.FindUnmatchedEntries());
        Assert.Single(manifest.EntriesOutOfScopeFor(null));
    }

    [Fact]
    public void Audit_StillReportsAScopedEntryThatMatchesNothingOnItsOwnMajor()
    {
        // The scope exempts an entry from other majors' audits, never from its own.
        var manifest = Load($"[{Entry("T1", "[29]")}]");
        manifest.ActiveBcMajor = 29;
        manifest.NoteDiscoveredTestCodeunit(new DiscoveredTestCodeunit(
            60810, "Expct Fixture Tests", "Codeunit60810", new[] { "Other" }));

        Assert.Single(manifest.FindUnmatchedEntries());
        Assert.Empty(manifest.EntriesOutOfScopeFor(null));
    }

    [Fact]
    public void TwoEntriesForOneTest_AreAcceptedWhenTheirScopesAreDisjoint_AndEachAppliesAlone()
    {
        var manifest = Load($"[{Entry("T1", "[29]")}, {Entry("T1", "[27, 28]")}]");

        manifest.ActiveBcMajor = 29;
        Assert.Equal(new[] { 29 }, manifest.Lookup("Expct Fixture Tests", "T1")!.BcMajors);
        manifest.ActiveBcMajor = 28;
        Assert.Equal(new[] { 27, 28 }, manifest.Lookup("Expct Fixture Tests", "T1")!.BcMajors);
    }

    [Theory]
    [InlineData("[29]", "[28, 29]")]   // overlap
    [InlineData("[29]", null)]         // one unscoped: applies everywhere, so it overlaps
    public void TwoEntriesForOneTest_AreRefusedWhenTheirScopesOverlap(string first, string? second)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Load($"[{Entry("T1", first)}, {Entry("T1", second)}]"));

        Assert.Contains("Duplicate expectation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameTestInTwoFiles_IsRefusedUnlessTheScopesAreDisjoint()
    {
        var dir = TestScratch.Dir("al-runner-bcmajor-scope-files");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "known-gaps-a.json"), $"[{Entry("T1", "[29]")}]");
            File.WriteAllText(Path.Combine(dir, "known-gaps-b.json"), $"[{Entry("T1", "[28, 29]")}]");
            var ex = Assert.Throws<InvalidOperationException>(() => ExpectationManifest.LoadFromDirectory(dir));
            Assert.Contains("declared in multiple files", ex.Message, StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(dir, "known-gaps-b.json"), $"[{Entry("T1", "[28]")}]");
            Assert.Equal(2, ExpectationManifest.LoadFromDirectory(dir).Entries.Count);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Theory]
    [InlineData("[]", "present but empty")]
    [InlineData("[0]", "BC major version number >= 1")]
    [InlineData("[\"29\"]", "BC major version number >= 1")]
    [InlineData("29", "must be a JSON array")]
    public void Malformed_BcMajors_IsRefusedAtLoad(string bcMajors, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load($"[{Entry("T1", bcMajors)}]"));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }
}
