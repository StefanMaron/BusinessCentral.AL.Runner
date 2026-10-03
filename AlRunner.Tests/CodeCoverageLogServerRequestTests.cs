// CodeCoverageLogServerRequestTests — #5260: in one --server process a request that takes the same
// app id from ANOTHER folder than an earlier one read Code Coverage (2000000049) rows built from the
// earlier request's compiled type: BC's ALCodeEnvironment keeps the per-object source info (and
// the object's text, and its scope contexts) in caches keyed by the object id, and the runner seeds
// ONE environment for the whole process, so the second request got the first one's statement lines
// over its own text. The text indexed past the end (an exception) or the hits landed on other lines.
// RecordPatches.ResetForReload now clears them with the other per-request source state.
//
// The layout is CodeCoverageSiblingLayout's: bundle/ and src/ share one app id, and each request
// names one of them with a tests app that accepts only that folder's own result and rows.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class CodeCoverageLogServerRequestTests : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(240);
    private readonly string _root = TestScratch.Dir("al-runner-codecoverage-log-server");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Layout()
    {
        // Two tests apps, so each request can assert its own folder's rows by name.
        CodeCoverageSiblingLayout.Write(_root, withSibling: true, testsDir: "tests-bundle", testsCodeunit: 79811,
            testsAppId: CodeCoverageSiblingLayout.TestAppId, fromBundle: true, fromSource: false);
        CodeCoverageSiblingLayout.Write(_root, withSibling: true, testsDir: "tests-src", testsCodeunit: 79812,
            testsAppId: CodeCoverageSiblingLayout.SecondTestAppId, fromBundle: false, fromSource: true);
    }

    private async Task<string> RunAsync(CliServer server, string compiled, string tests, string label, bool coverage = false)
    {
        var request = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { Path.Combine(_root, compiled), Path.Combine(_root, tests) },
            packagePaths = Array.Empty<string>(),
            testIsolation = "test",
            coverage,
        });
        var lines = await server.SendRequestStreamingAsync(request, RequestTimeout);
        var diagnostic = $"--- response ---\n{string.Join("\n", lines)}\n--- stderr ---\n{server.StdErr}";
        foreach (var line in lines)
            Assert.False(line.TrimStart().StartsWith("{\"error\"", StringComparison.Ordinal), $"{label}: {diagnostic}");
        var (_, summary) = ProtocolV2Streaming.Split(lines);
        Assert.True(summary.GetProperty("total").GetInt32() == 1 && summary.GetProperty("failed").GetInt32() == 0
            && summary.GetProperty("errors").GetInt32() == 0, $"{label}: {diagnostic}");
        Assert.DoesNotContain("Index was outside the bounds of the array", diagnostic);
        if (coverage)
        {
            // The report attributes Multi B to the folder this request compiled, as it does for the rows.
            var files = summary.GetProperty("coverage").EnumerateArray()
                .Select(f => f.GetProperty("file").GetString()!.Replace('\\', '/')).ToList();
            var expected = Path.GetFullPath(Path.Combine(_root, compiled, "MultiPair.Codeunit.al")).Replace('\\', '/');
            Assert.True(files.Contains(expected), $"{label}: the report names {string.Join(", ", files)}, not {expected}");
            var sibling = Path.GetFullPath(Path.Combine(_root, compiled == "bundle" ? "src" : "bundle", "MultiPair.Codeunit.al")).Replace('\\', '/');
            Assert.DoesNotContain(sibling, files);
        }
        return diagnostic;
    }

    // bundle -> src -> bundle: the first transition is sequence A of the issue, the second is B.
    // The first request alone passes on every build; the later two are the claim.
    [SkippableFact]
    public async Task ARequestThatTakesTheAppFromAnotherFolder_ReadsItsOwnRows()
    {
        TestArtifacts.SkipIfMissing();
        Layout();
        await using var server = await CliServer.StartAsync(new[] { "--cache", Path.Combine(_root, "cache") });

        await RunAsync(server, "bundle", "tests-bundle", "1 bundle (first request, alone)");
        await RunAsync(server, "src", "tests-src", "2 src, after bundle");
        await RunAsync(server, "bundle", "tests-bundle", "3 bundle, after src");
    }

    // The same sequence with the report requested too: both readers of the compiled text (the
    // report's map and the Code Coverage rows' map) follow the request, in one process.
    [SkippableFact]
    public async Task WithCoverageRequested_TheReportAndTheRows_BothFollowTheRequest()
    {
        TestArtifacts.SkipIfMissing();
        Layout();
        await using var server = await CliServer.StartAsync(new[] { "--cache", Path.Combine(_root, "cache") });

        await RunAsync(server, "bundle", "tests-bundle", "1 bundle, coverage", coverage: true);
        await RunAsync(server, "src", "tests-src", "2 src after bundle, coverage", coverage: true);
        await RunAsync(server, "bundle", "tests-bundle", "3 bundle after src, coverage", coverage: true);
    }
}
