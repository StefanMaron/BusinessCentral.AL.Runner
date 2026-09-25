// PackageDirMemoRenewalTests — issue #2218, the Program.cs half of the package-directory memo.
//
// SafeDirectoryScanRunMemoTests proves a fresh scope walks afresh. This proves Program.cs opens
// one per --server request and per --watch cycle: a bundle whose .alpackages/.deps-bin do not
// exist yet fails to resolve its dependency, the user adds them, and the NEXT request / cycle of
// the same process must find them. A memo that outlived its run would keep answering "none".
//
// Fixture: tests/runner-extras/precompiled-tableext-keys, whose only dependency is served by its
// own .alpackages symbol package and .deps-bin DLL.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class PackageDirMemoRenewalTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureRoot =
        Path.Combine(RepoRoot, "tests", "runner-extras", "precompiled-tableext-keys");
    private static readonly string[] PackageDirs = { ".alpackages", ".deps-bin" };
    private const string TestsFile = "RxkTests.Codeunit.al";
    private const int FixtureTestCount = 6;

    /// <summary>The fixture's AL and manifest, WITHOUT the directories that serve its dependency.</summary>
    private static string CopyBundleWithoutPackages(string scratchName)
    {
        var dir = Path.Combine(TestScratch.Dir(scratchName), "bundle");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(FixtureRoot))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        foreach (var name in PackageDirs)
            Assert.False(Directory.Exists(Path.Combine(dir, name)));
        return dir;
    }

    private static void AddPackages(string dir)
    {
        foreach (var name in PackageDirs)
        {
            var dst = Path.Combine(dir, name);
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(Path.Combine(FixtureRoot, name)))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        }
    }

    private static string[] PackageCacheArgs()
    {
        var platformApps = TestArtifacts.PlatformAppsDir();
        return Directory.Exists(platformApps) ? new[] { "--package-cache", platformApps } : Array.Empty<string>();
    }

    [SkippableFact]
    public async Task Server_PackagesAddedBetweenRequests_AreFoundByTheNextRequest()
    {
        TestArtifacts.SkipIfMissing();

        var dir = CopyBundleWithoutPackages("al-runner-pkgdir-memo-server");
        var cacheDir = TestScratch.Dir("al-runner-pkgdir-memo-server-cache");
        await using var server = await CliServer.StartAsync(
            new[] { "--cache", cacheDir }.Concat(PackageCacheArgs()));

        var req = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { dir },
            packagePaths = Array.Empty<string>(),
        });

        var (_, first) = ProtocolV2Streaming.Split(
            await server.SendRequestStreamingAsync(req, TimeSpan.FromSeconds(300)));
        // Precondition: without its package directories the dependency cannot resolve, so the
        // second request passing is attributable to the directories added in between.
        Assert.True(first.GetProperty("passed").GetInt32() == 0,
            $"the first request passed without the dependency's packages: {first}\n{server.StdErr}");

        AddPackages(dir);

        var (_, second) = ProtocolV2Streaming.Split(
            await server.SendRequestStreamingAsync(req, TimeSpan.FromSeconds(300)));
        Assert.True(second.GetProperty("passed").GetInt32() == FixtureTestCount,
            $"the second request did not find the .alpackages/.deps-bin added after the first: {second}\n{server.StdErr}");
        Assert.Equal(0, second.GetProperty("failed").GetInt32());
        Assert.Equal(0, second.GetProperty("errors").GetInt32());
    }

    /// <summary>
    /// A watch session cannot START without the dependency (the startup pre-pass exits), so the
    /// behavioural shape above is unavailable here. The phase log's process row carries the
    /// observable instead: cycle 2 must re-walk the bundle root, which a memo leaked from cycle 1
    /// would answer from cache. SIGTERM, not Kill, so ProcessExit writes the row.
    /// </summary>
    [SkippableFact]
    public async Task Watch_SecondCycle_WalksThePackageDirectoriesAgain()
    {
        TestArtifacts.SkipIfMissing();
        Skip.If(!OperatingSystem.IsLinux(), "reads child pids from /proc and stops the watcher with SIGTERM");

        var dir = CopyBundleWithoutPackages("al-runner-pkgdir-memo-watch");
        AddPackages(dir);
        var cacheDir = TestScratch.Dir("al-runner-pkgdir-memo-watch-cache");
        var logPath = Path.Combine(TestScratch.Dir("al-runner-pkgdir-memo-watch-log"), "phases.jsonl");
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
            + $" \"{dir}\" --watch --cache \"{cacheDir}\"");
        foreach (var a in PackageCacheArgs()) args.Append($" \"{a}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        psi.EnvironmentVariables["AL_RUNNER_PHASE_LOG"] = logPath;
        var lines = new List<CapturedLine>();
        using var p = Process.Start(psi)!;
        void Pump(StreamReader r, OutputStream stream) => Task.Run(async () =>
        {
            string? l;
            while ((l = await r.ReadLineAsync()) != null) lock (lines) lines.Add(new CapturedLine(stream, l));
        });
        Pump(p.StandardOutput, OutputStream.Stdout);
        Pump(p.StandardError, OutputStream.Stderr);
        string DumpAll() { lock (lines) return string.Join("\n", lines.Select(l => $"[{l.Stream}] {l.Text}")); }

        async Task<int> WaitForMarkerAfter(int fromIndex)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(300);
            while (DateTime.UtcNow < deadline)
            {
                List<int> found;
                lock (lines)
                    found = WatchOutputSlicing.FindStdoutMarkerIndices(
                        lines, WatchOutputSlicing.WaitingForSourceMarker, fromIndex);
                if (found.Count > 0) return found[0];
                if (p.HasExited) break;
                await Task.Delay(200);
            }
            throw new TimeoutException($"watch marker not seen.\n--- output ---\n{DumpAll()}");
        }
        int PassCount(int from, int to)
        {
            string window;
            lock (lines) window = WatchOutputSlicing.MergedJoin(lines, from, to);
            return window.Split('\n').Count(l => l.TrimStart().StartsWith("PASS ", StringComparison.Ordinal));
        }

        try
        {
            var m1 = await WaitForMarkerAfter(0);
            Assert.True(PassCount(0, m1) == FixtureTestCount, $"cycle 1 did not run the fixture.\n{DumpAll()}");

            await File.AppendAllTextAsync(Path.Combine(dir, TestsFile), "\n// edit: trigger the next watch cycle\n");
            var m2 = await WaitForMarkerAfter(m1 + 1);
            Assert.True(PassCount(m1 + 1, m2) == FixtureTestCount, $"cycle 2 did not run the fixture.\n{DumpAll()}");

            // The re-exec child does the work; its parent only waits on it.
            foreach (var pid in DescendantPids(p.Id).Append(p.Id))
                Process.Start("kill", $"-TERM {pid}")!.WaitForExit();
            Assert.True(p.WaitForExit(60_000), $"the watcher did not exit on SIGTERM.\n{DumpAll()}");
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }

        var row = File.ReadAllLines(logPath)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Single(r => r.GetProperty("kind").GetString() == "process");
        Assert.True(row.GetProperty("package_dir_walks").GetInt32() > 0, $"no walk recorded: {row}");
        Assert.True(row.GetProperty("package_dir_repeat_walks").GetInt32() > 0,
            $"cycle 2 answered the package-directory searches from cycle 1's memo: {row}");
    }

    private static IEnumerable<int> DescendantPids(int pid)
    {
        var taskDir = $"/proc/{pid}/task";
        if (!Directory.Exists(taskDir)) yield break;
        foreach (var t in Directory.GetDirectories(taskDir))
        {
            var childrenFile = Path.Combine(t, "children");
            if (!File.Exists(childrenFile)) continue;
            foreach (var tok in File.ReadAllText(childrenFile).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var child = int.Parse(tok);
                foreach (var d in DescendantPids(child)) yield return d;
                yield return child;
            }
        }
    }
}
