// DependencyLoader — turns a topo-sorted dep list into loaded Assemblies in
// the default ALC. Three-tier resolution per dep:
//
//   Tier 1: pre-compiled DLL at <bucketRoot>/.deps-bin/<Publisher>_<Name>_<Version>.dll
//   Tier 2: R2R `.app` (publishedartifacts/*.dll) — Microsoft-shipped binaries
//   Tier 3: source-only `.app` — extract src/*.al, run BcCompiler.Emit + BcAssembler.Compile
//
// All loads cache by AppId in a process-wide dictionary so cross-bucket sharing
// is free. A `Default.Resolving` handler is installed once at first use so the
// .NET runtime can re-resolve assemblies-by-name back to the byte[]-loaded
// instances (Assembly.Load(byte[]) puts the assembly in the default ALC, but
// reference resolution still goes by name).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using AlRunnerV2.Infrastructure;

namespace AlRunnerV2;

public sealed class DependencyLoader
{
    private static readonly ConcurrentDictionary<Guid, Assembly> _cache = new();
    private static readonly ConcurrentDictionary<string, Assembly> _byName =
        new(StringComparer.OrdinalIgnoreCase);
    private static int _resolverInstalled;

    private readonly BcCompiler _compiler;
    private readonly BcAssembler _assembler;

    public DependencyLoader(BcCompiler compiler, BcAssembler assembler)
    {
        _compiler = compiler;
        _assembler = assembler;
        EnsureResolverInstalled();
    }

    public IReadOnlyList<Assembly> LoadAll(
        IReadOnlyList<(AppManifest Manifest, string AppPath)> ordered,
        string bucketRoot)
    {
        var list = new List<Assembly>();
        foreach (var (m, path) in ordered)
        {
            // A source-only Microsoft app carries compile-time symbols, not a runtime DLL.
            // Upfront source-compiling large test-toolkit packages is both slow and can hang;
            // runtime codeunits are resolved lazily from extracted service-tier DLLs by
            // CodeunitPatches.FindCodeunitType (or safely no-op for known test-toolkit ranges).
            // R2R Microsoft apps still must load, because they carry the actual runtime chunks.
            if (string.Equals(m.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)
                && !AppLoader.IsR2R(path))
            {
                Console.Error.WriteLine($"[deps] load skip Microsoft source-only symbols: {m.Publisher}_{m.Name} v{m.Version}");
                continue;
            }
            if (_cache.TryGetValue(m.AppId, out var existing))
            {
                list.Add(existing);
                continue;
            }
            var asm = LoadOne(m, path, bucketRoot);
            if (asm != null)
            {
                _cache[m.AppId] = asm;
                _byName[asm.GetName().Name ?? ""] = asm;
                // Register app metadata so AlCallStackCapture can decorate frames.
                AlCallStackCapture.RegisterAssemblyInfo(asm, m.Name, m.Publisher, m.Version.ToString());
                list.Add(asm);
            }
        }
        return list;
    }

