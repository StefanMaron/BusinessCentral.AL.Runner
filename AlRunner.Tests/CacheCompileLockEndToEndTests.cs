// CacheCompileLockEndToEndTests — #5238: two runner processes on one COLD cache compile each
// cache key once between them, not once each.
//
// CLAIM: the AL-output cache and the compiled-deps cache both read "entry complete? else compile
// and publish", with nothing between the read and the publish. `--jobs` workers sharing a bundle
// (#5130) start together, so all of them missed together and all of them compiled: SMB on
// `--jobs 2` against a fresh cache took 418 s of CPU against 190 s for one process, at the same
// wall time (docs/jobs-unit-claiming.md § Memory). Two plain runner processes started together
// on one cache are the same race without the fan-out around it, which is what this runs.
//
// What is counted is the runner's own `[deps] source-cache WROTE` / `[cache] MISS` lines, one
// per compile that actually ran. The two processes' outputs are summed: with the gate one of them
// compiles and the other takes a HIT, without it both compile.
//
// The fixture is a source-only dependency package (the shape DependencyEmitExclusion... uses,
// NAVX header + zip, no SymbolReference) and a one-test bundle. A fresh --cache root per run:
// a warm entry would skip the compile path under test and read as a pass.

using Xunit;

namespace AlRunner.Tests;

public sealed class CacheCompileLockEndToEndTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string DepAppId = "c7d8e9fa-1b2c-4d3e-8f4a-5b6c7d8e9f01";
    private const string DepName = "CCL Source Dep";
    private const string DepPublisher = "AL Runner Fixtures";
    private const string DepVersion = "1.0.0.0";

    private const string DepAl = """
        codeunit 70980 "CCL Dep Math"
        {
            procedure Add(A: Integer; B: Integer): Integer
            begin
                exit(A + B);
            end;
        }
        """;

    /// <summary>Enough test codeunits that the bundle's own Emit+Compile takes seconds, not
    /// milliseconds: the second process must still be at its bundle key check while the first is
    /// compiling, or a missing gate reads as a pass.</summary>
    private const int BundleCodeunits = 40;

    private static string MainTestAl(int i, int sleepMs) => $$"""
        codeunit {{70900 + i}} "CCL Main Tests {{i}}"
        {
            Subtype = Test;

            [Test]
            procedure Arithmetic_Works()
            var
                Total: Integer;
                n: Integer;
            begin
                {{(sleepMs > 0 ? $"Sleep({sleepMs});" : "")}}
                for n := 1 to 3 do
                    Total += n;
                if Total <> 6 then
                    Error('arithmetic must work');
            end;

            [Test]
            procedure Text_Works()
            var
                Joined: Text;
            begin
                Joined := 'a' + 'b';
                if Joined <> 'ab' then
                    Error('text must work');
            end;
        }
        """;

    /// <param name="testSleepMs">Makes each test take that long, so a bundle's tests outlast the
    /// compile phase of the worker that started them.</param>
    private static string BuildFixture(string root, int testSleepMs = 0)
    {
        var main = Path.Combine(root, "main");
        var packages = Path.Combine(main, ".alpackages");
        Directory.CreateDirectory(packages);
        for (var i = 0; i < BundleCodeunits; i++)
            File.WriteAllText(Path.Combine(main, $"CclMainTests{i}.Codeunit.al"), MainTestAl(i, testSleepMs));
        File.WriteAllText(Path.Combine(main, "app.json"), $$"""
            {
              "id": "d2e3f4a5-6b7c-4809-9a1b-2c3d4e5f6071",
              "name": "Runner Tests Fixture - Cache Compile Lock",
              "publisher": "AL Runner",
              "version": "1.0.0.0",
              "dependencies": [
                { "id": "{{DepAppId}}", "name": "{{DepName}}",
                  "publisher": "{{DepPublisher}}", "version": "{{DepVersion}}" }
              ],
              "idRanges": [ { "from": 70900, "to": 70979 } ],
              "runtime": "17.0",
              "target": "Cloud",
              "features": []
            }
            """);
        File.WriteAllBytes(Path.Combine(packages, "AL_Runner_Fixtures_CCL_Source_Dep_1.0.0.0.app"), BuildSourceOnlyApp());
        return main;
    }

    /// <summary>NAVX container (magic, header length, version, app id, payload length, magic) over
    /// a zip of the manifest and one .al file; no SymbolReference.json, which is what sends the
    /// loader down the source-compile tier.</summary>
    private static byte[] BuildSourceOnlyApp()
    {
        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{DepAppId}" Name="{DepName}" Publisher="{DepPublisher}" Version="{DepVersion}" ShowMyCode="true" />
              <Dependencies />
            </Package>
            """;
        using var zipBuffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
                   zipBuffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
            Add("NavxManifest.xml", manifest);
            Add("src/DepMath.Codeunit.al", DepAl);
        }
        var payload = zipBuffer.ToArray();
        using var app = new MemoryStream();
        using var bw = new BinaryWriter(app);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("NAVX"));
        bw.Write(40);
        bw.Write(2);
        bw.Write(Guid.Parse(DepAppId).ToByteArray());
        bw.Write((long)payload.Length);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("NAVX"));
        bw.Write(payload);
        bw.Flush();
        return app.ToArray();
    }

    private sealed class Spawned
    {
        public required System.Diagnostics.Process Process { get; init; }
        public required System.Text.StringBuilder Output { get; init; }
        public required System.Text.StringBuilder Stdout { get; init; }
        public required System.Text.StringBuilder Stderr { get; init; }
    }

    private static Spawned Start(string bundlePath, string cacheDir,
        string extraArgs = "", IReadOnlyDictionary<string, string>? env = null)
    {
        var args = new System.Text.StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --verbose");
        args.Append(extraArgs);
        args.Append($" --cache \"{cacheDir}\"");
        args.Append($" \"{bundlePath}\"");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        foreach (var kv in env ?? new Dictionary<string, string>()) psi.Environment[kv.Key] = kv.Value;
        var sb = new System.Text.StringBuilder();
        var so = new System.Text.StringBuilder();
        var se = new System.Text.StringBuilder();
        var p = System.Diagnostics.Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) { sb.AppendLine(e.Data); so.AppendLine(e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) { sb.AppendLine(e.Data); se.AppendLine(e.Data); } };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return new Spawned { Process = p, Output = sb, Stdout = so, Stderr = se };
    }

    private static (string Output, int Exit) Finish(Spawned s)
    {
        if (!s.Process.WaitForExit(300_000))
        {
            try { s.Process.Kill(true); } catch { }
            throw new TimeoutException("runner hung");
        }
        s.Process.WaitForExit();
        lock (s.Output) return (s.Output.ToString(), s.Process.ExitCode);
    }

    private static int Count(string haystack, string needle)
        => haystack.Split(needle).Length - 1;

    /// <summary>
    /// The proving test, over both gates at once: the dependency is source-compiled through
    /// DependencyLoader's cache, the bundle through Program's AL-output cache. Before the gate each
    /// process printed its own WROTE and its own MISS; after it one does and the other prints HIT.
    /// Both runs still pass their test — the gate changes who compiles, never what runs.
    /// </summary>
    [SkippableFact]
    public void TwoProcessesStartedTogetherOnAColdCache_CompileEachKeyOnce()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.FlatDir("al-runner-cache-lock-");
        Directory.CreateDirectory(root);
        try
        {
            var bundle = BuildFixture(root);
            var cacheDir = Path.Combine(root, "cache");
            Directory.CreateDirectory(cacheDir);

            var a = Start(bundle, cacheDir);
            var b = Start(bundle, cacheDir);
            var (outA, exitA) = Finish(a);
            var (outB, exitB) = Finish(b);
            var both = outA + "\n" + outB;

            Assert.True(exitA == 0, $"process A exited {exitA}:\n{outA}");
            Assert.True(exitB == 0, $"process B exited {exitB}:\n{outB}");
            // Every test of the bundle ran in each process: the gate changes who compiles.
            var total = 2 * BundleCodeunits;
            Assert.Equal(1, Count(outA, $"Tests: {total}   passed {total}"));
            Assert.Equal(1, Count(outB, $"Tests: {total}   passed {total}"));

            // The dependency: compiled and published by one process, replayed by the other.
            var depWrote = Count(both, $"[deps] source-cache WROTE: {DepName}");
            var depHit = Count(both, $"[deps] source-cache HIT: {DepName}");
            Assert.True(depWrote == 1 && depHit == 1,
                $"dependency: expected 1 compile + 1 HIT across both processes, saw {depWrote} compile(s) + {depHit} HIT(s):\n{both}");

            // The bundle: one Emit+Compile, one HIT.
            var bundleMiss = Count(both, "[cache] MISS key=");
            var bundleHit = Count(both, "[cache] HIT  key=");
            Assert.True(bundleMiss == 1 && bundleHit == 1,
                $"bundle: expected 1 compile + 1 HIT across both processes, saw {bundleMiss} MISS(es) + {bundleHit} HIT(s):\n{both}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The same fixture through `--jobs 2`, where the two workers SHARE the bundle: one of them
    /// compiles everything and the other compiles nothing, and both still run tests. Per-key locks
    /// alone leave the first to chance (the workers race for each key, so one may compile the
    /// dependency and the other the bundle, and both then carry the compiler's footprint), which is
    /// the memory half of #5238.
    /// </summary>
    [SkippableFact]
    public void JobsWorkersSharingABundle_OneCompilesEverything_TheOtherCompilesNothing()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.FlatDir("al-runner-cache-phase-");
        Directory.CreateDirectory(root);
        try
        {
            var bundle = BuildFixture(root, testSleepMs: 150);
            var cacheDir = Path.Combine(root, "cache");
            Directory.CreateDirectory(cacheDir);

            var run = Start(bundle, cacheDir, " --jobs 2",
                new Dictionary<string, string> { ["AL_RUNNER_JOBS_SPLIT_MIN_FILES"] = "1" });
            var (output, exit) = Finish(run);
            Assert.True(exit == 0, $"exit {exit}:\n{output}");
            var total = 2 * BundleCodeunits;
            Assert.Contains($"Tests: {total}   passed {total}", run.Stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("is shared by 2 worker(s)", run.Stdout.ToString(), StringComparison.Ordinal);

            // The parent prints each worker's stderr in turn, and the parent itself never loads BC's
            // runtime: this line appears once per worker and nowhere else.
            const string WorkerStart = "[BcRuntime] Ncl in AppDomain";
            var blocks = run.Stderr.ToString().Split(WorkerStart).Skip(1).ToArray();
            Assert.True(blocks.Length == 2, $"expected 2 worker stderr blocks, saw {blocks.Length}:\n{output}");

            int Compiles(string b) => Count(b, "[deps] source-cache WROTE:") + Count(b, "[cache] MISS key=");
            int Hits(string b) => Count(b, "[deps] source-cache HIT:") + Count(b, "[cache] HIT  key=");
            var compiling = blocks.Where(b => Compiles(b) > 0).ToArray();
            var loading = blocks.Where(b => Compiles(b) == 0).ToArray();
            Assert.True(compiling.Length == 1 && loading.Length == 1,
                $"expected one worker to compile and one to compile nothing, saw compile counts "
                + $"[{string.Join(", ", blocks.Select(Compiles))}]:\n{output}");
            Assert.Equal(2, Compiles(compiling[0]));          // the dependency and the bundle, both here
            Assert.Equal(2, Hits(loading[0]));                // and both read back by the other

            // The compile phase ends before the tests start, so the worker that waited for it is
            // not made to wait for the other's tests as well: it joins in, and each ran some.
            var perWorker = System.Text.RegularExpressions.Regex.Matches(run.Stdout.ToString(), @"^Tests: (\d+) ",
                System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value)).ToArray();
            Assert.True(perWorker.Length >= 3 && perWorker.Take(2).All(n => n > 0),
                $"expected both workers to run tests, saw per-run counts [{string.Join(", ", perWorker)}]:\n{output}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The mechanism, pinned without a race: a worker of a shared bundle that misses must wait for
    /// whoever holds the bundle's compile phase and compile nothing meanwhile. The test is the
    /// holder. With the phase wired in, the worker prints that it is waiting (after
    /// <see cref="CacheCompileLock.AnnounceAfter"/>) and has compiled nothing; once the test lets
    /// go it finishes, having compiled everything itself. Without it the worker never waits, so
    /// the line never comes and the process exits first.
    /// </summary>
    [SkippableFact]
    public void AWorkerOfASharedBundle_WaitsForTheCompilePhaseBeforeCompilingAnything()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.FlatDir("al-runner-cache-phase-hold-");
        Directory.CreateDirectory(root);
        try
        {
            var bundle = BuildFixture(root);
            var cacheDir = Path.Combine(root, "cache");
            var claimDir = Path.Combine(root, "claims");
            Directory.CreateDirectory(cacheDir);
            Directory.CreateDirectory(claimDir);

            // The same hand-off ParallelFanOut gives a worker, for a bundle this one worker shares.
            var queue = new AlRunner.Infrastructure.UnitClaimQueue(claimDir, bundle);
            var phaseLock = AlRunner.Infrastructure.CacheCompileLock.Acquire(
                queue.CompilePhaseLockPath, "the phase", TimeSpan.FromSeconds(5), _ => { });
            Assert.True(phaseLock.HoldsLock);

            var worker = Start(bundle, cacheDir, "", new Dictionary<string, string>
            {
                [AlRunner.Infrastructure.UnitClaimQueue.DirEnvVar] = claimDir,
                [AlRunner.Infrastructure.UnitClaimQueue.BundlesEnvVar] = bundle,
            });
            try
            {
                string Snapshot() { lock (worker.Output) return worker.Output.ToString(); }

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
                while (DateTime.UtcNow < deadline && !worker.Process.HasExited
                       && !Snapshot().Contains("another process is compiling", StringComparison.Ordinal))
                    Thread.Sleep(200);

                var waiting = Snapshot();
                Assert.True(waiting.Contains("another process is compiling", StringComparison.Ordinal),
                    $"the worker never waited for the compile phase (exited={worker.Process.HasExited}):\n{waiting}");
                Assert.DoesNotContain("compiled-on-the-fly", waiting, StringComparison.Ordinal);
                Assert.DoesNotContain("[cache] MISS", waiting, StringComparison.Ordinal);
                Assert.False(worker.Process.HasExited, "the worker ended while the phase was held");

                phaseLock.Dispose();
                var (output, exit) = Finish(worker);
                Assert.True(exit == 0, $"exit {exit}:\n{output}");
                Assert.Equal(1, Count(output, "[deps] source-cache WROTE:"));
                Assert.Equal(1, Count(output, "[cache] MISS key="));
                Assert.Equal(1, Count(output, $"Tests: {2 * BundleCodeunits}   passed {2 * BundleCodeunits}"));
            }
            finally
            {
                phaseLock.Dispose();
                if (!worker.Process.HasExited) { try { worker.Process.Kill(true); } catch { } }
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
