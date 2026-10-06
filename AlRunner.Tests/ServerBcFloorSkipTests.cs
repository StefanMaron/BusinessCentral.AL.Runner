using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #5137: an app whose app.json declares a BC floor above the running BC is skipped by the CLI
/// ("[skip] ... declares BC >= ...", exit 0). A <c>--server</c> <c>runTests</c> serves every
/// <c>sourcePaths</c> entry as a bundle root, where the gate never fired, so it ran the app the
/// CLI skipped. The fixture is <c>Fixtures/BcFloorSkip</c>: a healthy app with one passing test
/// and a future app (<c>99.0.0.0</c>) whose only test fails unconditionally, so a regressed
/// gate cannot go green by accident. The skip must also be VISIBLE in the response
/// (<c>warnings</c> on the summary), because a silent skip reads as a pass.
/// </summary>
public class ServerBcFloorSkipTests : IClassFixture<ServerBcFloorSkipTests.SharedServer>
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static string Fixture(params string[] relative) => Path.GetFullPath(Path.Combine(
        new[] { RepoRoot, "AlRunner.Tests", "Fixtures", "BcFloorSkip" }.Concat(relative).ToArray()));

    private const string HealthyTest = "Codeunit60810.BcFloorSkip_HealthySibling_StillRuns";
    private const string FutureTest = "Codeunit60820.BcFloorSkip_FutureSuite_MustNeverExecute";

    private sealed record Served(int Exit, int Passed, int Failed, int Total, List<string> Tests, List<string> Warnings, string Text);

    /// <summary>
    /// One server for every fact of the class, started on first use so a box without artifacts
    /// skips before spawning anything. The facts differ only in the request they send, so each
    /// still gets its own requests and its own assertions; what they share is the process, which
    /// is also how a warm server is used.
    /// </summary>
    public sealed class SharedServer : IAsyncLifetime
    {
        private CliServer? _server;
        private string _scratch = "";

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task<CliServer> GetAsync()
        {
            if (_server != null) return _server;
            _scratch = TestScratch.Dir("server-bc-floor");
            var args = new List<string> { "--cache", Path.Combine(_scratch, "al-out-server") };
            var platformApps = TestArtifacts.PlatformAppsDir();
            if (Directory.Exists(platformApps)) { args.Add("--package-cache"); args.Add(platformApps); }
            return _server = await CliServer.StartAsync(args);
        }

        public async Task DisposeAsync()
        {
            if (_server != null) await _server.DisposeAsync();
        }
    }

    private readonly SharedServer _shared;

    public ServerBcFloorSkipTests(SharedServer shared) => _shared = shared;

    private async Task<List<Served>> ServeEach(params string[][] requests)
    {
        var server = await _shared.GetAsync();
        var served = new List<Served>();
        foreach (var sourcePaths in requests)
        {
            var req = JsonSerializer.Serialize(new { command = "runTests", sourcePaths, packagePaths = Array.Empty<string>() });
            var lines = await server.SendRequestStreamingAsync(req, TimeSpan.FromSeconds(300));
            var (events, summary) = ProtocolV2Streaming.Split(lines);
            var warnings = summary.TryGetProperty("warnings", out var w)
                ? w.EnumerateArray().Select(x => x.GetString()!).ToList()
                : new List<string>();
            served.Add(new Served(
                summary.GetProperty("exitCode").GetInt32(),
                summary.GetProperty("passed").GetInt32(),
                summary.GetProperty("failed").GetInt32(),
                summary.GetProperty("total").GetInt32(),
                events.Select(e => e.GetProperty("name").GetString()!).ToList(),
                warnings,
                string.Join(" | ", lines) + "\n--- server stderr ---\n" + server.StdErr));
        }
        return served;
    }

    private static (string Output, int Exit) RunCli(string cacheRoot, string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundle}\" --show-pass --cache \"{cacheRoot}\"");
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) args.Append($" --package-cache \"{platformApps}\"");
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
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>One warning naming the suite, its floor and the running BC, in the CLI's own words.</summary>
    private static void AssertSkipReported(Served s)
    {
        var skip = Assert.Single(s.Warnings.Where(w => w.Contains("BC Floor Skip - Future", StringComparison.Ordinal)));
        Assert.Contains("[skip]", skip, StringComparison.Ordinal);
        Assert.Contains("declares BC >= 99.0.0.0", skip, StringComparison.Ordinal);
        Assert.Contains("running", skip, StringComparison.Ordinal);
        Assert.DoesNotContain(s.Warnings, w => w.Contains("BC Floor Skip - Healthy", StringComparison.Ordinal));
    }

    /// <summary>The issue's scenario: one container holding a healthy app and a future app.</summary>
    [SkippableFact]
    public async Task Container_AppDeclaringANewerBc_IsSkippedAndReported_AsTheCliDoes()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("server-bc-floor-container");

        var (cli, cliExit) = RunCli(Path.Combine(scratch, "al-out-cli"), Fixture());
        Assert.True(cliExit == 0, cli);
        Assert.Contains("[skip] BC Floor Skip - Future", cli, StringComparison.Ordinal);
        Assert.Contains("PASS  " + HealthyTest, cli, StringComparison.Ordinal);
        Assert.DoesNotContain(FutureTest, cli, StringComparison.Ordinal);

        var s = Assert.Single(await ServeEach(new[] { Fixture() }));
        Assert.True(s.Exit == 0, s.Text);
        Assert.True(s.Passed == 1 && s.Failed == 0 && s.Total == 1, s.Text);
        Assert.Equal(new[] { HealthyTest }, s.Tests);
        AssertSkipReported(s);
    }

    /// <summary>Each app listed as its own <c>sourcePaths</c> entry: the second shape the issue names.</summary>
    [SkippableFact]
    public async Task EachAppListedAsItsOwnSourcePath_AppDeclaringANewerBc_IsSkippedAndReported()
    {
        TestArtifacts.SkipIfMissing();

        var s = Assert.Single(await ServeEach(new[] { Fixture("healthy-suite"), Fixture("future-suite") }));
        Assert.True(s.Exit == 0, s.Text);
        Assert.True(s.Passed == 1 && s.Failed == 0 && s.Total == 1, s.Text);
        Assert.Equal(new[] { HealthyTest }, s.Tests);
        AssertSkipReported(s);
    }

    /// <summary>
    /// Nothing is left to run: like the CLI, a green request with zero tests, and the skip is the
    /// one thing the response says. Then the SAME server is asked again — the report is per
    /// request, not once per process like the CLI's ledger — and finally for the healthy app
    /// alone, which has nothing to report.
    /// </summary>
    [SkippableFact]
    public async Task OnlyANewerBcApp_RunsNothing_ReportsTheSkipEveryRequest_AndAHealthyAppReportsNone()
    {
        TestArtifacts.SkipIfMissing();

        var served = await ServeEach(
            new[] { Fixture("future-suite") },
            new[] { Fixture("future-suite") },
            new[] { Fixture("healthy-suite") });

        foreach (var s in served.Take(2))
        {
            Assert.True(s.Exit == 0, s.Text);
            Assert.True(s.Passed == 0 && s.Failed == 0 && s.Total == 0 && s.Tests.Count == 0, s.Text);
            AssertSkipReported(s);
        }
        var healthy = served[2];
        Assert.True(healthy.Exit == 0 && healthy.Passed == 1 && healthy.Failed == 0, healthy.Text);
        Assert.Equal(new[] { HealthyTest }, healthy.Tests);
        Assert.DoesNotContain(healthy.Warnings, w => w.Contains("[skip]", StringComparison.Ordinal));
    }
}

