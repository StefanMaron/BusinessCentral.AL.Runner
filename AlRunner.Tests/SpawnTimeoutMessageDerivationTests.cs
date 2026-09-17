// SpawnTimeoutMessageDerivationTests — issue #3488.
//
// A subprocess spawn's TimeoutException message must report the cap that was ACTUALLY applied,
// derived from the constant, never a literal repeating it.
//
// The failure is silent and has fired for real (#3435): a cap temporarily squeezed to 3s still
// threw "al-runner did not exit within 120s" — three orders of magnitude out, with nothing to
// indicate it. That sentence is what someone reads to decide whether a CI timeout means the cap
// is too tight or the runner genuinely hung, and it is only ever produced on a path that is
// already failing, so nothing else catches a stale figure.
//
// #3487 fixed one instance (BcVersionDefaultDocumentationTests) and pinned it with a per-file
// guard. This covers the five sites #3488 lists, in ONE test rather than five copies of that
// logic: they share no code, so a per-file guard would be the same reasoning five times, and a
// sixth spawn site added tomorrow would be covered by none of them.
//
// Deliberately NOT asserting any cap's VALUE. #3488 puts raising a cap explicitly out of scope,
// and a test that pinned 120s would have to be edited by anyone legitimately changing it — which
// is the coupling this whole issue exists to remove.

using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class SpawnTimeoutMessageDerivationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// The files #3488 names, plus the #3487 precedent so a regression there is caught here too.
    /// Named rather than globbed: a glob over AlRunner.Tests would silently widen this test's
    /// subject as the suite grows, and "which spawn sites are in scope" is a decision, not a
    /// side effect of a pattern.
    /// </summary>
    private static readonly string[] Files =
    {
        "CrossMajorNoteTests.cs",
        "CountryFlagTests.cs",
        "ArtifactsRootEnvOverrideTests.cs",
        "HomeDirectoryMissingLoudFailureTests.cs",
        "BcVersionDefaultDocumentationTests.cs",

        // The 180s-cap cohort of #4275. Added as one batch because they share a cap value, so a
        // reviewer checks one figure against thirteen call sites rather than thirteen figures.
        "EventSubscriptionVirtualTableTests.cs",
        "FailedTestRollbackBoundaryTests.cs",
        "MaskedTriggerErrorDiagnosisTests.cs",
        "OlderBcVersionSelectionWithNewerProvisionedTests.cs",
        "PageOnInitTriggerTests.cs",
        "PageRowsetTriggerTests.cs",
        "PageTriggerMetadataTests.cs",
        "ProvisionExplicitModesTests.cs",
        "SessionUserRowRefusalTests.cs",
        "TableTriggerMetadataTests.cs",
        "TestPageNewRecordValidationTests.cs",
        "TestPageOnNewRecordCountTests.cs",
        "TestPageSubscriberRefusalTests.cs",
    };

    [Fact]
    public void EveryListedSpawnSite_DerivesItsTimeoutFigureFromTheCapItApplied()
    {
        var offenders = new List<string>();
        var sitesChecked = 0;

        foreach (var file in Files)
        {
            var path = Path.Combine(RepoRoot, "AlRunner.Tests", file);
            Assert.True(File.Exists(path), $"{file} no longer exists; update this test's list deliberately");
            var source = File.ReadAllText(path);

            // Anchored on the throw STATEMENT, not on message text: the phrase "did not exit
            // within" also appears in comments, and scanning for it would pass or fail on where
            // the prose sits. Assembled rather than written whole so this file's own comments
            // cannot match when it is itself scanned.
            var anchor = "throw new " + nameof(TimeoutException) + "(";
            for (var i = source.IndexOf(anchor, StringComparison.Ordinal); i >= 0;
                 i = source.IndexOf(anchor, i + 1, StringComparison.Ordinal))
            {
                var end = source.IndexOf(");", i, StringComparison.Ordinal);
                if (end <= i) { offenders.Add($"{file}: a throw statement does not terminate"); continue; }
                var stmt = source[i..end];

                // Only the spawn-timeout throws are in scope; a TimeoutException thrown for some
                // other reason has no cap to report.
                if (!stmt.Contains("within", StringComparison.Ordinal)) continue;
                sitesChecked++;

                if (!stmt.Contains("SpawnTimeoutMs", StringComparison.Ordinal))
                {
                    offenders.Add($"{file}: a timeout message does not mention SpawnTimeoutMs: {Compact(stmt)}");
                    continue;
                }

                // Co-occurrence is weaker than derivation, and the gap is reachable by accident:
                // appending the constant to an otherwise-hardcoded message ("...within 120s...
                // (cap {SpawnTimeoutMs})") mentions it while the figure a reader acts on is still
                // a literal. So the statement may carry NO standalone number but the 1000 that
                // converts milliseconds to seconds, and the 0/1 of a format placeholder.
                //
                // Leading guard only: the literal that matters is spelled "120s", glued to a
                // letter, so requiring a non-word character AFTER the digits would skip exactly
                // the case being forbidden.
                var digits = Regex.Matches(stmt, @"(?<![\w.])\d+")
                    .Select(m => m.Value)
                    .Where(v => v is not ("1000" or "0" or "1"))
                    .ToArray();
                if (digits.Length > 0)
                    offenders.Add($"{file}: literal number(s) {string.Join(", ", digits)} in {Compact(stmt)}");

                // A placeholder only interpolates in a $-prefixed string. Without the $ the
                // reader is shown the BRACES — "did not exit within {SpawnTimeoutMs / 1000}s" —
                // and the two checks above both pass, because the statement does mention the
                // constant and carries no literal but the 1000. Caught while writing #4275: the
                // edit that adds the placeholder and the edit that adds the $ are separate, so
                // this is the state 11 of 13 files were briefly in.
                foreach (var seg in Regex.Matches(stmt, @"(?<prefix>\$?)""(?<body>(?:[^""\\]|\\.)*)""")
                             .Where(sm => sm.Groups["body"].Value.Contains("SpawnTimeoutMs", StringComparison.Ordinal))
                             .Where(sm => sm.Groups["prefix"].Value.Length == 0))
                    offenders.Add($"{file}: {{SpawnTimeoutMs}} sits in a string with no $ prefix, so the braces "
                                  + $"are printed rather than the cap: {Compact(seg.Value)}");
            }
        }

        // The population must be non-empty, or an empty scan reads as a pass. #3488 lists five
        // sites, #3487 contributes one, and #4275's 180s cohort thirteen.
        Assert.True(sitesChecked >= 19,
            $"expected at least 19 spawn-timeout throw sites across {Files.Length} files, found {sitesChecked} — "
            + "the anchor stopped matching, so this test measured almost nothing");

        Assert.True(offenders.Count == 0,
            "a spawn timeout message must DERIVE its figure from the cap actually applied (#3488):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static string Compact(string s) =>
        Regex.Replace(s, @"\s+", " ").Trim() is { Length: > 120 } long_ ? long_[..120] + "…" : Regex.Replace(s, @"\s+", " ").Trim();
}
