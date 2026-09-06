// DependencyR2rChunkModuleIdentityTests — #3054.
//
// Microsoft ships large apps as several ReadyToRun DLL chunks (Base Application is five).
// DependencyLoader's Tier-2 loop loads every chunk, but until #3054 it handed the caller
// back only the FIRST one, and LoadAll then registered only that one. Every AL object the
// AL compiler happened to place in chunks 2..n therefore had no app identity in any of the
// per-Assembly registries the runner keys module ownership on.
//
// The two things that broke, both measured against real BC artifacts before the fix:
//
//   * NavApp.GetCallerModuleInfo (BcRuntime.TryGetImmediateCallerModule) walks the managed
//     stack and SKIPS any frame whose assembly is not in BcRuntime's module map, so a call
//     out of an unregistered chunk was attributed to whichever registered frame sat below
//     it. On BC 27.3, Base Application's codeunit 3999 "Reten. Pol. Install - BaseApp"
//     lives in a non-primary chunk; its AddAllowedTable(405) call reported System
//     Application as the caller, BC's own ModuleOwnsTable check correctly refused ("the
//     table is not owned by module {63CA2FA4-…}"), and codeunit 2 "Company-Initialize"
//     then died on "Table 405 Change Log Entry is not in the list of allowed tables".
//   * InstallTriggerRunner fires the Install codeunits of exactly the assemblies LoadAll
//     returns (TestExecutor → SetDependencyAssemblies), so an Install codeunit in a
//     non-primary chunk never ran. Same measurement: BC 27.3 fired no install trigger for
//     codeunit 3999 at all, while BC 28.1 — where that codeunit happens to land in the
//     primary chunk — did.
//
// WHY THIS TEST DOES NOT USE A BC ARTIFACT
//   The claim is entirely about the runner's own loader bookkeeping — "an app loaded as N
//   assemblies is one module across all N" — and nothing about Business Central, so it does
//   not belong upstream in the al-language corpus. Two Roslyn-compiled stub assemblies
//   zipped into a synthetic `publishedartifacts/*.dll` package drive the REAL Tier-2 path
//   (AppLoader.IsR2R → ExtractAllDllPaths → LoadFromAssemblyPath → LoadAll's registration)
//   in milliseconds, with no Base Application floor — see
//   .claude/rules/no-base-app-in-csharp-tests.md.
//
// The BC-behaviour half of #3054 — that table 405 IS in the retention-policy allowed list
// after a company is initialised — is a statement about BC and is asserted upstream in the
// al-language corpus, where a real service tier adjudicates it.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using AlRunner;
using AlRunner.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// In <see cref="CacheRootsSerialCollection"/> because AppLoader.ExtractAllDllPaths resolves
/// the r2r-chunks cache directory through the process-wide CacheRoots override this class
/// sets for the duration.
/// </summary>
[Collection(CacheRootsSerialCollection.Name)]
public sealed class DependencyR2rChunkModuleIdentityTests : IDisposable
{
    private const string Publisher = "R2rChunkFixture";
    private const string AppName = "MultiChunkDep";
    private static readonly Version AppVersion = new(1, 2, 3, 4);

    private readonly string _root = TestScratch.Dir("al-runner-r2r-chunk-module-identity");
    private readonly string _cacheRoot = TestScratch.FlatDir("al-runner-r2r-chunk-module-identity-cache-");

