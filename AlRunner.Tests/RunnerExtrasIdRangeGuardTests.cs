using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Guards the object-ID namespace of the <c>tests/runner-extras*</c> bundle roots: no two app
/// groups compiled into the SAME root may declare overlapping <c>idRanges</c>.
///
/// Why the range and not the object ID (#3040)
/// -------------------------------------------
/// Each <c>app.json</c> under a runner-extras root becomes its own <see cref="AlRunner.AppGroup"/>
/// and therefore its own emitted module, but the whole root runs as ONE bundle in ONE process
/// with one runtime init and one database. So the <c>Object</c> virtual table is global to the
/// invocation, not scoped to an app group, and two app groups defining the same object are two
/// rows where a suite reading that table expects one.
///
/// That is what PR #2969 hit. It declared 65550-65559 for a new app group, which is
/// <c>object-system-table</c>'s range, and the run failed like this:
///
///     FAIL Codeunit65551.RowSet_ListsObjectsTheRunnerKnows_WithNoApplicationDatabase
///          Expected OST Tests but got PAS Control Subscriber
///     FAIL Codeunit65551.KindsTheTypeOptionCannotName_GetNoRow
///          Expected 0 but got 1: Object must not list enum 65552 under any Type
///
/// Neither message names the cause, which is that two app groups now define object 65551 and
/// object 65552. An agent reading them has no reason to suspect its own app.json.
///
/// The range is the right level to guard because it is the UPSTREAM invariant, and guarding it
/// is sufficient: the AL compiler already refuses an object outside its own app's declared
/// ranges (AL0297), so two app groups with disjoint ranges CANNOT declare the same object ID.
/// Blocking new overlaps therefore blocks every new cross-group object collision, without this
/// guard having to parse AL source at all.
///
/// Which matters, because parsing would mean matching TEXT. That is the shape that made
/// BaseAppFloorFixtureGuardTests fire on a code comment (#3064). Everything below is arithmetic
/// over parsed JSON: two closed intervals overlap or they do not, and no comment or string
/// literal can change the answer.
///
/// Endpoints are INCLUSIVE at both ends — measured, not assumed. The runner itself never reads
/// idRanges; the AL compiler enforces them. AlRunner.Tests/TestPagePartAdoptedFromHostTests.cs
/// declares a fixture with <c>{ "from": 62410, "to": 62413 }</c> containing both
/// <c>table 62410</c> (the low endpoint) and <c>codeunit 62413</c> (the high endpoint), and
/// asserts <c>PASS  Codeunit62413.HostSeededTemporaryPart_TestPageSeesTheSameRow</c> — so it
/// compiles and runs on all eight BC legs with objects sitting on both endpoints. Hence
/// 60000..60099 and 60100..60199 are ADJACENT, not overlapping, and this guard must let them
/// through; an off-by-one here would either block legitimate adjacent ranges or miss a
/// one-ID overlap.
///
/// Scoping is PER ROOT. tests/runner-extras-isolation-disabled is a second bundle, run by its
/// own <c>dotnet run</c> in a second process (isolation is a process-global flag), and its one
/// app group declares 61103-61109 — which overlaps two groups in tests/runner-extras and is
/// entirely fine, because they never share a database.
/// </summary>
public sealed class RunnerExtrasIdRangeGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// The roots CI runs as a single bundle, one <c>dotnet run</c> each — see the two
    /// "Run runner-extras" steps in .github/workflows/bc-tests.yml.
    /// <see cref="EveryRunnerExtrasRoot_IsScanned_SoAThirdBundleCannotAppearUnguarded"/> fails if
    /// a third one appears, so this list cannot quietly go stale.
    /// </summary>
    private static readonly string[] BundleRoots =
    {
        "tests/runner-extras",
        "tests/runner-extras-isolation-disabled",
    };

    // ---------------------------------------------------------------- model

    /// <summary>A closed interval [From, To] — both endpoints inclusive, see the class remarks.</summary>
    internal readonly record struct IdRange(int From, int To)
    {
        public override string ToString() => $"{From}..{To}";
    }

    internal sealed record AppGroupRanges(string Name, IReadOnlyList<IdRange> Ranges);

    internal sealed record RangeConflict(string GroupA, string GroupB, IdRange Overlap)
    {
        /// <summary>The pair key, order-independent, so an allowlist entry cannot be dodged by swapping the names.</summary>
        public string PairKey => string.CompareOrdinal(GroupA, GroupB) <= 0
            ? $"{GroupA} | {GroupB}"
            : $"{GroupB} | {GroupA}";

        public override string ToString() => $"{PairKey}  both claim [{Overlap}]";
    }

    /// <summary>
    /// The whole arithmetic claim. Two closed intervals intersect when the greatest low endpoint
    /// is not above the least high endpoint; the intersection is exactly that pair. Adjacency
    /// (60000..60099 vs 60100..60199) gives 60100 &gt; 60099 and is correctly NOT a conflict.
    /// </summary>
    internal static bool TryIntersect(IdRange a, IdRange b, out IdRange intersection)
    {
        var low = Math.Max(a.From, b.From);
        var high = Math.Min(a.To, b.To);
        intersection = new IdRange(low, high);
        return low <= high;
    }

    /// <summary>Every overlapping (group, group, interval) triple among the given app groups.</summary>
    internal static IReadOnlyList<RangeConflict> FindConflicts(IReadOnlyList<AppGroupRanges> groups)
    {
        var conflicts = new List<RangeConflict>();
        for (var i = 0; i < groups.Count; i++)
        for (var j = i + 1; j < groups.Count; j++)
        foreach (var ra in groups[i].Ranges)
        foreach (var rb in groups[j].Ranges)
            if (TryIntersect(ra, rb, out var overlap))
                conflicts.Add(new RangeConflict(groups[i].Name, groups[j].Name, overlap));

        return conflicts;
    }

    // ---------------------------------------------------------------- reading the repo

    private static IReadOnlyList<AppGroupRanges> ReadAppGroups(string root)
    {
        var dir = Path.Combine(RepoRoot, root.Replace('/', Path.DirectorySeparatorChar));
        var groups = new List<AppGroupRanges>();

        foreach (var manifest in Directory.EnumerateFiles(dir, "app.json", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var options = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest), options);

            var ranges = new List<IdRange>();
            if (doc.RootElement.TryGetProperty("idRanges", out var declared)
                && declared.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in declared.EnumerateArray())
                    ranges.Add(new IdRange(r.GetProperty("from").GetInt32(), r.GetProperty("to").GetInt32()));
            }

            var name = Path.GetFileName(Path.GetDirectoryName(manifest)!);
            groups.Add(new AppGroupRanges(name, ranges));
        }

        return groups;
    }

    // ---------------------------------------------------------------- the allowlist

    /// <summary>
    /// Overlaps that already existed on main when this guard landed (#3040). Keyed
    /// <c>"&lt;root&gt;: &lt;a&gt; | &lt;b&gt;"</c> with the group names in ordinal order.
    ///
    /// These are debt, not permission. A new entry is only ever correct if two app groups must
    /// genuinely share a range, which so far nothing does — the right move for a new overlap is
    /// to pick a free range.
    ///
    /// Three of them are not merely latent: those app groups ALREADY declare the same object,
    /// which is exactly the #2969 failure sitting dormant because no suite happens to read
    /// those rows out of the global Object table. Renumbering them is follow-up work and is
    /// tracked separately; it is deliberately not folded into the PR that adds this guard,
    /// because moving AL object IDs is a change with its own blast radius.
    /// </summary>
    private static readonly Dictionary<string, string> KnownOverlaps = new(StringComparer.Ordinal)
    {
        ["tests/runner-extras: dep-tableext-invoke-dep | install-trigger-seed"] =
            "60710..60719 — install-trigger-seed owns every object in the overlap; "
            + "dep-tableext-invoke-dep declares 60700-60749 but uses only 60700/60701. No live "
            + "collision.",

        ["tests/runner-extras: dep-tableext-invoke-dep | standalone-suites"] =
            "60700..60749 — both use objects here (dep: table 60700, tableextension 60701; "
            + "standalone: 60700-60708) but never the same kind+id. No live collision.",

        ["tests/runner-extras: dep-tableext-invoke-main | standalone-suites"] =
            "60750..60799 — standalone-suites declares the block and uses nothing in it. No live "
            + "collision.",

        ["tests/runner-extras: field-virtual-table-item-tracking | report-precompiled-dep-metadata"] =
            "61100..61199 — LIVE COLLISION: both define codeunit 61100 and codeunit 61101. Dormant "
            + "only because no suite reads those Object rows. Renumbering tracked separately.",

        ["tests/runner-extras: field-virtual-table-item-tracking-ext | navapp-moduleinfo-dep"] =
            "61230..61239 — field-virtual-table-item-tracking-ext declares 61200-61249 and uses "
            + "nothing above 61200; the whole 61210-61249 span is over-declaration. No live collision.",

        ["tests/runner-extras: field-virtual-table-item-tracking-ext | navapp-moduleinfo-main"] =
            "61240..61249 — same over-declaration as the entry above. No live collision.",

        ["tests/runner-extras: field-virtual-table-item-tracking-ext | xasm-event-dispatch-dep"] =
            "61210..61219 — same over-declaration as the entry above. No live collision.",

        ["tests/runner-extras: field-virtual-table-item-tracking-ext | xasm-event-dispatch-main"] =
            "61220..61229 — same over-declaration as the entry above. No live collision.",

        ["tests/runner-extras: http-egress-boundary-oos | testpage-promoted-actionref"] =
            "64550..64559 — testpage-promoted-actionref declares 64540-64559 and uses only 64546 "
            + "upward of the overlap's floor. No live collision.",

        ["tests/runner-extras: http-egress-boundary-oos | windows-language-license-stub"] =
            "64550..64555 — windows-language-license-stub declares 64546-64555 and uses only "
            + "64546/64547. No live collision.",

        ["tests/runner-extras: install-trigger-seed | standalone-suites"] =
            "60710..60719 — standalone-suites declares the block and uses nothing in it. No live "
            + "collision.",

        ["tests/runner-extras: microsoft-test-library | standalone-suites"] =
            "62200..62209 — LIVE COLLISION: both define codeunit 62200. #1847 folded sixteen "
            + "standalone suites into standalone-suites and it kept a range microsoft-test-library "
            + "still claims. Renumbering tracked separately.",

        ["tests/runner-extras: server-multibundle-dep | standalone-suites"] =
            "64300..64309 — both use id 64300, but as table and codeunit respectively, and the Object "
            + "table keys on (Type, ID). No live collision, and a narrow miss.",

        ["tests/runner-extras: session-user-row | testpage-lookup-tablerelation-oos"] =
            "65560..65569 — LIVE COLLISION: both define codeunit 65560. This is the pair #3040 was "
            + "filed about. Renumbering tracked separately.",

        ["tests/runner-extras: testpage-promoted-actionref | windows-language-license-stub"] =
            "64546..64555 — testpage-promoted-actionref has report 64546, "
            + "windows-language-license-stub has codeunit 64546; different kinds, so no live collision."
    };

    // ---------------------------------------------------------------- the facts

    [Fact]
    public void NoTwoAppGroups_InTheSameBundleRoot_DeclareOverlappingIdRanges()
    {
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var root in BundleRoots)
        {
            var groups = ReadAppGroups(root);
            scanned += groups.Count;

            foreach (var c in FindConflicts(groups))
                if (!KnownOverlaps.ContainsKey($"{root}: {c.PairKey}"))
                    offenders.Add($"{root}: {c}");
        }

        // Non-vacuity: nothing else here notices a scan that read no manifest at all.
        Assert.True(scanned > 0,
            $"expected app.json manifests under {string.Join(", ", BundleRoots)}, found none — "
            + "the guard is not looking at anything, so an overlapping range would pass unseen.");

        Assert.True(offenders.Count == 0,
            "These app groups declare OVERLAPPING idRanges inside a single runner-extras bundle:\n  "
            + string.Join("\n  ", offenders.OrderBy(s => s, StringComparer.Ordinal))
            + "\n\nEvery app group in one root shares one process, one database and one global Object "
            + "table, so two groups drawing from the same range will eventually define the same object "
            + "— and the failure surfaces as an unrelated-looking assertion in whichever suite reads "
            + "the Object table (#2969, #3040). Pick a range no other app group in that root claims. "
            + "Endpoints are inclusive, so 60000..60099 and 60100..60199 are adjacent and fine.");
    }

    /// <summary>
    /// The negative direction. An allowlist entry whose pair no longer overlaps — because someone
    /// renumbered, renamed or deleted a group — silently re-permits the next pair that takes those
    /// names. This is what makes the list above a shrinking budget rather than decoration, and it
    /// is also this class's proof of non-vacuity: it only passes while the detector really does
    /// find these overlaps in the real repo.
    /// </summary>
    [Fact]
    public void EveryKnownOverlapEntry_StillOverlaps_SoTheListCannotGoStale()
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in BundleRoots)
            foreach (var c in FindConflicts(ReadAppGroups(root)))
                live.Add($"{root}: {c.PairKey}");

        var stale = KnownOverlaps.Keys.Where(k => !live.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(stale.Count == 0,
            "These KnownOverlaps entries no longer name a real overlap (renumbered, renamed or "
            + "deleted). Delete them — a stale entry silently permits the next pair that takes "
            + "those names:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>
    /// A third <c>tests/runner-extras*</c> root would get its own <c>dotnet run</c> in
    /// bc-tests.yml and its own shared Object table, and would be invisible to this guard until
    /// someone remembered to add it. Fail instead of silently narrowing.
    /// </summary>
    [Fact]
    public void EveryRunnerExtrasRoot_IsScanned_SoAThirdBundleCannotAppearUnguarded()
    {
        var found = Directory.EnumerateDirectories(Path.Combine(RepoRoot, "tests"), "runner-extras*")
            .Select(d => "tests/" + Path.GetFileName(d))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(BundleRoots.OrderBy(s => s, StringComparer.Ordinal).ToList(), found);
    }

    // ---------------------------------------------------------------- the detector, on synthetic input
    //
    // The facts above measure the repo, so they go green the moment the allowlist covers what is
    // there. These pin the arithmetic itself, and are what stop the detector degenerating into
    // "always false" (which would satisfy every repo-state fact above).

    private static AppGroupRanges Group(string name, params (int From, int To)[] ranges) =>
        new(name, ranges.Select(r => new IdRange(r.From, r.To)).ToList());

    [Fact]
    public void OverlappingRanges_AreReported_NamingBothGroupsAndTheOverlap()
    {
        // #2969's exact shape: a new app group taking object-system-table's range.
        var conflicts = FindConflicts(new[]
        {
            Group("object-system-table", (65550, 65559)),
            Group("new-suite-from-2969", (65550, 65559)),
        });

        var c = Assert.Single(conflicts);
        Assert.Equal(new IdRange(65550, 65559), c.Overlap);
        Assert.Equal("new-suite-from-2969 | object-system-table", c.PairKey);
        Assert.Contains("object-system-table", c.ToString());
        Assert.Contains("new-suite-from-2969", c.ToString());
        Assert.Contains("65550..65559", c.ToString());
    }

    [Fact]
    public void PartiallyOverlappingRanges_ReportOnlyTheIntersection()
    {
        var c = Assert.Single(FindConflicts(new[]
        {
            Group("a", (64546, 64555)),
            Group("b", (64550, 64559)),
        }));

        Assert.Equal(new IdRange(64550, 64555), c.Overlap);
    }

    /// <summary>
    /// The control. Adjacent ranges are the common, correct case — every suite that picks the
    /// next free block produces one — and a guard that flagged them would be worse than no guard.
    /// This is also the assertion that fails if the detector is rewritten to return "conflict"
    /// unconditionally.
    /// </summary>
    [Fact]
    public void AdjacentRanges_AreNotAConflict()
    {
        Assert.Empty(FindConflicts(new[]
        {
            Group("lower", (60000, 60099)),
            Group("upper", (60100, 60199)),
        }));
    }

    /// <summary>
    /// The off-by-one, from both sides: the two ranges that differ from the adjacent pair above
    /// by exactly one ID. Sharing a single ID IS a conflict; missing by a single ID is not.
    /// </summary>
    [Theory]
    [InlineData(60000, 60100, 60100, 60199, true, 60100, 60100)]  // touch on exactly one ID
    [InlineData(60000, 60099, 60100, 60199, false, 0, 0)]         // adjacent, miss by one
    [InlineData(60000, 60098, 60100, 60199, false, 0, 0)]         // a one-ID gap between them
    public void OneIdApart_IsDecidedCorrectlyInBothDirections(
        int aFrom, int aTo, int bFrom, int bTo, bool expectConflict, int overlapFrom, int overlapTo)
    {
        var conflicts = FindConflicts(new[] { Group("a", (aFrom, aTo)), Group("b", (bFrom, bTo)) });

        if (!expectConflict)
        {
            Assert.Empty(conflicts);
            return;
        }

        Assert.Equal(new IdRange(overlapFrom, overlapTo), Assert.Single(conflicts).Overlap);
    }

    [Fact]
    public void ASingleAppGroup_NeverConflictsWithItself_EvenWithSeveralRanges()
    {
        Assert.Empty(FindConflicts(new[] { Group("only", (60000, 60099), (60100, 60199)) }));
    }

    /// <summary>
    /// standalone-suites declares twenty ranges (#1847 folded sixteen former suites into it), so
    /// a group's ranges must be compared pairwise against every other group's, not just the first.
    /// </summary>
    [Fact]
    public void EveryRangeOfAMultiRangeGroup_IsCompared_NotJustTheFirst()
    {
        var c = Assert.Single(FindConflicts(new[]
        {
            Group("many", (60300, 60399), (62200, 62209), (64300, 64309)),
            Group("one", (62205, 62215)),
        }));

        Assert.Equal(new IdRange(62205, 62209), c.Overlap);
    }

    [Fact]
    public void AGroupDeclaringNoRanges_IsNotAConflict()
    {
        Assert.Empty(FindConflicts(new[] { Group("empty"), Group("other", (60000, 60099)) }));
    }
}
