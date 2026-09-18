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

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        // #4275 widening 1: failure paths that ASSERT rather than throw. The guard's anchor was
        // `throw new TimeoutException(`, so these nine were outside it entirely — each spells its
        // own cap twice, once in the wait and once in the message.
        "CoverageDependencySourceTests.cs",
        "CoverageTests.cs",
        "HandlerLoopJitTierGuardTests.cs",
        "PlainRunInstrumentationGateTests.cs",
        "PrecompileEngineVariantSelectionTests.cs",
        "PrecompileNclShadowHopTests.cs",
        "StartupJitModeTests.cs",
        "StartupOutputReexecDedupTests.cs",

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

        // The 300s-cap cohort of #4275, batched on the same principle as the two above.
        "BundleInstallTriggerSeedVisibilityTests.cs",
        "CacheGateProbeScopeTests.cs",
        "DepInstallTriggerSessionIdentityTests.cs",
        "EventSubscriptionMultiBundleScopeTests.cs",
        "InstallExecutionContextTests.cs",
        "InstallTriggerSessionIdentityTests.cs",

        // The last six of #4275, spanning three caps (240s x3, 60s x2, 600s x1). Batched together
        // rather than by cap: the population is now small enough that three PRs of two files each
        // would be more review overhead than the figures they carry.
        "AutoProvisionDefaultTests.cs",
        "CliDocumentationTests.cs",
        "DefaultProvisionTargetMessagingTests.cs",
        "EmitAppPackageCacheRefusalTests.cs",
        "HomeRootedPathsEnvOverrideTests.cs",
        "OutputPathPreparationTests.cs",

        // The 120s-cap cohort of #4275, batched on the same principle as the 180s one above.
        "ActiveSessionTableTests.cs",
        "AggregatePermissionSetVirtualTableTests.cs",
        "CodeunitMetadataVirtualTableTests.cs",
        "EngineMajorConsistencyTests.cs",
        "FeatureKeyVirtualTableTests.cs",
        "PermissionMetadataPopulationTests.cs",
        "SessionVirtualTableTests.cs",
        "TimeZoneVirtualTableTests.cs",
        "WindowsLanguageVirtualTableTests.cs",
    };

    [Fact]
    public void EveryListedSpawnSite_DerivesItsTimeoutFigureFromTheCapItApplied()
    {
        var offenders = new List<string>();
        var sitesPerFile = new Dictionary<string, int>();

        foreach (var file in Files)
        {
            var path = Path.Combine(RepoRoot, "AlRunner.Tests", file);
            Assert.True(File.Exists(path), $"{file} no longer exists; update this test's list deliberately");
            var source = File.ReadAllText(path);

            // Anchored on the throw STATEMENT, not on message text: the phrase "did not exit
            // within" also appears in comments, and scanning for it would pass or fail on where
            // the prose sits. Assembled rather than written whole so this file's own comments
            // cannot match when it is itself scanned.
            foreach (var (i, anchor) in FailureSites(source))
            {
                var end = source.IndexOf(");", i, StringComparison.Ordinal);
                if (end <= i) { offenders.Add($"{file}: a {anchor} statement does not terminate"); continue; }
                var stmt = source[i..end];

                // Only the spawn-timeout throws are in scope; a TimeoutException thrown for some
                // other reason has no cap to report.
                if (!stmt.Contains("within", StringComparison.Ordinal)) continue;
                sitesPerFile[file] = sitesPerFile.GetValueOrDefault(file) + 1;

                // The property is "the figure is DERIVED from the cap this site applied", not
                // "the identifier is spelled SpawnTimeoutMs". A file with two genuinely different
                // caps needs two names — DefaultProvisionTargetMessagingTests bounds the whole
                // spawn with SpawnTimeoutMs and watches stderr with LineWatchTimeoutMs — and
                // keying on one literal name rejected the correctly-derived second one (#4275).
                //
                // So: some identifier ending in TimeoutMs, which is this assembly's convention for
                // a cap constant, and it must appear in the DIVISION that produces the figure, not
                // merely somewhere in the statement. The `/ 1000` half is what makes this stronger
                // than co-occurrence — a name mentioned in passing does not satisfy it.
                var derivesFromACap = Regex.IsMatch(stmt, @"\b\w*TimeoutMs\s*/\s*1000\b");
                if (!derivesFromACap)
                {
                    offenders.Add($"{file}: a timeout message does not derive its figure from a "
                                  + $"*TimeoutMs cap: {Compact(stmt)}");
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
                // The test is "does this string interpolate", so the predicate asks for the $ —
                // it is NOT a test of prefix length. A verbatim interpolated string is spelled
                // both $@"..." and @$"...", so the character class has to admit the @; but with
                // a length test, admitting it also exonerates @"...{SpawnTimeoutMs}...", which
                // is verbatim and NOT interpolated and prints the braces. Widening the class
                // while keeping Length == 0 trades a loud false positive for a SILENT false
                // negative, the worse direction (caught in review of the first attempt at this
                // fix, #4286). The [Theory] below pins all five spellings.
                foreach (var seg in NonInterpolatedSpawnTimeoutStrings(stmt))
                    offenders.Add($"{file}: {{SpawnTimeoutMs}} sits in a string with no $ prefix, so the braces "
                                  + $"are printed rather than the cap: {Compact(seg)}");
            }
        }

        // EVERY listed file must contribute at least one site, per file rather than in total.
        //
        // A `sitesChecked >= N` floor was the earlier form and it has slack, because a total
        // cannot say WHICH files contributed it: one file losing its site is fungible with
        // another gaining one, and the population already contains a two-site file
        // (ArtifactsRootEnvOverrideTests), which is why 33 files yield 34 sites. Measured in
        // review of #4307 — reword one file's message so `within` stops matching (caught), add a
        // second correctly-derived site elsewhere (green again), then regress the first file to a
        // hardcoded literal: STILL GREEN, with a file in this very list carrying exactly the
        // spelling this test forbids.
        //
        // "at least one", not "exactly one": the two-site file is legitimate —
        // ArtifactsRootEnvOverrideTests spawns al-runner AND `dotnet msbuild -getProperty`. This
        // also retires the >= N constant, which every cohort had to edit — one fewer thing to get
        // right.
        //
        // THE REMAINING BLIND SPOT, and it is the only one: this proves every listed file is
        // MEASURED, never that every site WITHIN a file is. A two-site file losing one site while
        // keeping the other still contributes, so a regression in the lost one is invisible.
        // Deliberately not closed: a per-file expected-count map would reintroduce exactly the
        // per-cohort constant retired above (#4307).
        // Two causes, different fixes, so the message says which: a file with NO
        // TimeoutException at all has lost its spawn (or never had one), while a file that still
        // throws one but contributes no site has a message the `within` anchor no longer matches.
        var silent = Files
            .Where(f => sitesPerFile.GetValueOrDefault(f) == 0)
            .Select(f =>
            {
                // Since #4275 a listed file may report its timeout by ASSERTING rather than
                // throwing, so "no throw here" is no longer the same statement as "no spawn-timeout
                // site here" — saying the first about an asserting file sends the reader looking
                // for a throw that was never supposed to exist.
                var text = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner.Tests", f));
                var hasSite = FailureSites(text).Any();
                return hasSite
                    ? $"{f}: has a failure path that could report a timeout, but no message the "
                      + $"`within` anchor matches — reworded?"
                    : $"{f}: no spawn-timeout site at all — the spawn moved, or the file no longer "
                      + $"has one";
            })
            .ToArray();
        Assert.True(silent.Length == 0,
            "these listed files contributed NO spawn-timeout throw site, so this test measured "
            + "nothing about them. Restore the site, or remove the file from the list deliberately:"
            + Environment.NewLine + string.Join(Environment.NewLine, silent));

        Assert.True(offenders.Count == 0,
            "a spawn timeout message must DERIVE its figure from the cap actually applied (#3488):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The five string spellings a timeout message can carry, and whether each one actually
    /// INTERPOLATES. Two of them print the braces verbatim and must be reported; three are real
    /// interpolations and must not be.
    ///
    /// Pinned as data rather than left to the scan, because both wrong answers have shipped in
    /// this file: `\$?` flagged the correct `$@"..."` (a loud false positive, #4278), and
    /// `[$@]*` with a LENGTH test exonerated the broken `@"..."` (a silent false negative, found
    /// in review of #4286). One is noisy and one is the defect this whole guard exists to catch,
    /// so a change that fixes either direction has to be checked against the other.
    /// </summary>
    [Theory]
    [InlineData("\"within {SpawnTimeoutMs / 1000}s.\"", true)]      // bare: prints the braces
    [InlineData("@\"within {SpawnTimeoutMs / 1000}s.\"", true)]     // verbatim, NOT interpolated
    [InlineData("$\"within {SpawnTimeoutMs / 1000}s.\"", false)]    // interpolated
    [InlineData("$@\"within {SpawnTimeoutMs / 1000}s.\"", false)]   // verbatim interpolated
    [InlineData("@$\"within {SpawnTimeoutMs / 1000}s.\"", false)]   // the other spelling of it
    // Raw strings. The regex this replaced matched on PAIRS of quotes, so it split \"\"\"...\"\"\"
    // into an empty match plus a bare-looking middle and reported all three of these — two of
    // them wrongly. A token walk sees one token per literal and one node per interpolation.
    [InlineData("\"\"\"within {SpawnTimeoutMs / 1000}s.\"\"\"", true)]    // raw, NOT interpolated
    [InlineData("$\"\"\"within {SpawnTimeoutMs / 1000}s.\"\"\"", false)]  // raw interpolated
    [InlineData("$$\"\"\"within {{SpawnTimeoutMs / 1000}}s.\"\"\"", false)] // raw, two-dollar form
    public void TheScan_ReportsExactlyTheSpellingsThatDoNotInterpolate(string literal, bool expectedReported)
    {
        var stmt = "throw new TimeoutException(" + literal + ");";

        var reported = NonInterpolatedSpawnTimeoutStrings(stmt).Any();

        Assert.Equal(expectedReported, reported);
    }

    /// <summary>
    /// The string literals in <paramref name="stmt"/> that mention SpawnTimeoutMs and do NOT
    /// interpolate — so the braces reach the reader verbatim.
    ///
    /// Roslyn rather than a regex, because the regex could not see raw strings: it matched on
    /// pairs of quotes, so `"""..."""` split into an empty match plus a bare-looking middle and
    /// the interpolated `$"""` / `$$"""` forms were reported as if they printed their braces.
    /// A token walk has no such blind spot — interpolated text is an InterpolatedStringTextToken
    /// rather than a literal, and every raw form is its own token kind (#3527, #4275).
    ///
    /// One implementation, read by the scan AND by the [Theory]. An earlier revision gave the
    /// [Theory] its own copy of the matching logic, which made it unable to fail: a mutation of
    /// the scan left every case green (a pin must read production code).
    /// </summary>
    /// <summary>
    /// Every offset in <paramref name="source"/> where a failure path that reports a timeout
    /// begins, with the anchor that matched.
    ///
    /// <para>A `throw` was the only anchor until #4275's first widening, and a failure path that
    /// ASSERTS was invisible to it — nine sites across eight files, each spelling its cap twice:
    /// <c>Assert.True(p.WaitForExit(240_000), "runner did not exit within 240s")</c>. The
    /// hardcoded figure is the defect whether a throw or an assert carries it, so the anchor is
    /// about *reporting a timeout*, not about the statement kind.</para>
    ///
    /// <para>Trap: `Assert.True(` is far more common than the throw was, and most uses have
    /// nothing to do with timeouts. The caller's existing `within` filter is what keeps the
    /// population honest — it runs on the matched statement, so a non-timeout assertion is
    /// skipped there rather than here. Widening the anchor without that filter would put every
    /// assertion in this assembly into the population.</para>
    /// </summary>
    private static IEnumerable<(int Offset, string Anchor)> FailureSites(string source)
    {
        var anchors = new[]
        {
            "throw new " + nameof(TimeoutException) + "(",
            "Assert.True(",
        };

        foreach (var anchor in anchors)
            for (var i = source.IndexOf(anchor, StringComparison.Ordinal); i >= 0;
                 i = source.IndexOf(anchor, i + 1, StringComparison.Ordinal))
                yield return (i, anchor);
    }

    private static IEnumerable<string> NonInterpolatedSpawnTimeoutStrings(string stmt)
    {
        foreach (var node in CSharpSyntaxTree.ParseText(stmt).GetRoot().DescendantNodes())
        {
            // Only a NON-interpolated literal is a LiteralExpressionSyntax. The text inside an
            // interpolated string of any spelling — $"...", $@"...", @$"...", $"""...""",
            // $$"""...""" — is an InterpolatedStringTextToken hanging off an
            // InterpolatedStringExpressionSyntax, so it cannot reach this branch at all. That is
            // what the walk buys over the quote-pair regex it replaced, and it needs no explicit
            // skip: an earlier revision carried `if (node is InterpolatedStringExpressionSyntax)
            // continue;`, and review measured it as DEAD — deleting it changed no answer at any
            // spawn site in this assembly, nor on a set of adversarial spellings. (A literal
            // inside a `{…}` hole is a different node and IS still reported, correctly: it does
            // print its braces.)
            if (node is LiteralExpressionSyntax lit
                && lit.Token.Text.Contains("SpawnTimeoutMs", StringComparison.Ordinal))
                yield return lit.Token.Text;
        }
    }

    private static string Compact(string s) =>
        Regex.Replace(s, @"\s+", " ").Trim() is { Length: > 120 } long_ ? long_[..120] + "…" : Regex.Replace(s, @"\s+", " ").Trim();
}
