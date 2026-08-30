using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AlRunner.Infrastructure;

/// <summary>
/// Bootstraps a fresh process whose <c>AppContext.BaseDirectory</c> legitimately
/// contains <c>Microsoft.Dynamics.Nav.Ncl.dll</c>, for installs that no longer ship it
/// in the tool package (see <c>.github/scripts/check-nupkg-contents.sh</c> — the
/// package must not redistribute Microsoft's BC assembly; the user's own artifact
/// cache supplies it at runtime instead, same as every other BC/Aspose/Graph DLL that
/// was already stripped).
///
/// <para><b>Why this can't be fixed in-process.</b> CoreCLR's trusted-platform-assemblies
/// (TPA) list is computed once, by the native host, before any of our managed code
/// runs — from the app's <c>deps.json</c> plus the literal on-disk contents of
/// <c>AppContext.BaseDirectory</c> AT THAT MOMENT. Writing Ncl.dll into that directory
/// a few statements into <c>Main</c> (as <see cref="NclCecilRewrite.RewriteInPlace"/>
/// already does, for the case where the file already exists there) is too late: TPA
/// was already fixed without it. A later <c>Assembly.Load("Microsoft.Dynamics.Nav.Ncl")</c>
/// then falls through to the <c>AssemblyLoadContext.Default.Resolving</c> fallback
/// (<see cref="AlRunner.DependencyLoader"/>), which serves the RAW, un-Cecil-rewritten
/// copy straight from the BC artifact cache — and <c>NavEnvironment</c>'s static
/// constructor then calls the real <c>WindowsIdentity.GetCurrent()</c>, which throws
/// <c>PlatformNotSupportedException</c> on Linux. Confirmed empirically before this
/// class existed (see the PR that introduced it).
/// </para>
///
/// <para><b>The fix.</b> Build (or reuse) a runner-owned "shadow" directory that
/// mirrors this install: most of the large, numerous dependency DLLs are symlinked
/// (near-zero cost — so they resolve exactly as they would from the real install
/// directory), but the entry assembly (<c>al-runner.dll</c>) plus its deps/runtimeconfig
/// manifests are real, independent COPIES, not symlinks — confirmed empirically that a
/// symlinked entry assembly makes CoreCLR report <c>AppContext.BaseDirectory</c> as the
/// symlink's TARGET directory (the real install), silently defeating the whole point.
/// The Cecil-rewritten Ncl.dll (produced via the existing <c>ncl-cecil</c> cache) is
/// likewise a real file. Then re-exec via the <c>dotnet</c> muxer pointed at the shadow
/// copy of <c>al-runner.dll</c>. The CHILD process's TPA is computed fresh, from a
/// directory where Ncl.dll genuinely exists on disk before that process starts, so it
/// resolves faithfully with no further trickery — the child takes the exact same
/// <see cref="NclCecilRewrite.RewriteInPlace"/> path any normal (Ncl.dll-shipping)
/// install already takes.</para>
/// </summary>
public static class NclShadowRuntime
{
    private const string NclFileName = "Microsoft.Dynamics.Nav.Ncl.dll";
    private const string MarkerFileName = ".al-runner-shadow-source";
    private const string EntryDllName = "al-runner.dll";

