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

using Microsoft.CodeAnalysis;
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
        "DapPreLaunchBreakpointTests.cs",
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

            foreach (var site in ScanSites(File.ReadAllText(path)))
            {
                if (site.IsInScope) sitesPerFile[file] = sitesPerFile.GetValueOrDefault(file) + 1;
                foreach (var complaint in site.Offenders) offenders.Add($"{file}: {complaint}");
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
    /// No listed file may carry a hardcoded <c>within &lt;N&gt;s</c> anywhere in its CODE.
    ///
    /// <para>Per-SITE, which is what the scan above is not. That scan proves every listed file is
    /// MEASURED — at least one site each — never that every site WITHIN a file is: a two-site file
    /// losing one site still contributes, so a regression in the lost one is invisible. Measured
    /// on this tree before the check existed: a hardcoded <c>within 90s</c> added to a listed file
    /// left the whole suite GREEN at <c>Failed: 0, Passed: 9</c> (#4332).</para>
    ///
    /// <para>Needs no count constant, which is the point. A per-file expected-count map would
    /// close the same gap and reintroduce exactly the per-cohort constant #4307 retired — every
    /// cohort would have to edit a number, which is the coupling this whole guard exists to
    /// remove. "Zero of this spelling, anywhere" carries no figure to maintain.</para>
    ///
    /// <para>Scanned with comments BLANKED and literals KEPT. Both halves are load-bearing and in
    /// opposite directions: literals must survive because the defect IS a literal, so
    /// <c>CodeOnly</c> would blank the very thing being looked for; comments must go because the
    /// #3435 narrative is quoted in most of these files' doc comments, and a raw text scan
    /// reports every one of them. The property, not a count: over the listed files every raw
    /// match is prose, and this check reports zero of them.</para>
    ///
    /// <para>The anchor set does not bound this one. The scan above can only see a literal inside
    /// a statement one of its three anchors matched, so a figure in a helper that BUILDS a message
    /// — or in any statement shape not yet anchored — is invisible to it while being exactly as
    /// stale. That is the gap the 90s measurement above fell into: an <c>Assert.True</c> carrying
    /// it is caught by the existing digits check, a plain assignment is not.</para>
    /// </summary>
    [Fact]
    public void NoListedFile_HardcodesATimeoutFigureInCode()
    {
        var offenders = new List<string>();

        foreach (var file in Files)
        {
            var path = Path.Combine(RepoRoot, "AlRunner.Tests", file);
            Assert.True(File.Exists(path), $"{file} no longer exists; update this test's list deliberately");

            var code = CSharpSource.CommentsBlanked(File.ReadAllText(path));

            foreach (Match hit in Regex.Matches(code, @"within \d+s"))
            {
                var line = code.Take(hit.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{file}:{line}: hardcoded \"{hit.Value}\" — derive the figure from the "
                              + $"cap this site applies, e.g. $\"within {{SomeTimeoutMs / 1000}}s\"");
            }
        }

        Assert.True(offenders.Count == 0,
            "a timeout figure must be DERIVED from the cap applied, never spelled as a literal "
            + "(#3488, #4332). These are in the code of files this guard lists, so the figure goes "
            + "stale the moment someone moves the cap and nothing says so:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Synthetic sources that separate METHOD scope from the STATEMENT scope it replaced, so the
    /// widening is pinned by a RED rather than by the live tree happening to be clean.
    ///
    /// <para>Why a fixture and not the tree: the tree carries ZERO cross-wires across all 51
    /// sites, so both scopes agree everywhere on it and mutating <see cref="EnclosingMethod"/> to
    /// <c>return null</c> left the suite GREEN. The check the widening exists to make possible had
    /// therefore never been shown to fire (#4332, found in review). These cases make it fire.</para>
    ///
    /// <para>Measured over these cases: every <c>CrossWire_</c> case is reported under METHOD
    /// scope, and <c>CrossWire_SameStatement</c> is the only one STATEMENT scope also sees —
    /// the rest put wait and message in different statements, which is the shape the live tree
    /// is made of (a <c>throw</c> inside <c>if (!WaitForExit(...))</c>, a <c>try</c>/<c>catch</c>
    /// pair). Every <c>Correct_</c> case must stay SILENT under both, which is what stops
    /// "catches more" from being satisfied by a check that simply reports everything.</para>
    ///
    /// <para><c>Correct_LocalFunctionIgnoresOuterWait</c> is one of the silent ones, not a
    /// cross-wire: the <see cref="LocalFunctionStatementSyntax"/> clause is load-bearing only in
    /// the FALSE-POSITIVE direction. Dropping it makes the walk reach <c>Outer</c> and report a
    /// cross-wire the local function does not have; it can never make a real one go unreported,
    /// because <c>Outer.ToString()</c> contains the local function's text and so the cap is found
    /// either way.</para>
    /// </summary>
    [Theory]
    // Wait and message in DIFFERENT statements of one method — invisible to statement scope, and
    // the shape 42 of the 51 live sites have.
    [InlineData("CrossWire_TwoStatements", """
        class C {
          void M() {
            var ok = p.WaitForExit(SpawnTimeoutMs);
            if (!ok) throw new TimeoutException($"runner did not exit within {LineWatchTimeoutMs / 1000}s");
          }
        }
        """, true)]
    // The same two statements, correctly wired. Proves the check reports a CROSS-wire rather than
    // "the wait is not in this statement".
    [InlineData("Correct_TwoStatements", """
        class C {
          void M() {
            var ok = p.WaitForExit(SpawnTimeoutMs);
            if (!ok) throw new TimeoutException($"runner did not exit within {SpawnTimeoutMs / 1000}s");
          }
        }
        """, false)]
    // Wait and message in ONE statement: the only shape statement scope ever saw, kept so a
    // regression that loses the narrow case is still caught.
    [InlineData("CrossWire_SameStatement", """
        class C {
          void M() {
            Assert.True(p.WaitForExit(SpawnTimeoutMs), $"runner did not exit within {LineWatchTimeoutMs / 1000}s");
          }
        }
        """, true)]
    // Two caps in two methods, each correctly wired — the live two-cap file's shape. Method scope
    // must separate them; a file-scoped check would intersect both and report neither, and a
    // check that reported everything would red this.
    [InlineData("Correct_TwoMethodsTwoCaps", """
        class C {
          void M1() {
            var a = p.WaitForExit(SpawnTimeoutMs);
            if (!a) throw new TimeoutException($"runner did not exit within {SpawnTimeoutMs / 1000}s");
          }
          void M2() {
            var b = q.WaitForExit(LineWatchTimeoutMs);
            if (!b) throw new TimeoutException($"watch did not fire within {LineWatchTimeoutMs / 1000}s");
          }
        }
        """, false)]
    // Wait in the try, message in the catch — the Assert.Fail shape, a statement away by
    // construction.
    [InlineData("CrossWire_TryCatch", """
        class C {
          void M() {
            try { t.Wait(SpawnTimeoutMs); }
            catch { Assert.Fail($"did not settle within {LineWatchTimeoutMs / 1000}s"); }
          }
        }
        """, true)]
    // A local function is the scope, not the method around it. Without
    // LocalFunctionStatementSyntax the walk reaches Outer, finds ITS wait, and reports a
    // cross-wire the local function does not have — a spurious red. Measured: dropping the clause
    // turns this case from clean to reported.
    [InlineData("Correct_LocalFunctionIgnoresOuterWait", """
        class C {
          void Outer() {
            var outerWait = q.WaitForExit(SpawnTimeoutMs);
            void Inner() {
              throw new TimeoutException($"runner did not exit within {LineWatchTimeoutMs / 1000}s");
            }
            Inner();
          }
        }
        """, false)]
    public void TheCrossWireCheck_ScopesToTheEnclosingMethod(string name, string source, bool expectedCrossWire)
    {
        var crossWires = ScanSites(source)
            .SelectMany(s => s.Offenders)
            .Where(o => o.StartsWith("waits on", StringComparison.Ordinal))
            .ToArray();

        Assert.True(expectedCrossWire == (crossWires.Length > 0),
            $"{name}: expected cross-wire={expectedCrossWire}, got {crossWires.Length}:"
            + Environment.NewLine + string.Join(Environment.NewLine, crossWires));
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

    /// <summary>One spawn-timeout site the scan found, and what it has to say about it.</summary>
    private readonly record struct Site(bool IsInScope, IReadOnlyList<string> Offenders);

    /// <summary>
    /// Every spawn-timeout site in <paramref name="source"/>, with the complaints each one earns.
    ///
    /// <para>Taking SOURCE rather than a file path is what makes the cross-wire rule testable.
    /// The live tree carries zero cross-wires — that is the finding of #4332, not a coincidence —
    /// so on this tree the method-scoped rule and the statement-scoped one it replaced return the
    /// same answer at all 51 sites, and a mutation of <see cref="EnclosingMethod"/> to
    /// <c>return null</c> stayed GREEN. A guard whose correctness rests on the tree not growing a
    /// defect is not pinned: the day someone writes the cross-wire, the check that was supposed to
    /// catch it has never once been shown to. The [Theory] below feeds it synthetic sources that
    /// discriminate the two scopes, so the widening is proven by a RED rather than by the absence
    /// of one.</para>
    /// </summary>
    private static IEnumerable<Site> ScanSites(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();

        // Anchored on the throw STATEMENT, not on message text: the phrase "did not exit
        // within" also appears in comments, and scanning for it would pass or fail on where
        // the prose sits. Assembled rather than written whole so this file's own comments
        // cannot match when it is itself scanned.
        foreach (var (i, anchor) in FailureSites(source))
        {
            var end = source.IndexOf(");", i, StringComparison.Ordinal);
            if (end <= i)
            {
                yield return new Site(false, new[] { $"a {anchor} statement does not terminate" });
                continue;
            }
            var stmt = source[i..end];

            // Only the spawn-timeout throws are in scope; a TimeoutException thrown for some
            // other reason has no cap to report.
            if (!stmt.Contains("within", StringComparison.Ordinal)) continue;

            var offenders = new List<string>();

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
            var reported = Regex.Matches(stmt, @"\b(\w*TimeoutMs)\s*/\s*1000\b")
                .Select(m => m.Groups[1].Value).Distinct().ToArray();
            if (reported.Length == 0)
            {
                offenders.Add($"a timeout message does not derive its figure from a "
                              + $"*TimeoutMs cap: {Compact(stmt)}");
                yield return new Site(true, offenders);
                continue;
            }

            // Deriving from SOME cap is not deriving from THIS site's cap. Dropping the exact
            // name made that reachable for the first time: cross-wiring a site so it waits on
            // one constant and reports another passed, leaving a message that says 30s beside
            // a 60s wait — the very shape #3435 measured, now spelled with two correct-looking
            // identifiers (found in review of #4275).
            //
            // Scoped to the ENCLOSING METHOD, not to the statement. Statement scope saw only
            // a cap the message's own statement applied — 9 of 51 sites — because a `throw`
            // lives in an `if (!p.WaitForExit(...))` body and the Assert.Fail site waits in a
            // `try` and reports in the `catch`, so the wait is a statement away by
            // construction (#4332).
            //
            // A line window was the obvious widening and is rejected: the window size would
            // be a constant with no principle behind it, and a wait can precede its message
            // by any distance. A method declaration is a syntax node, so it bounds the search
            // without a number and without a distance limit.
            //
            // Measured at the commit that made this change: all 42 previously-blind sites
            // have an enclosing method that applies a cap (0 fall through), and no enclosing
            // method applies more than one DISTINCT cap — so widening the scope adds no slack
            // here, it only removes the blindness. The two-cap file the comment above names
            // keeps its two caps in two different methods, which is why the method boundary
            // separates them where a file-scoped check would not.
            var applied = AppliedCaps(EnclosingMethod(root, i) ?? stmt);
            var crossed = applied.Length > 0 && !applied.Intersect(reported).Any();
            if (crossed)
                offenders.Add($"waits on {string.Join("/", applied)} but reports "
                              + $"{string.Join("/", reported)} — the figure is derived from a "
                              + $"cap this site does not apply: {Compact(stmt)}");

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
                offenders.Add($"literal number(s) {string.Join(", ", digits)} in {Compact(stmt)}");

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
                offenders.Add($"{{SpawnTimeoutMs}} sits in a string with no $ prefix, so the braces "
                              + $"are printed rather than the cap: {Compact(seg)}");

            yield return new Site(true, offenders);
        }
    }

    /// <summary>
    /// Every offset in <paramref name="source"/> where a failure path that reports a timeout
    /// begins, with the anchor that matched. Scanned as TEXT, deliberately — the `within` filter
    /// in the caller is what decides membership, and a token walk here would buy nothing it does
    /// not already get.
    ///
    /// <para>A `throw` was the only anchor until #4275's first widening, and a failure path that
    /// ASSERTS was invisible to it — nine sites across eight files, each spelling its cap twice:
    /// <c>Assert.True(p.WaitForExit(240_000), "runner did not exit within 240s")</c>. The
    /// hardcoded figure is the defect whether a throw or an assert carries it, so the anchor is
    /// about *reporting a timeout*, not about the statement kind.</para>
    ///
    /// <para>The set is three spellings and was two until review: `Assert.Fail(` carried a live
    /// hardcoded figure in DapPreLaunchBreakpointTests, invisible because I had pinned the two
    /// spellings in front of me rather than the population. Scanned by the OBSERVABLE — every
    /// `within &lt;N&gt;s` literal in the assembly — `Assert.False(` and a bare `.Wait(` carry zero
    /// live sites, so this set is complete as measured rather than as guessed (#4275).</para>
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
            "Assert.Fail(",
        };

        foreach (var anchor in anchors)
            for (var i = source.IndexOf(anchor, StringComparison.Ordinal); i >= 0;
                 i = source.IndexOf(anchor, i + 1, StringComparison.Ordinal))
                yield return (i, anchor);
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

    /// <summary>
    /// The cap constants applied by a wait in <paramref name="scope"/>.
    ///
    /// <para>One implementation, read by the cross-wire check with a METHOD as its scope. Kept
    /// separate from the call site so the scope is a visible argument rather than a regex written
    /// twice against two different strings — the shape that let the statement-scoped version go
    /// unnoticed at 9 of 51 sites (#4332).</para>
    /// </summary>
    private static string[] AppliedCaps(string scope) =>
        Regex.Matches(scope, @"(?:WaitForExit|Wait|FromMilliseconds)\(\s*(\w*TimeoutMs)\s*\)")
            .Select(m => m.Groups[1].Value).Distinct().ToArray();

    /// <summary>
    /// The source of the method declaration enclosing <paramref name="offset"/>, or null when the
    /// offset is not inside one.
    ///
    /// <para>Null rather than a fallback to the whole file: a site outside any method is a shape
    /// this guard has never seen, and silently widening to file scope would let a cap applied in
    /// an unrelated method satisfy it. The caller falls back to the STATEMENT, which is the
    /// narrower of the two and therefore cannot manufacture a pass (guards-need-a-third-state.md
    /// — the degraded answer must not be the permissive one).</para>
    ///
    /// <para>A local function counts as a method, and the clause is load-bearing in the direction
    /// that produces FALSE POSITIVES. Without it the walk continues to the enclosing method and
    /// picks up ITS wait, so a local function reporting a cap it never applies is blamed for the
    /// outer method's constant. Pinned by <c>Correct_LocalFunctionIgnoresOuterWait</c>, which goes
    /// from clean to reported when the clause is dropped — no live site exercises it, so the
    /// fixture is the only thing holding it (#4332).</para>
    /// </summary>
    private static string? EnclosingMethod(SyntaxNode root, int offset)
    {
        for (var n = root.FindToken(offset).Parent; n != null; n = n.Parent)
            if (n is MethodDeclarationSyntax or LocalFunctionStatementSyntax)
                return n.ToString();
        return null;
    }

    private static string Compact(string s) =>
        Regex.Replace(s, @"\s+", " ").Trim() is { Length: > 120 } long_ ? long_[..120] + "…" : Regex.Replace(s, @"\s+", " ").Trim();
}
