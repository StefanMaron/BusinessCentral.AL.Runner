// JmpHookAuditCollectionGuardTests — #5188.
//
// JmpHookAuditSerialCollection only protects classes that join it. A new class that resets or
// reads the JmpHook audit sets without joining reintroduces the race, and the symptom is a test
// that fails about one run in twenty, which reads as a flake. This reads the test SOURCES
// (comments and string literals blanked by CSharpSource) because the claim is about what a
// future editor writes, and the file is where they write it.
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class JmpHookAuditCollectionGuardTests
{
    private static readonly string TestsDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AlRunner.Tests"));

    // Every JmpHook member that adds to, reads or clears the two audit sets.
    private static readonly Regex AuditApi = new(
        @"\bJmpHook\s*\.\s*(ResetOrphanAudit|RecordIndirectInstallSkip|OrphanedHooks|RedundantHooks|Apply|InstallIndirect)\b",
        RegexOptions.Compiled);

    private static readonly Regex JoinsTheCollection = new(
        @"\[\s*Collection\s*\(\s*JmpHookAuditSerialCollection\s*\.\s*Name\s*\)\s*\]",
        RegexOptions.Compiled);

    /// <summary>Over TEXT so both predicates can be tested against synthetic inputs.</summary>
    internal static bool TouchesTheAuditSets(string sourceText) =>
        AuditApi.IsMatch(CSharpSource.CodeOnly(sourceText));

    internal static bool JoinsTheAuditCollection(string sourceText) =>
        JoinsTheCollection.IsMatch(CSharpSource.CodeOnly(sourceText));

    private static IEnumerable<string> AuditTouchingSources() =>
        Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => AuditApi.IsMatch(CSharpSource.ReadCodeOnly(p)));

    [Fact]
    public void TheGuardCanSeeTheTestSources_SoAnEmptyResultIsNotAFalsePass()
    {
        Assert.True(Directory.Exists(TestsDir), $"cannot see the test sources at '{TestsDir}'");
        // JmpHookOrphanAuditTests and JmpHookInstallIndirectGuardTests are the two known callers.
        var found = AuditTouchingSources().Select(Path.GetFileName).ToList();
        Assert.True(found.Count >= 2,
            "expected at least JmpHookOrphanAuditTests.cs and JmpHookInstallIndirectGuardTests.cs, found: "
            + string.Join(", ", found) + ". Either the probe stopped seeing the sources or a caller was "
            + "legitimately deleted; check which before lowering this floor.");
    }

    [Fact]
    public void EveryClassTouchingTheAuditSets_JoinsTheSerialCollection()
    {
        var offenders = AuditTouchingSources()
            .Where(p => !JoinsTheAuditCollection(File.ReadAllText(p)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "these test classes touch JmpHook's process-wide audit sets but do not join "
            + $"[Collection({nameof(JmpHookAuditSerialCollection)}.Name)], so xunit runs them in parallel "
            + "with a class whose ResetOrphanAudit() clears the entries they just added (#5188): "
            + string.Join(", ", offenders));
    }

    // ── The predicates themselves: a control that fires and a negative per arm ──────────

    [Fact]
    public void RealAuditCalls_AreSeen_AndComments_AndStrings_AreNot()
    {
        Assert.True(TouchesTheAuditSets("class C { void M() { JmpHook.ResetOrphanAudit(); } }"));
        Assert.True(TouchesTheAuditSets("class C { void M() { var x = JmpHook.RedundantHooks; } }"));
        Assert.False(TouchesTheAuditSets("class C { /* JmpHook.ResetOrphanAudit() */ }"));
        Assert.False(TouchesTheAuditSets("class C { string s = \"JmpHook.OrphanedHooks\"; }"));
    }

    [Fact]
    public void OnlyTheRealAttribute_CountsAsJoining()
    {
        Assert.True(JoinsTheAuditCollection(
            "[Collection(JmpHookAuditSerialCollection.Name)]\npublic class C { }"));
        Assert.False(JoinsTheAuditCollection("public class C { }"));
        Assert.False(JoinsTheAuditCollection(
            "[Collection(RecordPatchesSerialCollection.Name)]\npublic class C { }"));
        Assert.False(JoinsTheAuditCollection(
            "// [Collection(JmpHookAuditSerialCollection.Name)]\npublic class C { }"));
    }
}