    private Assembly? LoadOne(AppManifest m, string appPath, string bucketRoot)
    {
        // Tier 1: precompiled DLL.
        var depsBin = Path.Combine(bucketRoot, ".deps-bin");
        var fileName = SanitizeFileName($"{m.Publisher}_{m.Name}_{m.Version}.dll");
        var precompiled = Path.Combine(depsBin, fileName);
        if (File.Exists(precompiled))
        {
            try
            {
                var bytes = File.ReadAllBytes(precompiled);
                return Assembly.Load(bytes);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[deps] tier-1 load failed for {m.Name}: {ex.Message}");
            }
        }

        // Tier 2: R2R extract. Microsoft ships large apps (notably Base
        // Application — 5 DLL chunks) as multiple `publishedartifacts/*.dll`
        // entries. Load every DLL; the chunk that defines the user-visible
        // app type (e.g. `Codeunit9015` for "Application System Constants")
        // is not necessarily the first one. We return the chunk whose
        // assembly name matches the manifest's app name when present, else
        // the first chunk; all chunks are registered in the by-name cache so
        // the Resolving handler can serve cross-chunk references.
        if (AppLoader.IsR2R(appPath))
        {
            var dlls = AppLoader.ExtractAllDlls(appPath);
            if (dlls.Count > 0)
            {
                Assembly? primary = null;
                int loaded = 0;
                foreach (var dll in dlls)
                {
                    try
                    {
                        var asm = Assembly.Load(dll);
                        var n = asm.GetName().Name ?? "";
                        _byName[n] = asm;
                        primary ??= asm;
                        loaded++;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[deps] tier-2 R2R chunk load failed for {m.Name}: {ex.Message}");
                    }
                }
                if (loaded > 1)
                    Console.Error.WriteLine($"[deps] tier-2 R2R: {m.Name} loaded {loaded} DLL chunk(s)");
                return primary;
            }
        }

        // Tier 3: source-only compile-on-the-fly.
        var sw = Stopwatch.StartNew();
        var alSources = AppLoader.ExtractAl(appPath);

        // Tier 2.5 (DLL-first): Microsoft ships its test toolkit symbol-only (AL source,
        // no compiled code). The same objects are precompiled in the extracted service-tier
        // DLL cache. If the cache covers this dep's codeunits, skip the expensive whole-app
        // source compile and let CodeunitPatches.FindCodeunitType resolve each codeunit body
        // lazily from the cache at dispatch (runs the REAL Microsoft code). Per the chosen
        // policy: source-compile only remains the fallback for objects the cache lacks.
        if (alSources.Count > 0 && ServiceTierDllIndex.Available)
        {
            var codeunitIds = ExtractCodeunitTypeNames(alSources);
            if (codeunitIds.Count > 0 && codeunitIds.All(ServiceTierDllIndex.Contains))
            {
                Console.Error.WriteLine(
                    $"[deps] DLL-first: {m.Publisher}_{m.Name} v{m.Version} — {codeunitIds.Count} codeunit(s) " +
                    $"served from extracted service-tier DLLs; skipping source compile");
                return null; // lazy dispatch via ServiceTierDllIndex
            }
        }

        if (alSources.Count == 0)
        {
            // Symbol-only package (no runtime code in this .app — normal for Microsoft
            // platform apps that are provided via service-tier DLLs loaded elsewhere).
            Console.Error.WriteLine(
                $"[deps] NOTE: {m.Publisher}_{m.Name} v{m.Version} is symbol-only " +
                $"(no runtime code in package); relying on service-tier/already-loaded assembly");
            return null;
        }

        var cacheKey = ComputeSourceDependencyCacheKey(m, appPath);
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "al-runner", "compiled-deps");
        var cachedDll = Path.Combine(cacheDir, cacheKey + ".dll");
        if (File.Exists(cachedDll))
        {
            try
            {
                var cachedBytes = File.ReadAllBytes(cachedDll);
                Console.Error.WriteLine(
                    $"[deps] source-cache HIT: {m.Name} v{m.Version} key={cacheKey[..12]} ({cachedBytes.Length} bytes)");
                return Assembly.Load(cachedBytes);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[deps] source-cache read/load failed for {m.Name}: {ex.Message}; rebuilding");
            }
        }

        var tempDir = Path.Combine(Path.GetTempPath(),
            "al-runner-deps", SanitizeFileName($"{m.Publisher}_{m.Name}_{m.Version}"));
        Directory.CreateDirectory(tempDir);
        // Clean previously emitted .al files so a stale one doesn't pollute the compile.
        foreach (var existing in Directory.EnumerateFiles(tempDir, "*.al"))
        {
            try { File.Delete(existing); } catch { }
        }
        foreach (var (name, src) in alSources)
        {
            var fileSafe = SanitizeFileName(name);
            File.WriteAllText(Path.Combine(tempDir, fileSafe), src);
        }

