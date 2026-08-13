// BcCompilerLoaderSelfExclusionTests — reference-loader self-exclusion contract.
//
// Root cause being tested
// ------------------------
// This is a SECOND, independent root cause behind the same reported symptom as
// BcCompilerEmitRetryTests (the Pageworks Copilot-flow ObjectId==0 / MissingMethod
// failures): even after the atomic-Emit retry fix, "System Application Test
// Library"'s own Tier-3 source compile (DependencyLoader recompiling a dependency
// from its decompiled AL source, scoping _currentAppId to that dep's own identity)
// still failed to emit — this time with AL0275 "'X' is an ambiguous reference
// between [ext] and [ext]", naming the SAME extension twice, on the very first
// (pre-retry) compile attempt.
//
// GetSharedReferences already excludes the currently-compiling dep's own AppId
// from the requested SymbolReferenceSpecification list via _currentAppId — that
// part works (confirmed via diagnostics: the dep's own AppId is correctly ABSENT
// from the printed spec list for the exact compile that still produced AL0275).
// The remaining gap: the reference LOADER (NavCA.ISymbolReferenceLoader) is a
// SEPARATE object built from a raw directory scan of every .app in the package
// cache. BC's binder resolves some references (e.g. a Permission Set's
// `tabledata "X"` grant) by asking the loader for ANY module declaring "X",
// regardless of whether a spec ever requested it — so even with the spec
// correctly excluded, the dep's own .app remained visible via the loader and
// collided with the dep's own primary-source declaration of the same object,
// producing the self-ambiguous-reference failure and, ultimately, EMIT-ZERO for
// the whole module (the same NoOpCodeunit-fallback / "object ID 0" symptom).
//
// Fix: BcCompiler.DeduplicateAppPackageDirs (which builds the loader's scan-dir
// set) now also accepts an excludeAppId and physically drops every .app matching
// it from the scanned/staged set — not merely from the requested specs.
// ComputeLoaderSignature was extended to hash-in the same excludeAppId so the
// cached/shared loader is correctly invalidated and rebuilt whenever the
// currently-excluded AppId changes between compiles (main bundle vs. dep A's own
// Tier-3 compile vs. dep B's own Tier-3 compile), instead of incorrectly reusing
// a loader that was filtered for a different app (or not filtered at all).
//
// Test strategy
// -------------
// DeduplicateAppPackageDirs and AppLoader.ReadManifest are pure filesystem/zip
// logic with no BC-runtime dependency at all (no Ncl.dll, no BcRuntime bootstrap
// needed) — so this test invokes DeduplicateAppPackageDirs directly via
// reflection (it is private) against synthetic, minimal NAVX .app fixtures. This
// keeps the test fast and avoids the Cecil-rewrite "run twice" requirement that
// BcCompilerEmitRetryTests needs (that one exercises the full Emit() pipeline).
//
// RED before the fix (single-arg DeduplicateAppPackageDirs, no exclusion
// mechanism): the self-app's .app remains in the loader's scan set alongside the
// unrelated dep's .app. GREEN after: the self-app's .app is excluded from the
// scan set entirely while the unrelated dep's .app is kept untouched.

using System.IO.Compression;
using System.Reflection;
using System.Text;
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

public sealed class BcCompilerLoaderSelfExclusionTests : IDisposable
{
    private readonly string _root;

    public BcCompilerLoaderSelfExclusionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-loader-selfexclusion-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// One package dir holds two .apps: the dep currently being compiled as PRIMARY
    /// source ("SelfApp") and an unrelated dependency ("OtherApp"). Excluding
    /// SelfApp's AppId must drop ONLY SelfApp's .app from the resulting scan dirs —
    /// OtherApp's .app must remain discoverable (a real reference the compile still
    /// needs). Concrete assertion: re-scanning the returned dirs for .app manifests
    /// yields exactly OtherApp's AppId, never SelfApp's.
    /// </summary>
    [Fact]
    public void ExcludeAppId_DropsOnlyThatApp_KeepsUnrelatedDepVisible()
    {
        var selfAppId = "aaaaaaaa-1111-0000-0000-000000000001";
        var otherAppId = "bbbbbbbb-2222-0000-0000-000000000002";
        var dir = MakeDir("pkg");
        WriteApp(dir, "SelfApp.app", selfAppId, "System Application Test Library", "Microsoft", "28.2.0.0");
        WriteApp(dir, "OtherApp.app", otherAppId, "Some Other Dependency", "Microsoft", "28.2.0.0");

        var resultDirs = InvokeDeduplicateAppPackageDirs(
            new List<string> { dir }, Guid.Parse(selfAppId));

        var remainingAppIds = ScanAppIds(resultDirs);

        Assert.DoesNotContain(Guid.Parse(selfAppId), remainingAppIds);
        Assert.Contains(Guid.Parse(otherAppId), remainingAppIds);
    }

    /// <summary>
    /// Sanity check on the untouched path: with excludeAppId=null and no
    /// cross-dir duplicate, the original list must be returned completely
    /// unchanged (same dirs, zero staging) — the fast path the corpus/main-bundle
    /// compile relies on for zero added cost.
    /// </summary>
    [Fact]
    public void NoExclusion_NoDuplicates_ReturnsOriginalListUnchanged()
    {
        var appId = "cccccccc-3333-0000-0000-000000000003";
        var dir = MakeDir("pkg");
        WriteApp(dir, "Solo.app", appId, "Solo Dependency", "Microsoft", "28.2.0.0");
        var input = new List<string> { dir };

        var resultDirs = InvokeDeduplicateAppPackageDirs(input, excludeAppId: null);

        Assert.Same(input, resultDirs);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static List<string> InvokeDeduplicateAppPackageDirs(List<string> packageDirs, Guid? excludeAppId)
    {
        // Overloaded since #1831 (a 3-arg variant also yields the scan inventory), so select
        // by arity — a plain GetMethod throws AmbiguousMatchException.
        var method = typeof(BcCompiler)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(m => m.Name == "DeduplicateAppPackageDirs" && m.GetParameters().Length == 2)
            ?? throw new InvalidOperationException(
                "BcCompiler.DeduplicateAppPackageDirs(dirs, excludeAppId) not found by reflection — signature may have changed.");
        return (List<string>)method.Invoke(null, new object?[] { packageDirs, excludeAppId })!;
    }

    private static HashSet<Guid> ScanAppIds(IEnumerable<string> dirs)
    {
        var ids = new HashSet<Guid>();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var app in Directory.EnumerateFiles(dir, "*.app", SearchOption.AllDirectories))
            {
                var manifest = AppLoader.ReadManifest(app);
                if (manifest != null) ids.Add(manifest.AppId);
            }
        }
        return ids;
    }

    private string MakeDir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Writes a minimal NAVX .app file (header + ZIP with NavxManifest.xml).
    /// Same fixture format as DependencyResolverTests.WriteApp.</summary>
    private static void WriteApp(string dir, string fileName,
        string appId, string name, string publisher, string version)
    {
        File.WriteAllBytes(Path.Combine(dir, fileName), MakeMinimalApp(appId, name, publisher, version));
    }

    private static byte[] MakeMinimalApp(string appId, string name, string publisher, string version)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"/>
            </Package>
            """;

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("NavxManifest.xml");
            using (var es = entry.Open())
                es.Write(Encoding.UTF8.GetBytes(xml));

            // DeduplicateAppPackageDirs (the method under test) now drops any .app with no
            // SymbolReference.json from the loader's scan set — see BcCompiler.cs, the
            // symbol-less-package filter added alongside BcFloorGate. A real BC package
            // always carries one; this fixture must too, or it is dropped before the
            // AppId-exclusion logic being tested here ever runs. Content is irrelevant —
            // only its presence is checked.
            var symEntry = zip.CreateEntry("SymbolReference.json");
            using (var symStream = symEntry.Open())
                symStream.Write(Encoding.UTF8.GetBytes("{}"));
        }
        var zipBytes = ms.ToArray();

        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        return result;
    }
}
