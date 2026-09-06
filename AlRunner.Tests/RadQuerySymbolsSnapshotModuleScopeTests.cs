// RadQuerySymbolsSnapshotModuleScopeTests — the module-scope guard for #2939's replay snapshot.
//
// #2939's second half shadows a bundle's query-symbol source file per module
// (BcCompiler._radQuerySymbolsPathByModule) so the RAD fast paths can re-register it on a
// --watch/--server cycle that never calls Emit. The value captured came from
// BcCompiler.LastBundleQuerySymbolsPath, a PROCESS-GLOBAL static, and the code justified that
// with "CaptureRadMetadataSnapshotFull is called from RecordIncrementalBaseline, i.e. from
// inside that same Emit". That is true of one of RecordIncrementalBaseline's two call sites and
// false of the other:
//
//   BcCompiler.Emit                                    nulls the static at the top and sets it
//                                                      via EmitAndRegisterBundleQuerySymbols —
//                                                      the justification holds.
//   BcCompiler.EmitDepSymbols(trackIncrementalBaseline) never touches the static at all. It
//                                                      reads whatever the last Emit ANYWHERE in
//                                                      the process left behind — and source-dep
//                                                      compilers are separate BcCompiler
//                                                      instances from the bundle emitter
//                                                      (Program.cs's layered pre-pass), so that
//                                                      value names a DIFFERENT module.
//
// Consequence: a dependency module's snapshot gets populated with another module's
// SymbolReference.json. TryEmitIncremental's fast-path returns then call
// ReplayRadMetadataSnapshot(thatDepModule), which re-registers the foreign file into
// RecordPatches._bcQuerySymbolJsonPaths. The merge in EnsureBcSymbolQueryIndex is FIRST-WINS by
// query id, so a colliding id hands back the foreign module's column ids, which go verbatim to
// NavQuery.GetColumnValueSafe — a wrong value out of a real row, the exact shape #2939 exists
// to close.
//
// Cycle 1 is safe (the static is null before any Emit), which is why nothing else in the suite
// sees this: WatchQuerySymbolsReloadTests drives a single-app bundle with no source dependency.
//
// What each test proves (tdd.md: must prove, not just pass):
//   - The dep path, run in the same process AFTER a query-declaring bundle's Emit, records NO
//     query-symbol path for its own module. RED before the fix: it records the bundle's.
//   - The bundle's OWN module still records its own path — the control that stops the fix from
//     being "capture nothing, ever", which would satisfy the first assertion and silently
//     re-break #2939's --watch half.
using System.Reflection;
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class RadQuerySymbolsSnapshotModuleScopeTests : IDisposable
{
    private readonly string _bundleRoot;
    private readonly string _depRoot;
    private readonly BcEngineFixture _engine;

    private const string BundleModule = "RadQsBundleAlpha";
    private const string DepModule = "RadQsDepBeta";

    private static readonly Guid DepAppId = new("c4d5e6f7-9360-4b22-9222-222222222222");

    public RadQuerySymbolsSnapshotModuleScopeTests(BcEngineFixture engine)
    {
        _engine = engine;
        var root = TestScratch.Dir("al-runner-radqs-module-scope");
        _bundleRoot = Path.Combine(root, "bundle");
        _depRoot = Path.Combine(root, "dep");
        Directory.CreateDirectory(_bundleRoot);
        Directory.CreateDirectory(_depRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_bundleRoot)!, recursive: true); } catch { /* best-effort */ }
    }

    // A bundle that genuinely declares a query, so Emit's BundleDeclaresQuery probe fires and
    // EmitAndRegisterBundleQuerySymbols actually writes a SymbolReference.json for it.
    private const string BundleAl = """
        table 90360 "RadQs Alpha Row"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
                field(2; Amount; Integer) { DataClassification = CustomerContent; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        query 90361 "RadQs Alpha Totals"
        {
            QueryType = Normal;
            elements
            {
                dataitem(Row; "RadQs Alpha Row")
                {
                    column(EntryNo; "Entry No.") { }
                    column(TotalAmount; Amount) { Method = Sum; }
                }
            }
        }
        """;

    // The source dependency. Declares NO query, so a correct capture records nothing for it.
    private const string DepAl = """
        codeunit 90362 "RadQs Beta Lib"
        {
            procedure Ping(): Integer
            begin
                exit(7);
            end;
        }
        """;

    /// <summary>The private per-module query-symbol snapshot on a BcCompiler instance. Read by
    /// reflection deliberately: this is runner-internal shadow state with no public surface, and
    /// the defect is precisely that the wrong value lands IN it — asserting on a public
    /// side effect instead would only observe it once a query id happened to collide.</summary>
    private static IReadOnlyDictionary<string, string> QuerySymbolsSnapshotOf(BcCompiler compiler)
    {
        var field = typeof(BcCompiler).GetField(
            "_radQuerySymbolsPathByModule", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "BcCompiler._radQuerySymbolsPathByModule no longer exists — if the snapshot moved, "
                + "move this test with it rather than deleting it.");
        return (Dictionary<string, string>)field.GetValue(compiler)!;
    }

    [SkippableFact]
    public void DepSymbolBaseline_DoesNotAdoptAnotherModulesQuerySymbolsPath()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        using var identityScope = BcCompiler.ScopeCurrentAppIdentity(DepAppId, "AL Runner", new Version(1, 0, 0, 0));

        File.WriteAllText(Path.Combine(_bundleRoot, "Alpha.al"), BundleAl);
        File.WriteAllText(Path.Combine(_depRoot, "Beta.al"), DepAl);

        // 1. The bundle emitter. A separate BcCompiler instance, exactly as Program.cs's layered
        //    pre-pass builds one per source dependency and another for the bundle itself.
        var bundleCompiler = new BcCompiler();
        var bundleOut = bundleCompiler.Emit(new[] { _bundleRoot }, BundleModule, trackIncrementalBaseline: true);
        Assert.Empty(bundleOut.Diagnostics);

        var bundleQuerySymbolsPath = BcCompiler.LastBundleQuerySymbolsPath;
        Assert.NotNull(bundleQuerySymbolsPath);
        Assert.True(File.Exists(bundleQuerySymbolsPath), $"'{bundleQuerySymbolsPath}' should have been written by the bundle's Emit");
        // The path encodes the module it belongs to (PerProcessScratch, #2967) — this is what
        // makes "borrowed" observable at all.
        Assert.Contains(BundleModule, bundleQuerySymbolsPath!, StringComparison.Ordinal);

        // 2. The source dependency, on its own instance, in the same process, AFTER the bundle's
        //    Emit left the static pointing at the bundle's file. Nothing on this path writes or
        //    clears LastBundleQuerySymbolsPath.
        var depCompiler = new BcCompiler();
        depCompiler.EmitDepSymbols(
            new[] { _depRoot }, DepModule, DepAppId, "AL Runner", new Version(1, 0, 0, 0),
            Path.Combine(_depRoot, "out.symbols.json"), appRootDir: null, trackIncrementalBaseline: true);

        var depSnapshot = QuerySymbolsSnapshotOf(depCompiler);
        Assert.False(
            depSnapshot.TryGetValue(DepModule, out var borrowed),
            $"'{DepModule}' declares no query, so its RAD snapshot must record no query-symbol source. "
            + $"It recorded '{borrowed}' — which belongs to '{BundleModule}'. Replaying that on a RAD "
            + "fast path re-registers a foreign SymbolReference.json, and the first-wins merge in "
            + "EnsureBcSymbolQueryIndex then serves the wrong column ids for any colliding query id.");

        // 3. The control: the bundle's own module DID capture its own path. Without this a fix
        //    that simply never captures anything passes the assertion above while silently
        //    re-breaking #2939's --watch half (4 PASS cycle 1, 4 FAIL from cycle 2).
        var bundleSnapshot = QuerySymbolsSnapshotOf(bundleCompiler);
        Assert.True(
            bundleSnapshot.TryGetValue(BundleModule, out var own),
            $"'{BundleModule}' declares a query, so its own RAD snapshot must record the "
            + "SymbolReference.json its Emit just wrote — that is what the fast paths replay.");
        Assert.Equal(bundleQuerySymbolsPath, own);
    }

    [SkippableFact]
    public void DepSymbolBaseline_LeavesTheBundlesOwnSnapshotUntouched()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        using var identityScope = BcCompiler.ScopeCurrentAppIdentity(DepAppId, "AL Runner", new Version(1, 0, 0, 0));

        File.WriteAllText(Path.Combine(_bundleRoot, "Alpha.al"), BundleAl);
        File.WriteAllText(Path.Combine(_depRoot, "Beta.al"), DepAl);

        var bundleCompiler = new BcCompiler();
        bundleCompiler.Emit(new[] { _bundleRoot }, BundleModule, trackIncrementalBaseline: true);
        var afterEmit = QuerySymbolsSnapshotOf(bundleCompiler)[BundleModule];

        // A dep compile between two of the bundle's cycles must not disturb what the bundle
        // replays. (Same instance re-emitting is the --watch shape; the dep compile in between is
        // the multi-app bundle shape.)
        new BcCompiler().EmitDepSymbols(
            new[] { _depRoot }, DepModule, DepAppId, "AL Runner", new Version(1, 0, 0, 0),
            Path.Combine(_depRoot, "out2.symbols.json"), appRootDir: null, trackIncrementalBaseline: true);

        Assert.Equal(afterEmit, QuerySymbolsSnapshotOf(bundleCompiler)[BundleModule]);
        Assert.Contains(BundleModule, afterEmit, StringComparison.Ordinal);
    }
}
