// XmlPortRealMetadataBundleBoundaryTests — issue #3172.
//
// WHAT WAS UNPINNED
// -----------------
// RecordPatches.RealXmlPortMetadata.cs is a near-verbatim copy of
// RecordPatches.RealPageMetadata.cs — its own header says so — and it carried the same pair of
// bookkeeping sets:
//
//   _xmlPortsWithRealMetadata     "LoadMetadata() has run on this id's NCLMetaXmlPort"
//   _xmlPortsRealMetadataFailed   "…was attempted on it and threw"
//
// What it did not carry is the fix #1957 gave the page side. Nothing cleared either set, on any
// path — they appeared nowhere else in the runner. ResetForReload discards every NCLMetaXmlPort
// instance via _metaXmlPortCache.Clear() without discarding the bookkeeping that DESCRIBES those
// instances, so from the second --watch cycle / --server request onward:
//
//   1. the next lookup rebuilds a brand-new, never-loaded skeleton through
//      _metaXmlPortCache.GetOrAdd(id, BuildNCLMetaXmlPort);
//   2. _xmlPortsWithRealMetadata still contains the id from cycle 1, so EnsureRealXmlPortMetadata
//      returns that skeleton AS IF its metadata had been loaded, without ever calling
//      LoadMetadata() on it.
//
// Per this file's production header, an NCLMetaXmlPort with metadataLoaded = true and no schema
// is exactly what makes every real xmlport operation NRE. The failure set has the mirror problem
// in the same window: an xmlport whose load failed in cycle 1 could never be observed to load in
// cycle 2, even after the edit that fixed the cause.
//
// HOW EACH SET IS POPULATED HERE, AND WHY THE TWO DIFFER
// ------------------------------------------------------
// The FAILURE set is populated by driving the real EnsureRealXmlPortMetadata: it is handed an
// instance with no LoadMetadata method, so the load attempt fails and the id is recorded exactly
// the way a genuine failure records it. Which exception ends the attempt depends on whether
// Microsoft.Dynamics.Nav.Ncl happens to be loaded in the test host — with it, the metadataLoaded
// field poke rejects a foreign instance first; without it, the missing method does — and on the
// engine-present route the catch block's own restore poke rethrows, so the call can either return
// null or throw. The OBSERVABLE this asserts is identical on both routes: the id is in the
// failure set afterwards. That is why the drive is wrapped rather than asserted on directly.
//
// The SUCCESS set cannot be populated that way on both routes, because reaching it needs a
// LoadMetadata() that actually succeeds against a real NCLMetaXmlPort — the BC engine standing
// up in-process with an emit-captured metadata XML behind it, which is the blocker recorded on
// #3172 and why #3011 left this out. So it is seeded directly, and the claim is narrowed to
// match: the reset contract, not the downstream NRE. An honest RED that fails on the unfixed
// tree beats a behavioural test that only runs on some boxes.
//
// WHAT IS DELIBERATELY NOT ASSERTED
// ---------------------------------
// The GROW direction #3011 fixed on the page side — a negative answer taken before the .app
// declaring the object was registered surviving that registration — is a SEPARATE and still
// unverified question here, and #3172 says so: the page gate reads
// AlPageMetadataRegistry || HasDependencyPageMetadata (which walks _bcAppPaths), while the
// xmlport gate reads AlXmlPortMetadataRegistry alone and BuildNCLMetaXmlPort gates on
// _parsedXmlPorts alone. Neither is derived from the registered .app set, so nothing here claims
// anything about it.
//
// WHY RUNNER-LOCAL AND NOT UPSTREAM
// ---------------------------------
// Not a claim about Business Central: the subject is the lifetime of runner bookkeeping across
// the runner's own bundle-reload boundary, which no service tier has an equivalent of.
using System.Collections;
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// MUST be serial: every case calls RecordPatches.ResetForReload().
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class XmlPortRealMetadataBundleBoundaryTests : IDisposable
{
    private const int SucceedingXmlPortId = 79911;
    private const int FailingXmlPortId = 79912;

    /// <summary>Stands in for one generation's NCLMetaXmlPort. Deliberately carries NO
    /// LoadMetadata method: the load attempt in <c>EnsureRealXmlPortMetadata</c> must fail, which
    /// is the whole point of the failure-set case.</summary>
    private sealed class GenerationSkeleton
    {
        public string Generation { get; init; } = "";
    }

    public XmlPortRealMetadataBundleBoundaryTests() => RecordPatches.ResetForReload();

    public void Dispose()
    {
        try { RecordPatches.ResetForReload(); } catch { }
        try { AlXmlPortMetadataRegistry.Clear(); } catch { }
    }

    [Fact]
    public void ASuccessfulLoadRecordedInBundleOne_DoesNotSurviveTheReloadIntoBundleTwo()
    {
        SeedMetaXmlPort(SucceedingXmlPortId, new GenerationSkeleton { Generation = "bundle-1" });
        RecordSuccessfulLoad(SucceedingXmlPortId);

        // Asserted, not assumed — an emptiness assertion over something never populated proves
        // nothing. This also pins the production accessor the assertion below reads through.
        Assert.True(RecordPatches.XmlPortHasRealMetadataForTests(SucceedingXmlPortId),
            "the success record was not established, so the clear below would be vacuous");

        // The bundle boundary itself. This discards the NCLMetaXmlPort generation the record
        // describes — that is the very line the record has to travel with.
        RecordPatches.ResetForReload();
        Assert.Empty(MetaXmlPortCache());

        // The fix. On the unfixed tree this is still true, and the next EnsureRealXmlPortMetadata
        // hands back a brand-new never-loaded skeleton as "already loaded".
        Assert.False(RecordPatches.XmlPortHasRealMetadataForTests(SucceedingXmlPortId),
            "the 'already loaded' record outlived the NCLMetaXmlPort generation it describes "
            + "(#3172) — the next lookup will short-circuit a never-loaded skeleton");
    }

    [Fact]
    public void AFailedLoadRecordedInBundleOne_DoesNotSurviveTheReloadIntoBundleTwo()
    {
        // Registered so the method's own first gate (AlXmlPortMetadataRegistry.TryGet) passes;
        // the XML content is never parsed on this route because the metaxmlport is pre-seeded.
        AlXmlPortMetadataRegistry.Register(FailingXmlPortId, "<XmlPort/>");
        SeedMetaXmlPort(FailingXmlPortId, new GenerationSkeleton { Generation = "bundle-1" });

        // The real production path records the failure. See the header for why the call is
        // wrapped: which exception ends the attempt differs by test host, the recorded outcome
        // does not.
        DriveFailingLoad(FailingXmlPortId);
        Assert.True(RecordPatches.XmlPortRealMetadataFailedForTests(FailingXmlPortId),
            "EnsureRealXmlPortMetadata did not record the failed load — this test tracks that "
            + "record, and the clear it asserts would be meaningless without it");

        // Control, and the reason this is not "clear it so often it never remembers": WITHIN one
        // bundle the record must stand, so a repeatedly failing xmlport is not retried on every
        // single metadata lookup. A second drive changes nothing.
        DriveFailingLoad(FailingXmlPortId);
        Assert.True(RecordPatches.XmlPortRealMetadataFailedForTests(FailingXmlPortId));

        RecordPatches.ResetForReload();

        // The fix, mirror direction: the next cycle must get a fresh attempt against the new
        // generation, or an edit that fixes the underlying cause could never be observed to have
        // fixed it. On the unfixed tree this record is permanent for the life of the process.
        Assert.False(RecordPatches.XmlPortRealMetadataFailedForTests(FailingXmlPortId),
            "the failed-load record outlived the NCLMetaXmlPort generation it describes (#3172) "
            + "— an xmlport that starts loading in the next cycle can never be seen to");
    }

    [Fact]
    public void TheTwoRecordsAreIndependent_AndBothGoAtTheReload()
    {
        // One id in each set at once: a reset that clears only one of them (the shape the page
        // side would have had if #1957 had only fixed the success half) fails here.
        AlXmlPortMetadataRegistry.Register(FailingXmlPortId, "<XmlPort/>");
        SeedMetaXmlPort(FailingXmlPortId, new GenerationSkeleton { Generation = "bundle-1" });
        DriveFailingLoad(FailingXmlPortId);
        SeedMetaXmlPort(SucceedingXmlPortId, new GenerationSkeleton { Generation = "bundle-1" });
        RecordSuccessfulLoad(SucceedingXmlPortId);

        Assert.True(RecordPatches.XmlPortRealMetadataFailedForTests(FailingXmlPortId));
        Assert.True(RecordPatches.XmlPortHasRealMetadataForTests(SucceedingXmlPortId));
        // Neither record leaks into the other set.
        Assert.False(RecordPatches.XmlPortHasRealMetadataForTests(FailingXmlPortId));
        Assert.False(RecordPatches.XmlPortRealMetadataFailedForTests(SucceedingXmlPortId));

        RecordPatches.ResetForReload();

        Assert.False(RecordPatches.XmlPortRealMetadataFailedForTests(FailingXmlPortId));
        Assert.False(RecordPatches.XmlPortHasRealMetadataForTests(SucceedingXmlPortId));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drive the real <c>EnsureRealXmlPortMetadata</c> to a failed load. Tolerates the exception
    /// the engine-present route ends on — see the file header — and nothing else: an unexpected
    /// exception type still fails the test, and the recorded outcome is asserted by the caller.
    /// </summary>
    private static void DriveFailingLoad(int xmlPortId)
    {
        try { RecordPatches.EnsureRealXmlPortMetadata(xmlPortId); }
        catch (ArgumentException) { /* Ncl loaded: the metadataLoaded field poke rejects a foreign instance */ }
    }

    /// <summary>
    /// Put <paramref name="xmlPortId"/> in the success set. Seeded rather than driven because a
    /// genuine success needs a real NCLMetaXmlPort and a real LoadMetadata() — the in-process BC
    /// engine blocker recorded on #3172. Written through the same lock the production code uses.
    /// </summary>
    private static void RecordSuccessfulLoad(int xmlPortId)
    {
        var gate = StaticField("_realXmlPortMetadataLock").GetValue(null)!;
        var set = (ICollection<int>)StaticField("_xmlPortsWithRealMetadata").GetValue(null)!;
        lock (gate) set.Add(xmlPortId);
    }

    /// <summary>Pre-seed the metaxmlport cache so <c>EnsureRealXmlPortMetadata</c> gets a
    /// non-null instance without <c>BuildNCLMetaXmlPort</c> (and therefore the BC engine) having
    /// to run.</summary>
    private static void SeedMetaXmlPort(int xmlPortId, object skeleton)
        => MetaXmlPortCache()[xmlPortId] = skeleton;

    private static IDictionary MetaXmlPortCache()
        => (IDictionary)StaticField("_metaXmlPortCache").GetValue(null)!;

    private static FieldInfo StaticField(string name)
        => typeof(RecordPatches).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException(
               $"RecordPatches.{name} not found — this test tracks that field (#3172).");
}