        IReadOnlyList<EmittedSource> emitted;
        // Scope _currentAppId to the dep's own identity for the duration of this compile.
        // GetSharedReferences uses _currentAppId to exclude the "current app" from its
        // reference specs. Without this, the dep's resolved spec (from _resolvedDeps of
        // the PARENT bundle) would be both in the reference list AND in the primary AL
        // source → AL0275 "ambiguous reference". The scope is restored on dispose.
        try { using (BcCompiler.ScopeCurrentAppIdentity(m.AppId, m.Publisher, m.Version))
                  emitted = _compiler.Emit(new[] { tempDir }, m.Name).Sources; }
        catch (Exception ex)
        {
            // EMIT-FAIL: the BC Compilation.Emit() call threw (e.g. "Unexpected value 'None'
            // of type NavTypeKind", "Index was outside the bounds", etc.).
            // Do NOT swallow — this dependency is broken and running without it will produce
            // cryptic failures (NavNCLMissingMethodException with object ID 0).
            var detail = DependencyLoadException.FlattenException(ex);
            Console.Error.WriteLine($"[dep-load-fail] {m.Publisher}_{m.Name} v{m.Version}: EMIT-FAIL — {detail}");
            throw new DependencyLoadException(m.Publisher, m.Name, m.Version.ToString(), "EMIT-FAIL", detail, ex);
        }
        if (emitted.Count == 0)
        {
            // EMIT-ZERO: Emit returned success but produced no sources — BC's silent
            // zero-output sentinel. The dependency has source but nothing was compiled.
            const string detail =
                "BC Compilation.Emit() returned 0 sources from app AL source " +
                "(silent zero-output sentinel — likely a NavTypeKind/emitter crash swallowed internally). " +
                "Run with BCCOMPILER_DIAG=1 or --precompile for full diagnostics.";
            Console.Error.WriteLine($"[dep-load-fail] {m.Publisher}_{m.Name} v{m.Version}: EMIT-ZERO — {detail}");
            throw new DependencyLoadException(m.Publisher, m.Name, m.Version.ToString(), "EMIT-ZERO", detail);
        }

        var asmName = $"Dep_{SanitizeIdent(m.Publisher)}_{SanitizeIdent(m.Name)}_{m.Version.ToString().Replace('.', '_')}";
        var compile = _assembler.Compile(asmName, emitted);
        if (!compile.Success)
        {
            // COMPILE-FAIL: Roslyn failed to compile the C# polyfill bodies BC emitted.
            var allErrors = string.Join(" | ", compile.Errors.Select(e => e.Split('\n')[0]));
            Console.Error.WriteLine($"[dep-load-fail] {m.Publisher}_{m.Name} v{m.Version}: COMPILE-FAIL — {allErrors}");
            throw new DependencyLoadException(m.Publisher, m.Name, m.Version.ToString(), "COMPILE-FAIL", allErrors);
        }

