// ServerCoveragePackagedDependencyTests — #4273 review (PR #4982).
//
// The packaged-dependency registry names a PATH that is read again when a coverage map is built.
// Held for the whole process, it let one --server request's package be re-read by a later,
// unrelated request: after the package left the disk, every coverage request failed with
// "Could not find a part of the path". The registry is now per request, and a request that
// reuses an already-loaded package registers it again.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerCoveragePackagedDependencyTests
{
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "CoverageDependencySource"));
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(240);
    private const int TwiceLine = 9;

    private static string Request(string bundle, bool coverage) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = new[] { bundle },
        packagePaths = Array.Empty<string>(),
        coverage,
    });

    private static async Task<JsonElement> RunAsync(CliServer server, string request, string label)
    {
        var lines = await server.SendRequestStreamingAsync(request, RequestTimeout);
        var diagnostic = $"--- response ---\n{string.Join("\n", lines)}\n--- stderr ---\n{server.StdErr}";
        foreach (var line in lines)
            Assert.False(line.TrimStart().StartsWith("{\"error\"", StringComparison.Ordinal), $"{label}: {diagnostic}");
        var (_, summary) = ProtocolV2Streaming.Split(lines);
        Assert.True(summary.GetProperty("failed").GetInt32() == 0, $"{label}: {diagnostic}");
        return summary;
    }

    private static JsonElement? SubjectFile(JsonElement summary) =>
        summary.TryGetProperty("coverage", out var coverage)
            ? coverage.EnumerateArray().Cast<JsonElement?>().FirstOrDefault(f =>
                f!.Value.GetProperty("file").GetString()!.Replace('\\', '/')
                    .EndsWith("/src/CdsSubject.Codeunit.al", StringComparison.Ordinal))
            : null;

    [SkippableFact]
    public async Task ALaterRequest_DoesNotReadAPackageOnlyAnEarlierRequestResolved()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-4273-server");
        try
        {
            // The packaged-only consumer, and an unrelated bundle with no dependencies at all.
            var isolated = Path.Combine(root, "isolated", "tests");
            var packages = Path.Combine(isolated, ".alpackages");
            Directory.CreateDirectory(packages);
            foreach (var f in Directory.GetFiles(Path.Combine(FixtureRoot, "main")))
                File.Copy(f, Path.Combine(isolated, Path.GetFileName(f)));
            File.WriteAllBytes(Path.Combine(packages, CoveragePackagedDependencyTests.DepPackageFile),
                CoveragePackagedDependencyTests.BuildSubjectApp());

            var unrelated = Path.Combine(root, "unrelated");
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "app.json"), """
                { "id": "5b7f0c0e-4c1e-4d7c-9a51-2f6e0d9a7e12", "name": "Runner Tests Fixture - Coverage Unrelated",
                  "publisher": "AL Runner", "version": "1.0.0.0", "dependencies": [],
                  "idRanges": [ { "from": 79870, "to": 79879 } ], "runtime": "15.0", "target": "OnPrem" }
                """);
            File.WriteAllText(Path.Combine(unrelated, "Unrelated.Codeunit.al"), """
                codeunit 79870 "Cov Unrelated Tests"
                {
                    Subtype = Test;

                    [Test]
                    procedure One()
                    begin
                        if 1 + 1 <> 2 then
                            Error('arithmetic');
                    end;
                }
                """);

            await using var server = await CliServer.StartAsync(new[] { "--cache", Path.Combine(root, "cache") });

            // 1. Resolves and compiles the package, without coverage: nothing is materialized.
            await RunAsync(server, Request(isolated, coverage: false), "1 packaged consumer, no coverage");

            // 2. The package leaves the disk; an unrelated request asks for coverage.
            var moved = Path.Combine(root, "moved-alpackages");
            Directory.Move(packages, moved);
            var second = await RunAsync(server, Request(unrelated, coverage: true), "2 unrelated, coverage");
            Assert.Null(SubjectFile(second));
            // Nothing of the earlier request's package may reach this map: not as a root to read,
            // and not as a scan failure marking this unrelated report incomplete.
            Assert.True(!second.TryGetProperty("sourceScanFailures", out var failures)
                    || failures.ValueKind == JsonValueKind.Null
                    || failures.GetArrayLength() == 0,
                $"2: the unrelated request's coverage was marked incomplete: {second}");
            Directory.Move(moved, packages);

            // 3. The package is reused from the loader's cache, not reloaded, and must be attributed.
            var third = await RunAsync(server, Request(isolated, coverage: true), "3 packaged consumer reused, coverage");
            var subject = SubjectFile(third);
            Assert.True(subject is not null, $"3: the reused package is absent from the coverage. stderr:\n{server.StdErr}");
            var twice = subject!.Value.GetProperty("statements").EnumerateArray()
                .FirstOrDefault(s => s.GetProperty("line").GetInt32() == TwiceLine);
            Assert.True(twice.ValueKind == JsonValueKind.Object && twice.GetProperty("hits").GetInt32() == 1,
                $"3: line {TwiceLine} must carry Twice()'s one hit");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