    public void Dispose()
    {
        CacheRoots.ResetForTests();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_cacheRoot, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] CompileStub(string assemblyName)
    {
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(
                Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
        };
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText("public static class Marker { public static int N => 1; }") },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success,
            string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return ms.ToArray();
    }

    /// <summary>
    /// A synthetic R2R package carrying <paramref name="chunkNames"/> as
    /// <c>publishedartifacts/*.dll</c> entries — the shape AppLoader.IsR2R recognises and
    /// DependencyLoader's Tier 2 loads. Returns the .app path.
    /// </summary>
    private string WriteMultiChunkApp(IReadOnlyList<string> chunkNames)
    {
        Directory.CreateDirectory(_root);
        var appPath = Path.Combine(_root, $"{Publisher}_{AppName}_{AppVersion}_{Guid.NewGuid():N}.app");
        using var zip = ZipFile.Open(appPath, ZipArchiveMode.Create);
        for (var i = 0; i < chunkNames.Count; i++)
        {
            var entry = zip.CreateEntry($"publishedartifacts/chunk{i:D3}.dll");
            using var s = entry.Open();
            var bytes = CompileStub(chunkNames[i]);
            s.Write(bytes, 0, bytes.Length);
        }
        return appPath;
    }

    [Fact]
    public void EveryR2rChunkOfOneApp_IsReturnedAndCarriesThatAppsModuleIdentity()
    {
        CacheRoots.SetOverride(_cacheRoot);

        var appId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");
        var chunkNames = new[] { $"r2rchunk-a-{suffix}", $"r2rchunk-b-{suffix}", $"r2rchunk-c-{suffix}" };
        var appPath = WriteMultiChunkApp(chunkNames);
        var manifest = new AppManifest(Publisher, AppName, AppVersion, appId, Array.Empty<DependencyRef>());

        var loaded = new DependencyLoader(null!, null!)
            .LoadAll(new[] { (manifest, appPath) }, _root);

        // 1. LoadAll hands back EVERY chunk. This list is exactly what
        //    InstallTriggerRunner.SetDependencyAssemblies scans, so a chunk missing here is a
        //    chunk whose Install codeunits can never fire. Pre-fix this was 1, not 3.
        var loadedNames = loaded.Select(a => a.GetName().Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(chunkNames.OrderBy(n => n, StringComparer.Ordinal).ToList(), loadedNames);

        // 2. Every chunk carries THIS app's identity in BcRuntime's module map — the map
        //    NavApp.GetCallerModuleInfo's stack walk consults, and the one
        //    RecordPatches.BuildObjectOwnerIndex reads through RegisteredModuleAssemblies().
        var registered = BcRuntime.RegisteredModuleAssemblies();
        foreach (var asm in loaded)
        {
            Assert.Contains(registered, e => ReferenceEquals(e.Assembly, asm) && e.AppId == appId);
            var info = BcRuntime.GetModuleAppInfoFor(asm);
            Assert.Equal(appId, info.AppId);
            Assert.Equal(AppName, info.Name);
            Assert.Equal(Publisher, info.Publisher);
            Assert.Equal(AppVersion.ToString(), info.Version);
        }

        // 3. The app is still ONE module, not three: RegisteredModules() deduplicates by app
        //    id, which is what seeds a single Published Application row per app (#2963). A fix
        //    that registered each chunk as its own module would break that instead.
        Assert.Single(BcRuntime.RegisteredModules().Where(m => m.AppId == appId));

        // 4. Not vacuous: the map does not answer "this app" for an arbitrary assembly. Without
        //    this, an implementation that returned the fixture's identity for everything would
        //    satisfy every assertion above.
        Assert.DoesNotContain(registered, e => ReferenceEquals(e.Assembly, typeof(string).Assembly));
    }

    [Fact]
    public void ASecondResolutionOfTheSameApp_StillYieldsEveryChunk()
    {
        // The DependencyLoader cache is keyed by app id and short-circuits LoadOne entirely on
        // the second bundle in a run (and on every later --watch/--server cycle). Handing back
        // only the cached primary there would put every app group after the first back into
        // the pre-fix state, so the cache entry has to carry the whole chunk set too.
        CacheRoots.SetOverride(_cacheRoot);

        var appId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");
        var chunkNames = new[] { $"r2rchunk-d-{suffix}", $"r2rchunk-e-{suffix}" };
        var appPath = WriteMultiChunkApp(chunkNames);
        var manifest = new AppManifest(Publisher, AppName, AppVersion, appId, Array.Empty<DependencyRef>());
        var loader = new DependencyLoader(null!, null!);

        var first = loader.LoadAll(new[] { (manifest, appPath) }, _root);
        var second = loader.LoadAll(new[] { (manifest, appPath) }, _root);

        Assert.Equal(2, first.Count);
        Assert.Equal(
            first.Select(a => a.GetName().Name).OrderBy(n => n, StringComparer.Ordinal).ToList(),
            second.Select(a => a.GetName().Name).OrderBy(n => n, StringComparer.Ordinal).ToList());
        // Same Assembly instances, not a second load of the same bytes under new identities.
        foreach (var asm in first) Assert.Contains(second, a => ReferenceEquals(a, asm));
    }
}
