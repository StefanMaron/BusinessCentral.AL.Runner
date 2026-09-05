// InstallBaselineKeySymbolStateTests — #2710.
//
// What went wrong
// ---------------
// The install-baseline cache (in-memory _depCompanyBaselineCache, and its cross-process disk
// tier InstallBaselineDiskCache) stores the rows the Install triggers and Company-Initialize
// wrote. RecordPatches.InstallBaselineDisk's own header claimed that snapshot is "deterministic
// given (dependency assembly set, runner build, BC version)", and TestExecutor
// .CurrentInstallBaselineCacheKey built the key from exactly those (plus #2258's --test-data
// identity).
//
// It is not. The triggers write through table metadata the runner builds from the BC .app
// symbol sources REGISTERED IN THIS PROCESS — RecordPatches._bcAppPaths, which
// EnsureBcSymbolTableIndex / EnsureBcSymbolExtensionIndex walk to build the table and
// table-extension indexes. That set is an input to the snapshot, and the key never named it,
// so a run whose registered set differed wrote its snapshot under the identical key another
// run then read back. Nothing at read time could tell: every entry passes its own validity
// check, which is why #2710's field report found that removing any ONE cache subdirectory left
// the bad result unchanged and only a full wipe restored it.
//
// The set varies between two runs whose (deps, runner, BC version) are identical:
//
//   * --server / --watch ACCUMULATE it. _bcAppPaths is process-global and nothing clears it —
//     RecordPatches.ResetForReload (the per-bundle reload path) calls InvalidateBcAppIndexes,
//     which drops the DERIVED indexes precisely so they rebuild FROM that list. Meanwhile the
//     key's only per-bundle term IS reset per bundle: InstallTriggerRunner.ResetForNewBundle
//     clears _depAssemblies. Two writers of the same per-bundle state, one holding the
//     invariant and one not.
//   * RegisterBundleSymbolApps SKIPS what it cannot read — an unreadable bundle-root .app is
//     dropped with a [warn] and the run continues, which silently removes a symbol source
//     without moving any other key term. Pinned below.
//
// What the difference is worth: #2712 measured a partial table-extension index (90 of 96 Base
// Application extensions dropped) flipping 47 Tests-SMB tests with an unchanged exit code.
//
// The fix is a key term, not a detector: RegisteredBcAppSymbolStateKey() names the input, so a
// run with a different symbol state simply MISSES instead of reading someone else's snapshot.
//
// Note on isolation: _bcAppPaths has no unregister, so these tests assert that registering
// something CHANGES the key rather than asserting an absolute key value — correct regardless of
// what an earlier test in the same process already registered.

