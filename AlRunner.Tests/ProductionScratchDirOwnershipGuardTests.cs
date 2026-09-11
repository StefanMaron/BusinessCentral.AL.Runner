using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// The production-side counterpart to <see cref="ScratchDirOwnershipGuardTests"/>, which scans
/// <c>AlRunner.Tests/</c> only. Nothing checked that <c>AlRunner/</c> owns the scratch
/// directories it creates, so a leaking production site was invisible until somebody read the
/// file — which is exactly how #3838 was found, by hand while sweeping the test side, not by
/// any guard (#3850).
///
/// A separate class rather than a widened scan, for two reasons that are not stylistic:
///
///   * The REMEDY differs, so the message must. The test-side guard says "use TestScratch",
///     which does not exist for production code. This one names ScratchDirs.Create / Reserve /
///     PerProcessScratch.Dir and says which to pick.
///   * The legitimate-exception population differs IN KIND. The test side's allowlist is three
///     variants of "this path must not exist". Production's exceptions are mostly directories
///     that MUST NOT be owned — content-addressed caches whose whole purpose is to outlive the
///     process that created them. An <c>.owner</c> sidecar on one of those would destroy it on
///     the first sweep after the creating process exits, so "add ownership" is the WRONG advice
///     there, not merely unnecessary. A single category would tell the next reader that a
///     deliberate design is an oversight.
///
/// So <see cref="Why"/> is an enum, not a string, and every entry declares which kind it is.
///
/// <para><b>What this guard does NOT claim.</b> It is a source-text scan: it proves that every
/// site naming a temp location has been classified and that the classification is current. It
/// cannot prove a site classified <see cref="Why.Owned"/> really reaches
/// <c>ScratchDirs.Create</c> at runtime — <see cref="EveryOwnedSite_NamesAnOwningEntryPoint"/>
/// narrows that to "the owning call appears within a few lines of the expression", which is a
/// textual check too. The runtime behaviour of the ownership machinery itself is
/// <c>ScratchDirsTests</c>' subject.</para>
/// </summary>
public sealed class ProductionScratchDirOwnershipGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string ProductionDir => Path.Combine(RepoRoot, "AlRunner");

    /// <summary>
    /// The same three expressions the test-side guard scans, and for the same reason: all three
    /// name a location under <c>TMPDIR</c> and all three leak identically. Kept as an
    /// independent copy rather than shared with
    /// <see cref="ScratchDirOwnershipGuardTests"/> — the two guards scan different trees for
    /// different reasons, and coupling them would mean widening one silently widens the other's
    /// allowlist semantics.
    /// </summary>
    private static readonly string[] Expressions =
    [
        "Path.GetTempPath()",
        "Directory.CreateTempSubdirectory",
        "Path.GetTempFileName",
    ];

    private static string ExpressionList => string.Join(", ", Expressions);

    /// <summary>
    /// Why a production site is not an ordinary owned scratch directory. The distinction between
    /// the first two is the load-bearing part of this guard: both are permitted, and the fix for
    /// getting one wrong is the opposite of the fix for the other.
    /// </summary>
    public enum Why
    {
        /// <summary>
        /// The site DOES own its directory — the expression is the path argument handed to
        /// <c>ScratchDirs.Create</c>, <c>ScratchDirs.Reserve</c>, or a helper that calls one.
        /// Listed so the count stays exact: without an entry, adding a second and UNOWNED site
        /// to one of these files would look like the file was already approved.
        /// </summary>
        Owned,

        /// <summary>
        /// MUST NOT be owned. A content-addressed or append-only artifact that is shared on
        /// purpose and whose value is precisely that it outlives its creator. Writing an
        /// <c>.owner</c> sidecar here would make <c>ScratchDirs.SweepStale</c> delete it as soon
        /// as the creating process exits — turning a working cache into a cache that is cold on
        /// every run, and an append-only crash log into one that disappears with the crash.
        /// Adding ownership to one of these is a BUG, not an improvement.
        /// </summary>
        MustNotBeOwned,

        /// <summary>
        /// CANNOT be owned: nothing is created on disk at this site at all. A property returning
        /// the temp root, a path handed to a resolver, a diagnostic string. There is nothing for
        /// a sidecar to sit beside, and writing one would be litter of its own.
        /// </summary>
        CannotBeOwned,

        /// <summary>
        /// A documented trade-off: a real directory or file IS created and IS unowned, and the
        /// reason it stays that way is written at the call site. Distinct from
        /// <see cref="MustNotBeOwned"/> because these are not shared-by-design — they are places
        /// where ownership was weighed and declined. This is the category to look at first when
        /// asking "what still leaks"; an entry here is a candidate for a future fix, whereas
        /// <see cref="MustNotBeOwned"/> never is.
        /// </summary>
        DocumentedTradeOff,
    }

    /// <summary>
    /// Source path relative to <c>AlRunner/</c>, with <c>/</c> separators → (permitted
    /// non-comment occurrence count, why, reason).
    ///
    /// The count is exact in both directions, exactly as on the test side: too many means a new
    /// unclassified site landed in an already-listed file, too few means the entry is stale and
    /// is silently pre-approving the next one.
    /// </summary>
    private static readonly Dictionary<string, (int Count, Why Why, string Reason)> Allowed = new()
    {
        // ── the site IS owned; listed to hold the count ──────────────────────────────────
        ["AppLoader.cs"] =
            (1, Why.Owned, "the r2r-chunks write fallback, wrapped in ScratchDirs.Create at the "
                         + "same statement"),
        ["DependencyMetadataProducer.cs"] =
            (1, Why.Owned, "ScratchContainer, the container name the per-compile directories sit "
                         + "under; the directories themselves are created by CreateScratchDir, "
                         + "which calls ScratchDirs.Create (#3838, the site this guard exists "
                         + "because nothing caught)"),
        ["Program.cs"] =
            (4, Why.Owned, "two owned ScratchDirs.Create call sites — the watchdog-resume carry "
                         + "directory and a --server inline bundle — plus two occurrences in the "
                         + "sweep's own diagnostic messages, which name the root being swept and "
                         + "create nothing"),
        ["Infrastructure/DepExtractionDir.cs"] =
            (1, Why.Owned, "RootForProcess builds the path; the only caller wraps it in "
                         + "ScratchDirs.Create"),
        ["Infrastructure/ParallelFanOut.cs"] =
            (1, Why.Owned, "the --jobs shard directory, wrapped in ScratchDirs.Create"),
        ["Infrastructure/PerProcessScratch.cs"] =
            (1, Why.Owned, "the helper every per-process scratch site delegates to; it returns "
                         + "ScratchDirs.Create(dir)"),
        ["Patches/MetadataPatches.cs"] =
            (1, Why.Owned, "the report engine's per-session user folder, wrapped in "
                         + "ScratchDirs.Create"),
        ["Patches/NavReportSync.cs"] =
            (1, Why.Owned, "TempPathHelper's base path; the pid-named leaf below it is owned via "
                         + "ScratchDirs, and a caller-NAMED folder is shared across processes and "
                         + "deliberately left as it was"),

        // ── MUST NOT be owned: shared on purpose, a sidecar would destroy them ───────────
        ["Infrastructure/PkgDedupCache.cs"] =
            (1, Why.MustNotBeOwned, "al-runner-pkgdedup, the content-addressed package-dedup "
                                  + "staging root. Keyed by content hash and validated before "
                                  + "reuse; outliving its creator is the entire point, and an "
                                  + ".owner sidecar would have the next runner start delete it"),
        ["Patches/RecordPatches.BcAppFallback.cs"] =
            (1, Why.MustNotBeOwned, "al-runner-systemapp-<len>-<mtime>.app, the extracted "
                                  + "SystemApp package. Content-addressed by length and mtime and "
                                  + "published with one rename, so a reader sees it absent or "
                                  + "complete; re-extracting it per process is the cost this "
                                  + "sharing exists to avoid"),
        ["Win32Stubs.cs"] =
            (1, Why.MustNotBeOwned, "alrunner-v2-win32-stubs, holding the compiled Win32 shim. "
                                  + "The .so is published by rename and loaded by every later "
                                  + "runner; the per-runner BUILD directory below it is private "
                                  + "and removed by its own creator"),
        ["Infrastructure/SiblingSymbolsDirectory.cs"] =
            (1, Why.MustNotBeOwned, "the Root container only, never written to directly and never "
                                  + "deleted as a unit. Every leaf below it carries the bundle "
                                  + "hash and the process nonce and IS owned (#2586)"),
        ["BcRuntime.cs"] =
            (1, Why.MustNotBeOwned, "al-runner-startup.log, append-only and one line per runner "
                                  + "start. It exists to be readable AFTER the process that wrote "
                                  + "it crashes, so an owner marker would delete the evidence it "
                                  + "is there to preserve"),

        // ── nothing is created on disk at this site ──────────────────────────────────────
        ["Infrastructure/CacheRoots.cs"] =
            (1, Why.CannotBeOwned, "ThrowawayRootParent returns the temp root itself, as the "
                                 + "PARENT a throwaway root is minted under; the root it mints is "
                                 + "owned elsewhere"),
        ["Infrastructure/ScratchDirs.cs"] =
            (1, Why.CannotBeOwned, "SweepStale's default root — the ownership machinery reading "
                                 + "the directory it sweeps. Owning the temp root would mean "
                                 + "deleting it"),

        // ── documented trade-offs: really unowned, reason written at the call site ───────
        ["BcAssembler.cs"] =
            (2, Why.DocumentedTradeOff, "two debug dumps behind DUMP_CS=1 and "
                                      + "AL_RUNNER_DUMP_BC_ASM=1. A predictable filename is the "
                                      + "whole purpose, nothing reads them back, and they are off "
                                      + "unless a developer asks (#2967)"),
        ["BcCompiler.cs"] =
            (1, Why.DocumentedTradeOff, "the bccompiler-dump directory behind BCCOMPILER_DUMP_CS=1; "
                                      + "same trade-off, and a GUID-named directory would make it "
                                      + "useless for the one purpose it has (#2967)"),
        ["Patches/TenantStoragePatches.cs"] =
            (1, Why.DocumentedTradeOff, "al-runner-navserver/encryption-keys, written by "
                                      + "ALExportKey. BC returns a server-side path the AL caller "
                                      + "reads and then File.Erase()s, so the FILES are the "
                                      + "caller's to delete and the runner must not delete them "
                                      + "underneath it. The containing directory is a named leaf "
                                      + "under al-runner-navserver, so SweepStale's legacy pid "
                                      + "rule does not match it and nothing reclaims it if the AL "
                                      + "caller never erases — bounded by one small file per "
                                      + "ExportKey call. Tracked as a trade-off rather than a "
                                      + "leak, and this is the entry to revisit first"),
    };

    private static string Key(string path) =>
        Path.GetRelativePath(ProductionDir, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Every <c>.cs</c> source under <c>AlRunner/</c> at any depth, minus build output.
    ///
    /// The non-vacuity assertion is not optional. An enumeration that reached nothing would make
    /// <see cref="NoProductionSource_BuildsAnUnclassifiedTempPath"/> pass having read no source
    /// at all — a guard reporting its success state about something it never measured, which is
    /// the defect <c>guards-need-a-third-state.md</c> is about and which #3856 measured as
    /// reachable on a guard this repository had recorded as safe.
    /// </summary>
    private static IReadOnlyList<string> ProductionSources()
    {
        var paths = Directory.EnumerateFiles(ProductionDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !Key(p).Split('/').Any(seg => seg is "bin" or "obj"))
            .ToList();

        Assert.True(paths.Count > 0,
            $"expected .cs sources under {ProductionDir}, found none — this guard is not looking "
            + "at anything, so an unowned production scratch path anywhere in the runner would "
            + "pass unseen.");

        return paths;
    }

    private static Dictionary<string, List<(int Line, string Expression)>> Occurrences()
    {
        var found = new Dictionary<string, List<(int, string)>>(StringComparer.Ordinal);
        foreach (var path in ProductionSources())
        {
            var hits = new List<(int, string)>();
            var lineNo = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                lineNo++;
                foreach (var expression in Expressions)
                {
                    if (!MatchesInLine(raw, expression)) continue;

                    var i = 0;
                    while ((i = raw.IndexOf(expression, i, StringComparison.Ordinal)) >= 0)
                    {
                        hits.Add((lineNo, expression));
                        i += expression.Length;
                    }
                }
            }
            if (hits.Count > 0) found[Key(path)] = hits;
        }
        return found;
    }

    /// <summary>
    /// The forward direction: a production source naming a temp location, with no entry here, is
    /// a scratch path nobody has classified — and therefore, by default, one nothing owns.
    /// </summary>
    [Fact]
    public void NoProductionSource_BuildsAnUnclassifiedTempPath()
    {
        var offenders = Occurrences()
            .Where(kv => !Allowed.ContainsKey(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .SelectMany(kv => kv.Value.Select(h => $"{kv.Key}:{h.Line}  {h.Expression}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"These runner sources name a temp location directly ({ExpressionList}) and are not "
            + "classified, so nothing records an owner and a killed runner leaks the directory "
            + "permanently:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nPick one:\n"
            + "  * ScratchDirs.Create(path)  — create the directory AND record this process as "
            + "its owner. The default for anything a single run writes and a later run does not "
            + "need.\n"
            + "  * ScratchDirs.Reserve(path) — record the owner WITHOUT creating the leaf, for a "
            + "path something else is about to create.\n"
            + "  * PerProcessScratch.Dir(container, name) — a per-(name, process) directory under "
            + "a shared container; it calls ScratchDirs.Create for you.\n"
            + "\nIf the path must NOT be owned — a content-addressed cache shared on purpose, "
            + "where a sidecar would have the next runner start delete it — add an entry here "
            + "with Why.MustNotBeOwned and say what is shared and why. If nothing is created on "
            + "disk at all, Why.CannotBeOwned. See ScratchDirs.cs's header table and issue "
            + "#3850.");
    }

    /// <summary>
    /// The count direction. An allowlisted file may not quietly grow a second, unowned site
    /// behind an entry that was written for a different one, and an entry whose count has
    /// dropped is stale — a stale entry pre-approves the next site to land in that file.
    /// </summary>
    [Fact]
    public void EveryAllowlistEntry_MatchesItsRecordedCount_SoTheListCannotGoStale()
    {
        var found = Occurrences();
        var wrong = new List<string>();

        foreach (var (name, (count, why, reason)) in Allowed)
        {
            var actual = found.TryGetValue(name, out var hits) ? hits.Count : 0;
            if (actual != count)
                wrong.Add($"{name}: allowlist says {count} ({why}), source has {actual} — {reason}"
                    + (actual > 0
                        ? " — at " + string.Join(", ", hits!.Select(h => $"line {h.Line} {h.Expression}"))
                        : string.Empty));
        }

        Assert.True(wrong.Count == 0,
            "These allowlist entries no longer match the source:\n  " + string.Join("\n  ", wrong)
            + "\n\nHigher than recorded means a NEW temp path was added to an already-classified "
            + "file — classify it, and route it through ScratchDirs.Create unless it is one of "
            + "the shared-by-design kinds. Lower means the entry is stale: correct the count, or "
            + "delete the entry, so it stops pre-approving the next site that lands there.");
    }

    /// <summary>
    /// The distinction this guard exists to hold. <see cref="Why.MustNotBeOwned"/> and
    /// <see cref="Why.CannotBeOwned"/> are not synonyms, and collapsing them is the specific
    /// mistake a widened test-side scan would have made: the test side's single category means
    /// "this path must not exist", and applying that vocabulary to a content-addressed cache
    /// would tell the next reader that a deliberate design is an oversight.
    ///
    /// So each category must be populated. A category with no entries is a category nobody is
    /// using, which is how a four-way classification decays into a one-way one — and this is
    /// also what makes the enum measured rather than decorative: if every entry drifted to a
    /// single value, the distinction would still be documented and no longer true.
    /// </summary>
    [Fact]
    public void EveryCategory_IsPopulated_SoTheDistinctionIsRealAndNotJustDocumented()
    {
        foreach (var why in Enum.GetValues<Why>())
        {
            var members = Allowed.Where(kv => kv.Value.Why == why).Select(kv => kv.Key).ToList();
            Assert.True(members.Count > 0,
                $"no allowlist entry is classified {why}. Either the category is unused — in "
                + "which case delete it rather than leave a distinction nothing holds — or "
                + "entries have drifted to another value. The MustNotBeOwned / CannotBeOwned "
                + "split is the reason this guard is separate from the test-side one (#3850).");
        }
    }

    /// <summary>
    /// Every reason is a real reason. An entry whose text is a placeholder documents nothing,
    /// and the whole value of the allowlist is that a reader can tell a deliberate design from
    /// an unreviewed one without leaving the file.
    /// </summary>
    [Fact]
    public void EveryAllowlistEntry_CarriesASubstantiveReason()
    {
        var thin = Allowed
            .Where(kv => kv.Value.Reason.Trim().Length < 40)
            .Select(kv => $"{kv.Key}: \"{kv.Value.Reason}\"")
            .ToList();

        Assert.True(thin.Count == 0,
            "These entries have a reason too short to tell a reader anything:\n  "
            + string.Join("\n  ", thin)
            + "\n\nSay what the path is and why it is not an ordinary owned scratch directory.");
    }

    /// <summary>
    /// A site classified <see cref="Why.Owned"/> must actually name an owning entry point near
    /// the expression. Without this the category would be an assertion about the code that the
    /// code could stop honouring silently — someone unwrapping a <c>ScratchDirs.Create</c> would
    /// leave the count unchanged and the entry unchallenged, which is precisely the
    /// pre-#3838 state this guard exists to end.
    ///
    /// <para>The check is FILE-scoped, not line-scoped, and that is a measured choice rather than
    /// laziness. A ±12-line window was tried first and failed on
    /// <c>DependencyMetadataProducer.cs</c>, where <c>ScratchContainer</c> at line 107 and the
    /// <c>ScratchDirs.Create</c> that consumes it at line 128 are separated by a 17-line doc
    /// comment — a true positive for "the window is too tight" and a false one for "ownership is
    /// missing". Any line distance would be a number chosen to fit today's formatting, and
    /// reformatting would then fail the guard for a reason unrelated to ownership. Asking
    /// whether the FILE names an owning entry point at all is the weaker claim, but it is the
    /// one that stays true under formatting and still catches the case that matters: somebody
    /// unwrapping the last <c>ScratchDirs.Create</c> in a file classified Owned.</para>
    ///
    /// <para>It is a textual proxy, not a proof of the runtime path — see the class remarks.</para>
    /// </summary>
    [Fact]
    public void EveryOwnedSite_NamesAnOwningEntryPoint()
    {
        string[] owning = ["ScratchDirs.Create", "ScratchDirs.Reserve", "PerProcessScratch.Dir"];
        var missing = new List<string>();

        foreach (var (name, (_, why, _)) in Allowed.Where(kv => kv.Value.Why == Why.Owned))
        {
            var path = Path.Combine(ProductionDir, name);
            Assert.True(File.Exists(path),
                $"the allowlist classifies {name} as Owned but the file does not exist — the "
                + "entry names nothing, which is the third state, not a pass.");

            var anyOwned = File.ReadAllLines(path).Any(raw =>
            {
                var text = raw.TrimStart();
                if (text.StartsWith("//", StringComparison.Ordinal)
                    || text.StartsWith("*", StringComparison.Ordinal)) return false;
                return owning.Any(o => raw.Contains(o, StringComparison.Ordinal));
            });

            if (!anyOwned) missing.Add($"{name} (classified {why})");
        }

        Assert.True(missing.Count == 0,
            "These files are classified Owned but name no owning call ("
            + string.Join(", ", owning)
            + ") in non-comment code anywhere:\n  "
            + string.Join("\n  ", missing)
            + "\n\nEither the ownership was removed — restore it — or the site is no longer "
            + "owned, in which case reclassify it and say why it may stay unowned.");
    }

    /// <summary>
    /// The scan itself, measured rather than assumed. <c>Infrastructure/ScratchDirs.cs</c> is the
    /// one production file guaranteed to name a temp location — it is the ownership machinery,
    /// and <c>SweepStale</c> reads the temp root by definition — so finding it there proves the
    /// matcher runs, reads real source, and does not skip the file.
    ///
    /// Without this, a typo in <see cref="Expressions"/>, a matcher that never fires, or an
    /// enumeration that reached no source would leave
    /// <see cref="NoProductionSource_BuildsAnUnclassifiedTempPath"/> green with an empty offender
    /// list. <see cref="ProductionSources"/> asserts the enumeration is non-empty; this asserts
    /// the MATCHING is.
    /// </summary>
    [Fact]
    public void TheScanItself_ActuallyMatches_SoAnEmptyOffenderListMeansSomething()
    {
        var found = Occurrences();

        Assert.True(found.TryGetValue("Infrastructure/ScratchDirs.cs", out var machinery),
            $"the scan found no temp expression ({ExpressionList}) in Infrastructure/ScratchDirs.cs, "
            + "the one production file that must contain one — so the matcher is not working and "
            + "an empty offender list from this class proves nothing.");

        Assert.Equal(1, machinery!.Count);
        Assert.Equal("Path.GetTempPath()", machinery[0].Expression);
    }

    /// <summary>
    /// Every entry in <see cref="Expressions"/> is matched by the same predicate the scan uses,
    /// and a comment carrying it is not. Two of the three have no live production call site
    /// today, so without this they would be strings nobody has ever seen fire — and a never-fire
    /// branch is indistinguishable from a correctly-silent one from the outside.
    /// </summary>
    [Fact]
    public void EveryScannedExpression_IsMatchedByTheScanner_OnSyntheticSource()
    {
        foreach (var expression in Expressions)
        {
            Assert.True(MatchesInLine($"        var x = {expression};", expression),
                $"'{expression}' is listed as scanned but the matcher does not find it in a line "
                + "that contains it — the entry would never fire.");

            Assert.False(MatchesInLine($"        // var x = {expression};", expression),
                $"'{expression}' matched inside a comment. Files quote these while explaining "
                + "their classification, and flagging those would train readers to ignore this "
                + "test.");
        }

        // Negative: a name that merely resembles a scanned one must not match, or the guard
        // would report offenders nobody can act on.
        Assert.False(MatchesInLine("var p = MyPath.GetTempPathish();", "Path.GetTempFileName"));
    }

    private static bool MatchesInLine(string raw, string expression)
    {
        var line = raw.TrimStart();
        if (line.StartsWith("//", StringComparison.Ordinal)
            || line.StartsWith("*", StringComparison.Ordinal)) return false;
        return raw.IndexOf(expression, StringComparison.Ordinal) >= 0;
    }
}
