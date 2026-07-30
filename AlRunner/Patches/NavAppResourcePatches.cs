// NavAppResourcePatches — faithful NavApp.GetResource / GetResourceAsText support.
//
// Real BC (Ncl: ALNavApp.GetPackagedResource) resolves the CALLING app via
// session.CurrentMethodScope → ApplicationObject → MetaApplicationObject → OwningApp,
// then asks NavEnvironment.Instance.NavAppMetadataRetriever for the resource stream
// out of the app's mounted package (/resources/<name> inside the NAVX zip; the
// manifest is /PackagedResources.json). On the headless skeleton that whole chain is
// null → NRE (the exact abort Pageworks's PageworksInstall.RegisterBaselineFonts hit).
//
// The runner already knows, for every loaded AL assembly, exactly which app it came
// from and where that app's bytes live:
//   • the bundle's own emitted test assembly     → the bundle source dir (app.json
//     "resourceFolders" declare where resource files live, names are folder-relative
//     '/'-separated paths — the same shape the AL compiler packages into /resources/),
//   • dependency assemblies (DependencyLoader)   → the resolved .app package
//     (/resources/<name> part) and, for sibling SOURCE deps packaged into the
//     synthetic workspace-deps .app (which carries no /resources/), the original
//     source dir registered from BuildSiblingSourceDeps.
//
// Owning-app resolution mirrors BC:
//   1. the session's CurrentMethodScope chain (ApplicationObject's declaring
//      assembly), exactly like ALNavApp.GetCallingAppId walks scopes;
//   2. fallback: the managed stack — the nearest frame whose declaring assembly is a
//      registered AL app assembly (equivalent nearest-AL-scope semantics for paths
//      where the runner invokes AL directly via MethodInfo.Invoke, e.g. install
//      triggers, where no NavMethodScope may be current yet).
//
// On a genuine miss this throws BC's OWN NavNclResourceNotFoundException with BC's
// message ("A resource matching '{0}' could not be found in app '{1}'.") — resources
// are IN SCOPE, so never RunnerOutOfScope and never a silent default
// (.claude/rules/loud-failures.md).
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Dynamics.Nav.Types.Exceptions;

namespace AlRunner.Patches;

public static class NavAppResourcePatches
{
    private sealed class AppResourceStore
    {
        public required string DisplayName { get; init; }
        /// <summary>Bundle/sibling source dir containing app.json (may be null).</summary>
        public string? SourceDir { get; set; }
        /// <summary>Resolved .app package path (may be null, e.g. the bundle itself).</summary>
        public string? AppPath { get; set; }

        private IReadOnlyList<string>? _resourceFolders;         // from app.json, lazy
        private Dictionary<string, byte[]>? _packageResources;   // from .app /resources/, lazy

        public byte[]? TryRead(string resourceName)
        {
            // Source dir first: for sibling source deps the synthetic workspace .app has
            // no /resources/ part, and for the bundle itself there is no .app at all.
            // The on-disk file IS what the AL compiler would have packaged.
            if (SourceDir != null)
            {
                _resourceFolders ??= ReadResourceFolders(Path.Combine(SourceDir, "app.json"));
                foreach (var folder in _resourceFolders)
                {
                    // Resource names are '/'-separated paths relative to a declared folder.
                    var rel = resourceName.Replace('/', Path.DirectorySeparatorChar);
                    var candidate = Path.Combine(SourceDir, folder, rel);
                    // Containment guard: never serve files outside the declared folder.
                    var folderFull = Path.GetFullPath(Path.Combine(SourceDir, folder))
                        + Path.DirectorySeparatorChar;
                    var candidateFull = Path.GetFullPath(candidate);
                    if (!candidateFull.StartsWith(folderFull, StringComparison.Ordinal)) continue;
                    if (File.Exists(candidateFull)) return File.ReadAllBytes(candidateFull);
                }
            }

            if (AppPath != null && File.Exists(AppPath))
            {
                _packageResources ??= ReadPackageResources(AppPath);
                if (_packageResources.TryGetValue(resourceName, out var bytes)) return bytes;
            }

            return null;
        }

