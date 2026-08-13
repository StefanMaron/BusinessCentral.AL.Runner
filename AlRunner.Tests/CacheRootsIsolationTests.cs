// CacheRootsIsolationTests — proves `--cache <dir>` actually isolates the four caches
// that used to ignore it entirely (issue #1821): `workspace-deps` (this file, via a
// source-only AL dependency, which Program.cs's layered/source-dep synthesis converts
// into a synthetic .app under the `workspace-deps` cache) and `ncl-cecil` (this file,
// free-riding on the same two runs, since it's populated on every invocation
// unconditionally). `compiled-deps`/`bc-symbols` share the exact same
// `CacheRoots.Resolve` call site shape and are covered at the unit level in
// CacheRootsTests.cs.
//
// The bug being fixed here is specifically "a caller asking for an isolated cache
// silently gets the shared real one" — so a test that only checks the resolved PATH
// STRING is correct would pass against code that computes the right path and then
// performs I/O against the old hardcoded one anyway. The decisive proof has to be
// behavioural: run the real runner twice, with two DIFFERENT --cache dirs but
// IDENTICAL fixture content (so the workspace-deps cache KEY — a content hash of the
// dep's own sources/identity/deps — is identical both times), and confirm the second
// run is STILL a cache MISS ("WROTE", not "cache HIT"). If workspace-deps secretly
// still wrote to/read from the shared real `~/.cache/al-runner/workspace-deps`, run 2
// would incorrectly HIT (served by run 1's entry in the real, unscoped location)
// instead of MISSing against its own, never-before-seen isolated dir — exactly the
// shape of bug this issue reports. On top of that, both runs' isolated
// `workspace-deps/` subfolder must actually contain the synthesized `.app` on disk —
// proving bytes really landed in `<dir>`, not just that a path string was computed
// correctly — and the two runs' per-dep subfolder NAME (itself the cache key's own
// 12-char prefix) must be identical, confirming this really is the same cache entry
// independently (re)computed twice under two different roots, not two different
// entries by coincidence.
//
// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
// See DefineFlagIntegrationTests for why this used to be [Collection("server-serial")]
// and no longer is — #1809.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public class CacheRootsIsolationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string bundleDir, string alCacheDir, string absentPackageCache)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundleDir}\"");
        args.Append($" --cache \"{alCacheDir}\"");
        // Same zero-package-cache pin as SourceDepCacheEnumMetadataTests — forces the
        // real Tier-3 source-compile path for the dependency on every leg, CI included.
        args.Append($" --package-cache \"{absentPackageCache}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    [SkippableFact]
    public void TwoDifferentCacheDirs_SameContent_BothMissWorkspaceDeps_AndBothLandBytesInTheirOwnDir()
    {
        TestArtifacts.SkipIfMissing();

        var scratchRoot = Path.Combine(Path.GetTempPath(), "al-runner-cacheroots-isolation", Guid.NewGuid().ToString("N"));
        var depDir = Path.Combine(scratchRoot, "dep-app");
        var testsDir = Path.Combine(scratchRoot, "tests-app");
        var cacheDirA = Path.Combine(scratchRoot, "cache-a");
        var cacheDirB = Path.Combine(scratchRoot, "cache-b");
        var absentPackageCache = Path.Combine(scratchRoot, "no-such-package-cache");
        Directory.CreateDirectory(depDir);
        Directory.CreateDirectory(testsDir);

        // Fresh random identity: guarantees this dependency's workspace-deps cache key
        // has never been seen before by ANY prior run (real or isolated) on this
        // machine, so run 1 (cache dir A) is unconditionally a MISS. The identity and
        // ALL source content stay IDENTICAL between the two runs below — only the
        // --cache DIRECTORY differs — so the cache KEY is identical both times too. A
        // correct fix must still MISS on run 2 (different, never-before-seen dir);
        // a "computes the path but writes to the old shared location" bug would HIT.
        var depId = Guid.NewGuid();
        var testsId = Guid.NewGuid();

        File.WriteAllText(Path.Combine(depDir, "app.json"), $$"""
        {
          "id": "{{depId}}",
          "name": "Repro1821 Dep App",
          "publisher": "Repro1821",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 61920, "to": 61929 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(depDir, "Repro1821Dep.al"), """
        codeunit 61920 "Repro1821 Greeter"
        {
            procedure Greet(): Text
            begin
                exit('hello');
            end;
        }
        """);

        File.WriteAllText(Path.Combine(testsDir, "app.json"), $$"""
        {
          "id": "{{testsId}}",
          "name": "Repro1821 Tests",
          "publisher": "Repro1821",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{depId}}", "name": "Repro1821 Dep App", "publisher": "Repro1821", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 61930, "to": 61939 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(testsDir, "Repro1821Tests.al"), """
        codeunit 61930 "Repro1821 Poison Test"
        {
            Subtype = Test;

            [Test]
            procedure GreeterWorks()
            var
                Greeter: Codeunit "Repro1821 Greeter";
                Result: Text;
            begin
                Result := Greeter.Greet();
                if Result <> 'hello' then
                    Error('Expected hello, got ''%1''', Result);
            end;
        }
        """);

        // Run A: isolated cache dir A, never seen before anywhere -> MISS.
        var (outputA, exitA) = RunRunner(testsDir, cacheDirA, absentPackageCache);
        Assert.True(exitA == 0 && outputA.Contains("1P/0F/0E"), $"run A must pass:\n{outputA}");
        Assert.Contains("[source-dep] WROTE Repro1821 Dep App", outputA);
        Assert.DoesNotContain("[source-dep] cache HIT Repro1821 Dep App", outputA);

        // Positive claim: the bytes actually landed on disk under cache dir A's OWN
        // workspace-deps subfolder — not just that a path string was computed.
        var workspaceDepsA = Path.Combine(cacheDirA, "workspace-deps");
        Assert.True(Directory.Exists(workspaceDepsA), $"expected {workspaceDepsA} to exist after run A");
        var subDirsA = Directory.GetDirectories(workspaceDepsA);
        Assert.True(subDirsA.Length > 0, $"expected at least one per-dep subfolder under {workspaceDepsA}, found none");
        var appsA = Directory.GetFiles(workspaceDepsA, "*.app", SearchOption.AllDirectories);
        Assert.True(appsA.Length > 0, $"expected at least one synthesized .app under {workspaceDepsA}, found none");
        var keyA = Path.GetFileName(subDirsA[0]);

        // Run B: identical dep/tests content (same cache KEY), but a DIFFERENT,
        // equally-never-before-seen --cache dir. The decisive assertion: this must
        // STILL be a MISS. A bug that resolves the isolated path correctly but
        // performs I/O against the old hardcoded ~/.cache/al-runner/workspace-deps
        // would instead HIT here, served by run A's entry in that shared location.
        var (outputB, exitB) = RunRunner(testsDir, cacheDirB, absentPackageCache);
        Assert.True(exitB == 0 && outputB.Contains("1P/0F/0E"), $"run B must pass:\n{outputB}");
        Assert.Contains("[source-dep] WROTE Repro1821 Dep App", outputB);
        Assert.True(!outputB.Contains("[source-dep] cache HIT Repro1821 Dep App"),
            $"run B (a DIFFERENT, equally fresh --cache dir) must ALSO be a workspace-deps MISS — " +
            $"a HIT here means workspace-deps is still reading/writing the shared real cache " +
            $"instead of the isolated --cache dir:\n{outputB}");

        var workspaceDepsB = Path.Combine(cacheDirB, "workspace-deps");
        Assert.True(Directory.Exists(workspaceDepsB), $"expected {workspaceDepsB} to exist after run B");
        var subDirsB = Directory.GetDirectories(workspaceDepsB);
        Assert.True(subDirsB.Length > 0, $"expected at least one per-dep subfolder under {workspaceDepsB}, found none");
        var appsB = Directory.GetFiles(workspaceDepsB, "*.app", SearchOption.AllDirectories);
        Assert.True(appsB.Length > 0, $"expected at least one synthesized .app under {workspaceDepsB}, found none");
        var keyB = Path.GetFileName(subDirsB[0]);

        // Same content -> same key -> both runs' per-dep subfolder NAME (the cache
        // key's own prefix) is identical, confirming this really is the same cache
        // entry being independently (re)computed twice, not two different entries by
        // coincidence.
        Assert.Equal(keyA, keyB);

        // Free ride on the same two subprocess runs above: ncl-cecil is populated on
        // EVERY invocation unconditionally (the Cecil rewrite runs before any AL is
        // even loaded), so both runs' isolated ncl-cecil/ subfolders must each contain
        // the rewritten Ncl.dll too — same "bytes actually landed in <dir>" proof,
        // for the second cache this issue fixes, at zero extra process-spawn cost.
        var nclCecilA = Path.Combine(cacheDirA, "ncl-cecil");
        Assert.True(Directory.Exists(nclCecilA) && Directory.GetFiles(nclCecilA, "*.dll").Length > 0,
            $"expected at least one ncl-cecil .dll under {nclCecilA} after run A");
        var nclCecilB = Path.Combine(cacheDirB, "ncl-cecil");
        Assert.True(Directory.Exists(nclCecilB) && Directory.GetFiles(nclCecilB, "*.dll").Length > 0,
            $"expected at least one ncl-cecil .dll under {nclCecilB} after run B");
    }
}
