// MetaQueryCacheBundleBoundaryTests — issue #3210.
//
// WHAT WAS UNPINNED
// -----------------
// RecordPatches holds THREE id-keyed caches of query metadata, and ResetForReload — the
// per-bundle reload path a --server / --watch process runs between bundles — cleared only the
// first of them:
//
//   _metaQueryCache          (skeleton NCLMetaQuery)          cleared, always was
//   _realMetaQueryCache      (BUILT NCLMetaQuery)             never cleared, on any path
//   _lazyMetaQueryByGetById  (built NCLMetaQuery + FindQueryType) never cleared, on any path
//
// Both survivors are derived from state the same method discards. BuildRealNCLMetaQuery keys
// on the query id ALONE while taking the emitted CLR type as an argument, so bundle 2's query
// 50100 was answered with bundle 1's NCLMetaQuery — built against bundle 1's CLR type, out of
// bundle 1's parsed query design. EnsureQueryInMetadataCache sits on top of it and additionally
// memoizes BcRuntime.FindQueryType(id), which is a per-bundle answer that
// BcRuntime.ResetForNewBundleReload clears (_queryTypeCache) in the line immediately before it
// calls ResetForReload.
//
// Both memoize the NEGATIVE answer too — GetOrAdd stores whatever the factory returns, null
// included — which is the half that bites without bundle 2 even having to declare the query
// differently: an id asked about while it was unresolvable stayed unresolvable for the rest of
// the process.
//
// This is the fourth defect of this exact shape in this exact reset path: #2478 (an index reset
// that did not reset enough), #2755 (a registered set that was not cleared), #3207 (a memo that
// outlived every input it was computed from), and now this.
//
// WHAT THESE TESTS PROVE, AND WHAT THEY DO NOT
// --------------------------------------------
// They prove the reset CONTRACT: each cache is really written by its production entry point,
// really holds the entry afterwards, and is really empty on the far side of the reload. That is
// a claim a no-op implementation cannot satisfy — both cases fail on the unfixed tree.
//
// They do NOT prove that the surviving entry was observably WRONG downstream, and saying so is
// the point rather than a hedge. Producing a genuinely different NCLMetaQuery for one id in two
// bundles means running BuildRealNCLMetaQueryCore to completion, which needs the reflection
// handles into Microsoft.Dynamics.Nav.Types/Ncl AND CreateDynamicQuery — the BC engine standing
// up in-process, driven by a real two-bundle --server fixture. That is the blocker recorded on
// #3210 itself and on its sibling #3172, and it is why the population below deliberately uses an
// id NOTHING declares: the factory then returns null identically whether or not the engine is
// loaded, so the assertions mean the same thing on every box and every CI leg.
//
// WHY RUNNER-LOCAL AND NOT UPSTREAM
// ---------------------------------
// Nothing here is a claim about Business Central. The subject is the lifetime of a runner cache
// across the runner's own bundle-reload boundary, which is not a concept BC has and not
// something AL running on a service tier can observe.
using System.Collections;
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// MUST be serial: every case calls RecordPatches.ResetForReload(), which clears roughly twenty
// process-global dictionaries other classes are reading.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class MetaQueryCacheBundleBoundaryTests : IDisposable
{
    // Deliberately an id no fixture, corpus app or runner-extras bundle declares — see the
    // header. Both factories then take their "cannot build this" exit before touching any BC
    // reflection, so the memoized value is null on a box with the engine and on one without.
    private const int UndeclaredQueryId = 79901;
    private const int OtherUndeclaredQueryId = 79902;

    public MetaQueryCacheBundleBoundaryTests() => RecordPatches.ResetForReload();

    public void Dispose()
    {
        try { RecordPatches.ResetForReload(); } catch { }
    }

    [Fact]
    public void ABuiltQueryMetadataMemoInBundleOne_DoesNotSurviveTheReloadIntoBundleTwo()
    {
        // Populated through the PRODUCTION entry point, not by poking the dictionary: that is
        // what makes "the reload emptied it" a statement about the real memo rather than about
        // a dictionary this test happens to own.
        Assert.Null(RecordPatches.BuildRealNCLMetaQuery(UndeclaredQueryId, typeof(object)));

        // Asserted, not assumed. If the call had not memoized anything, the emptiness assertion
        // below would be satisfied trivially and this test would prove nothing at all.
        var memo = RealMetaQueryCache();
        Assert.True(memo.Contains(UndeclaredQueryId),
            "BuildRealNCLMetaQuery did not memoize its answer — this test tracks that memo, "
            + "and the clear it is asserting would be meaningless without it.");
        Assert.Null(memo[UndeclaredQueryId]);

        // The bundle boundary itself.
        RecordPatches.ResetForReload();

        // The fix. On the unfixed tree the entry is still there, keyed on an id whose CLR type,
        // parsed query design and FindQueryType answer this reload has just discarded.
        Assert.Empty(RealMetaQueryCache());
    }

    [Fact]
    public void TheLazyGetByIdQueryMemoInBundleOne_DoesNotSurviveTheReloadIntoBundleTwo()
    {
        // The second cache, reached through its own production entry point. It wraps the first
        // one AND memoizes BcRuntime.FindQueryType(id) — the per-bundle answer whose own cache
        // ResetForNewBundleReload clears immediately before calling ResetForReload, so this memo
        // was defeating an invalidation its dependency performs.
        Assert.Null(RecordPatches.EnsureQueryInMetadataCache(OtherUndeclaredQueryId));

        var memo = LazyMetaQueryCache();
        Assert.True(memo.Contains(OtherUndeclaredQueryId),
            "EnsureQueryInMetadataCache did not memoize its answer — see the sibling case.");
        Assert.Null(memo[OtherUndeclaredQueryId]);

        RecordPatches.ResetForReload();

        Assert.Empty(LazyMetaQueryCache());
    }

    [Fact]
    public void BothMemosStillMemoise_WithinOneBundle()
    {
        // The control that stops the fix from being "clear it so often it never caches". Both
        // caches exist for a real reason INSIDE a bundle — BuildRealNCLMetaQueryCore builds a
        // whole MetaQuery design and calls CreateDynamicQuery, and EnsureQueryInMetadataCache
        // additionally re-resolves FindQueryType and re-inserts a BC metadata cache entry — and
        // the answer genuinely cannot change without a reload.
        for (var i = 0; i < 3; i++)
        {
            RecordPatches.BuildRealNCLMetaQuery(UndeclaredQueryId, typeof(object));
            RecordPatches.EnsureQueryInMetadataCache(OtherUndeclaredQueryId);
        }

        // Exactly one entry each, three calls in: repeated calls are memo hits, not rebuilds.
        // "The same answer three times" would be equally satisfied by no memo at all, which is
        // why this asserts against the dictionaries rather than against the return values.
        Assert.Equal(1, RealMetaQueryCache().Count);
        Assert.Equal(1, LazyMetaQueryCache().Count);
        Assert.True(RealMetaQueryCache().Contains(UndeclaredQueryId));
        Assert.True(LazyMetaQueryCache().Contains(OtherUndeclaredQueryId));
    }

    /// <summary>The built-NCLMetaQuery memo. Read by reflection on purpose: no public surface
    /// reports it, and inferring "it was cleared" from a later lookup cannot tell a cleared cache
    /// from one that happens to agree with the current bundle.</summary>
    private static IDictionary RealMetaQueryCache() => StaticDictionary("_realMetaQueryCache");

    /// <summary>The GetById memo that wraps it — same reasoning.</summary>
    private static IDictionary LazyMetaQueryCache() => StaticDictionary("_lazyMetaQueryByGetById");

    private static IDictionary StaticDictionary(string fieldName)
    {
        var field = typeof(RecordPatches).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"RecordPatches.{fieldName} not found — this test tracks that field (#3210).");
        return (IDictionary)field.GetValue(null)!;
    }
}