        sw.Stop();
        Console.Error.WriteLine(
            $"[deps] compiled-on-the-fly: {m.Name} v{m.Version} ({sw.ElapsedMilliseconds}ms). " +
            $"For faster CI, run --precompile to snapshot.");
        try
        {
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(cachedDll, compile.AssemblyBytes!);
            Console.Error.WriteLine(
                $"[deps] source-cache WROTE: {m.Name} v{m.Version} key={cacheKey[..12]} ({compile.AssemblyBytes!.Length} bytes)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[deps] source-cache write failed for {m.Name}: {ex.Message}");
        }
        try { return Assembly.Load(compile.AssemblyBytes!); }
        catch (Exception ex)
        {
            // LOAD-FAIL: the compiled bytes could not be loaded into the ALC.
            var detail = DependencyLoadException.FlattenException(ex);
            Console.Error.WriteLine($"[dep-load-fail] {m.Publisher}_{m.Name} v{m.Version}: LOAD-FAIL — {detail}");
            throw new DependencyLoadException(m.Publisher, m.Name, m.Version.ToString(), "LOAD-FAIL", detail, ex);
        }
    }

    private static string ComputeSourceDependencyCacheKey(AppManifest manifest, string appPath)
    {
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();
        void WriteLine(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s + "\n");
            ms.Write(bytes, 0, bytes.Length);
        }

        WriteLine("schema:v1");
        var runnerLoc = typeof(BcAssembler).Assembly.Location;
        if (!string.IsNullOrEmpty(runnerLoc) && File.Exists(runnerLoc))
            WriteLine($"runner:{File.GetLastWriteTimeUtc(runnerLoc).Ticks}:{new FileInfo(runnerLoc).Length}");
        else
            WriteLine("runner:unknown");
        WriteLine($"app:{manifest.AppId}:{manifest.Publisher}:{manifest.Name}:{manifest.Version}");
        foreach (var dep in manifest.Dependencies.OrderBy(d => $"{d.Publisher}/{d.Name}/{d.Version}/{d.AppId}", StringComparer.OrdinalIgnoreCase))
            WriteLine($"dep:{dep.AppId}:{dep.Publisher}:{dep.Name}:{dep.Version}");
        using (var fs = File.OpenRead(appPath))
            WriteLine($"app-bytes:{Convert.ToHexString(sha.ComputeHash(fs))}");

        ms.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(ms)).ToLowerInvariant();
    }

    // Cheap source scan for "codeunit <id> ..." declarations → "Codeunit<id>" type names,
    // used to test extracted-DLL coverage without a full compile. Object-extension and
    // non-codeunit objects are intentionally ignored (only codeunits carry dispatchable
    // runtime bodies the test calls into).
    private static readonly System.Text.RegularExpressions.Regex _codeunitDecl =
        new(@"(?im)^\s*codeunit\s+(\d+)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static HashSet<string> ExtractCodeunitTypeNames(IReadOnlyList<(string Name, string Src)> sources)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, src) in sources)
            foreach (System.Text.RegularExpressions.Match mm in _codeunitDecl.Matches(src))
                set.Add("Codeunit" + mm.Groups[1].Value);
        return set;
    }

    private static string SanitizeFileName(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '/', '\\' }).ToArray();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(Array.IndexOf(bad, ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }

    private static string SanitizeIdent(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return sb.ToString();
    }

    /// <summary>
    /// Idempotent install of the default-ALC Resolving handler. Public so callers
    /// (e.g. Program.cs at startup) can install it before BcRuntime applies patches,
    /// in case a patch's reflection on a BC type triggers an assembly load for a
    /// transitively-referenced service-tier DLL that's not in the application bin.
    /// </summary>
    public static void EnsureResolverInstalled_Public() => EnsureResolverInstalled();

    private static void EnsureResolverInstalled()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0) return;
        // BC service-tier artifact dir — same path BcRuntime/BcAssembler/Runner.csproj
        // resolve the 5 we project-reference (Types, Ncl, Common, Language, CodeAnalysis).
        // Microsoft.Dynamics.Nav.Ncl.dll transitively references ~24 BC DLLs, of which
        // we only project-reference 5; the rest sit in the artifact dir but aren't on
        // any probing path. When a generic instantiation or reflection call inside MS
        // R2R code reaches one (e.g. Microsoft.Dynamics.Nav.Core, .AL.Common, .Apps,
        // .TableProxyBuilder), it fails to load and the call NREs deep in MS code. The
        // probe below catches every Microsoft.Dynamics.Nav.* assembly request and serves
        // it from the artifact dir.
        // Single source of truth for the artifact dir (tracks AlRunner.csproj's _BCVersion).
        var serviceTierPath = AlRunnerV2.Infrastructure.BcArtifacts.ServiceTierDir;
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (name.Name == null) return null;
            if (_byName.TryGetValue(name.Name, out var asm))
                return asm;
            // Serve any service-tier assembly from the artifact dir. BC 28 modernised its
            // runtime onto a large external closure (Azure SDK, Microsoft.Identity / .Extensions,
            // IdentityModel) beyond the Microsoft.Dynamics.Nav.* set; all ship in the artifact
            // dir. This handler only fires after default resolution fails, so serving BC's own
            // shipped copy is the faithful choice.
            var probe = Path.Combine(serviceTierPath, name.Name + ".dll");
            if (File.Exists(probe))
                return ctx.LoadFromAssemblyPath(probe);
            return null;
        };
    }

    /// <summary>
    /// Lookup helper for callers that want to access a loaded dep by name
    /// (e.g. when verifying that a compile-time symbol matches a runtime one).
    /// </summary>
    public static Assembly? TryGetByAppId(Guid appId)
        => _cache.TryGetValue(appId, out var asm) ? asm : null;
}