/// <summary>
/// #5137: <c>ProgramSupport.DropAppsBelowBcFloor</c> over shapes the served fixtures do not cover.
/// An app whose app.json declares a floor above the running BC is skipped; anything else, including
/// a directory with no app.json and an app declaring no floor, is kept, in order.
/// </summary>
public sealed class DropAppsBelowBcFloorTests : IDisposable
{
    private readonly string _root = TestScratch.Dir("al-runner-drop-apps-below-bc-floor");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Dir(string name, string? appJson)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        if (appJson != null) File.WriteAllText(Path.Combine(dir, "app.json"), appJson);
        return dir;
    }

    private static string Manifest(string id, string name, string? platform = null) =>
        $$"""{ "id": "{{id}}", "name": "{{name}}", "publisher": "P", "version": "1.0.0.0"{{(platform == null ? "" : $", \"platform\": \"{platform}\"")}} }""";

    [Fact]
    public void OnlyAnAppAboveTheRunningBc_IsDropped_AndNamedInTheSkipLine()
    {
        var healthy = Dir("healthy", Manifest("00000000-0000-0000-0000-000000051371", "Healthy", "1.0.0.0"));
        var future = Dir("future", Manifest("00000000-0000-0000-0000-000000051372", "Future One", "999.0.0.0"));
        var noManifest = Dir("no-manifest", null);
        var noFloor = Dir("no-floor", Manifest("00000000-0000-0000-0000-000000051373", "No Floor"));

        var kept = ProgramSupport.DropAppsBelowBcFloor(
            new[] { future, healthy, noManifest, noFloor }, out var skipped);

        Assert.Equal(new[] { healthy, noManifest, noFloor }, kept);
        var line = Assert.Single(skipped);
        Assert.StartsWith("[skip] Future One: declares BC >= 999.0.0.0, running ", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The boundary: a floor EQUAL to the running BC is met, so the app is kept; one revision above
    /// is not. Read from the running version itself, so it holds whichever BC the process selected.
    /// </summary>
    [Fact]
    public void AFloorEqualToTheRunningBc_IsKept_AndOneRevisionAboveIsDropped()
    {
        var running = AlRunner.Infrastructure.BcArtifacts.SelectedVersion;
        var above = new Version(running.Major, running.Minor, Math.Max(running.Build, 0), Math.Max(running.Revision, 0) + 1);
        var equal = Dir("equal", Manifest("00000000-0000-0000-0000-000000051376", "Equal", running.ToString()));
        var oneAbove = Dir("above", Manifest("00000000-0000-0000-0000-000000051377", "Above", above.ToString()));

        var kept = ProgramSupport.DropAppsBelowBcFloor(new[] { equal, oneAbove }, out var skipped);

        Assert.Equal(new[] { equal }, kept);
        var line = Assert.Single(skipped);
        Assert.StartsWith($"[skip] Above: declares BC >= {above}, running {running}", line, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingDeclaringAFloorAboveTheRunningBc_KeepsEverythingAndReportsNothing()
    {
        var a = Dir("a", Manifest("00000000-0000-0000-0000-000000051374", "A", "1.0.0.0"));
        var b = Dir("b", Manifest("00000000-0000-0000-0000-000000051375", "B"));

        var kept = ProgramSupport.DropAppsBelowBcFloor(new[] { a, b }, out var skipped);

        Assert.Equal(new[] { a, b }, kept);
        Assert.Empty(skipped);
    }
}
