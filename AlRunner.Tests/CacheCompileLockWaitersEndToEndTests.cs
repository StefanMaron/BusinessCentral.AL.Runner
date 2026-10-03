// CacheCompileLockWaitersEndToEndTests — #5238: processes already WAITING for a lock when the
// holder finds out that its key can never be cached. Its own class (and so its own xUnit
// collection) so the spawning rows run beside CacheCompileLockEndToEndTests, not behind it.
// Fixture and process helpers are that class's.

using Xunit;
using Xunit.Abstractions;
using static AlRunner.Tests.CacheCompileLockEndToEndTests;

namespace AlRunner.Tests;

public sealed class CacheCompileLockWaitersEndToEndTests
{
    private readonly ITestOutputHelper _output;

    public CacheCompileLockWaitersEndToEndTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Two processes already WAITING for a lock when the holder finds out that its key can never be
    /// cached: each must see the marker and compile at once, not queue behind one another's compile.
    /// The test is the lock holder, writes the marker, then lets go. Ways to wait: on the key lock
    /// (processes sharing a cache but not a bundle) or on the compile phase (workers of one shared
    /// bundle), or on the key lock while ALSO sharing a phase; for the bundle or for a dependency.
    /// What is compared is when the two workers START their compile, against how long a compile
    /// takes on this box: side by side the second starts almost with the first, one behind the other
    /// it starts about one compile later. The bound is half the shorter compile, so a loaded box
    /// that slows both compiles does not trip it and a serial run still does.
    /// </summary>
    [SkippableTheory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void ProcessesWaitingOnALock_SeeTheMarkerTheHolderLeaves_AndCompileSideBySide(
        bool viaPhase, bool atDependency, bool workersShareAPhase)
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.FlatDir("al-runner-cache-waiters-");
        Directory.CreateDirectory(root);
        try
        {
            var bundle = atDependency
                ? BuildFixture(root, codeunits: 2, brokenDependency: true)
                : BuildFixture(root, codeunits: BundleCodeunits, withUncompilableCodeunit: true, uncompilableIsATest: true);
            var cacheDir = Path.Combine(root, "cache");
            var claimDir = Path.Combine(root, "claims");
            Directory.CreateDirectory(cacheDir);
            Directory.CreateDirectory(claimDir);
            var claimEnv = new Dictionary<string, string>
            {
                [AlRunner.Infrastructure.UnitClaimQueue.DirEnvVar] = claimDir,
                [AlRunner.Infrastructure.UnitClaimQueue.BundlesEnvVar] = bundle,
            };

            // Setup: learn the key (and file names) to hold, and warm everything that can be warmed.
            string markerPath, keyLockPath, compileLine;
            if (atDependency)
            {
                // The dependency fails to compile, so this run leaves its marker and its lock file.
                var (setup, _) = Finish(Start(bundle, cacheDir));
                var found = Directory.GetFiles(cacheDir, "*" + AlRunner.Infrastructure.UncacheableCompile.Suffix,
                    SearchOption.AllDirectories);
                Assert.True(found.Length == 1, $"setup left {found.Length} markers:\n{setup}");
                markerPath = found[0];
                keyLockPath = markerPath[..^AlRunner.Infrastructure.UncacheableCompile.Suffix.Length] + ".compile.lock";
                Assert.True(File.Exists(keyLockPath), $"no lock file beside the marker:\n{setup}");
                File.Delete(markerPath);               // the waiters must not know yet
                compileLine = "[deps] compiling ";
            }
            else
            {
                // --print-cache-key compiles nothing, but loads (and so warms) the dependency.
                var (setup, _) = Finish(Start(bundle, cacheDir, " --print-cache-key"));
                var m = System.Text.RegularExpressions.Regex.Match(setup, @"\[cache\] KEY key=([0-9a-f]{64})");
                Assert.True(m.Success, $"no cache key printed:\n{setup}");
                markerPath = Path.Combine(cacheDir, m.Groups[1].Value + AlRunner.Infrastructure.UncacheableCompile.Suffix);
                keyLockPath = Path.Combine(cacheDir, m.Groups[1].Value + ".compile.lock");
                compileLine = "[cache] MISS key=";
            }

            var lockPath = keyLockPath;
            if (viaPhase)
                lockPath = new AlRunner.Infrastructure.UnitClaimQueue(claimDir, bundle).CompilePhaseLockPath;
            using var held = AlRunner.Infrastructure.CacheCompileLock.Acquire(lockPath, "the lock", TimeSpan.FromSeconds(5), _ => { });
            Assert.True(held.HoldsLock);

            var env = workersShareAPhase ? claimEnv : null;
            var w1 = Start(bundle, cacheDir, "", env);
            var w2 = Start(bundle, cacheDir, "", env);
            bool Waiting(Spawned w) { lock (w.Output) return w.Output.ToString().Contains("another process is compiling", StringComparison.Ordinal); }
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (DateTime.UtcNow < deadline && !(Waiting(w1) && Waiting(w2))
                   && !w1.Process.HasExited && !w2.Process.HasExited)
                Thread.Sleep(200);
            Assert.True(Waiting(w1) && Waiting(w2), "the workers never both waited for the lock");

            File.WriteAllBytes(markerPath, Array.Empty<byte>());
            held.Dispose();
            var (out1, _) = Finish(w1);
            var (out2, _) = Finish(w2);

            DateTime StartOfCompile(Spawned w, string output)
            {
                lock (w.Output)
                {
                    var hit = w.Lines.FirstOrDefault(l => l.Line.Contains(compileLine, StringComparison.Ordinal));
                    Assert.True(hit.Line != null, $"a worker never attempted the compile ({compileLine}):\n{output}");
                    return hit.At;
                }
            }
            string Timeline(Spawned w)
            {
                lock (w.Output)
                    return string.Join("\n", w.Lines
                        .Where(l => l.Line.Contains("[cache]") || l.Line.Contains("[deps] compiling") || l.Line.Contains("EMIT-") || l.Line.Contains("Result:") || l.Line.Contains("dep-load-fail"))
                        .Select(l => $"  +{(l.At - w.Lines[0].At).TotalSeconds:F1}s {l.Line[..Math.Min(100, l.Line.Length)]}"));
            }
            DateTime LastLine(Spawned w) { lock (w.Output) return w.Lines[^1].At; }
            var s1 = StartOfCompile(w1, out1);
            var s2 = StartOfCompile(w2, out2);
            var gap = (s1 - s2).Duration();
            var compileTime = TimeSpan.FromTicks(Math.Min((LastLine(w1) - s1).Ticks, (LastLine(w2) - s2).Ticks));
            var bound = compileTime / 2;
            _output.WriteLine($"viaPhase={viaPhase} atDependency={atDependency} share={workersShareAPhase}: "
                + $"gap {gap.TotalSeconds:F2} s, shorter compile {compileTime.TotalSeconds:F2} s, bound {bound.TotalSeconds:F2} s");
            Assert.True(gap < bound,
                $"the second worker started its compile {gap.TotalSeconds:F1} s after the first (bound {bound.TotalSeconds:F1} s, half of the shorter compile): it waited for the first's compile\n--- 1 ---\n{Timeline(w1)}\n--- 2 ---\n{Timeline(w2)}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