    // The entry assembly and the small manifests hostfxr reads to launch it — these
    // must be real, independent files in the shadow dir, not symlinks. See the comment
    // at the call site in EnsureShadowDir for why.
    //
    // #2166: AlRunner.QueryJoin.dll and AlRunner.Provisioning.dll join this list for a
    // DIFFERENT reason — they are load-by-path assemblies, not entry-assembly manifests.
    // AlRunner.QueryJoin.dll is loaded explicitly via Assembly.LoadFrom(AppContext.
    // BaseDirectory + name) — see RecordPatches.QueryJoin.cs — lazily, on the first
    // multi-dataitem query JOIN a test actually runs. AlRunner.Provisioning.dll is
    // resolved implicitly by the CLR (a normal compiled-in reference, probed relative to
    // the entry assembly's own directory) from --auto-provision / ProvisioningCheck code
    // that runs AFTER the shadow-hop — confirmed by reading Program.cs: those call sites
    // sit past both re-exec decision points, so they execute in whatever directory this
    // method built.
    //
    // Before this fix both fell into the generic "every other dependency DLL" bucket
    // below and were symlinked back to origDir. That happened to keep working ONLY
    // because EnsureShadowDir's caller always passes the top-level "tools/.../any"
    // install directory as origDir, which does ship both files — so the symlink target
    // resolved. But nothing re-validates that symlink once the shadow dir is cached:
    // EnsureShadowDir's "reusable" check only looks at the marker file plus the entry
    // assembly and Ncl.dll, never at the rest of what it once linked. If the file a
    // symlink points to is later removed from the original install — a tool reinstall/
    // upgrade in place, `mise`/`dotnet tool` pruning an old version, disk cleanup — the
    // shadow dir is still reported "reusable", and the dangling symlink only surfaces
    // the next time something tries to load it. For AlRunner.QueryJoin.dll that is
    // deep inside the FIRST join test that happens to run, long after the shadow dir was
    // validated — reproduced empirically: deleting the original install's copy of
    // AlRunner.QueryJoin.dll out from under an already-built, already-"reusable" shadow
    // dir reliably reproduces the exact FileNotFoundException #2166 reports. A real,
    // independent copy at shadow-build time removes the dependency on origDir surviving.
    private static readonly string[] MustCopyNames =
    {
        "al-runner.dll", "al-runner.pdb", "al-runner.deps.json",
        "al-runner.runtimeconfig.json", "al-runner.runtimeconfig.dev.json",
        "AlRunner.QueryJoin.dll", "AlRunner.Provisioning.dll", "AlRunner.Provisioning.pdb",
    };

    private static bool MustBeRealCopy(string fileName) =>
        MustCopyNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when this install does not ship Ncl.dll beside the running assembly — i.e.
    /// <see cref="EnsureShadowDir"/> + a re-exec is required before any BC type can be
    /// touched. (A shadow child's own base directory always has the real file, so this
    /// is naturally false there — no separate "am I the child" flag needed.)
    /// </summary>
    public static bool NeedsShadow(string baseDirectory) =>
        !File.Exists(Path.Combine(baseDirectory, NclFileName));

