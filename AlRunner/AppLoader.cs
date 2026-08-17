// AppLoader — universal `.app` package reader.
//
// BC `.app` files are a NAVX header (4-byte magic "NAVX" + 4-byte LE uint32 ZIP
// offset) followed by a ZIP archive. Two flavours we care about:
//
//   1. R2R packages (Microsoft-shipped: System Application, Base Application).
//      Outer ZIP contains `readytorunappmanifest.json`, a nested AL `.app`,
//      and `publishedartifacts/.../<HASH>.dll` — the pre-compiled IL DLL
//      we want to load directly.
//
//   2. alc `/generatecode+` output. ZIP contains `bin/COD<id>.cs` (and `.xml`)
//      — C# source per AL object, post BC's Compilation.Emit. This is what
//      v2 feeds into Roslyn for the AL-source path.
//
// One method per shape so the bundle pipeline can ask the right question.
//
// Reference for NAVX wrapper handling: AlRunner/Program.cs:4540
// (AppPackageReader.ExtractAlSources in v1).
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AlRunner.Infrastructure;

namespace AlRunner;

public sealed record DependencyRef(Guid AppId, string Name, string Publisher, Version Version, bool Optional = false);

public sealed record AppManifest(
    string Publisher,
    string Name,
    Version Version,
    Guid AppId,
    IReadOnlyList<DependencyRef> Dependencies,
    // Implicit first-party dep versions from the NAVX manifest's `Application` /
    // `Platform` attributes (the real `al` compiler injects Microsoft/Application
    // and Microsoft/System from these). Null when the manifest omits them.
    // See AppLoader.ImplicitRoots for synthesizing the matching DependencyRefs.
    Version? Application = null,
    Version? Platform = null);

public static class AppLoader
{
    // Process-wide memo for ReadManifest, keyed by (full path, length, mtime) — cheap to
    // compute (a single FileInfo stat, never touches file content) so a repeat ReadManifest
    // call for the SAME file within this process is a dictionary lookup, not a re-parse.
    // Measured: this method is called ~113 times across ProvisioningCheck.CheckPlatformApps,
    // DependencyResolver.EnsureIndexed/Resolve and BcCompiler for the SAME package caches,
    // often for the SAME files more than once per process — see AppLoaderManifestCacheTests.
    private static readonly ConcurrentDictionary<string, AppManifest?> _manifestMemo = new(StringComparer.Ordinal);

    /// <summary>Test-only: clears the in-process memo so a test can simulate a fresh process.</summary>
    internal static void ResetManifestMemoForTests() => _manifestMemo.Clear();

    // Test-only: counts genuine ReadManifestUncached invocations (i.e. BOTH the memo AND
    // the disk index missed) per full .app path — mirrors BcAppSymbolCache
    // .ParseInvocationCountByPath so a test can assert a given ReadManifest call was
    // served from a cache rather than a re-parse that happens to produce equal content.
    private static readonly ConcurrentDictionary<string, int> _manifestParseInvocationCountByPath =
        new(StringComparer.OrdinalIgnoreCase);

    internal static int ManifestParseInvocationCountForTests(string appPath)
        => _manifestParseInvocationCountByPath.TryGetValue(Path.GetFullPath(appPath), out var c) ? c : 0;

    /// <summary>Test-only: the exact on-disk index path <see cref="ReadManifest"/> would use
    /// for <paramref name="appPath"/> AT ITS CURRENT length/mtime — lets a test corrupt/inspect
    /// that specific entry directly.</summary>
    internal static string ManifestIndexPathForTests(string appPath)
    {
        var fullPath = Path.GetFullPath(appPath);
        var fi = new FileInfo(fullPath);
        var memoKey = $"{fullPath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        return ManifestIndexPath(memoKey);
    }