        private static IReadOnlyList<string> ReadResourceFolders(string appJsonPath)
        {
            try
            {
                if (!File.Exists(appJsonPath)) return Array.Empty<string>();
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appJsonPath));
                if (doc.RootElement.TryGetProperty("resourceFolders", out var rf)
                    && rf.ValueKind == System.Text.Json.JsonValueKind.Array)
                    return rf.EnumerateArray()
                        .Select(e => e.GetString())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Select(s => s!)
                        .ToList();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[resources] failed reading resourceFolders from {appJsonPath}: {ex.Message}");
            }
            return Array.Empty<string>();
        }

        /// <summary>
        /// Read every /resources/* part of the NAVX package into memory. Handles the
        /// R2R nested case (outer package wrapping an inner .app that carries the
        /// /resources/ parts). Resource name = package path minus "resources/",
        /// case-sensitive exactly like BC's PackagedResourceManifest dictionary.
        /// </summary>
        private static Dictionary<string, byte[]> ReadPackageResources(string appPath)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            try
            {
                var bytes = File.ReadAllBytes(appPath);
                ReadResourcesFromNavx(bytes, result, allowNested: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[resources] failed reading /resources/ from {appPath}: {ex.Message}");
            }
            return result;
        }

        private static void ReadResourcesFromNavx(byte[] navxBytes, Dictionary<string, byte[]> into, bool allowNested)
        {
            var offset = NavxZipOffset(navxBytes);
            using var ms = new MemoryStream(navxBytes, offset, navxBytes.Length - offset, writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            bool any = false;
            foreach (var entry in zip.Entries)
            {
                var full = entry.FullName.TrimStart('/');
                if (!full.StartsWith("resources/", StringComparison.Ordinal)) continue;
                if (entry.Length == 0 && full.EndsWith("/", StringComparison.Ordinal)) continue;
                using var s = entry.Open();
                using var buf = new MemoryStream();
                s.CopyTo(buf);
                into[full["resources/".Length..]] = buf.ToArray();
                any = true;
            }
            if (any || !allowNested) return;
            // R2R nested case: the outer package wraps an inner .app carrying the parts.
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
                using var s = entry.Open();
                using var buf = new MemoryStream();
                s.CopyTo(buf);
                ReadResourcesFromNavx(buf.ToArray(), into, allowNested: false);
                if (into.Count > 0) return;
            }
        }

        private static int NavxZipOffset(byte[] bytes)
        {
            if (bytes.Length >= 8
                && bytes[0] == (byte)'N' && bytes[1] == (byte)'A'
                && bytes[2] == (byte)'V' && bytes[3] == (byte)'X')
                return (int)BitConverter.ToUInt32(bytes, 4);
            return 0;
        }
    }

    // Assembly → resource store. Assemblies are process-wide (Assembly.Load(byte[])),
    // so entries stay valid across bundles; re-registration updates in place.
    private static readonly ConcurrentDictionary<Assembly, AppResourceStore> _byAssembly = new();
    // AppId → sibling SOURCE dir (from BuildSiblingSourceDeps) so a source dep loaded
    // via its synthetic workspace .app (no /resources/ part) still finds its files.
    private static readonly ConcurrentDictionary<Guid, string> _sourceDirByAppId = new();

    private static string? _currentBundleDir;

    /// <summary>Remember the bundle dir whose test assembly is about to be emitted
    /// (Program.cs bundle loop). Null when the bundle has no app.json.</summary>
    public static void SetCurrentBundleDir(string? dir) => _currentBundleDir = dir;

    /// <summary>Register the bundle's own emitted test assembly against the current
    /// bundle source dir (called from BcRuntime.SetTestAssembly).</summary>
    public static void RegisterTestAssembly(Assembly asm)
    {
        var dir = _currentBundleDir;
        if (dir == null) return;
        var name = Path.GetFileName(dir);
        _byAssembly[asm] = new AppResourceStore { DisplayName = name, SourceDir = dir };
    }

    /// <summary>Register a dependency assembly against its resolved .app package
    /// (DependencyLoader.LoadAll). If the app is a sibling source dep, its original
    /// source dir (registered via <see cref="RegisterSourceDirForApp"/>) wins.</summary>
    public static void RegisterDependencyAssembly(
        Assembly asm, Guid appId, string name, string publisher, string version, string appPath)
    {
        _sourceDirByAppId.TryGetValue(appId, out var sourceDir);
        _byAssembly[asm] = new AppResourceStore
        {
            DisplayName = $"{name} by {publisher} {version}",
            SourceDir = sourceDir,
            AppPath = File.Exists(appPath) ? appPath : null,
        };
    }

    /// <summary>Remember a sibling source app's dir by AppId (BuildSiblingSourceDeps)
    /// so its dependency assembly can serve resources from the original files.</summary>
    public static void RegisterSourceDirForApp(Guid appId, string sourceDir)
    {
        if (appId != Guid.Empty) _sourceDirByAppId[appId] = sourceDir;
    }

    // ── Cecil-rewrite entry points ─────────────────────────────────────────────

    /// <summary>
    /// Body replacement for Ncl's private ALNavApp.GetPackagedResource(NavSession, string).
    /// Returns a completed Task&lt;Stream&gt; over the resource bytes (the awaiting real
    /// callers — ALGetResourceAsync / ALGetResourceAsTextAsync — continue synchronously),
    /// or throws BC's NavNclResourceNotFoundException on a miss, exactly like
    /// CachingDatabaseNavAppMetadataRetriever.GetPackagedResourceAsync does.
    /// </summary>
    public static object ALNavApp_GetPackagedResource(object session, string resourceName)
    {
        return System.Threading.Tasks.Task.FromResult<Stream>(
            new MemoryStream(GetResourceBytes(session, resourceName), writable: false));
    }

    /// <summary>
    /// Body replacement for ALNavApp.ALGetResourceAsTextAsync(NavSession, string, TextEncoding).
    /// Reimplements the real body's encoding switch skeleton-safely: UTF8/UTF16 map
    /// directly; MSDos/Windows use session.Tenant.DefaultEncoding when the skeleton has
    /// one, else the closest faithful default (OEM 437, falling back to Latin-1).
    /// </summary>
    public static object ALNavApp_ALGetResourceAsTextAsync(object session, string resourceName, object encoding)
    {
        var bytes = GetResourceBytes(session, resourceName);
        int enc = Convert.ToInt32(encoding); // Ncl TextEncoding: MSDos=0, UTF8=1, UTF16=2, Windows=3
        Encoding systemEncoding = enc switch
        {
            1 => Encoding.UTF8,
            2 => Encoding.Unicode,
            _ => TenantDefaultEncoding(session),
        };
        var navTextType = RequireNcl().GetType("Microsoft.Dynamics.Nav.Runtime.NavText")
            ?? Type.GetType("Microsoft.Dynamics.Nav.Types.NavText, Microsoft.Dynamics.Nav.Types")
            ?? throw new InvalidOperationException("NavText not found in Ncl/Types");
        var navText = Activator.CreateInstance(navTextType, systemEncoding.GetString(bytes))!;
        // Task.FromResult<NavText>(navText) with the runtime NavText type.
        var fromResult = typeof(System.Threading.Tasks.Task)
            .GetMethod("FromResult", BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(navTextType);
        return fromResult.Invoke(null, new[] { navText })!;
    }

    private static Encoding TenantDefaultEncoding(object session)
    {
        try
        {
            var tenant = session?.GetType().GetProperty("Tenant",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(session);
            var e = tenant?.GetType().GetProperty("DefaultEncoding",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(tenant) as Encoding;
            if (e != null) return e;
        }
        catch { /* skeleton tenant absent — fall through */ }
        try { return Encoding.GetEncoding(437); }
        catch { return Encoding.Latin1; }
    }

    // ── core lookup ────────────────────────────────────────────────────────────

    private static byte[] GetResourceBytes(object session, string resourceName)
    {
        var store = ResolveOwningStore(session);
        if (store == null)
        {
            // Real BC: method scope doesn't belong to an object → not-found with empty
            // app name (ALNavApp.GetPackagedResource's own null-chain branch).
            throw new NavNclResourceNotFoundException(resourceName ?? string.Empty, string.Empty);
        }
        var bytes = store.TryRead(resourceName ?? string.Empty);
        if (bytes == null)
            throw new NavNclResourceNotFoundException(resourceName ?? string.Empty, store.DisplayName);
        return bytes;
    }

    private static Assembly? _nclAsm;
    private static PropertyInfo? _pCurrentMethodScope;
    private static PropertyInfo? _pApplicationObject;
    private static PropertyInfo? _pParentScope;

    private static Assembly RequireNcl() =>
        _nclAsm ??= AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");

    /// <summary>
    /// Find the calling app's resource store: walk the session's method-scope chain
    /// (BC's own owning-app semantics), then fall back to the managed stack for
    /// runner-invoked AL (install triggers, direct test dispatch).
    /// </summary>
    private static AppResourceStore? ResolveOwningStore(object session)
    {
        try
        {
            if (session != null)
            {
                _pCurrentMethodScope ??= session.GetType().GetProperty("CurrentMethodScope",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var scope = _pCurrentMethodScope?.GetValue(session);
                while (scope != null)
                {
                    _pApplicationObject ??= scope.GetType().GetProperty("ApplicationObject",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var appObj = _pApplicationObject?.GetValue(scope);
                    if (appObj != null
                        && _byAssembly.TryGetValue(appObj.GetType().Assembly, out var s))
                        return s;
                    _pParentScope ??= scope.GetType().GetProperty("ParentScope",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    scope = _pParentScope?.GetValue(scope);
                }
            }
        }
        catch { /* skeleton scope chain incomplete — use the stack fallback */ }

        try
        {
            var trace = new System.Diagnostics.StackTrace(fNeedFileInfo: false);
            for (int i = 0; i < trace.FrameCount; i++)
            {
                var asm = trace.GetFrame(i)?.GetMethod()?.DeclaringType?.Assembly;
                if (asm != null && _byAssembly.TryGetValue(asm, out var s)) return s;
            }
        }
        catch { }
        return null;
    }
}