using System.IO.Compression;
using System.Text;
using AlRunner;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: this class registers .app paths into the process-global
// RecordPatches registry (AddBcAppPath / RegisterBundleSymbolApps), the same shared state
// RecordPatchesBcAppSymbolReadFailureTests mutates.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class InstallBaselineKeySymbolStateTests : IDisposable
{
    private readonly string _root;

    public InstallBaselineKeySymbolStateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-2710-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // A minimal but COMPLETE .app: AddBcAppPath reads both symbol surfaces to completion
    // (#2712) and throws if either fails, so the file has to parse all the way through.
    // tableName varies the bytes, which is how the content-hash half of the key is exercised.
    private string WriteApp(string fileName, string tableName)
    {
        var path = Path.Combine(_root, fileName);
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
                  "Id": 60900,
                  "Name": "{{tableName}}",
                  "Properties": [],
                  "Fields": [
                    { "TypeDefinition": { "Name": "Code[10]" }, "Properties": [], "Id": 1, "Name": "Code" }
                  ]
                }
              ],
              "TableExtensions": []
            }
            """);
        return path;
    }

    private static string WriteCorruptApp(string path)
    {
        // Not a zip at all: OpenAppZip fails, so AddBcAppPath throws and
        // RegisterBundleSymbolApps takes its per-file skip branch.
        File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0xFF, 0xFF, 0xFF, 0xFF });
        return path;
    }

    // ── the key ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void InstallBaselineKey_ChangesWhenAnotherBcAppIsRegistered()
    {
        var before = TestExecutor.CurrentInstallBaselineCacheKey();

        RecordPatches.AddBcAppPath(WriteApp("Extra_1.0.0.0.app", "Two Sixty Nine"));

        var after = TestExecutor.CurrentInstallBaselineCacheKey();

        // [THEN] the key MOVED. Before the fix these were byte-identical: the registered .app
        // set was invisible to the key, so the snapshot captured with this app's tables in
        // scope was stored under the key a run without them would look up.
        Assert.NotEqual(before, after);
        Assert.Contains("|bcapps:", after);

        // [AND] it is the bcapps term that moved, not the dependency-set term — the two runs
        // have the same loaded dependency assemblies.
        Assert.Equal(
            before[..before.IndexOf("|bcapps:", StringComparison.Ordinal)],
            after[..after.IndexOf("|bcapps:", StringComparison.Ordinal)]);
    }

    [Fact]
    public void InstallBaselineKey_IsStableWhenNothingWasRegistered()
    {
        // Negative direction: the term must not be a nonce. Two consecutive reads with no
        // registration in between have to agree, or every run would MISS and the cache would
        // be dead weight rather than fixed.
        var first = TestExecutor.CurrentInstallBaselineCacheKey();
        var second = TestExecutor.CurrentInstallBaselineCacheKey();
        Assert.Equal(first, second);
    }

    [Fact]
    public void InstallBaselineKey_IsUnchangedByReRegisteringTheSameApp()
    {
        var app = WriteApp("Idempotent_1.0.0.0.app", "Idempotent Table");
        RecordPatches.AddBcAppPath(app);
        var after1 = TestExecutor.CurrentInstallBaselineCacheKey();

        RecordPatches.AddBcAppPath(app);
        var after2 = TestExecutor.CurrentInstallBaselineCacheKey();

        Assert.Equal(after1, after2);
        Assert.Single(RecordPatches.RegisteredBcAppPathsForTests(), p =>
            string.Equals(p, app, StringComparison.OrdinalIgnoreCase));
    }

    // ── the reachable path: a bundle-root .app that cannot be read is silently dropped ─────

    [Fact]
    public void RegisterBundleSymbolApps_SkippingAnUnreadableApp_LeavesADifferentKeyThanReadingIt()
    {
        // Two bundle roots that differ ONLY in whether their bundle-root .app is readable.
        // RegisterBundleSymbolApps skips the unreadable one with a [warn] and the run
        // continues — the right call for an optional input, but it removes a symbol source
        // that install triggers would otherwise have seen.
        var healthyRoot = Path.Combine(_root, "healthy");
        var brokenRoot = Path.Combine(_root, "broken");
        Directory.CreateDirectory(healthyRoot);
        Directory.CreateDirectory(brokenRoot);
        WriteCorruptApp(Path.Combine(brokenRoot, "Bundle_1.0.0.0.app"));

        var baseline = TestExecutor.CurrentInstallBaselineCacheKey();

        RecordPatches.RegisterBundleSymbolApps(brokenRoot);
        var afterSkip = TestExecutor.CurrentInstallBaselineCacheKey();

        // [THEN] the skipped .app contributed nothing — no registration, so no key movement.
        // That is the honest answer; the defect was that the HEALTHY case did not move it
        // either, so the two were indistinguishable.
        Assert.Equal(baseline, afterSkip);

        // Now the same bundle root with a readable .app.
        var healthyApp = Path.Combine(healthyRoot, "Bundle_1.0.0.0.app");
        using (var fs = new FileStream(healthyApp, FileMode.Create))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write("""
                { "RuntimeVersion": "15.1", "Namespaces": [], "Tables": [], "TableExtensions": [] }
                """);
        }
        RecordPatches.RegisterBundleSymbolApps(healthyRoot);
        var afterHealthy = TestExecutor.CurrentInstallBaselineCacheKey();

        // [THEN] reading it DOES move the key, so "this run saw the bundle's symbols" and
        // "this run did not" are now different cache entries instead of the same one.
        Assert.NotEqual(afterSkip, afterHealthy);
    }

    // ── the digest itself ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SymbolStateKey_IsOrderIndependent()
    {
        // Registration order follows dependency-resolution order, which is not part of what
        // was registered. Two orderings of the same set must be one cache entry, not two.
        var a = ("/pkg/A_1.0.0.0.app", "aaaa");
        var b = ("/pkg/B_1.0.0.0.app", "bbbb");
        Assert.Equal(
            RecordPatches.ComputeBcAppSymbolStateKey(new[] { a, b }, Array.Empty<string>()),
            RecordPatches.ComputeBcAppSymbolStateKey(new[] { b, a }, Array.Empty<string>()));
    }

    [Fact]
    public void SymbolStateKey_ChangesWhenTheSameBytesParseToADifferentShape()
    {
        // #2756 — the property #2753 MISSED, and the one #2710's field incident most likely ran
        // through. #2753 keyed on (path, content hash), reasoning that identical bytes mean
        // identical symbols. #2712 is a measured counter-example: the same Base Application
        // SymbolReference.json parsed to 90 of 96 table extensions after an allocation failure
        // was swallowed mid-parse, and the process carried on. The install triggers wrote through
        // that degraded metadata and the snapshot was persisted complete, valid, and WRONG under a
        // key byte-identical to a healthy run's.
        //
        // So the per-app term now carries the parse SHAPE alongside the content hash. Same bytes,
        // different parse result, different key — which is the difference between a cross-process
        // silent wrong answer and a cache miss.
        var healthy = RecordPatches.ComputeBcAppSymbolStateKey(
            new[] { ("/pkg/Base Application.app", "same-bytes|t2100|x96") }, Array.Empty<string>());
        var degraded = RecordPatches.ComputeBcAppSymbolStateKey(
            new[] { ("/pkg/Base Application.app", "same-bytes|t2100|x6") }, Array.Empty<string>());

        Assert.NotEqual(healthy, degraded);
    }

    [Fact]
    public void SymbolStateKey_ChangesWhenAnAppsContentChangesUnderTheSamePath()
    {
        // The #2710 field scenario in one line: same path, different bytes. Keying on the
        // path alone would have served the snapshot captured against the old package.
        var before = RecordPatches.ComputeBcAppSymbolStateKey(
            new[] { ("/pkg/Base Application_28.1.49838.53910.app", "hash-one") }, Array.Empty<string>());
        var after = RecordPatches.ComputeBcAppSymbolStateKey(
            new[] { ("/pkg/Base Application_28.1.49838.53910.app", "hash-two") }, Array.Empty<string>());
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void SymbolStateKey_ChangesWhenAnAppIsDropped()
    {
        var two = RecordPatches.ComputeBcAppSymbolStateKey(
            new[] { ("/pkg/A.app", "aaaa"), ("/pkg/B.app", "bbbb") }, Array.Empty<string>());
        var one = RecordPatches.ComputeBcAppSymbolStateKey(
            new[] { ("/pkg/A.app", "aaaa") }, Array.Empty<string>());
        Assert.NotEqual(two, one);
    }

    [Fact]
    public void SymbolStateKey_ChangesWhenAQuerySymbolJsonIsRegistered()
    {
        var without = RecordPatches.ComputeBcAppSymbolStateKey(
            new[] { ("/pkg/A.app", "aaaa") }, Array.Empty<string>());
        var with = RecordPatches.ComputeBcAppSymbolStateKey(
            new[] { ("/pkg/A.app", "aaaa") }, new[] { "/out/SymbolReference.json" });
        Assert.NotEqual(without, with);
    }

    [Fact]
    public void SymbolStateKey_EmptyRegistry_IsTheNamedNoneSentinel()
    {
        // A named sentinel rather than the SHA-256 of nothing, so a key read by a human says
        // "no symbol sources were registered" instead of an opaque constant that looks like a
        // hash of something.
        Assert.Equal("|bcapps:none",
            RecordPatches.ComputeBcAppSymbolStateKey(
                Array.Empty<(string, string)>(), Array.Empty<string>()));
    }

    [Fact]
    public void SymbolStateKey_DoesNotConfuseAnAppPathWithAQueryJsonPath()
    {
        // Framing check: without the per-entry "app"/"qjson" tags a path moving from one list
        // to the other would hash identically, and the two are not the same registration.
        var asApp = RecordPatches.ComputeBcAppSymbolStateKey(
            new[] { ("/x/thing", "") }, Array.Empty<string>());
        var asJson = RecordPatches.ComputeBcAppSymbolStateKey(
            Array.Empty<(string, string)>(), new[] { "/x/thing" });
        Assert.NotEqual(asApp, asJson);
    }
}