    /// <summary>
    /// Reads NavxManifest.xml from an `.app` package and returns the App element's
    /// Publisher / Name / Version / Id. Returns null if the file is malformed or
    /// missing the manifest.
    ///
    /// Backed by a two-level cache (issue #perf-B): an in-process memo, and — on a memo
    /// miss — a small on-disk index under <c>CacheRoots.Resolve("app-manifests")</c> so a
    /// SEPARATE process (a later `al-runner` invocation, or one of the 4 that run in
    /// parallel in CI) can skip re-parsing too. Both are keyed by (full path, length,
    /// last-write-time-UTC) — a plain stat, not a content hash: the whole point is to avoid
    /// touching a 98MB Base Application .app's bytes just to answer "have I seen this
    /// exact file before". A file that changes on disk (new size or mtime — e.g. CI
    /// re-downloading a package) gets a different key and is simply reparsed; this is not
    /// a correctness hazard, only a cache miss (see AppLoaderManifestCacheTests
    /// .ReadManifest_TouchedMtime_IsReparsedNotServedStale).
    /// </summary>
    public static AppManifest? ReadManifest(string appPath)
    {
        string fullPath;
        long length;
        long mtimeTicks;
        try
        {
            fullPath = Path.GetFullPath(appPath);
            var fi = new FileInfo(fullPath);
            if (!fi.Exists) return null;
            length = fi.Length;
            mtimeTicks = fi.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            // Can't even stat the path (invalid chars, permission error, ...) — fall back
            // to a plain uncached read so behaviour matches the pre-cache contract exactly
            // (any failure here returns null, never throws).
            return ReadManifestUncached(appPath);
        }

        var memoKey = $"{fullPath}|{length}|{mtimeTicks}";
        if (_manifestMemo.TryGetValue(memoKey, out var memoized))
            return memoized;

        var indexed = TryReadManifestIndex(memoKey);
        if (indexed != null)
        {
            PerfTrace.Log($"app-manifests HIT {Path.GetFileName(fullPath)}");
            _manifestMemo[memoKey] = indexed;
            return indexed;
        }

        _manifestParseInvocationCountByPath.AddOrUpdate(fullPath, 1, static (_, c) => c + 1);
        var parsed = ReadManifestUncached(fullPath);
        PerfTrace.Log($"app-manifests MISS {Path.GetFileName(fullPath)}");
        _manifestMemo[memoKey] = parsed;
        if (parsed != null)
            TryWriteManifestIndex(memoKey, parsed);
        return parsed;
    }

    /// <summary>The actual parse, with no caching — streams the .app straight off disk via
    /// <see cref="OpenAppZip"/> rather than reading the whole file into memory first (the
    /// dominant cost this cache exists to avoid on a cold/uncached call).</summary>
    private static AppManifest? ReadManifestUncached(string appPath)
    {
        try
        {
            using var zip = OpenAppZip(appPath);
            return ReadManifestFromZip(zip);
        }
        catch { return null; }
    }

    // ── on-disk manifest index (issue #perf-B) ─────────────────────────────────

