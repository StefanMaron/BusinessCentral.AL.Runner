// BcCompilerPkgDedupRelativePathTests — pkgdedup staging must not produce dangling
// symlinks when the caller passes a RELATIVE --package-cache directory.
//
// Root cause being tested (issue #1652)
// --------------------------------------
// DeduplicateAppPackageDirs stages one symlink per deduplicated .app into a
// process-wide temp dir (al-runner-pkgdedup/<hash>/) so the reference loader scans
// a single, collision-free directory. It builds each symlink with:
//
//     File.CreateSymbolicLink(dst, src)
//
// .NET's File.CreateSymbolicLink treats `src` (pathToTarget) LITERALLY: if it is a
// relative path, the OS resolves it relative to the symlink's OWN directory
// (the temp stage dir), NOT relative to the process's current working directory.
// `src` here comes straight from `Directory.EnumerateFiles(dir, ...)` where `dir`
// is one of the caller-supplied package-cache directories — and those are used
// AS GIVEN on the CLI (ExpandPackageCacheDirs performs no Path.GetFullPath). A
// user who runs (exactly as in the issue repro):
//
//     al-runner app unit-tests --package-cache app/.alpackages \
//                               --package-cache unit-tests/.alpackages
//
// passes RELATIVE package-cache dirs. Once cross-dir duplication or self-exclusion
// forces the staging branch (always true once --auto-provision's test-toolkit dir
// is added on top of a project's own .alpackages), every symlink's target text is
// the same relative string (e.g. "app/.alpackages/Foo.app") — which the OS resolves
// against /tmp/al-runner-pkgdedup/<hash>/, a location that never contains an
// "app/.alpackages/" subtree. The symlink is dangling. BC's native package reader
// then reports the perfectly valid package as `AL1023 ... is not valid`, the
// resulting declaration errors cascade into `Compilation.Emit`, and the emitter
// crashes on unbound types (NavTypeKind.None) — the EMIT-ZERO failure from #1652.
//
// Test strategy
// -------------
// DeduplicateAppPackageDirs is pure filesystem/zip logic (no BC runtime needed),
// invoked here by reflection exactly like BcCompilerLoaderSelfExclusionTests. The
// test temporarily chdirs into a synthetic project root and passes package-cache
// dirs as RELATIVE strings (mirroring the CLI's ExpandPackageCacheDirs output for
// a relative --package-cache argument), forces the staging path via a cross-dir
// duplicate, then asserts every symlink placed in the returned stage dir actually
// resolves to a live, readable file — not a dangling link.
//
// RED before the fix: at least one staged entry is a broken symlink (its target
// does not exist), which is exactly the corruption that produces `AL1023` against
// a package that is otherwise perfectly valid. GREEN after the fix: every staged
// entry resolves and is openable as a zip.

using System.IO.Compression;
using System.Reflection;
using System.Text;
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

public sealed class BcCompilerPkgDedupRelativePathTests : IDisposable
{
    private readonly string _root;
    private readonly string _savedCwd;

    public BcCompilerPkgDedupRelativePathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-pkgdedup-relpath-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _savedCwd = Directory.GetCurrentDirectory();
    }

    public void Dispose()
    {
        try { Directory.SetCurrentDirectory(_savedCwd); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Two RELATIVE package-cache dirs, each holding a distinct, symbol-bearing
    /// .app — plus a byte-for-byte duplicate of one of them in the second dir (the
    /// same forcing condition ExpandPackageCacheDirs's own test-toolkit dir adds:
    /// a cross-dir AppId+Version collision) so the staging branch is exercised.
    /// Every .app the staging dir ends up pointing at must be reachable through
    /// its symlink from the CWD the compiler actually runs in.
    /// </summary>
    [Fact]
    public void RelativePackageCacheDirs_StagedSymlinks_ResolveToRealFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "app", ".alpackages"));
        Directory.CreateDirectory(Path.Combine(_root, "unit-tests", ".alpackages"));

        var appPkgAppId = "aaaaaaaa-1111-0000-0000-0000000000a1";
        var sharedAppId = "bbbbbbbb-2222-0000-0000-0000000000b2";

        WriteApp(Path.Combine(_root, "app", ".alpackages"), "AppPkg.app",
            appPkgAppId, "App Package", "Contoso", "1.0.0.0");
        WriteApp(Path.Combine(_root, "app", ".alpackages"), "Shared.app",
            sharedAppId, "Library Assert", "Microsoft", "28.1.0.0");
        // Same (AppId, Version) present again under the SECOND package-cache dir —
        // the exact cross-dir duplicate that forces the "changed" staging branch.
        WriteApp(Path.Combine(_root, "unit-tests", ".alpackages"), "Shared.app",
            sharedAppId, "Library Assert", "Microsoft", "28.1.0.0");

        Directory.SetCurrentDirectory(_root);

        var packageDirs = new List<string> { "app/.alpackages", "unit-tests/.alpackages" };
        var resultDirs = InvokeDeduplicateAppPackageDirs(packageDirs, excludeAppId: null);

        // The staging branch must actually have engaged (else this test is vacuous).
        Assert.NotEqual(packageDirs, resultDirs);

        var stagedApps = resultDirs
            .SelectMany(d => Directory.EnumerateFiles(d, "*.app", SearchOption.AllDirectories))
            .ToList();
        Assert.NotEmpty(stagedApps);

        foreach (var staged in stagedApps)
        {
            var info = new FileInfo(staged);
            Assert.True(
                (info.Attributes & FileAttributes.ReparsePoint) == 0 || File.Exists(staged),
                $"staged package '{staged}' is a dangling symlink (target does not resolve) — " +
                "this is the exact corruption BC's package reader reports as AL1023.");

            // Must be openable as a real zip-backed NAVX package — the same check
            // BC's native reader effectively performs before accepting the package.
            using var fs = File.OpenRead(staged);
            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            using var payload = new MemoryStream(bytes, 8, bytes.Length - 8);
            using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
            Assert.Contains(zip.Entries, e => e.FullName.Equals("NavxManifest.xml", StringComparison.OrdinalIgnoreCase));
        }
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

            // A real BC package always carries SymbolReference.json; the dedup filter
            // drops any .app lacking one before the relative-path bug ever gets a
            // chance to run, so this fixture must carry one too.
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
