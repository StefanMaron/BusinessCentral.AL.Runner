// ObjectIndexMemoTests — issue #4189, the object-inventory index behind ResolvePageReference.
//
// What was wrong
// --------------
// RecordPatches.BuildObjectIndexes walks EnumerateKnownAlObjects — every parsed table, page
// and pageextension plus every registered dependency's symbol-declared objects — and builds
// two dictionaries from scratch. Nothing memoized it, and ResolvePageReference calls it once
// per invocation. BuildNCLMetaTable resolves one page reference per table (#1918's
// LookupFormId population), so a bundle whose tables declare LookupPageId NAMES re-walked the
// whole inventory once per such table.
//
// Measured on tests/runner-extras/standalone-suites at BC 28.1.49838.53910, steady state
// (five runs against one --cache root, 116 tests passing): 59 walks of a 4,492-object
// inventory costing 3.1-4.0 s, which is 71% of BuildNCLMetaTable's ~4.25 s and ~35% of the
// run's ~12 s wall clock. EnumerateKnownTableMetadata had already hit this and worked around
// it locally, by hoisting ONE BuildObjectIndexes call out of its own per-table loop; its
// comment says so. The NclMetaTableBuilder caller cannot do that — each table resolves its
// own reference — so the memo belongs in BuildObjectIndexes itself.
//
// What these tests pin, and why a count
// -------------------------------------
// ObjectIndexBuildCountForTests counts genuine inventory walks. The claim is "N resolutions
// at one generation cost ONE walk", which is a count, not a duration: a duration assertion
// would be a flake on a loaded box and would not distinguish a memo from a fast machine.
// Correctness is asserted separately and in the same breath — a memo that returned a stale or
// different answer would be a far worse defect than the cost it removes, so every test here
// checks the ANSWER as well as the walk count.
//
// Why these are mechanism tests and not corpus tests
// --------------------------------------------------
// Every claim is about how often a runner-internal index is rebuilt inside ONE process, and
// about its lifetime across a registration-set change. Real BC has no BuildObjectIndexes, no
// _bcAppPaths and no registration epoch; the AL a corpus test would run is identical on both
// sides of this change, because the RESOLVED page id is unchanged — that is precisely what
// AnswersAreIdentical_AcrossTheMemoBoundary asserts. Same reasoning, and the same conclusion,
// as BcAppRegistrationEpochInvalidationTests, whose header sets out this split for the #2888
// family this memo joins.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: these tests read and reset the parse statics
// (_parsedTables / _parsedPages), which ParserStaticsIsolationGuardTests requires to be
// serialized — a concurrent class parsing its own sources would move the generation key
// underneath a walk-count assertion and turn it into a flake.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class ObjectIndexMemoTests
{
    private static readonly Type T = typeof(RecordPatches);

    // Ids process-wide unique among AlRunner.Tests statics — _parsedTables and _parsedPages are
    // process-global and nothing in the test host unparses an object. Blocks in use: 881234xx,
    // 881235xx, 881236xx, 881237xx, 881238xx (BcAppRegistrationEpochInvalidationTests) and
    // 881239xx (PageRealMetadataEpochRevalidationTests, which uses 88123901-88123905 and is in
    // this same serial collection). This file owns 881240xx.
    private const int PageId = 88124001;
    private const string PageName = "Issue4189 Memo Probe Page";

    private static int BuildCount =>
        (int)T.GetProperty("ObjectIndexBuildCountForTests",
            BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static int Resolve(string? reference) =>
        (int)T.GetMethod("ResolvePageReference", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { reference })!;

    private static void ParsePage(int id, string name) =>
        T.GetMethod("TryParsePageFile", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { $"page {id} \"{name}\"\n{{\n}}\n" });

    /// <summary>
    /// The cost claim. Ten resolutions of a NAME — the branch that reaches the inventory —
    /// at one generation must walk the inventory at most once.
    ///
    /// <para>A name, never a bare id: ResolvePageReference short-circuits a numeric reference
    /// before touching the index at all, so an id-based probe would pass with the memo removed
    /// and prove nothing. That is the mutation this test is written to survive.</para>
    /// </summary>
    [Fact]
    public void TenNameResolutionsAtOneGeneration_WalkTheInventoryAtMostOnce()
    {
        ParsePage(PageId, PageName);

        // Prime: the first call may legitimately walk, and may be the first walk since another
        // test moved the generation. The claim is about the calls AFTER a known-warm memo.
        Resolve(PageName);
        var before = BuildCount;

        for (var i = 0; i < 10; i++) Resolve(PageName);

        var walks = BuildCount - before;
        Assert.True(walks == 0,
            $"10 resolutions at one generation walked the inventory {walks} time(s); expected 0 "
            + "after priming. An unmemoized BuildObjectIndexes walks once per resolution.");
    }

    /// <summary>
    /// The correctness claim, and the reason the count claim is safe to make: the memo must
    /// hand back the SAME answer the unmemoized walk did, for a name that resolves and for one
    /// that does not.
    ///
    /// <para>Asserts a concrete id rather than "non-zero": a memo handing out a different
    /// page's id would satisfy a non-zero check and be a worse bug than the cost it removed.
    /// The unresolvable arm pins the 0 that ResolvePageReference documents as a truthful
    /// answer, so the memo cannot turn "no such page" into a stale hit.</para>
    /// </summary>
    [Fact]
    public void AnswersAreIdentical_AcrossTheMemoBoundary()
    {
        ParsePage(PageId, PageName);

        var first = Resolve(PageName);
        var second = Resolve(PageName);
        var third = Resolve(PageName);

        Assert.Equal(PageId, first);
        Assert.Equal(PageId, second);
        Assert.Equal(PageId, third);

        // AL compares object names case-insensitively and the index is built with
        // OrdinalIgnoreCase; the memo must not have narrowed that to ordinal.
        Assert.Equal(PageId, Resolve(PageName.ToUpperInvariant()));

        // A name no page declares stays a truthful 0 — not a stale hit from the memo.
        Assert.Equal(0, Resolve("Issue4189 No Such Page Anywhere"));

        // A bare id never reaches the index at all, memo or not.
        Assert.Equal(4189, Resolve("4189"));
    }

    /// <summary>
    /// The invalidation claim. A page parsed AFTER the memo was built must be resolvable, so
    /// the generation key has to carry the parsed-page count.
    ///
    /// <para>This is the direction that costs something: a memo that never invalidated would
    /// answer 0 for a page the runner does now know about, and #1918's LookupFormId population
    /// would silently record "no lookup page" — the same plausible-looking wrong answer that
    /// motivated resolving LookupPageId in the first place.</para>
    /// </summary>
    [Fact]
    public void APageParsedAfterTheMemoWasBuilt_IsStillResolvable()
    {
        const int lateId = 88124002;
        const string lateName = "Issue4189 Late Probe Page";

        ParsePage(PageId, PageName);
        // Build the memo at a generation that does NOT know about the late page, and assert
        // that precondition rather than assume it: if the id were already parsed by an earlier
        // run of this class in the same process, the test would pass without invalidating
        // anything.
        Assert.Equal(PageId, Resolve(PageName));
        Assert.Equal(0, Resolve(lateName));

        ParsePage(lateId, lateName);

        Assert.Equal(lateId, Resolve(lateName));
        // The earlier entry survives the rebuild — invalidation must rebuild, never truncate.
        Assert.Equal(PageId, Resolve(PageName));
    }
}