    private static string ManifestIndexPath(string memoKey)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(memoKey))).ToLowerInvariant();
        return Path.Combine(CacheRoots.Resolve("app-manifests"), hash + ".json");
    }

    private static AppManifest? TryReadManifestIndex(string memoKey)
    {
        var path = ManifestIndexPath(memoKey);
        if (!File.Exists(path)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<ManifestCachePayload>(File.ReadAllText(path));
            var manifest = payload == null ? null : FromPayload(payload);
            if (manifest == null)
                PerfTrace.Log($"app-manifests index entry unusable {Path.GetFileName(path)}: payload malformed — reparsing");
            return manifest;
        }
        catch (Exception ex)
        {
            // Corrupt/unreadable index entry: never throw, never propagate a wrong answer —
            // just log (verbose) and let the caller fall through to a fresh parse.
            PerfTrace.Log($"app-manifests index read failed {Path.GetFileName(path)}: {ex.Message} — reparsing");
            return null;
        }
    }

    private static void TryWriteManifestIndex(string memoKey, AppManifest manifest)
    {
        try
        {
            var path = ManifestIndexPath(memoKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = ToPayload(manifest);
            // Atomic (temp file + rename) — 4 CI processes can race a write to the SAME
            // key for the SAME shared package cache; a torn write must never be visible.
            AlCacheWriter.AtomicPublish(path, tmp => File.WriteAllText(tmp, JsonSerializer.Serialize(payload)));
        }
        catch (Exception ex)
        {
            PerfTrace.Log($"app-manifests index write failed: {ex.Message}");
        }
    }

    // JSON-serializable mirror of AppManifest/DependencyRef — Version/Guid round-trip as
    // strings (System.Text.Json has no default converter for System.Version, and keeping
    // Guid as text sidesteps ever depending on that assumption changing).
    private sealed record ManifestCachePayload(
        string Publisher, string Name, string Version, string AppId,
        List<DependencyCachePayload> Dependencies,
        string? Application, string? Platform);

    private sealed record DependencyCachePayload(
        string AppId, string Name, string Publisher, string Version, bool Optional);

    private static ManifestCachePayload ToPayload(AppManifest m) => new(
        m.Publisher, m.Name, m.Version.ToString(), m.AppId.ToString("D"),
        m.Dependencies.Select(d => new DependencyCachePayload(
            d.AppId.ToString("D"), d.Name, d.Publisher, d.Version.ToString(), d.Optional)).ToList(),
        m.Application?.ToString(), m.Platform?.ToString());

    private static AppManifest? FromPayload(ManifestCachePayload p)
    {
        if (!Guid.TryParse(p.AppId, out var appId)) return null;
        if (!Version.TryParse(p.Version, out var version)) return null;
        var deps = new List<DependencyRef>(p.Dependencies.Count);
        foreach (var d in p.Dependencies)
        {
            Guid.TryParse(d.AppId, out var depId); // Guid.Empty is itself a legitimate stored value
            if (!Version.TryParse(d.Version, out var depVer)) depVer = new Version(0, 0, 0, 0);
            deps.Add(new DependencyRef(depId, d.Name, d.Publisher, depVer, d.Optional));
        }
        Version? appVer = p.Application != null && Version.TryParse(p.Application, out var av) ? av : null;
        Version? platVer = p.Platform != null && Version.TryParse(p.Platform, out var pv) ? pv : null;
        return new AppManifest(p.Publisher, p.Name, version, appId, deps, appVer, platVer);
    }

    /// <summary>
    /// True if the .app is a real, compiler-valid BC package — i.e. its NAVX zip
    /// contains a <c>SymbolReference.json</c> part. Such a package can serve
    /// compile-time symbols directly through BC's native .app scanner (no synthetic
    /// symbols.json needed), and merges tableextensions/etc. correctly. A synthetic
    /// source-only .app emitted by InProcessAppPackager returns false here.
    /// Returns false on any read/format error.
    /// </summary>
    public static bool HasSymbolReference(string appPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(appPath);
            using var zip = OpenZipFromNavx(bytes);
            if (zip.Entries.Any(e => e.FullName.Equals("SymbolReference.json", StringComparison.OrdinalIgnoreCase)))
                return true;
            // R2R nested case: the inner .app carries the SymbolReference.json.
            var nested = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
            if (nested == null) return false;
            using var ns = nested.Open();
            using var nms = new MemoryStream();
            ns.CopyTo(nms);
            using var innerZip = OpenZipFromNavx(nms.ToArray());
            return innerZip.Entries.Any(e => e.FullName.Equals("SymbolReference.json", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>
    /// Byte[]-backed entry point — used to recurse into a nested R2R .app once its (small,
    /// unavoidably-buffered — see <see cref="ReadManifestFromZip"/>) bytes are already in
    /// hand. Not used for the outer .app anymore; that path goes through
    /// <see cref="OpenAppZip"/> so it never reads the whole (potentially 100+MB) file.
    /// </summary>
    private static AppManifest? ReadManifestFromBytes(byte[] bytes)
    {
        try
        {
            using var zip = OpenZipFromNavx(bytes);
            return ReadManifestFromZip(zip);
        }
        catch { return null; }
    }

    /// <summary>
    /// Core manifest lookup shared by the streamed (<see cref="OpenAppZip"/>, outer .app)
    /// and byte[]-backed (<see cref="ReadManifestFromBytes"/>, nested .app) entry points —
    /// only the ZipArchive's backing stream differs between callers.
    /// </summary>
    private static AppManifest? ReadManifestFromZip(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, "NavxManifest.xml", StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            using var s = entry.Open();
            return ParseManifestXml(s);
        }

        // R2R outer .app — recurse into the nested .app. Unlike the outer zip (read
        // straight off disk, central-directory-only, via OpenAppZip), a zip ENTRY's
        // Open() stream is forward-only/compressed, and ZipArchive needs its central
        // directory readable from the end of a seekable stream — so the nested .app's
        // bytes are still buffered fully here, same as before this cache existed. That
        // remains a large net win: the nested .app carries no publishedartifacts/*.dll
        // (those live only in the OUTER zip, which this path never touches), so it is
        // far smaller than the whole package — e.g. nowhere near Base Application's ~98MB.
        var nested = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
        if (nested == null) return null;
        using var nestedStream = nested.Open();
        using var nms = new MemoryStream();
        nestedStream.CopyTo(nms);
        return ReadManifestFromBytes(nms.ToArray());
    }

    /// <summary>Parses a NavxManifest.xml stream into an <see cref="AppManifest"/>. Pure XML
    /// parse — no I/O beyond what the caller's already-open <paramref name="xmlStream"/> does.</summary>
    private static AppManifest? ParseManifestXml(Stream xmlStream)
    {
        var doc = XDocument.Load(xmlStream);
        XNamespace ns = "http://schemas.microsoft.com/navx/2015/manifest";
        var app = doc.Root?.Element(ns + "App");
        if (app == null) return null;
        var idStr = app.Attribute("Id")?.Value;
        var name = app.Attribute("Name")?.Value ?? "";
        var publisher = app.Attribute("Publisher")?.Value ?? "";
        var verStr = app.Attribute("Version")?.Value ?? "1.0.0.0";
        if (idStr == null || !Guid.TryParse(idStr, out var id)) return null;
        if (!Version.TryParse(verStr, out var ver)) return null;

        // <Dependencies><Dependency Id="..." Name="..." Publisher="..."
        //   MinVersion="..." CompatibilityId="..." /></Dependencies>
        var deps = new List<DependencyRef>();
        var depsRoot = doc.Root?.Element(ns + "Dependencies");
        if (depsRoot != null)
        {
            foreach (var dep in depsRoot.Elements(ns + "Dependency"))
            {
                var depIdStr = dep.Attribute("Id")?.Value;
                var depName = dep.Attribute("Name")?.Value ?? "";
                var depPub = dep.Attribute("Publisher")?.Value ?? "";
                var depVerStr = dep.Attribute("MinVersion")?.Value
                    ?? dep.Attribute("Version")?.Value
                    ?? "0.0.0.0";
                Guid depId = Guid.Empty;
                if (!string.IsNullOrEmpty(depIdStr))
                    Guid.TryParse(depIdStr, out depId);
                if (!Version.TryParse(depVerStr, out var depVer))
                    depVer = new Version(0, 0, 0, 0);
                deps.Add(new DependencyRef(depId, depName, depPub, depVer));
            }
        }
        // Implicit first-party deps: the `Application` / `Platform` attributes
        // on <App>. Modern apps do NOT list Microsoft apps under <Dependencies>;
        // the real `al` compiler injects them from these attributes. Capture the
        // versions so callers resolving a ROOT app can synthesize the matching
        // Microsoft/Application + Microsoft/System roots (see ImplicitRoots).
        Version.TryParse(app.Attribute("Application")?.Value, out var appVer);
        Version.TryParse(app.Attribute("Platform")?.Value, out var platVer);
        return new AppManifest(publisher, name, ver, id, deps, appVer, platVer);
    }

    /// <summary>
    /// Synthetic implicit first-party dependency roots for a ROOT app being
    /// compiled, derived from its manifest's `Application` / `Platform` versions.
    /// `Application` → Microsoft/Application (the umbrella app that transitively
    /// pulls Base Application + System Application + Business Foundation);
    /// `Platform` → Microsoft/System (platform symbols). Mirrors the app.json
    /// synthesis in Program.ReadDependencies so `.app` inputs resolve BaseApp the
    /// same way app.json inputs do. Roots are Optional (warn-not-throw if absent)
    /// and resolved by (Name, Publisher) — version is informational.
    ///
    /// Apply ONLY to the root app being compiled, never transitively: the
    /// dependency resolver throws on cycles, and every Microsoft app's manifest
    /// carries these same attributes (Application → Base Application → Application …).
    /// </summary>
    public static IEnumerable<DependencyRef> ImplicitRoots(AppManifest manifest)
    {
        if (manifest.Application != null)
            yield return new DependencyRef(Guid.Empty, "Application", "Microsoft", manifest.Application, Optional: true);
        if (manifest.Platform != null)
            yield return new DependencyRef(Guid.Empty, "System", "Microsoft", manifest.Platform, Optional: true);
    }

    /// <summary>
    /// True if the package contains an R2R `publishedartifacts/*.dll`.
    /// Used by the loader to pick between Tier-2 (R2R extract) and Tier-3
    /// (source-only on-the-fly compile).
    /// </summary>
    public static bool IsR2R(string appPath)
    {
        try
        {
            using var zip = OpenAppZip(appPath);
            return zip.Entries.Any(e =>
                e.FullName.StartsWith("publishedartifacts/", StringComparison.OrdinalIgnoreCase)
                && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }


    /// <summary>
    /// True if the package ships AL source under <c>src/*.al</c> — i.e. the loader's
    /// Tier-3 on-the-fly source compile can produce an implementation from it.
    ///
    /// This is the other half of "can this package supply runnable code", and it is NOT
    /// implied by <see cref="IsR2R"/>. Microsoft ships its test toolkit (Library Assert,
    /// Library Variable Storage, …) with NO <c>publishedartifacts/*.dll</c> but WITH AL
    /// source: verified against the real 28.1.49838.53479 test-apps artifact, where
    /// `Microsoft_Library Assert.app` is 22 KB with IsR2R=false and one `src/*.al`.
    /// Treating !IsR2R alone as "carries no implementation" therefore misclassifies the
    /// entire healthy test toolkit — see #1689 and DependencyResolver.SelectBestVersion.
    ///
    /// Entry-name scan only: nothing is decompressed, so this stays cheap on packages
    /// with thousands of sources.
    /// </summary>
    public static bool HasAlSource(string appPath)
    {
        static bool AnyAl(ZipArchive zip) => zip.Entries.Any(e =>
            e.FullName.StartsWith("src/", StringComparison.OrdinalIgnoreCase)
            && e.FullName.EndsWith(".al", StringComparison.OrdinalIgnoreCase));
        try
        {
            var bytes = File.ReadAllBytes(appPath);
            using var zip = OpenZipFromNavx(bytes);
            if (AnyAl(zip)) return true;
            // R2R nested case: the inner .app carries the source, mirroring HasSymbolReference.
            var nested = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
            if (nested == null) return false;
            using var ns = nested.Open();
            using var nms = new MemoryStream();
            ns.CopyTo(nms);
            using var innerZip = OpenZipFromNavx(nms.ToArray());
            return AnyAl(innerZip);
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns the IL DLL bytes from a Microsoft R2R `.app` package, or null
    /// if no `publishedartifacts/*.dll` is present (i.e. the package is not R2R).
    /// Returns only the first DLL — kept for backwards-compat callers that
    /// happen to want a single-DLL result. Use <see cref="ExtractAllDlls"/>
    /// for multi-DLL R2R packages (e.g. Base Application is 5 DLL chunks).
    /// </summary>
    public static byte[]? ExtractDll(string appPath)
    {
        var all = ExtractAllDlls(appPath);
        return all.Count == 0 ? null : all[0];
    }

    /// <summary>
    /// Returns ALL `publishedartifacts/*.dll` byte blobs from a Microsoft R2R
    /// `.app` package. Microsoft ships large apps (notably Base Application)
    /// as multiple DLL chunks under `publishedartifacts/...`; loading only
    /// the first leaves the majority of types unresolved at runtime.
    /// Returns an empty list if the package is not R2R.
    /// </summary>
    public static IReadOnlyList<byte[]> ExtractAllDlls(string appPath)
    {
        using var zip = OpenAppZip(appPath);
        var dllEntries = zip.Entries
            .Where(e => e.FullName.StartsWith("publishedartifacts/", StringComparison.OrdinalIgnoreCase)
                     && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();
        var result = new List<byte[]>(dllEntries.Count);
        foreach (var entry in dllEntries)
        {
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            result.Add(ms.ToArray());
        }
        return result;
    }

    // Test-only: counts genuine ExtractAllDlls invocations (i.e. an r2r-chunks on-disk
    // cache MISS that required re-inflating the zip), per full .app path — mirrors
    // BcAppSymbolCache.ParseInvocationCountByPath so a test can assert the SECOND
    // ExtractAllDllPaths call for the same file was a genuine disk HIT, not a re-extract
    // that happens to produce the same bytes.
    private static readonly ConcurrentDictionary<string, int> _r2rExtractInvocationCountByPath =
        new(StringComparer.OrdinalIgnoreCase);

    internal static int R2rExtractInvocationCountForTests(string appPath)
        => _r2rExtractInvocationCountByPath.TryGetValue(Path.GetFullPath(appPath), out var c) ? c : 0;

    /// <summary>
    /// Like <see cref="ExtractAllDlls"/>, but extracts each R2R DLL chunk to a
    /// content-addressed cache directory on disk (once) and returns file paths instead of
    /// byte[] blobs (issue #perf-B). Callers can then
    /// <c>AssemblyLoadContext.LoadFromAssemblyPath</c> — which memory-maps the file
    /// instead of duplicating it on the GC heap the way <c>Assembly.Load(byte[])</c> does —
    /// and a warm cache skips the zip inflate entirely on every subsequent process, not
    /// just within one. Measured: Base Application alone ships 5 chunks totalling ~210MB;
    /// re-inflating and re-copying that on every single `al-runner` invocation was 0.9s
    /// wall + hundreds of MB of transient RSS, paid again by every one of the 4 parallel
    /// CI processes.
    ///
    /// Cache key is path+size+mtime (the same cheap-stat approach <see cref="ReadManifest"/>'s
    /// index uses), not a content hash of the chunk bytes: hashing 200+MB just to decide
    /// HIT-or-MISS would reintroduce the exact "read the whole file" cost this exists to
    /// avoid. A stale key (the .app replaced with a new mtime, e.g. CI re-downloading it)
    /// just re-extracts under a new cache-dir name — safe, not a correctness issue, only a
    /// forgone warm-cache saving for that one file.
    /// </summary>
    public static IReadOnlyList<string> ExtractAllDllPaths(string appPath)
    {
        string fullPath;
        FileInfo fi;
        try
        {
            fullPath = Path.GetFullPath(appPath);
            fi = new FileInfo(fullPath);
            if (!fi.Exists) return Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }

        var keyInput = $"{fullPath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyInput))).ToLowerInvariant();
        var cacheDir = Path.Combine(CacheRoots.Resolve("r2r-chunks"), hash);
        // Published LAST by the writer below — its presence is the commit point that
        // guarantees every *.dll beside it is a complete, non-torn set (same convention as
        // AlCacheSidecars.IsCompleteEntry / BcAppSymbolCache's DLL-published-last rule).
        var marker = Path.Combine(cacheDir, "complete.marker");

        if (File.Exists(marker))
        {
            var cached = TryReadCachedDllPaths(cacheDir, marker);
            if (cached != null)
            {
                PerfTrace.Log($"r2r-chunks HIT {Path.GetFileName(fullPath)} {cached.Count} chunk(s)");
                return cached;
            }
        }

        _r2rExtractInvocationCountByPath.AddOrUpdate(fullPath, 1, static (_, c) => c + 1);
        var dlls = ExtractAllDlls(fullPath);
        if (dlls.Count == 0) return Array.Empty<string>();

        try
        {
            Directory.CreateDirectory(cacheDir);
            var paths = new List<string>(dlls.Count);
            for (int i = 0; i < dlls.Count; i++)
            {
                var dllPath = Path.Combine(cacheDir, $"{i:D3}.dll");
                AlCacheWriter.AtomicPublish(dllPath, tmp => File.WriteAllBytes(tmp, dlls[i]));
                paths.Add(dllPath);
            }
            AlCacheWriter.AtomicPublish(marker, tmp => File.WriteAllText(tmp, dlls.Count.ToString()));
            PerfTrace.Log($"r2r-chunks MISS {Path.GetFileName(fullPath)} wrote {paths.Count} chunk(s)");
            return paths;
        }
        catch (Exception ex)
        {
            // Cache write failed (read-only cache dir, disk full, ...) — not fatal: fall
            // back to a per-process temp dir so the return contract (file paths that exist
            // and stay valid for the process's lifetime) still holds. Never silently drop
            // a chunk the caller asked for.
            PerfTrace.Log($"r2r-chunks write failed for {Path.GetFileName(fullPath)}: {ex.Message} — using temp fallback");
            return WriteDllsToTempFallback(dlls);
        }
    }

    private static List<string>? TryReadCachedDllPaths(string cacheDir, string marker)
    {
        try
        {
            var countText = File.ReadAllText(marker);
            if (!int.TryParse(countText, out var expectedCount) || expectedCount <= 0) return null;
            var files = Directory.GetFiles(cacheDir, "*.dll").OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (files.Count != expectedCount) return null;
            return files;
        }
        catch (Exception ex)
        {
            // Corrupt/unreadable cache entry (partial write from a crashed process, torn
            // read, ...) — log (verbose) and let the caller re-extract, same "cache that
            // falls back to recomputing on miss/corruption is fine, but log it" contract
            // ReadManifest's index follows.
            PerfTrace.Log($"r2r-chunks index read failed {Path.GetFileName(cacheDir)}: {ex.Message} — re-extracting");
            return null;
        }
    }

    private static List<string> WriteDllsToTempFallback(IReadOnlyList<byte[]> dlls)
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-r2r-chunks-fallback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new List<string>(dlls.Count);
        for (int i = 0; i < dlls.Count; i++)
        {
            var p = Path.Combine(dir, $"{i:D3}.dll");
            File.WriteAllBytes(p, dlls[i]);
            paths.Add(p);
        }
        return paths;
    }

    /// <summary>
    /// Returns the per-AL-object C# sources from an alc `/generatecode+` `.app`
    /// (the `bin/*.cs` entries). Empty list if the package contains no `bin/*.cs`.
    /// </summary>
    public static IReadOnlyList<EmittedSource> ExtractCSharp(string appPath)
    {
        using var zip = OpenAppZip(appPath);
        var result = new List<EmittedSource>();
        foreach (var entry in zip.Entries
            .Where(e => e.FullName.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                     && e.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            using var s = entry.Open();
            using var reader = new StreamReader(s, Encoding.UTF8);
            result.Add(new EmittedSource(entry.Name, reader.ReadToEnd()));
        }
        return result;
    }

    /// <summary>
    /// Returns the AL `.al` sources from an `.app` package's `src/`. Handles
    /// the R2R nested-app shape (outer ZIP contains a nested `.app` whose
    /// inner ZIP holds `src/*.al`). Returned as (Name, Source) for parity
    /// with v1's AppPackageReader.
    /// </summary>
    public static IReadOnlyList<(string Name, string Source)> ExtractAl(string appPath)
    {
        var bytes = File.ReadAllBytes(appPath);
        var direct = ReadAlFromNavx(bytes);
        if (direct.Count > 0) return direct;

        // R2R nested case.
        using var zipStream = new MemoryStream(bytes, NavxZipOffset(bytes), bytes.Length - NavxZipOffset(bytes));
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var nested = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            && !e.FullName.Contains('/'));
        if (nested == null) return Array.Empty<(string, string)>();

        using var ns = nested.Open();
        using var nms = new MemoryStream();
        ns.CopyTo(nms);
        return ReadAlFromNavx(nms.ToArray());
    }

    /// <summary>
    /// Returns report layout resources (`.rdlc`, `.docx`, `.xlsx`) shipped in an `.app`'s
    /// <c>layout/</c> folder, as (FileName, Bytes). A code-bearing report object declares
    /// <c>LayoutFile = './X.rdlc'</c> relative to its source; BC's compile-time layout-embed
    /// step reads that file and NREs (AL1081 "Unable to update report layout … Object reference
    /// not set") if it is absent. The Tier-3 source compile must therefore stage these next to
    /// the extracted `.al` so the relative reference resolves. Handles both the direct NAVX zip
    /// and the R2R nested-.app case, mirroring <see cref="ExtractAl"/>.
    /// </summary>
    public static IReadOnlyList<(string FileName, byte[] Bytes)> ExtractReportLayouts(string appPath)
    {
        var bytes = File.ReadAllBytes(appPath);
        var direct = ReadLayoutsFromNavx(bytes);
        if (direct.Count > 0) return direct;

        // R2R nested case.
        using var zipStream = new MemoryStream(bytes, NavxZipOffset(bytes), bytes.Length - NavxZipOffset(bytes));
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var nested = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            && !e.FullName.Contains('/'));
        if (nested == null) return Array.Empty<(string, byte[])>();
        using var ns = nested.Open();
        using var nms = new MemoryStream();
        ns.CopyTo(nms);
        return ReadLayoutsFromNavx(nms.ToArray());
    }

    private static List<(string FileName, byte[] Bytes)> ReadLayoutsFromNavx(byte[] data)
    {
        var offset = NavxZipOffset(data);
        var result = new List<(string, byte[])>();
        using var ms = new MemoryStream(data, offset, data.Length - offset, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries
            .Where(e => e.FullName.StartsWith("layout/", StringComparison.OrdinalIgnoreCase)
                     && (e.FullName.EndsWith(".rdlc", StringComparison.OrdinalIgnoreCase)
                      || e.FullName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
                      || e.FullName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))))
        {
            // The package stores layout names URL-encoded (e.g. "Test%20Report%20-%20Default=RDLC.rdlc");
            // the report's LayoutFile reference uses the decoded name. Decode so the staged file
            // name matches the './<Name>' reference.
            var decoded = Uri.UnescapeDataString(entry.Name);
            using var s = entry.Open();
            using var msEntry = new MemoryStream();
            s.CopyTo(msEntry);
            result.Add((decoded, msEntry.ToArray()));
        }
        return result;
    }

    // ── internals ────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the given .app's outer ZIP straight off disk via a <see cref="FileStream"/>,
    /// WITHOUT reading the whole file into memory first (issue #perf-B — was
    /// <c>File.ReadAllBytes</c>, the dominant cost of every caller of this method:
    /// <see cref="ReadManifest"/>, <see cref="IsR2R"/>, <see cref="ExtractAllDlls"/>,
    /// <see cref="ExtractDll"/>). ZipArchive's Read-mode constructor only parses the
    /// central directory + whichever entries the caller actually <c>.Open()</c>s, so this
    /// alone turns "read all N MB of a package" into "read the directory, then only the
    /// bytes of the entries actually touched" for every one of those callers, not just the
    /// manifest path. The returned archive owns the FileStream (disposed transitively
    /// through <see cref="NavxZipView"/> when the caller disposes the archive).
    /// </summary>
    private static ZipArchive OpenAppZip(string appPath)
    {
        var fs = new FileStream(appPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: false);
        try
        {
            var offset = ReadNavxOffsetFromStream(fs);
            var view = new NavxZipView(fs, offset);
            return new ZipArchive(view, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private static int ReadNavxOffsetFromStream(FileStream fs)
    {
        fs.Position = 0;
        Span<byte> header = stackalloc byte[8];
        int total = 0;
        while (total < 8)
        {
            int n = fs.Read(header.Slice(total));
            if (n == 0) break; // shorter than 8 bytes — not NAVX-prefixed
            total += n;
        }
        if (total == 8 && header[0] == (byte)'N' && header[1] == (byte)'A'
            && header[2] == (byte)'V' && header[3] == (byte)'X')
            return (int)BitConverter.ToUInt32(header.Slice(4, 4));
        return 0;
    }

    /// <summary>
    /// Read-only, offset view of a seekable stream: makes byte <paramref name="offset"/> of
    /// the wrapped stream appear as position 0 to a consumer. ZipArchive parses a ZIP's
    /// central directory via absolute <c>Seek(..., SeekOrigin.Begin)</c> calls that must
    /// land at "start of the zip data" — but a BC .app's zip payload starts a few bytes
    /// into the physical file (the NAVX magic + offset header), so reading a FileStream for
    /// the whole file directly would misalign every central-directory offset. This view is
    /// what lets <see cref="OpenAppZip"/> hand ZipArchive a stream where position 0 really
    /// is the zip's start, without ever copying the file's bytes into memory. Only the
    /// members ZipArchive's Read-mode constructor actually calls are implemented.
    /// </summary>
    private sealed class NavxZipView : Stream
    {
        private readonly Stream _inner;
        private readonly long _offset;

        internal NavxZipView(Stream inner, long offset)
        {
            _inner = inner;
            _offset = offset;
            _inner.Position = offset;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length - _offset;
        public override long Position
        {
            get => _inner.Position - _offset;
            set => _inner.Position = _offset + value;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override long Seek(long offset, SeekOrigin origin)
        {
            long absolute = origin switch
            {
                SeekOrigin.Begin => _offset + offset,
                SeekOrigin.Current => _inner.Position + offset,
                SeekOrigin.End => _offset + Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            _inner.Position = absolute;
            return absolute - _offset;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static ZipArchive OpenZipFromNavx(byte[] bytes)
    {
        var offset = NavxZipOffset(bytes);
        var ms = new MemoryStream(bytes, offset, bytes.Length - offset, writable: false);
        return new ZipArchive(ms, ZipArchiveMode.Read);
    }

    private static int NavxZipOffset(byte[] bytes)
    {
        if (bytes.Length >= 8
            && bytes[0] == (byte)'N' && bytes[1] == (byte)'A'
            && bytes[2] == (byte)'V' && bytes[3] == (byte)'X')
            return (int)BitConverter.ToUInt32(bytes, 4);
        return 0;
    }

    private static List<(string Name, string Source)> ReadAlFromNavx(byte[] data)
    {
        var offset = NavxZipOffset(data);
        var result = new List<(string, string)>();
        using var ms = new MemoryStream(data, offset, data.Length - offset, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries
            .Where(e => e.FullName.StartsWith("src/", StringComparison.OrdinalIgnoreCase)
                     && e.FullName.EndsWith(".al", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            using var s = entry.Open();
            using var reader = new StreamReader(s, Encoding.UTF8);
            result.Add((entry.Name, reader.ReadToEnd()));
        }
        return result;
    }
}
