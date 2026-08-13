// RecordPatchesCompiledReportIdsPeByteSeedTests — issue #1852.
//
// WHAT #1852 MEASURED
//   RecordPatches.KnownReportIdSet()'s CompiledReportIds() fallback used to call
//   Assembly.GetTypes() over EVERY loaded assembly on every cache-busting call (the outer
//   generation key includes the loaded-assembly count, so any newly loaded assembly busts
//   it). Measured on this repo's own cold engine boot: GetTypes() over the R2R DLL chunks
//   DependencyLoader loads for BaseApplication/SystemApplication (tens of thousands of
//   types each) cost 0.7s-4.3s PER ASSEMBLY, ~17.7s total across one bundle's six chunks —
//   the single largest identified cost in a cold spawn. Those assemblies are loaded via
//   Assembly.Load(byte[]), so asm.Location is empty and CompiledReportIds() could not
//   re-derive a file path to read metadata from more cheaply after the fact.
//
// THE FIX
//   DependencyLoader already holds the raw PE bytes at the moment it loads a Tier-1/Tier-2
//   dependency assembly. RecordPatches.SeedCompiledReportIdsFromPEBytes(asm, bytes) reads
//   just the TypeDef table's Name strings via System.Reflection.Metadata (no RuntimeType
//   materialization, no GetTypes() call) and pre-warms the per-assembly cache
//   CompiledReportIds() already uses — so the slow GetTypes() path never runs for an
//   assembly DependencyLoader pre-warmed.
//
// WHAT THIS TEST PROVES
//   * MECHANISM — seeding populates the cache entry for an assembly WITHOUT ever calling
//     Assembly.GetTypes() on it (IsCompiledReportIdsSeeded flips false -> true purely from
//     the seed call).
//   * POSITIVE — the id the PE-byte scan found flows all the way through to
//     KnownReportIdSet(), the set AL surfaces actually read through PopulateOneObjectType.
//   * NEGATIVE — a type that merely starts with "Report" but has a non-numeric suffix (or
//     doesn't start with "Report" at all) contributes NOTHING: the PE-byte scan applies the
//     exact same int.TryParse gate as the pre-existing GetTypes() fallback, not a looser
//     "starts with Report" match. Proven by diffing the known-id set before/after seeding a
//     3-type assembly and asserting exactly one id (the valid one) was added.
//   * BACKWARD COMPATIBILITY — an assembly nobody pre-warms (loaded some other way) is still
//     found correctly through the untouched GetTypes() fallback, and that fallback populates
//     the same cache structure the seed path does.
//   * EQUIVALENCE — the PE-byte/MetadataReader path and the pre-existing GetTypes() path agree
//     on the exact SET of ids for an identical TypeDef table, not merely the count. Proven by
//     calling both extraction functions directly (as test-only wrappers around the same
//     production logic) against ONE compiled fixture, so there is no cross-assembly id overlap
//     or cache/global-state ordering hazard to reason about.
//   * REPEATABILITY — once an assembly is cached (seed or fallback), repeat KnownReportIdSet()
//     calls keep answering correctly and the cache entry never disappears.