    /// <summary>
    /// Builds (or reuses) the shadow directory mirroring <paramref name="origDir"/> and
    /// returns the absolute path to its <c>al-runner.dll</c> — the argument the caller
    /// should re-exec via <c>dotnet exec &lt;path&gt;</c>.
    ///
    /// <para><b><paramref name="entrySourceDir"/> — per-BC-minor engine variants
    /// (#2024 item 3 / #2027).</b> Null (the default) mirrors <paramref name="origDir"/>
    /// entirely, including its own entry assembly — today's behaviour, unchanged. When
    /// non-null (the caller selected a DIFFERENT shipped variant than the one currently
    /// running — see <see cref="EngineVariants"/>), the entry-assembly manifest set
    /// (<see cref="MustCopyNames"/>) is copied from THAT directory instead, while every
    /// other dependency DLL is still symlinked from <paramref name="origDir"/> — variants
    /// share the exact same dependency closure (same commit, same NuGet package
    /// versions; only the BC <c>&lt;Reference&gt;</c> set differs, and that's resolved
    /// per-request from the artifact cache, not CopyLocal'd — see
    /// <c>AlRunner.csproj</c>'s "Version-agnosticism" comment), so nothing needs
    /// duplicating beyond the entry assembly itself. The Ncl.dll pre-rewrite below is
    /// SKIPPED in this case: it would be keyed off THIS process's own
    /// <see cref="RunnerFingerprint.ContentHash"/>, not the swapped-in variant's — the
    /// child process (running as that variant) does its own first-start rewrite via the
    /// existing, unchanged <see cref="NclCecilRewrite.RewriteInPlace"/> call in
    /// Program.cs, correctly keyed to ITSELF. Skipping it here just avoids wasted work
    /// (and a wrongly-keyed cache write) rather than risking incorrect bytes either way —
    /// the rewrite transform of a given BC version's Ncl.dll is deterministic and
    /// identical across variants of the same commit, so at worst an unswapped pre-write
    /// would have been immediately overwritten by the child's own correctly-keyed one.
    /// </para>
    /// </summary>
    public static string EnsureShadowDir(string origDir, string bcServiceTierDir, string? entrySourceDir = null)
    {
        var origFull = Path.GetFullPath(origDir);
        var entrySource = entrySourceDir != null ? Path.GetFullPath(entrySourceDir) : null;

        // Keys off the SAME hash NclCecilRewrite's own ncl-cecil cache uses (source Ncl
        // bytes + this runner build's content hash + its CACHE_VERSION) so the two
        // caches invalidate together, plus a hash of origFull itself: what's cached
        // HERE is a set of symlinks to a specific PATH, not just content, so two
        // installs that happen to be byte-identical must not share a shadow dir — if
        // one install is later removed, the other would be left with dangling links.
        //
        // When swapping in a different variant (entrySource != null), the identity
        // component is the variant's OWN directory name (a stable per-build version
        // string — see EngineVariants) rather than RunnerFingerprint.ContentHash (which
        // would describe the CURRENTLY RUNNING process, not the variant being shadowed
        // in) — otherwise two different variant swaps requested by the same running
        // process would collide on one shadow dir. No nclBytes/contentKey needed in this
        // branch since the Ncl pre-rewrite is skipped (see the doc comment above).
        string key;
        if (entrySource != null)
        {
            var pathHash0 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origFull)))
                .ToLowerInvariant()[..16];
            key = $"variant-{Path.GetFileName(entrySource)}-{pathHash0}";
        }
        else
        {
            var nclSrc = Path.Combine(bcServiceTierDir, NclFileName);
            var nclBytes = File.ReadAllBytes(nclSrc);
            var contentKey = NclCecilRewrite.ComputeCacheKeyCore(nclBytes, RunnerFingerprint.ContentHash);
            var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origFull)))
                .ToLowerInvariant()[..16];
            key = $"{contentKey}-{pathHash}";
        }

        var shadowRoot = CacheRoots.Resolve("ncl-shadow");
        var shadowDir = Path.Combine(shadowRoot, key);
        var markerPath = Path.Combine(shadowDir, MarkerFileName);
        var shadowDll = Path.Combine(shadowDir, EntryDllName);
        var shadowNcl = Path.Combine(shadowDir, NclFileName);

        // AL_RUNNER_NCL_CACHE=0 (NclCecilRewrite's own escape hatch) means "always do a
        // fresh Cecil rewrite" — honour that here too rather than silently reusing a
        // stale shadow dir built before the flag was set.
        var forceFresh = Environment.GetEnvironmentVariable("AL_RUNNER_NCL_CACHE") == "0";

        var reusable = !forceFresh
            && File.Exists(markerPath)
            && File.ReadAllText(markerPath) == origFull
            && File.Exists(shadowDll)
            && File.Exists(shadowNcl);

        if (reusable)
        {
            Console.Error.WriteLine($"[Cecil] Reusing Ncl shadow runtime dir at {shadowDir}");
            return shadowDll;
        }

        Console.Error.WriteLine($"[Cecil] Building Ncl shadow runtime dir at {shadowDir}");

        // Build into a private temp dir, then Directory.Move (rename(2) — atomic on the
        // same filesystem) into place. AlRunner.Tests's own parallel test collections
        // proved this matters: several test classes spawn the real runner concurrently,
        // and since the shadow-dir key depends only on (runner content hash, Ncl bytes,
        // origFull) — not on anything per-process — two concurrent invocations against
        // the same install legitimately compute the SAME key and would otherwise both
        // delete-and-rebuild the SAME final directory at once, so one process's
        // File.Copy could read a file the other had just deleted mid-rebuild
        // (IOException: "being used by another process"). Building off to the side and
        // rename-ing into place means every reader only ever sees either nothing or a
        // fully-built directory, never a half-built one — same invariant AtomicReplace
        // already gives the single-file ncl-cecil cache.
        Directory.CreateDirectory(shadowRoot);
        var tempDir = Path.Combine(shadowRoot, $"{key}.building.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            MirrorInstallDirectory(origFull, tempDir, entrySource);

            // The one real file: Cecil-rewritten Ncl.dll, via the existing ncl-cecil
            // cache (populates it on MISS, reuses it on HIT — the same cache the child
            // process's own RewriteInPlace call reads once it starts from this dir).
            // Skipped when swapping in a different variant — see the entrySourceDir doc
            // comment on EnsureShadowDir above for why the child does this itself instead.
            if (entrySource == null)
                NclCecilRewrite.RewriteInPlace(bcServiceTierDir, Path.Combine(tempDir, NclFileName));

            // Marker goes in LAST, inside the temp dir, so the invariant "marker present
            // => fully built" survives the rename: nobody can observe a marker-bearing
            // shadowDir that isn't complete.
            File.WriteAllText(Path.Combine(tempDir, MarkerFileName), origFull);

            Directory.Move(tempDir, shadowDir);
        }
        catch (IOException) when (Directory.Exists(shadowDir))
        {
            // Lost the race: another process's Directory.Move landed first. Its
            // directory is complete by construction (only ever created via this same
            // rename-into-place path), and — same key — content-equivalent to what we
            // would have built. Discard our temp build rather than leave it orphaned.
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
        finally
        {
            // Belt-and-braces cleanup if something above threw for an unrelated reason
            // (e.g. RewriteInPlace failing) — don't leave a half-built temp dir behind.
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }

        PruneStaleShadowDirs(shadowRoot, shadowDir, keepNewest: 4);

        return shadowDll;
    }

    /// <summary>
    /// Symlinks <paramref name="target"/> to <paramref name="source"/> (near-zero cost —
    /// no data copied). Windows requires admin rights or Developer Mode to create
    /// symlinks; falls back to a real copy there (and on any other platform where link
    /// creation is refused) so the shadow dir still comes up correctly, just slower to
    /// build the first time.
    /// </summary>
    /// <summary>
    /// Mirrors every entry of <paramref name="origFull"/> into <paramref name="shadowDir"/>:
    /// the entry assembly and its deps/runtimeconfig manifests (<see cref="MustCopyNames"/>)
    /// as real, independent copies; everything else as a symlink (near-zero cost — these
    /// are typically dozens of large, numerous dependency DLLs). Internal and side-effect-
    /// isolated from the Ncl-specific/caching logic above so it's directly testable without
    /// needing real BC artifact bytes to Cecil-rewrite — see NclShadowRuntimeTests.
    /// </summary>
    /// <param name="origFull">The install directory to mirror (dependency closure,
    /// satellite resource dirs, Win32Stubs, …).</param>
    /// <param name="shadowDir">Destination.</param>
    /// <param name="entrySource">When non-null, the entry-assembly manifest set
    /// (<see cref="MustCopyNames"/>) is copied from HERE instead of
    /// <paramref name="origFull"/> — the per-BC-minor engine-variant swap (see the doc
    /// comment on <see cref="EnsureShadowDir"/>). A name missing from
    /// <paramref name="entrySource"/> (e.g. a variant built without a
    /// <c>.runtimeconfig.dev.json</c>, which only plain <c>dotnet build</c> produces)
    /// falls back to <paramref name="origFull"/>'s copy — harmless, since only
    /// <c>al-runner.dll</c> itself needs to be the swapped-in variant; the manifests
    /// alongside it (deps/runtimeconfig) are only consulted for correctness when
    /// they exist.</param>
    internal static void MirrorInstallDirectory(string origFull, string shadowDir, string? entrySource = null)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(origFull))
        {
            var name = Path.GetFileName(entry);
            var target = Path.Combine(shadowDir, name);
            if (MustBeRealCopy(name))
            {
                // Confirmed empirically: a SYMLINKED entry assembly makes CoreCLR report
                // AppContext.BaseDirectory as the symlink's TARGET directory (origFull),
                // not the directory the symlink itself lives in (shadowDir) — silently
                // defeating the entire point of this class (the "in-place" Cecil rewrite
                // further down Program.cs would then write Ncl.dll back into origFull,
                // the very directory check-nupkg-contents.sh asserts stays clean). The
                // entry assembly plus its deps/runtimeconfig manifests must be real,
                // independent files; every other (large, numerous) dependency DLL is
                // still fine as a symlink since nothing resolves BaseDirectory from them.
                var source = entrySource != null && File.Exists(Path.Combine(entrySource, name))
                    ? Path.Combine(entrySource, name)
                    : entry;
                File.Copy(source, target, overwrite: true);
            }
            else
            {
                LinkOrCopy(entry, target, isDirectory: Directory.Exists(entry));
            }
        }

        // A variant swap's entry assembly might carry a MustCopyNames file that
        // origFull's own enumeration never produced a `target` for (e.g. origFull is a
        // dev build lacking al-runner.pdb, but the variant has one) — cheap top-up pass.
        if (entrySource != null)
        {
            foreach (var name in MustCopyNames)
            {
                var source = Path.Combine(entrySource, name);
                var target = Path.Combine(shadowDir, name);
                if (File.Exists(source) && !File.Exists(target))
                    File.Copy(source, target, overwrite: true);
            }
        }
    }

    private static void LinkOrCopy(string source, string target, bool isDirectory)
    {
        try
        {
            if (isDirectory) Directory.CreateSymbolicLink(target, source);
            else File.CreateSymbolicLink(target, source);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // #2034 audit: a genuine unexpected condition during the shadow-dir build
            // (not routine per-method Cecil-rewrite noise), so it uses the `[reexec]`
            // tag — same reasoning as Program.cs's re-exec explanation lines.
            Console.Error.WriteLine($"[reexec] Symlink for '{Path.GetFileName(source)}' refused ({ex.GetType().Name}) — falling back to a real copy");
        }

        if (isDirectory) CopyDirectoryRecursive(source, target);
        else File.Copy(source, target, overwrite: true);
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
            CopyDirectoryRecursive(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    /// <summary>Mirrors NclCecilRewrite's own cache pruning — bounds how many stale
    /// shadow dirs (one per distinct runner-build + Ncl-version + install-path
    /// combination) accumulate under ncl-shadow/ across upgrades.</summary>
    private static void PruneStaleShadowDirs(string shadowRoot, string protectedDir, int keepNewest)
    {
        var protectedFull = Path.GetFullPath(protectedDir);
        List<string> stale;
        try
        {
            stale = Directory.EnumerateDirectories(shadowRoot)
                .Select(d => new DirectoryInfo(d))
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Skip(keepNewest)
                .Select(d => d.FullName)
                .Where(d => !string.Equals(d, protectedFull, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        foreach (var dir in stale)
        {
            // #2034 audit: same reasoning as the symlink-fallback WARN above — a real
            // failure during shadow-dir upkeep, not routine Cecil-rewrite diagnostics.
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException ex) { Console.Error.WriteLine($"[reexec] WARN: failed to prune stale shadow dir {dir}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Console.Error.WriteLine($"[reexec] WARN: failed to prune stale shadow dir {dir}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Locates the <c>dotnet</c> muxer this process is running under, without depending
    /// on PATH: <c>RuntimeEnvironment.GetRuntimeDirectory()</c> resolves to
    /// <c>&lt;dotnet-root&gt;/shared/Microsoft.NETCore.App/&lt;ver&gt;/</c> for a
    /// framework-dependent app (which is what this tool always is — see AlRunner.csproj,
    /// no RuntimeIdentifier); three directories up is the muxer's own install root.
    /// Falls back to DOTNET_ROOT / PATH for the rare host shape where that layout
    /// assumption doesn't hold.
    /// </summary>
    public static string FindDotnetMuxer()
    {
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";

        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var candidate = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", "..", exeName));
        if (File.Exists(candidate)) return candidate;

        foreach (var envVar in new[] { "DOTNET_ROOT", "DOTNET_ROOT(x86)" })
        {
            var root = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrEmpty(root)) continue;
            var fromEnv = Path.Combine(root, exeName);
            if (File.Exists(fromEnv)) return fromEnv;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        foreach (var dir in pathVar.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            var fromPath = Path.Combine(dir, exeName);
            if (File.Exists(fromPath)) return fromPath;
        }

        throw new InvalidOperationException(
            "Could not locate the 'dotnet' muxer needed to re-exec into the Ncl runtime " +
            "shadow directory (Microsoft.Dynamics.Nav.Ncl.dll is deliberately not shipped " +
            "in this package — see check-nupkg-contents.sh). Tried: this runtime's own " +
            "install layout, DOTNET_ROOT/DOTNET_ROOT(x86), and PATH. Set DOTNET_ROOT to " +
            "your .NET install directory and retry.");
    }
}
