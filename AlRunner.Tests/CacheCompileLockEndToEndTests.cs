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

    private static string MainTestAl(int i) => $$"""
        codeunit {{70900 + i}} "CCL Main Tests {{i}}"
        {
            Subtype = Test;

            [Test]
            procedure Arithmetic_Works()
            var
                Total: Integer;
                n: Integer;
            begin
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

    private static string BuildFixture(string root)
    {
        var main = Path.Combine(root, "main");
        var packages = Path.Combine(main, ".alpackages");
        Directory.CreateDirectory(packages);
        for (var i = 0; i < BundleCodeunits; i++)
            File.WriteAllText(Path.Combine(main, $"CclMainTests{i}.Codeunit.al"), MainTestAl(i));
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
    }

    private static Spawned Start(string bundlePath, string cacheDir)
    {
        var args = new System.Text.StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --verbose");
        args.Append($" --cache \"{cacheDir}\"");
        args.Append($" \"{bundlePath}\"");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new System.Text.StringBuilder();
        var p = System.Diagnostics.Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return new Spawned { Process = p, Output = sb };
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
}
