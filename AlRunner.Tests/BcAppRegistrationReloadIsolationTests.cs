// BcAppRegistrationReloadIsolationTests — #2755.
//
// The asymmetry
// -------------
// RecordPatches._bcAppPaths is a process-global List<string> that only ever grows:
// AddBcAppPath appends, and nothing removed anything. Every index DERIVED from it does
// reset — RecordPatches.ResetForReload (the per-bundle/per-request reload path) clears
// _parsedTables, _parsedPages, _parsedReports, _parsedObjectDecls and the rest, then calls
// InvalidateBcAppIndexes() precisely so the next lookup rebuilds the symbol table/extension/
// query indexes FROM _bcAppPaths.
//
// So the two halves of every one of those indexes reset on different boundaries: the
// source-parsed half per bundle, the .app-symbol half never. In --server and --watch, bundle
// 2 is therefore resolved against its own registered .app symbol sources UNION every earlier
// bundle's, while a fresh single-bundle process running bundle 2 alone sees only its own.
// Nothing errors; the run stays green answering a question nobody asked.
//
// Three separate defects fall out of that one asymmetry, and each has its own fact below:
//
//   1. CONTAMINATION (the reported one). Bundle 1's .app stays registered for bundle 2.
//   2. ENUM METADATA LOSS, in the opposite direction. BcRuntime.ResetForNewBundleReload()
//      calls AlEnumMetadataRegistry.Clear() and then RecordPatches.ResetForReload(). The ONLY
//      live path by which a precompiled dependency's enums reach that registry is
//      AddBcAppPath (see its own comment; AlEnumMetadataRegistry.RegisterFromAppPath has no
//      callers) — and AddBcAppPath's first act is `if (_bcAppPaths.Contains(appPath)) return`.
//      Program.cs re-registers every dependency .app on every request, but from request 2 on
//      that call is a no-op, so the per-value Captions and the DefaultImplementation /
//      UnknownValueImplementation fallbacks the registry was just emptied of are never put
//      back. Same shape as #2478: a reset that reset one half of a pair.
//   3. NEGATIVE-CACHE STALENESS. _bcMissCache remembers table ids that were looked for and
//      not found. It is derived from exactly the same inputs as the indexes
//      InvalidateBcAppIndexes drops, and it was not dropped with them — so an .app registered
//      AFTER a miss can never satisfy that miss, for the life of the process. That one bites
//      plain CLI multi-bundle runs too, not just --server/--watch.
//
// What must NOT be cleared: the SystemApp package. RecordPatches.RegisterSystemAppPackage()
// extracts Microsoft.BusinessCentral.SystemApp.dll's embedded SystemPackage to a temp .app and
// registers it, and it is called exactly once, from Register() (hook install) — never again.
// Clearing it on reload would silently remove the NCL-internal system tables (Field
// 2000000041, RecordLink 2000000068, Object 2000000038, …) from request 2 onward. That is the
// "cleared too much" failure mode, and BcAppPathsSurvivingReload is the pure core that decides
// it, pinned in both directions below.
//
// The AL-visible, two-bundles-in-one-process proof of (1) lives in
// ServerBundleSymbolIsolationTests — this class pins the mechanism.

