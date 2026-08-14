// #1888 defect 2 — a --server process's phase-log row must survive the process
// being killed, not just a graceful shutdown.
//
// Before this fix, RunAllBundlesForServer/RunBundleForServer never called into
// AlRunner.Infrastructure.PhaseLog at all: every Stage()/AppStage() mark already
// sprinkled through DependencyLoader and TestExecutor was a silent no-op in
// --server mode (AddStageTo/AddApp bail out when _bundle/_app is null — see
// PhaseLog.cs), because nothing ever opened those rows. The single heaviest
// class in the suite that talks exclusively through --server, ServerCancelTests,
// therefore produced ZERO phase-log rows of any kind — not because its server was
// cancelled or killed specifically, but because the server code path never wrote
// bundle/app rows regardless of how the process ended.
//
// The fix (see RunAllBundlesForServer/RunBundleForServer in Program.cs) opens a
// bundle+app row per request bundle and appends it via EndBundle the moment that
// bundle's compile+run finishes — which happens INSIDE the runTests round trip,
// well before the test harness's `await using` disposal later SIGKILLs the
// server subprocess (see CliServer.DisposeAsync). So the row is written before
// the kill, not "on" the kill: there is no clean-append-on-SIGKILL mechanism here
// (SIGKILL is uncatchable, so a killed process cannot run any of its own code —
// only a row written earlier, by a process still alive, can possibly survive).
// The one row that IS still lost on kill is the once-per-process "process" kind
// row: it is written only from PhaseLog's AppDomain.ProcessExit hook, which never
// fires on SIGKILL. That is called out explicitly below rather than silently
// left unproven.
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class PhaseLogServerKillTests : IDisposable
{
    private readonly string _root;
    private readonly string _logPath;

    public PhaseLogServerKillTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-phaselog-server-kill", Guid.NewGuid().ToString("N"));
        _logPath = Path.Combine(_root, "logs", "phases.jsonl");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string[] ExtraServerArgs()
    {
        var platformApps = TestArtifacts.PlatformAppsDir();
        return Directory.Exists(platformApps)
            ? new[] { "--package-cache", platformApps }
            : Array.Empty<string>();
    }

    /// <summary>
    /// A minimal, self-contained, always-fresh (nonce'd) one-suite AL bundle — same
    /// shape as ServerCancelTests.MakeFastBundle, distinct id range (60330-60339) so
    /// concurrent test classes' dynamic fixtures never collide inside one process.
    /// </summary>
    private static string MakeFixtureBundle(out string dirName)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-phaselog-kill-fixture", nonce);
        Directory.CreateDirectory(dir);
        dirName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "f2b3c4d5-e6f7-4819-a0cb-dcedfe0f4455",
          "name": "PhaseLog Server Kill Probe {{nonce}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 60330, "to": 60339 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "KillProbe.Codeunit.al"), $$"""
        codeunit 60330 "PhaseLog Kill Probe SX"
        {
            Subtype = Test;

            [Test]
            procedure OnlyTest()
            begin
            end;
        }
        """);
        return dir;
    }

    private static string RunTestsReq(string bundleDir)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { bundleDir },
            packagePaths = Array.Empty<string>(),
        });

    private static List<JsonElement> ReadRecords(string path, string kind) =>
        File.Exists(path)
            ? File.ReadAllLines(path)
                .Where(l => l.Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.GetProperty("kind").GetString() == kind)
                .ToList()
            : new List<JsonElement>();

    /// <summary>
    /// Positive direction AND the honest complement, folded into one server spawn (#1888
    /// review): a runTests round trip completes, the test harness then SIGKILLs the
    /// server (CliServer.DisposeAsync, exactly what every ServerCancelTests test does),
    /// and against that SAME log artifact:
    ///   (a) the bundle+app rows for the completed request are still on disk — with
    ///       identifying content, not merely a non-empty file;
    ///   (b) the once-per-process "process" kind row (patches_ms, peak_rss_bytes,
    ///       exit_code) is NOT there — it is written only from PhaseLog's
    ///       AppDomain.ProcessExit hook (see WriteProcessRecord), which a SIGKILL never
    ///       fires. Asserting this here rather than silently is the point: it is a real,
    ///       currently-unfixed gap distinct from the bundle/app rows fixed above, and a
    ///       future change to close it should start by making this assertion fail.
    /// These two claims used to be separate tests, each starting its own server; they
    /// were merged because they exercise the identical flow (same env, same runTests,
    /// same SIGKILL, same log file) and the second one's only distinct assertion was the
    /// Assert.Empty below — folding costs one server spawn (~18s) and no claim.
    /// </summary>
    [SkippableFact]
    public async Task KilledServerProcess_StillWritesBundleAndAppRowsForCompletedWork()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = MakeFixtureBundle(out var dirName);
        int pid;
        try
        {
            await using var server = await CliServer.StartAsync(ExtraServerArgs(),
                extraEnv: new Dictionary<string, string> { ["AL_RUNNER_PHASE_LOG"] = _logPath });
            // CliServer.StartAsync's Process.Start returns the OUTER re-exec parent
            // (see PhaseLog's process-reexec-parent kind): the runner re-execs itself
            // with DOTNET_ReadyToRun=0 on every invocation, so the process that
            // actually opens the bundle/app rows is a CHILD of this pid, not this pid
            // itself. Kept only for diagnostics below — row identity is proven instead
            // by content (bundle/app name, index, cache decision), and by this test's
            // _logPath being exclusive to this one CliServer session.
            pid = server.Pid;

            var lines = await server.SendRequestStreamingAsync(RunTestsReq(bundle));
            var summary = JsonDocument.Parse(lines[^1]).RootElement;
            Assert.Equal("summary", summary.GetProperty("type").GetString());
            Assert.Equal(1, summary.GetProperty("total").GetInt32());

            // The request has already completed and been summarised; nothing further
            // happens on the server before `await using` disposes it below, which
            // Process.Kill(true)s it (SIGKILL — uncatchable, no ProcessExit fires).
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
        }

        // The server process is now dead. If the bundle/app rows were only ever
        // deferred to process exit (the way the process-level row checked at (b) below
        // is), this file would not exist at all.
        Assert.True(File.Exists(_logPath),
            $"no phase log written (server outer pid {pid}) — the killed server produced no rows at all");

        var bundleRows = ReadRecords(_logPath, "bundle");
        var appRows = ReadRecords(_logPath, "app");

        var bundleRow = Assert.Single(bundleRows);
        Assert.EndsWith(dirName, bundleRow.GetProperty("bundle").GetString());
        Assert.Equal(1, bundleRow.GetProperty("bundle_index").GetInt32());
        Assert.True(bundleRow.GetProperty("wall_ms").GetInt64() > 0,
            $"bundle row wall_ms is zero — not a real measurement: {bundleRow}");

        var appRow = Assert.Single(appRows);
        // Named after the emitted module, which server mode derives as "V2_<dir>" —
        // a stubbed/default row would not carry this, only a real BeginApp call would.
        Assert.Equal($"V2_{dirName}", appRow.GetProperty("app").GetString());
        Assert.Equal(1, appRow.GetProperty("app_index").GetInt32());
        Assert.Equal(1, appRow.GetProperty("apps_in_bundle").GetInt32());
        Assert.True(appRow.GetProperty("emit_ms").GetInt64() > 0, $"app emit_ms not wired: {appRow}");
        Assert.True(appRow.GetProperty("compile_ms").GetInt64() > 0, $"app compile_ms not wired: {appRow}");
        Assert.True(appRow.GetProperty("run_ms").GetInt64() > 0, $"app run_ms not wired: {appRow}");
        // A fresh nonce'd bundle always MISSes the AL-output cache on its first (only)
        // compile — a stub returning cache_hits=0/cache_misses=0 would fail this.
        Assert.Equal(1, appRow.GetProperty("cache_misses").GetInt32());
        Assert.Equal(0, appRow.GetProperty("cache_hits").GetInt32());

        // (b) the honest complement, against the same artifact: _logPath is exclusive to
        // this one CliServer session (fresh temp dir per test), so ANY "process" row here
        // would have to come from this session's worker or its re-exec parent(s) — none
        // of which reached a graceful exit.
        Assert.Empty(ReadRecords(_logPath, "process"));
    }

    /// <summary>
    /// Negative direction: without AL_RUNNER_PHASE_LOG set, the same kill flow writes
    /// no phase log at all — a hard-coded/always-on writer would fail this exactly the
    /// way PhaseLogIntegrationTests.WithoutTheEnvVar_NoPhaseLogIsWritten pins it for
    /// the CLI path.
    /// </summary>
    [SkippableFact]
    public async Task KilledServerProcess_WithoutTheEnvVar_WritesNoPhaseLog()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = MakeFixtureBundle(out _);
        try
        {
            await using var server = await CliServer.StartAsync(ExtraServerArgs());
            var lines = await server.SendRequestStreamingAsync(RunTestsReq(bundle));
            Assert.Equal("summary", JsonDocument.Parse(lines[^1]).RootElement.GetProperty("type").GetString());
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
        }

        Assert.False(File.Exists(_logPath));
    }
}
