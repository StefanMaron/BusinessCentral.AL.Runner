// Issue #2107 — Program.cs prints "package caches (requested): N dir(s)" for the
// explicit/default set BEFORE three later additions fold into packageCacheDirs
// (extraProvisionSearchDirs, platformAppsOut, testAppsOut), so that count is never the
// set dependency resolution actually searches a moment later. Before the fix the line
// carried no "(requested)" qualifier at all, so it read as the whole story: it printed
// "package caches: 0 dir(s)" on a machine where resolution went on to search several
// directories, because a prior --auto-provision/`provision` run for the exact same BC
// build had left runner-owned `<artifacts>/<version>/{platform-apps,test-apps}` on disk,
// and Program.cs folds those in right after printing the count. That is what made #2067
// slow to diagnose.
//
// This test forces that fold deterministically and asserts the run also reports a SECOND,
// later line naming the TRUE final count under its own "(final search set)" label — not
// just that some number gets printed (the pre-fix code already did that; it was simply
// the wrong number, unlabeled, at the wrong time).
//
// Uses --artifact-path (not --bc-version) so the engine/service-tier directory stays
// pinned at the REAL, already-provisioned machine path — BcArtifacts.ServiceTierDir takes
// the literal explicit-root branch there, independent of $HOME — while
// BcArtifacts.ArtifactsRootDir (used ONLY to compute the runnerOwnedPlatformAppsDir /
// runnerOwnedTestAppsDir fold-in candidates in Program.cs) resolves under the isolated
// $HOME this test controls. That lets the fold be forced or withheld on demand without
// ever touching this machine's real ~/.local/share/al-runner/artifacts.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class PackageCacheFinalSearchSetTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    /// <summary>
    /// A dep/tests pair that declares nothing Microsoft (mirrors
    /// SourceDepSymbolsWithoutPackageCacheTests's fixture) — so whatever the fold adds to
    /// packageCacheDirs is visible only in the SEARCH SET this test asserts on, never in a
    /// provisioning decision (which would otherwise try to download real platform/test
    /// apps and make the test depend on network access).
    ///
    /// Issue #2205: "nothing Microsoft" now has to mean it literally. These manifests used
    /// to carry `"application"`/`"platform"`, from which ReadDependencies synthesises
    /// implicit Microsoft/Application + Microsoft/System roots — a real Microsoft
    /// dependency the need detection had simply never looked at. Once it does, this fixture
    /// triggers a genuine 116 MB platform-apps download mid-test, which folds another dir
    /// into packageCacheDirs and makes the final count neither 0 nor 2. Dropping the two
    /// fields is what the comment above always claimed; the AL here uses no Microsoft type,
    /// so nothing else changes.
    /// </summary>
    private static string WriteFixture(string scratchRoot)
    {
        var depDir = Path.Combine(scratchRoot, "dep-app");
        var testsDir = Path.Combine(scratchRoot, "tests-app");
        Directory.CreateDirectory(depDir);
        Directory.CreateDirectory(testsDir);
        var depId = Guid.NewGuid();
        var testsId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(depDir, "app.json"), $$"""
        {
          "id": "{{depId}}",
          "name": "Repro2107 Dep App",
          "publisher": "Repro2107",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 61950, "to": 61959 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(depDir, "Repro2107Dep.al"), """
        codeunit 61950 "Repro2107 Service"
        {
            procedure Echo(): Text
            begin
                exit('ok');
            end;
        }
        """);
        File.WriteAllText(Path.Combine(testsDir, "app.json"), $$"""
        {
          "id": "{{testsId}}",
          "name": "Repro2107 Tests",
          "publisher": "Repro2107",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{depId}}", "name": "Repro2107 Dep App", "publisher": "Repro2107", "version": "1.0.0.0" }
          ],
          "idRanges": [ { "from": 61960, "to": 61969 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(testsDir, "Repro2107Tests.al"), """
        codeunit 61960 "Repro2107 Tests"
        {
            Subtype = Test;

            [Test]
            procedure DepResolves()
            var
                Svc: Codeunit "Repro2107 Service";
            begin
                if Svc.Echo() <> 'ok' then
                    Error('dep did not resolve');
            end;
        }
        """);
        return testsDir;
    }

    private static string RealServiceTierDir()
    {
        var version = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion()
            ?? throw new InvalidOperationException(
                "EngineBuiltVersion() unavailable — cannot locate a real artifact dir to pin --artifact-path at.");
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? throw new InvalidOperationException("HOME not set on this machine.");
        return Path.Combine(TestArtifacts.StandardCacheDir(home), version.ToString());
    }

    /// <summary>
    /// Issue #3037: three patch directories of the selected major.minor, each holding its
    /// own copy of the same two packages at that patch's version — the shape a developer box
    /// reaches after provisioning a few BC patches. Nothing Microsoft, for the same reason
    /// <see cref="WriteFixture"/>'s manifests declare nothing Microsoft: need detection must
    /// stay out of this, or the test acquires a 116 MB download.
    /// </summary>
    private static IReadOnlyList<string> DuplicatePatchVersions()
    {
        var engine = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion()
            ?? throw new InvalidOperationException("EngineBuiltVersion() unavailable.");
        // Same major.minor as the selected engine (that is what CollectRunnerOwnedProvisionDirs
        // matches on), and deliberately NOT the engine's own patch, so this exercises the
        // sibling-patch path on every BC leg of the matrix rather than only on 28.1.
        return new[] { $"{engine.Major}.{engine.Minor}.0.1", $"{engine.Major}.{engine.Minor}.0.2", $"{engine.Major}.{engine.Minor}.0.3" };
    }

    private static void WriteDuplicatePatchDirs(string isolatedHome)
    {
        var artifactsRoot = TestArtifacts.StandardCacheDir(isolatedHome);
        foreach (var version in DuplicatePatchVersions())
        {
            foreach (var kind in new[] { "platform-apps", "test-apps" })
            {
                var dir = Path.Combine(artifactsRoot, version, kind);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(
                    Path.Combine(dir, $"Repro3037_{kind} Filler_{version}.app"),
                    MinimalNavxApp(Guid.NewGuid().ToString(), $"{kind} Filler", "Repro3037", version));
            }
        }
    }

    /// <summary>A minimal NAVX .app — NAVX header, then a ZIP holding only NavxManifest.xml.</summary>
    private static byte[] MinimalNavxApp(string appId, string name, string publisher, string version)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"/>
            </Package>
            """;
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
            ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("NavxManifest.xml");
            using var es = entry.Open();
            es.Write(Encoding.UTF8.GetBytes(xml));
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        return result;
    }

    private static string RunAgainstIsolatedHome(
        string testsDir, string alCacheDir, string isolatedHome, bool foldRunnerOwnedDirs)
    {
        var realServiceTierDir = RealServiceTierDir();
        TestArtifacts.SkipIf(!Directory.Exists(realServiceTierDir),
            $"real BC service-tier dir not provisioned at '{realServiceTierDir}' " +
            "(needed so --artifact-path can pin the engine while $HOME is isolated).");

        if (foldRunnerOwnedDirs)
        {
            var version = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion()!.ToString();
            var artifactsRoot = Path.Combine(TestArtifacts.StandardCacheDir(isolatedHome), version);
            Directory.CreateDirectory(Path.Combine(artifactsRoot, "platform-apps"));
            Directory.CreateDirectory(Path.Combine(artifactsRoot, "test-apps"));
        }

        var absentPackageCache = Path.Combine(isolatedHome, "no-such-package-cache");
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append($" --artifact-path \"{realServiceTierDir}\"");
        args.Append($" \"{testsDir}\"");
        args.Append($" --cache \"{alCacheDir}\"");
        // Deliberately NEVER created: ExpandPackageCacheDirs drops non-existent dirs, so
        // this takes the explicit-arg branch and resolves to the EMPTY set — the same
        // "package caches (requested): 0 dir(s)" precondition
        // SourceDepSymbolsWithoutPackageCacheTests relies on.
        args.Append($" --package-cache \"{absentPackageCache}\"");
        // Issue #2239: both "package caches" lines this class asserts on moved behind
        // --verbose (diagnostic detail, not a test result) — pass it explicitly so this
        // test still observes what it is actually testing.
        args.Append(" --verbose");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        // Isolates BcArtifacts.ArtifactsRootDir (and therefore the fold-in candidates
        // Program.cs computes from it) from this machine's real provisioning history,
        // WITHOUT touching BcArtifacts.ServiceTierDir — that stays pinned at the literal
        // --artifact-path above regardless of $HOME.
        psi.Environment["HOME"] = isolatedHome;

        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        string output;
        lock (sb) output = sb.ToString();

        Assert.True(p.ExitCode == 0 && output.Contains("1P/0F/0E"),
            $"fixture (nothing Microsoft, one dep, one passing test) must compile and run:\n{output}");
        return output;
    }

    /// <summary>
    /// RED (pre-fix): the ONLY "package caches" line prints the pre-fold count (0, and
    /// unlabeled — no "(requested)" qualifier existed yet), taken before Program.cs adds
    /// the two runner-owned dirs this test pre-creates — the exact #2067 shape. GREEN: the
    /// "(requested)" line still reads 0, and a second, later "(final search set)" line
    /// reports the true final count (2).
    /// </summary>
    [SkippableFact]
    public void FinalSearchSet_ReflectsRunnerOwnedFoldIn()
    {
        TestArtifacts.SkipIfMissing();
        var scratchRoot = TestScratch.Dir("al-runner-pkgcache-final");
        var testsDir = WriteFixture(scratchRoot);
        var isolatedHome = Path.Combine(scratchRoot, "home");
        Directory.CreateDirectory(isolatedHome);
        var alCacheDir = Path.Combine(scratchRoot, "al-out");
        try
        {
            var output = RunAgainstIsolatedHome(testsDir, alCacheDir, isolatedHome, foldRunnerOwnedDirs: true);

            Assert.Contains("package caches (requested): 0 dir(s)", output);
            Assert.Matches(new Regex(@"package caches \(final search set\): 2 dir\(s\)"), output);
        }
        finally
        {
            try { Directory.Delete(scratchRoot, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Issue #3037, end to end: three provisioned patch directories of the selected
    /// major.minor, each holding a duplicate of the same two packages. Before the fix the
    /// final search set was all SIX runner-owned directories and dependency resolution read
    /// a manifest — and therefore a content hash — for every package in every one of them.
    /// It is now the newest patch's two directories, and the bundle still compiles and its
    /// test still passes, which is the half that matters: a narrower search set that stopped
    /// resolving the closure would be a regression, not an optimisation.
    /// </summary>
    [SkippableFact]
    public void FinalSearchSet_DuplicatePatchDirs_FoldsInOnlyTheNewest()
    {
        TestArtifacts.SkipIfMissing();
        var scratchRoot = TestScratch.Dir("al-runner-pkgcache-3037");
        var testsDir = WriteFixture(scratchRoot);
        var isolatedHome = Path.Combine(scratchRoot, "home");
        Directory.CreateDirectory(isolatedHome);
        WriteDuplicatePatchDirs(isolatedHome);
        var alCacheDir = Path.Combine(scratchRoot, "al-out");
        try
        {
            // foldRunnerOwnedDirs: false — WriteDuplicatePatchDirs has already created the
            // runner-owned dirs this case is about, under patch versions of its own.
            var output = RunAgainstIsolatedHome(testsDir, alCacheDir, isolatedHome, foldRunnerOwnedDirs: false);

            Assert.Contains("package caches (requested): 0 dir(s)", output);
            Assert.Contains("package caches (final search set): 2 dir(s)", output);
            var versions = DuplicatePatchVersions();
            var newest = versions[^1];
            Assert.Contains(Path.Combine(TestArtifacts.StandardCacheDir(isolatedHome), newest, "platform-apps"), output);
            Assert.Contains(Path.Combine(TestArtifacts.StandardCacheDir(isolatedHome), newest, "test-apps"), output);
            // The two older patches are no longer in the search set at all.
            foreach (var older in versions.Take(versions.Count - 1))
                Assert.DoesNotContain(Path.Combine(TestArtifacts.StandardCacheDir(isolatedHome), older), output);
        }
        finally
        {
            try { Directory.Delete(scratchRoot, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The plain-path guard: when nothing runner-owned exists to fold in, the "(final
    /// search set)" line must report the SAME count as the "(requested)" line — the fix
    /// must not distort the common case (no --auto-provision history for this BC build)
    /// by inventing a phantom addition.
    /// </summary>
    [SkippableFact]
    public void FinalSearchSet_UnchangedWhenNothingFolds()
    {
        TestArtifacts.SkipIfMissing();
        var scratchRoot = TestScratch.Dir("al-runner-pkgcache-final-plain");
        var testsDir = WriteFixture(scratchRoot);
        var isolatedHome = Path.Combine(scratchRoot, "home");
        Directory.CreateDirectory(isolatedHome);
        var alCacheDir = Path.Combine(scratchRoot, "al-out");
        try
        {
            var output = RunAgainstIsolatedHome(testsDir, alCacheDir, isolatedHome, foldRunnerOwnedDirs: false);

            Assert.Contains("package caches (requested): 0 dir(s)", output);
            Assert.Contains("package caches (final search set): 0 dir(s)", output);
        }
        finally
        {
            try { Directory.Delete(scratchRoot, recursive: true); } catch { }
        }
    }
}
