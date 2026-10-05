// #5315 — one condition, one exit code: a declared dependency that no package cache holds.
//
// README Exit Codes: 1 is "a test FAILED or ERRORED", 2 is "a bundle could not execute". A missing
// package is the second. The pre-passes and --precompile already answered 2; the bundled loop (plain and
// --per-suite share it) answered 1, so a caller reading only the exit code could not tell a provisioning
// gap from a failing test, and a --server request answered 3 ("compile") with the one-line message.
//
// #5333: the same condition under --jobs also has to reach the aggregate's NOT RUN line, which a worker
// that stopped before it printed any COMPILE FAIL / EXEC FAIL header used to leave out.
//
// Runner-only claim: which exit code the runner's own CLI and protocol carry for a resolution failure.
// Every test spawns the real runner. The pre-pass and --precompile answers are pinned where they were
// written (SiblingSourceDepProvisioningReportingTests, LayeredPrePassProvisioningReportingTests,
// PrecompileDependencyRefusalTests), not repeated here.
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class MissingDependencyExitCodeTests : IClassFixture<SharedCliServer>, IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string GhostId = "d0105132-eeee-4b22-8c33-d44455566677";
    private const string GhostPublisher = "Nobody";
    private const string GhostName = "Ghost Pkg";

    private const string GapHeadline = "A required dependency package is missing from your package cache.";
    private const string GapNamesGhost = "Missing: Nobody/Ghost Pkg v1.0.0.0";

    private const string PassingTest = """
        procedure Passes()
        begin
        end;
        """;

    private const string FailingTest = """
        procedure Fails()
        begin
            Error('boom');
        end;
        """;

    private readonly SharedCliServer _server;
    private readonly string _scratch = TestScratch.Dir("al-runner-missing-dep-exit");

    public MissingDependencyExitCodeTests(SharedCliServer server) => _server = server;

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    // ── the bundled loop ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The reported shape. Exit 2, the provisioning-gap report naming the package, no test counted. The
    /// second spawn shares the first one's --cache root: a state the refusal left behind would change the
    /// answer on the warm run.
    /// </summary>
    [SkippableFact]
    public void PlainRun_DeclaredDependencyInNoCache_ExitsTwoNotOne_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();
        var fx = Arrange("plain", withGhostPackage: false, PassingTest);

        var cold = Run(fx, fx.Suite);
        AssertRefusedAsAGap(cold);

        var warm = Run(fx, fx.Suite);
        AssertRefusedAsAGap(warm);
    }

    /// <summary>--per-suite resolves in the same block as the plain run and must answer the same.</summary>
    [SkippableFact]
    public void PerSuite_DeclaredDependencyInNoCache_ExitsTwoNotOne()
    {
        TestArtifacts.SkipIfMissing();
        var fx = Arrange("per-suite", withGhostPackage: false, PassingTest);

        var run = Run(fx, fx.Suite, "--per-suite");

        AssertRefusedAsAGap(run);
    }

    /// <summary>
    /// --jobs: the worker that hits the gap exits 2, the other runs its test, and the aggregate takes the
    /// worst code. Before #5315 the aggregate read 1, "at least one test failed", with 0 failures counted.
    /// The cold and the warm run share one --cache root, and the aggregate's NOT RUN line (#5333) is the
    /// same on both: the worker printed the gap report and no per-bundle header, so the line read the
    /// header and counted nothing.
    /// </summary>
    [SkippableFact]
    public void Jobs_OneWorkerHitsTheMissingDependency_AggregateExitsTwoAndCountsItNotRun_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();
        var broken = Arrange("jobs-broken", withGhostPackage: false, PassingTest);
        var clean = Arrange("jobs-clean", withGhostPackage: false, PassingTest, declareGhost: false, appId: "71000000-0000-4000-8000-0000000000c1");

        foreach (var attempt in new[] { "cold", "warm" })
        {
            var run = Run(broken, broken.Suite, clean.Suite, "--jobs", "2");

            Assert.True(run.ExitCode == 2, $"{attempt}: exit {run.ExitCode}\n{run.Output}");
            Assert.Contains(GapHeadline, run.Output);
            Assert.Contains(GapNamesGhost, run.Output);
            // One worker per bundle: the gap is that worker's exit, the clean one's is 0, and the survivor's test is counted.
            Assert.Single(Regex.Matches(run.Output, @"shard \d+ \(exit 2\)"));
            Assert.Single(Regex.Matches(run.Output, @"shard \d+ \(exit 0\)"));
            Assert.Matches(@"Tests:\s+1\s+passed\s+1\s+failed\s+0\s+errors\s+0", run.Output);
            Assert.Contains("exit code 2", Regex.Match(run.Output, @"^Result: .*$", RegexOptions.Multiline).Value);
            AssertNotRun(run.Output, 1, attempt);
            AssertNoPartial(run.Output);
            // The note names the bundle that was lost, from the one worker that never reported.
            Assert.Single(Regex.Matches(run.Output, @"^jobs: shard \d+ ended \(exit 2\) without writing its test results", RegexOptions.Multiline));
            Assert.Matches(@"MISSING from the totals: .*jobs-broken", run.Output);
        }
    }

    /// <summary>
    /// The same run with every report named: the lost worker is already an ExecuteFailed bucket in --out and
    /// --output-json (#5338), and the aggregate counts it once, not once per route. The NOT RUN line is
    /// read off the aggregate block only: the report-lost-worker note and the new one are different lines.
    /// </summary>
    [SkippableFact]
    public void Jobs_WithEveryReportNamed_TheLostWorkerIsCountedOnceNotRun()
    {
        TestArtifacts.SkipIfMissing();
        var broken = Arrange("jobs-rep-broken", withGhostPackage: false, PassingTest);
        var clean = Arrange("jobs-rep-clean", withGhostPackage: false, PassingTest, declareGhost: false, appId: "71000000-0000-4000-8000-0000000000c2");
        var reports = Path.Combine(_scratch, "jobs-rep-out");
        Directory.CreateDirectory(reports);

        var run = Run(broken, broken.Suite, clean.Suite, "--jobs", "2", "--out", Path.Combine(reports, "out.json"),
            "--output-json", "--output-junit", Path.Combine(reports, "out.xml"));

        Assert.True(run.ExitCode == 2, $"exit {run.ExitCode}\n{run.Output}");
        Assert.Contains("without handing back its results", run.Output);   // the report route fired as well
        AssertNotRun(run.Output, 1, "reports");
        AssertNoPartial(run.Output);
    }

    /// <summary>
    /// A gap bundle several workers share: each worker stops on it, and it is one NOT RUN bundle. Both
    /// workers are lost, so the shared-bundle correction applies to workers that printed nothing.
    /// </summary>
    [SkippableFact]
    public void Jobs_AGhostBundleSharedByTwoWorkers_IsOneNotRunBundle()
    {
        TestArtifacts.SkipIfMissing();
        var shared = Arrange("jobs-shared", withGhostPackage: false, PassingTest);
        File.WriteAllText(Path.Combine(shared.Suite, "Probe2.Codeunit.al"), SecondCodeunit(PassingTest));

        var run = Run(shared, shared.Suite, new Dictionary<string, string> { ["AL_RUNNER_JOBS_SPLIT_MIN_FILES"] = "1" },
            "--jobs", "2");

        Assert.True(run.ExitCode == 2, $"exit {run.ExitCode}\n{run.Output}");
        Assert.Contains("is shared by 2 worker(s)", run.Output);   // the premise: two workers hold the one bundle
        Assert.Equal(2, Regex.Matches(run.Output, @"shard \d+ \(exit 2\)").Count);
        Assert.Equal(2, Regex.Matches(run.Output, GapHeadline).Count);
        AssertNotRun(run.Output, 1, "shared");
        AssertNoPartial(run.Output);
    }

    /// <summary>The edge: every worker stops on the gap. Nothing ran, every bundle is NOT RUN, exit 2.</summary>
    [SkippableFact]
    public void Jobs_EveryWorkerHitsTheGap_EveryBundleIsNotRun()
    {
        TestArtifacts.SkipIfMissing();
        var first = Arrange("jobs-all-1", withGhostPackage: false, PassingTest, appId: "71000000-0000-4000-8000-0000000000e1");
        var second = Arrange("jobs-all-2", withGhostPackage: false, PassingTest, appId: "71000000-0000-4000-8000-0000000000e2");

        var run = Run(first, first.Suite, second.Suite, "--jobs", "2");

        Assert.True(run.ExitCode == 2, $"exit {run.ExitCode}\n{run.Output}");
        Assert.Equal(2, Regex.Matches(run.Output, @"shard \d+ \(exit 2\)").Count);
        Assert.Matches(@"Tests:\s+0\s+passed\s+0\s+failed\s+0\s+errors\s+0\s+skipped\s+0", run.Output);
        AssertNotRun(run.Output, 2, "every worker");
    }

    /// <summary>--tdd and --per-suite resolve in their own blocks; a worker running either stops on the gap
    /// the same way and the aggregate counts it the same way.</summary>
    [SkippableTheory]
    [InlineData("--tdd")]
    [InlineData("--per-suite")]
    public void Jobs_TheGapUnderAnotherMode_IsCountedNotRun(string mode)
    {
        TestArtifacts.SkipIfMissing();
        var name = "jobs" + mode.Replace("--", "-");
        var broken = Arrange(name + "-broken", withGhostPackage: false, PassingTest);
        var clean = Arrange(name + "-clean", withGhostPackage: false, PassingTest, declareGhost: false,
            appId: mode == "--tdd" ? "71000000-0000-4000-8000-0000000000c3" : "71000000-0000-4000-8000-0000000000c4");

        var run = Run(broken, broken.Suite, clean.Suite, "--jobs", "2", mode);

        Assert.True(run.ExitCode == 2, $"{mode}: exit {run.ExitCode}\n{run.Output}");
        Assert.Contains(GapNamesGhost, run.Output);
        Assert.Matches(@"Tests:\s+1\s+passed\s+1\s+failed\s+0\s+errors\s+0", run.Output);
        AssertNotRun(run.Output, 1, mode);
    }

    /// <summary>
    /// Control: a worker that exits for another reason and DID report keeps the line it had. A bundle that
    /// does not compile prints its COMPILE FAIL header and writes its JUnit file, so it is one NOT RUN
    /// bundle by the header and is not also counted as a worker that left nothing.
    /// </summary>
    [SkippableFact]
    public void Jobs_ACompileFailingBundle_IsCountedOnceAndIsNotAWorkerThatLeftNothing()
    {
        TestArtifacts.SkipIfMissing();
        var broken = Arrange("jobs-cf-broken", withGhostPackage: false, PassingTest, declareGhost: false,
            appId: "71000000-0000-4000-8000-0000000000f1");
        File.WriteAllText(Path.Combine(broken.Suite, "Probe.Codeunit.al"), Codeunit("""
            procedure Passes()
            begin
                NoSuchProcedure();
            end;
            """));
        var clean = Arrange("jobs-cf-clean", withGhostPackage: false, PassingTest, declareGhost: false, appId: "71000000-0000-4000-8000-0000000000f2");

        var run = Run(clean, broken.Suite, clean.Suite, "--jobs", "2");

        Assert.True(run.ExitCode == 3, $"exit {run.ExitCode}\n{run.Output}");
        Assert.DoesNotContain(GapHeadline, run.Output);
        Assert.DoesNotContain("without writing its test results", run.Output);
        AssertNotRun(run.Output, 1, "compile fail");
        Assert.Matches(@"Tests:\s+1\s+passed\s+1\s+failed\s+0\s+errors\s+0", run.Output);
    }

    // ── controls: the neighbouring exit codes did not move ────────────────────────────────

    /// <summary>
    /// The dependency is present and a test fails: exit 1, as before. A fix that mapped every early stop
    /// to 2 would fail here. Then the passing variant on the same cache: exit 0.
    /// </summary>
    [SkippableFact]
    public void DependencyPresent_FailingTestExitsOne_PassingTestExitsZero()
    {
        TestArtifacts.SkipIfMissing();
        var failing = Arrange("present-failing", withGhostPackage: true, FailingTest);

        var failed = Run(failing, failing.Suite);

        Assert.True(failed.ExitCode == 1, $"exit {failed.ExitCode}\n{failed.Output}");
        Assert.DoesNotContain(GapHeadline, failed.Output);
        Assert.Matches(@"Tests:\s+1\s+passed\s+0\s+failed\s+1\s+errors\s+0", failed.Output);

        File.WriteAllText(Path.Combine(failing.Suite, "Probe.Codeunit.al"), Codeunit(PassingTest));
        var passed = Run(failing, failing.Suite);

        Assert.True(passed.ExitCode == 0, $"exit {passed.ExitCode}\n{passed.Output}");
        Assert.Matches(@"Tests:\s+1\s+passed\s+1\s+failed\s+0\s+errors\s+0", passed.Output);
    }

    // ── --server ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A runTests request for a bundle whose dependency no cache holds is "could not execute" (2), with the
    /// provisioning-gap report in the protocol, as the server's own pre-pass answers. It answered 3, the
    /// "compile" code, with the one-line message. A clean request on the same server still runs.
    /// </summary>
    [SkippableFact]
    public async Task Server_DeclaredDependencyInNoCache_AnswersExecutionErrorWithTheGapReport()
    {
        TestArtifacts.SkipIfMissing();
        var broken = Arrange("server-broken", withGhostPackage: false, PassingTest,
            appId: "71000000-0000-4000-8000-0000000000d1");
        var clean = Arrange("server-clean", withGhostPackage: false, PassingTest, declareGhost: false,
            appId: "71000000-0000-4000-8000-0000000000d2");
        var server = await _server.GetAsync();

        var gap = await server.SendRequestStreamingAsync(Request(broken.Suite), TimeSpan.FromSeconds(300));
        var (gapTests, gapSummary) = ProtocolV2Streaming.Split(gap);

        Assert.True(gapSummary.GetProperty("exitCode").GetInt32() == 2, string.Join("\n", gap));
        Assert.Empty(gapTests);
        var text = ErrorText(gapSummary);
        Assert.Contains(GapHeadline, text);
        Assert.Contains(GapNamesGhost, text);
        Assert.DoesNotContain("DEP-RESOLVE-FAIL", text);

        var ok = await server.SendRequestStreamingAsync(Request(clean.Suite), TimeSpan.FromSeconds(300));
        var (okTests, okSummary) = ProtocolV2Streaming.Split(ok);

        Assert.True(okSummary.GetProperty("exitCode").GetInt32() == 0, string.Join("\n", ok));
        Assert.Single(okTests);
    }

    // ── fixture ───────────────────────────────────────────────────────────────────────────

    private sealed record Fixture(string Suite, string PkgDir, string CacheDir);

    private static string Codeunit(string testBody) => $$"""
        codeunit 71301 "Mde Probe"
        {
            Subtype = Test;

            [Test]
            {{testBody}}
        }
        """;

    private static string SecondCodeunit(string testBody) => $$"""
        codeunit 71302 "Mde Probe Two"
        {
            Subtype = Test;

            [Test]
            {{testBody}}
        }
        """;

    /// <summary>
    /// A suite and its own package cache and --cache root. No <c>application</c> or <c>platform</c>
    /// property (.claude/rules/no-base-app-in-csharp-tests.md): the closure is the one ghost package.
    /// </summary>
    private Fixture Arrange(string name, bool withGhostPackage, string testBody, bool declareGhost = true,
        string appId = "71000000-0000-4000-8000-0000000000b0")
    {
        var root = Path.Combine(_scratch, name);
        var suite = Path.Combine(root, "suite");
        var pkg = Path.Combine(root, "pkg");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(suite);
        Directory.CreateDirectory(pkg);
        Directory.CreateDirectory(cache);
        var deps = declareGhost
            ? $$"""{ "id": "{{GhostId}}", "name": "{{GhostName}}", "publisher": "{{GhostPublisher}}", "version": "1.0.0.0" }"""
            : "";
        File.WriteAllText(Path.Combine(suite, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "Mde Probe {{name}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [ {{deps}} ],
          "idRanges": [ { "from": 71301, "to": 71310 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(suite, "Probe.Codeunit.al"), Codeunit(testBody));
        if (withGhostPackage) WriteGhostPackage(pkg);
        return new Fixture(suite, pkg, cache);
    }

    /// <summary>A minimal NAVX .app holding the ghost package: a manifest and a payload, no symbols.</summary>
    private static void WriteGhostPackage(string dir)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{GhostId}" Name="{GhostName}" Publisher="{GhostPublisher}" Version="1.0.0.0"/>
              <Dependencies />
            </Package>
            """;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("NavxManifest.xml").Open(), Encoding.UTF8)) w.Write(xml);
            using (var s = zip.CreateEntry("payload.bin").Open()) s.Write(new byte[4096]);
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        File.WriteAllBytes(Path.Combine(dir, $"{GhostPublisher}_{GhostName}_1.0.0.0.app"), result);
    }

    // ── invocation ────────────────────────────────────────────────────────────────────────

    private static void AssertRefusedAsAGap((int ExitCode, string Output) run)
    {
        Assert.True(run.ExitCode == 2, $"exit {run.ExitCode}\n{run.Output}");
        Assert.Contains(GapHeadline, run.Output);
        Assert.Contains(GapNamesGhost, run.Output);
        Assert.DoesNotContain("Unhandled exception", run.Output);
        Assert.DoesNotMatch(@"Tests:\s+\d", run.Output);
    }

    /// <summary>The aggregate block's NOT RUN line, which a --jobs caller reads: exactly one, with the count.</summary>
    private static void AssertNotRun(string output, int bundles, string run)
    {
        var aggregate = output[output.LastIndexOf("aggregate across", StringComparison.Ordinal)..];
        var lines = Regex.Matches(aggregate, @"^  NOT RUN: +(\d+) bundle\(s\).*$", RegexOptions.Multiline);
        Assert.True(lines.Count == 1 && lines[0].Groups[1].Value == bundles.ToString(),
            $"{run}: expected one NOT RUN line counting {bundles}, found {lines.Count} ({string.Join(" | ", lines.Select(l => l.Value))})\n{output}");
    }

    private static void AssertNoPartial(string output)
        => Assert.DoesNotContain("  PARTIAL: ", output[output.LastIndexOf("aggregate across", StringComparison.Ordinal)..]);

    private static string Request(string suite)
        => JsonSerializer.Serialize(new { command = "runTests", sourcePaths = new[] { suite } });

    private static string ErrorText(JsonElement summary)
    {
        Assert.True(summary.TryGetProperty("compilationErrors", out var errors), summary.GetRawText());
        return string.Join(" | ", errors.EnumerateArray()
            .SelectMany(g => g.GetProperty("errors").EnumerateArray().Select(e => e.GetString())));
    }

    /// <summary>One runner child over <paramref name="bundles"/>, with the fixture's own package cache and --cache root.</summary>
    private static (int ExitCode, string Output) Run(Fixture fx, string bundle, params string[] more)
        => Run(fx, bundle, null, more);

    /// <summary><paramref name="env"/> goes to the child's own environment only (never this process's).</summary>
    private static (int ExitCode, string Output) Run(Fixture fx, string bundle,
        IReadOnlyDictionary<string, string>? env, params string[] more)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath))
            .Append(TestBuildConfig.BcVersionArg)
            .Append($" \"{bundle}\"");
        foreach (var m in more) args.Append(m.StartsWith("--", StringComparison.Ordinal) ? $" {m}" : $" \"{m}\"");
        args.Append($" --package-cache \"{fx.PkgDir}\" --cache \"{fx.CacheDir}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        // The plan sizes a shared bundle from the free memory it reads: pin it, as JobsUnitClaimEndToEndTests does.
        psi.Environment[AlRunner.Infrastructure.JobsMemory.FreeMemoryEnvVar] = "1000000";
        foreach (var kv in env ?? new Dictionary<string, string>()) psi.Environment[kv.Key] = kv.Value;
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (p.ExitCode, sb.ToString());
    }
}