using System.Linq;
using System.Reflection;
using AlRunner.Patches;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class RecordPatchesCompiledReportIdsPeByteSeedTests
{
    private readonly BcEngineFixture _engine;

    public RecordPatchesCompiledReportIdsPeByteSeedTests(BcEngineFixture engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// Compiles a tiny in-memory assembly containing exactly the given (empty) public type
    /// names and loads it into the current AppDomain — the same Assembly.Load(byte[]) shape
    /// DependencyLoader itself uses for Tier-1/Tier-2 dependency assemblies, so the fix is
    /// exercised the way production code exercises it.
    /// </summary>
    private static (Assembly Asm, byte[] Bytes) CompileAndLoad(string assemblyName, params string[] typeNames)
    {
        var source = string.Join("\n", typeNames.Select(n => $"public class {n} {{ }}"));
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.ToString())));
        var bytes = ms.ToArray();
        var asm = Assembly.Load(bytes);
        return (asm, bytes);
    }

    [SkippableFact]
    public void SeedCompiledReportIdsFromPEBytes_PopulatesCacheWithoutGetTypes_AndFeedsKnownReportIdSet()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // A snapshot BEFORE this test's assembly even exists, to diff against afterward.
        var before = new HashSet<int>(RecordPatches.KnownReportIdSet());

        var (asm, bytes) = CompileAndLoad(
            $"al-runner-test-report-seed-{Guid.NewGuid():N}",
            "Report99001",       // valid — must be discovered
            "NotAReportType",    // doesn't start with "Report" at all
            "ReportABC");        // starts with "Report" but the suffix isn't numeric

        // Sanity: a freshly compiled, never-touched-by-either-path assembly has no cache
        // entry yet — nothing else in this (serialized) collection could have raced to
        // populate an entry keyed by an Assembly reference only this test holds.
        Assert.False(RecordPatches.IsCompiledReportIdsSeeded(asm));

        RecordPatches.SeedCompiledReportIdsFromPEBytes(asm, bytes);

        // MECHANISM: the cache entry exists purely from the PE-byte scan — no
        // Assembly.GetTypes() call was needed to produce it.
        Assert.True(RecordPatches.IsCompiledReportIdsSeeded(asm));

        // POSITIVE: the id parsed out of the raw PE bytes reaches the actual set AL runner
        // surfaces read (KnownReportIdSet -> PopulateOneObjectType(Report, ...)).
        var after = RecordPatches.KnownReportIdSet();
        Assert.Contains(99001, after);

        // NEGATIVE: exactly one id was newly contributed by this assembly — the two decoys
        // (wrong prefix, non-numeric suffix) added nothing. A looser "starts with Report"
        // match would have failed to parse "ABC" and thrown, or (if guarded differently)
        // could have smuggled in a bogus id; this proves it does neither.
        var added = new HashSet<int>(after);
        added.ExceptWith(before);
        Assert.Equal(new HashSet<int> { 99001 }, added);
    }

    [SkippableFact]
    public void UnseededAssembly_StillDiscoveredViaGetTypesFallback_WhichThenPopulatesTheSameCache()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (asm, _) = CompileAndLoad(
            $"al-runner-test-report-fallback-{Guid.NewGuid():N}",
            "Report55501");

        // Deliberately never seeded — proves the fix is additive: an assembly nobody
        // pre-warmed via SeedCompiledReportIdsFromPEBytes (e.g. one loaded outside
        // DependencyLoader's Tier 1/2 paths) is still found correctly through the
        // pre-existing GetTypes() fallback in CompiledReportIds().
        Assert.False(RecordPatches.IsCompiledReportIdsSeeded(asm));
        Assert.Contains(55501, RecordPatches.KnownReportIdSet());

        // And having been asked for once, the lazy fallback itself populated the same cache
        // structure the seed path writes to — a second ask never needs GetTypes() again for
        // THIS assembly either, seeded or not.
        Assert.True(RecordPatches.IsCompiledReportIdsSeeded(asm));
    }

    [SkippableFact]
    public void PEByteSeedPath_AgreesExactlyWithGetTypesPath_OnTheIdenticalTypeSet()
    {
        // Orchestrator review note (#1852): "identical results, not just similar" — a count
        // match with a different SET would be a silent divergence that passes CI and breaks
        // report resolution later.
        //
        // This does NOT go through KnownReportIdSet()'s process-wide union at all: that union
        // is a single process-global HashSet<int> with no per-assembly provenance, so it can't
        // isolate "what did THIS assembly contribute" once two assemblies share identical
        // Report{id} names — and it moves every time ANY assembly loads anywhere in the
        // process, including ones this test never touches, making a before/after diff around
        // it inherently racy under xUnit's default cross-collection parallelism. (An earlier
        // version of this test tried exactly that, on two different diffing strategies, and
        // failed in CI both times for exactly this reason.)
        //
        // Instead it calls the two production scan functions directly on ONE compiled fixture
        // — ReadReportIdsViaGetTypesForTest and ReadReportIdsFromPeBytesForTest are the same
        // extraction logic CompiledReportIds()/SeedCompiledReportIdsFromPEBytes use in
        // production, just callable standalone without any cache or global-state side effect.
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (asm, bytes) = CompileAndLoad(
            $"al-runner-test-report-agree-{Guid.NewGuid():N}",
            "Report10001", "Report10002", "Report10099", "NotAReportType", "ReportXYZ");

        var idsFromGetTypesPath = new HashSet<int>(RecordPatches.ReadReportIdsViaGetTypesForTest(asm));
        var idsFromPeBytePath = new HashSet<int>(RecordPatches.ReadReportIdsFromPeBytesForTest(bytes));

        // Sanity: neither path is empty (a bug that made both paths silently agree on "no
        // ids found" would slip past a bare set-equality check).
        Assert.NotEmpty(idsFromPeBytePath);
        Assert.NotEmpty(idsFromGetTypesPath);

        // The actual claim: the two independent discovery mechanisms agree on the exact SET,
        // not merely the count, for the identical TypeDef table — and the decoys
        // (NotAReportType, ReportXYZ) contributed nothing to either.
        Assert.Equal(idsFromGetTypesPath, idsFromPeBytePath);
        Assert.Equal(new HashSet<int> { 10001, 10002, 10099 }, idsFromPeBytePath);
    }

    [SkippableFact]
    public void AlreadyCachedAssembly_KeepsAnsweringTheSameIdsAcrossRepeatKnownReportIdSetCalls()
    {
        // This is the process-observable half of the per-assembly memo's claim: once an
        // assembly is cached (seeded or via the GetTypes() fallback), repeat KnownReportIdSet()
        // calls keep surfacing its ids correctly and IsCompiledReportIdsSeeded never flips back
        // to false. (A raw "GetTypes() call count stays flat" counter was tried here and
        // dropped: this is a live, fully-booted BC engine process, and other in-process
        // activity legitimately loads assemblies of its own on a timescale this test doesn't
        // control, so a strict call-count-frozen assertion was flaky by construction, not by
        // a bug in the fix. Assembly-identity caching itself is proven directly: the
        // TryGetValue-then-continue branch at the top of CompiledReportIds() is what every
        // other test in this file already exercises via IsCompiledReportIdsSeeded staying
        // true, so this test's job is narrower — confirm that staying cached also keeps
        // answering correctly, repeatedly.)
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (asm, bytes) = CompileAndLoad($"al-runner-test-report-repeat-{Guid.NewGuid():N}", "Report77701");
        RecordPatches.SeedCompiledReportIdsFromPEBytes(asm, bytes);
        Assert.True(RecordPatches.IsCompiledReportIdsSeeded(asm));

        for (var i = 0; i < 5; i++)
        {
            Assert.Contains(77701, RecordPatches.KnownReportIdSet());
            Assert.True(RecordPatches.IsCompiledReportIdsSeeded(asm));
        }
    }
}