using System.IO.Compression;
using System.Text;
using AlRunner;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: these facts drive RecordPatches.ResetForReload() and the
// process-global .app registry, the same shared statics the parser tests read.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class BcAppRegistrationReloadIsolationTests : IDisposable
{
    // 60911-60919 (checked free across AlRunner.Tests and tests/runner-extras). These ids
    // never compile — they exist only inside synthetic SymbolReference.json payloads.
    private const int AlphaTableId = 60911;
    private const int BetaTableId = 60912;
    private const int MissProbeTableId = 60913;
    private const int AlphaEnumId = 60914;

    private readonly string _root;

    public BcAppRegistrationReloadIsolationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-2755-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// A minimal but COMPLETE .app — AddBcAppPath reads both symbol surfaces to completion
    /// (#2712) and throws if either fails, so the payload has to parse all the way through.
    /// </summary>
    private string WriteApp(string fileName, int tableId, string tableName, int? enumId = null)
    {
        var path = Path.Combine(_root, fileName);
        var enums = enumId == null
            ? "[]"
            : $$"""
                [
                    {
                      "Id": {{enumId}},
                      "Name": "Iso Reload Enum",
                      "Properties": [],
                      "Values": [
                        { "Ordinal": 0, "Name": "Alpha", "Properties": [ { "Name": "Caption", "Value": "Alpha Caption" } ] },
                        { "Ordinal": 1, "Name": "Beta", "Properties": [ { "Name": "Caption", "Value": "Beta Caption" } ] }
                      ]
                    }
                  ]
                """;
        using var fs = new FileStream(path, FileMode.Create);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write($$"""
            {
              "RuntimeVersion": "15.1",
              "Namespaces": [],
              "Tables": [
                {
                  "Id": {{tableId}},
                  "Name": "{{tableName}}",
                  "Properties": [],
                  "Fields": [
                    { "TypeDefinition": { "Name": "Code[10]" }, "Properties": [], "Id": 1, "Name": "Code" }
                  ]
                }
              ],
              "EnumTypes": {{enums}},
              "TableExtensions": []
            }
            """);
        return path;
    }

    // ── 1. contamination: bundle 2 must not resolve bundle 1's symbols ──────────────────────

    [Fact]
    public void AfterReload_TheEarlierBundlesAppIsNoLongerRegistered_ButTheNewOneIs()
    {
        // Bundle 1.
        var alpha = WriteApp("IsoAlpha_1.0.0.0.app", AlphaTableId, "Iso Alpha Ghost");
        RecordPatches.AddBcAppPath(alpha);

        // Sanity: the registration really did make bundle 1's table resolvable by name
        // through the symbol index. Without this the "not visible later" assertion below
        // could pass for the trivial reason that it was never visible at all.
        var seenInBundle1 = RecordPatches.TryPopulateParsedTableByName("Iso Alpha Ghost");
        Assert.NotNull(seenInBundle1);
        Assert.Equal(AlphaTableId, seenInBundle1!.TableId);

        // The reload boundary: exactly what --server runs once per request and --watch once
        // per bundle (BcRuntime.ResetForNewBundleReload -> RecordPatches.ResetForReload).
        RecordPatches.ResetForReload();

        // Bundle 2 registers its OWN app and nothing else.
        var beta = WriteApp("IsoBeta_1.0.0.0.app", BetaTableId, "Iso Beta Ghost");
        RecordPatches.AddBcAppPath(beta);

        var registered = RecordPatches.RegisteredBcAppPathsForTests();

        // [THEN] bundle 1's .app is gone. Before the fix _bcAppPaths only ever grew, so it
        // was still there and every index rebuilt from it included bundle 1's tables.
        Assert.DoesNotContain(registered, p => string.Equals(p, alpha, StringComparison.OrdinalIgnoreCase));

        // [AND] the derived index agrees — this is the assertion that would still catch the
        // defect if _bcAppPaths were emptied but something else kept serving bundle 1.
        Assert.Null(RecordPatches.TryPopulateParsedTableByName("Iso Alpha Ghost"));

        // [AND] the positive arm: bundle 2's OWN symbols still resolve. Clearing too much —
        // e.g. dropping the registration list without letting the new one repopulate it —
        // would pass a contamination-only assertion and break every real run.
        Assert.Contains(registered, p => string.Equals(p, beta, StringComparison.OrdinalIgnoreCase));
        var seenInBundle2 = RecordPatches.TryPopulateParsedTableByName("Iso Beta Ghost");
        Assert.NotNull(seenInBundle2);
        Assert.Equal(BetaTableId, seenInBundle2!.TableId);
    }

    // ── 2. the opposite direction: enum metadata must come BACK after a reload ─────────────

    [Fact]
    public void AfterReload_ReRegisteringTheSameApp_RestoresItsEnumMetadata()
    {
        var alpha = WriteApp("IsoEnum_1.0.0.0.app", AlphaTableId, "Iso Enum Ghost", enumId: AlphaEnumId);

        RecordPatches.AddBcAppPath(alpha);
        Assert.True(AlEnumMetadataRegistry.TryGet(AlphaEnumId, out var before),
            "registering the .app must publish its enum — if this fails the fixture is wrong, not the runner.");
        Assert.Equal(new[] { "Alpha", "Beta" }, before.Options);
        Assert.Equal("Alpha Caption", before.Captions![0]);

        // The production pair, in production order: BcRuntime.ResetForNewBundleReload() calls
        // AlEnumMetadataRegistry.Clear() and then RecordPatches.ResetForReload().
        AlEnumMetadataRegistry.Clear();
        RecordPatches.ResetForReload();
        Assert.False(AlEnumMetadataRegistry.TryGet(AlphaEnumId, out _));

        // Program.cs re-registers every dependency .app on every request. Before the fix this
        // call hit `if (_bcAppPaths.Contains(appPath)) return` and did nothing at all, so the
        // enum stayed missing for the rest of the process's life.
        RecordPatches.AddBcAppPath(alpha);

        Assert.True(AlEnumMetadataRegistry.TryGet(AlphaEnumId, out var after),
            "after a reload, re-registering the same dependency .app must put its enum metadata "
            + "back — AddBcAppPath is the only live path that does it.");
        Assert.Equal(new[] { "Alpha", "Beta" }, after.Options);
        Assert.Equal(new[] { 0, 1 }, after.Indexes);
        // The per-value captions specifically (#1775): a registration that published the
        // option names but dropped the captions would satisfy the line above and still be
        // the bug that made Base App enum 205 uncastable to its interface.
        Assert.Equal("Alpha Caption", after.Captions![0]);
        Assert.Equal("Beta Caption", after.Captions![1]);
    }

    // ── 3. the negative cache is derived state too ─────────────────────────────────────────

    [Fact]
    public void RegisteringAnAppAfterAMiss_SatisfiesThatMiss()
    {
        // A lookup for a table nothing has registered: a genuine miss, recorded in
        // _bcMissCache so a non-existent table does not re-scan every .app on every Init().
        Assert.False(RecordPatches.TryPopulateParsedTableFromBcAppsForTests(MissProbeTableId));

        // Now an .app that DOES declare it is registered — the CLI multi-bundle case (bundle
        // 2's dependency) as much as the --server one.
        var probe = WriteApp("IsoMiss_1.0.0.0.app", MissProbeTableId, "Iso Miss Probe");
        RecordPatches.AddBcAppPath(probe);

        // [THEN] the miss is re-evaluated against the new registration. Before the fix
        // InvalidateBcAppIndexes dropped every derived index EXCEPT this one, so the answer
        // stayed "not found" permanently even though the symbols were right there.
        Assert.True(RecordPatches.TryPopulateParsedTableFromBcAppsForTests(MissProbeTableId),
            "an .app registered after a miss must be able to satisfy that miss — the negative "
            + "cache is derived from the registration set and has to be invalidated with it.");
    }

    // ── 4. what a reload must NOT drop ────────────────────────────────────────────────────

    [Fact]
    public void BcAppPathsSurvivingReload_KeepsOnlyTheSystemAppPackage()
    {
        // RegisterSystemAppPackage() runs once from Register() and is never called again, so
        // its temp .app is the one registration that is NOT bundle-derived. Dropping it would
        // remove Field/RecordLink/Object (2000000041/68/38) from request 2 onward.
        var registered = new[] { "/pkg/Base Application.app", "/tmp/al-runner-systemapp-abc.app", "/bundle/Mine.app" };

        var survivors = RecordPatches.BcAppPathsSurvivingReload(registered, "/tmp/al-runner-systemapp-abc.app");

        Assert.Equal(new[] { "/tmp/al-runner-systemapp-abc.app" }, survivors);
    }

    [Fact]
    public void BcAppPathsSurvivingReload_KeepsNothingWhenTheSystemPackageWasNeverRegistered()
    {
        // Negative arm: the survivor rule is "this exact path", not "anything that looks like
        // a platform package" and not "the first entry". With no system package registered,
        // a reload clears the list completely.
        var registered = new[] { "/pkg/Base Application.app", "/bundle/Mine.app" };

        Assert.Empty(RecordPatches.BcAppPathsSurvivingReload(registered, null));
    }
}
