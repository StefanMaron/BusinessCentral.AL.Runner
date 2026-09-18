// ServerCoverageDependencySourceTests — issue #4272.
//
// #4271 fixed the CLI --coverage path for #3965; the two SERVER coverage sites were left
// building their map from the request's own sourcePaths, so a statement executed in a sibling
// SOURCE dependency is tracked and then dropped. The sites, by handler:
//
//   runTests  AlCoverageSourceMap.Build(req.SourcePaths, ...)
//   execute   AlCoverageSourceMap.Build(sourcePaths, ...)
//
// Both arguments are the SAME variable each handler passes to RunAllBundlesForServer as its
// execution roots, so neither is a caller-supplied coverage filter — see the PR body.
//
// Fixture: Fixtures/CoverageDependencySource. `dep` owns Twice() (executed) and Never() (not),
// so a fix that attributes every dependency line fails as loudly as the drop it replaces.
// `run` is this issue's addition — `main`'s only codeunit is Subtype = Test and execute runs
// the lowest-object-id OnRun-bearing codeunit, so it cannot reach the execute site.
//
// Every request is sent TWICE against one server and one --cache root, and both answers are
// asserted. The roots come from a registry populated while the compile reads folders, so a
// warm request that serves the dependency from cache is the arm that could lose them
// (.claude/rules/local-test-scope.md).

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerCoverageDependencySourceTests
{
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "CoverageDependencySource"));

    private static string Bundle(string name) => Path.Combine(FixtureRoot, name);

    // Read from dep/CdsSubject.Codeunit.al, not inferred.
    private const int TwiceLine = 9;
    private const int NeverLine = 14;

    // Both consumers are written to put these on the same lines: an un-taken statement INSIDE
    // the executed scope, and the body of a procedure that is never called at all.
    private const int ConsumerUntakenLine = 13;      // Error('expected 42, actual %1', Actual);
    private const int ConsumerNeverRunLine = 23;     // exit(Value * 5);  — in NeverIn*Consumer

    private const string DepFileSuffix = "/CdsSubject.Codeunit.al";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(240);

    /// <summary>
    /// The statement entries the response attributes to the dependency's own source file, or
    /// null when the file is absent from the report altogether — the two states #4272 is about,
    /// and they must not be conflated: an absent file is the defect, an empty statement list
    /// would be a different one.
    /// </summary>
    private static List<JsonElement>? DepStatements(JsonElement coverage)
    {
        var file = coverage.EnumerateArray().FirstOrDefault(f =>
            f.GetProperty("file").GetString()!.Replace('\\', '/')
                .EndsWith(DepFileSuffix, StringComparison.Ordinal));
        if (file.ValueKind != JsonValueKind.Object) return null;
        return file.GetProperty("statements").EnumerateArray().ToList();
    }

    private static int? HitsAt(List<JsonElement> statements, int line)
    {
        var stmt = statements.FirstOrDefault(s => s.GetProperty("line").GetInt32() == line);
        return stmt.ValueKind == JsonValueKind.Object ? stmt.GetProperty("hits").GetInt32() : null;
    }

    /// <summary>
    /// The whole claim, applied to one response. <paramref name="label"/> names which request
    /// it was, because the cold and warm arms fail for different reasons.
    /// </summary>
    private static void AssertDependencyAttributed(JsonElement coverage, string label, string diagnostic)
    {
        var statements = DepStatements(coverage);
        Assert.True(statements is not null,
            $"{label}: the dependency's source file is absent from the server's coverage report "
            + $"although its statement executed (#4272). Files present: "
            + $"{string.Join(", ", coverage.EnumerateArray().Select(f => f.GetProperty("file").GetString()))}"
            + $"\n{diagnostic}");

        var twice = HitsAt(statements!, TwiceLine);
        Assert.True(twice is not null,
            $"{label}: line {TwiceLine} (Twice) is missing from the dependency's statements.\n{diagnostic}");
        Assert.True(twice == 1,
            $"{label}: Twice() was called exactly once, so line {TwiceLine} must carry exactly one "
            + $"hit; got {twice}.\n{diagnostic}");

        // THE over-attribution control. A fix that lists the dependency's LINES rather than its
        // executed statements reports Never()'s body as covered; this is what catches it.
        var never = HitsAt(statements!, NeverLine);
        Assert.True(never is null or 0,
            $"{label}: Never() is not called, so line {NeverLine} must never carry a hit; got {never} "
            + $"— the report is attributing the dependency's lines, not its executed statements.\n{diagnostic}");
    }

    /// <summary>
    /// The consuming bundle was attributed before this fix and must still be.
    ///
    /// <para>It does NOT catch "swapped one root set for another rather than widening it", which
    /// an earlier version of this comment claimed: measured in review, dropping the
    /// execution-roots loop from RootsWithParsedSourceDependencies entirely leaves this green,
    /// because Program.cs registers the execution bundles' own suite dirs as source dirs anyway,
    /// so the registry alone still covers them. The helper's own comment carries the honest
    /// version ("insurance, not a demonstrated requirement").</para>
    ///
    /// <para>It also carries the discrimination that makes <c>Never()</c>'s ABSENCE above
    /// readable. Within this one response the consumer shows a hits:0 statement (the un-taken
    /// Error branch, inside a scope that ran) AND no entry at all for a procedure that was never
    /// called. So zero-hit statements are plainly not being dropped, and a never-run scope is
    /// plainly not listed — for the consumer and the dependency alike, which is the claim: the
    /// dependency became an ordinary parsed root. AlCoverageTracker.GetHitTrackedTypes is why,
    /// and it is deliberate (a warm server holds stale assembly generations whose types would
    /// otherwise emit phantom hits:0 twins).</para>
    /// </summary>
    private static void AssertConsumerAttributedTheSameWay(
        JsonElement coverage, string consumerFileSuffix, string label, string diagnostic)
    {
        var file = coverage.EnumerateArray().FirstOrDefault(f =>
            f.GetProperty("file").GetString()!.Replace('\\', '/')
                .EndsWith(consumerFileSuffix, StringComparison.Ordinal));
        Assert.True(file.ValueKind == JsonValueKind.Object,
            $"{label}: the execution bundle's own file {consumerFileSuffix} vanished from the report — "
            + "the execution roots must be widened, never replaced. Files present: "
            + string.Join(", ", coverage.EnumerateArray().Select(f => f.GetProperty("file").GetString())));

        var statements = file.GetProperty("statements").EnumerateArray().ToList();

        var untaken = HitsAt(statements, ConsumerUntakenLine);
        Assert.True(untaken == 0,
            $"{label}: line {ConsumerUntakenLine} is the un-taken branch of a scope that DID run, so it "
            + $"must be listed with no hit; got {untaken}. Without this, Never()'s absence could just "
            + $"mean zero-hit statements are dropped.\n{diagnostic}");

        Assert.True(HitsAt(statements, ConsumerNeverRunLine) is null,
            $"{label}: the consumer's own never-called procedure (line {ConsumerNeverRunLine}) is listed, "
            + $"so a never-run scope IS reported here and the dependency's Never() being absent would be "
            + $"an asymmetry rather than the documented behaviour.\n{diagnostic}");
    }

    /// <summary>
    /// Site 7188 (runTests). RED: `coverage` has no entry for dep/CdsSubject.Codeunit.al,
    /// because req.SourcePaths names only the consuming bundle. GREEN: it is there, with a hit
    /// on Twice and none on Never, cold AND warm.
    /// </summary>
    [SkippableFact]
    public async Task RunTests_SiblingSourceDependency_IsAttributedInServerCoverage()
    {
        TestArtifacts.SkipIfMissing();

        var cacheDir = TestScratch.Dir("al-runner-4272-runtests-cache");
        try
        {
            await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir, "--verbose" });

            var request = JsonSerializer.Serialize(new
            {
                command = "runTests",
                sourcePaths = new[] { Bundle("main") },
                packagePaths = Array.Empty<string>(),
                coverage = true,
            });

            foreach (var label in new[] { "cold", "warm (same server, same --cache root)" })
            {
                var lines = await server.SendRequestStreamingAsync(request, RequestTimeout);
                var (_, summary) = ProtocolV2Streaming.Split(lines);
                var diagnostic = $"--- response ---\n{string.Join("\n", lines)}\n--- stderr ---\n{server.StdErr}";

                Assert.Equal(0, summary.GetProperty("failed").GetInt32());
                Assert.True(summary.TryGetProperty("coverage", out var coverage),
                    $"{label}: coverage:true produced no coverage array.\n{diagnostic}");

                AssertConsumerAttributedTheSameWay(coverage, "/CdsTests.Codeunit.al", label, diagnostic);
                AssertDependencyAttributed(coverage, label, diagnostic);
            }
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Site 7528 (execute). The same claim through the other handler — it is a separate
    /// expression on a separate variable, so a fix to runTests alone leaves this RED.
    /// </summary>
    [SkippableFact]
    public async Task Execute_SiblingSourceDependency_IsAttributedInServerCoverage()
    {
        TestArtifacts.SkipIfMissing();

        var cacheDir = TestScratch.Dir("al-runner-4272-execute-cache");
        try
        {
            await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir, "--verbose" });

            var request = JsonSerializer.Serialize(new
            {
                command = "execute",
                sourcePaths = new[] { Bundle("run") },
                packagePaths = Array.Empty<string>(),
                coverage = true,
            });

            foreach (var label in new[] { "cold", "warm (same server, same --cache root)" })
            {
                var raw = await server.SendAsync(request, RequestTimeout);
                var response = JsonSerializer.Deserialize<JsonElement>(raw);
                var diagnostic = $"--- response ---\n{raw}\n--- stderr ---\n{server.StdErr}";

                Assert.False(response.TryGetProperty("error", out _), $"{label}: {diagnostic}");
                Assert.True(response.TryGetProperty("coverage", out var coverage),
                    $"{label}: coverage:true produced no coverage array.\n{diagnostic}");

                AssertConsumerAttributedTheSameWay(coverage, "/CdsRun.Codeunit.al", label, diagnostic);
                AssertDependencyAttributed(coverage, label, diagnostic);
            }
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
