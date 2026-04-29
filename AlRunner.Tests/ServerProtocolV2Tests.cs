using System.Text.Json;
using Newtonsoft.Json.Schema;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Protocol-v2 streaming runtests tests. Each test asserts on the wire-format
/// JSON shape directly via JsonDocument so changes to internal types can't
/// silently drift the protocol contract.
///
/// Most tests use the protocol-v2-line-directives fixture (3 tests: 2 pass + 1
/// fail) so we exercise both pass and fail event shapes in a single fixture.
/// </summary>
public class ServerProtocolV2Tests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string FixturePath(string testCase, string sub) =>
        Path.Combine(RepoRoot, "tests", testCase, sub);

    private static readonly string LineDirectivesSrc = FixturePath("protocol-v2-line-directives", "src");
    private static readonly string LineDirectivesTest = FixturePath("protocol-v2-line-directives", "test");
    private static readonly string PerTestIsolationSrc = FixturePath("protocol-v2-per-test-isolation", "src");
    private static readonly string PerTestIsolationTest = FixturePath("protocol-v2-per-test-isolation", "test");
    private static readonly string BrokenSyntaxSrc = FixturePath("protocol-v2-broken-syntax", "src");
    private static readonly string BrokenSyntaxTest = FixturePath("protocol-v2-broken-syntax", "test");

    /// <summary>Build a runTests request JSON for the line-directives fixture.</summary>
    private static string LineDirectivesRequest(object? extra = null)
    {
        if (extra == null)
        {
            return JsonSerializer.Serialize(new
            {
                command = "runtests",
                sourcePaths = new[] { LineDirectivesSrc, LineDirectivesTest }
            });
        }
        // Merge extra fields by serializing two objects into one. Easier than
        // dynamic merge: project both into a single anonymous object via reflection.
        var baseDict = new Dictionary<string, object?>
        {
            ["command"] = "runtests",
            ["sourcePaths"] = new[] { LineDirectivesSrc, LineDirectivesTest },
        };
        foreach (var prop in extra.GetType().GetProperties())
        {
            baseDict[prop.Name] = prop.GetValue(extra);
        }
        return JsonSerializer.Serialize(baseDict);
    }

    private static (List<JsonDocument> TestEvents, JsonDocument Summary) Split(IReadOnlyList<string> lines)
    {
        var events = new List<JsonDocument>();
        JsonDocument? summary = null;
        foreach (var line in lines)
        {
            var doc = JsonDocument.Parse(line);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "summary")
            {
                summary = doc;
            }
            else if (type == "test")
            {
                events.Add(doc);
            }
            // progress lines (if any) are ignored for these assertions.
        }
        Assert.NotNull(summary);
        return (events, summary!);
    }

    [Fact]
    public async Task RunTests_StreamsTestLinesThenSummary()
    {
        await using var server = await CliServer.StartAsync();
        var lines = await server.SendRequestStreamingAsync(LineDirectivesRequest());

        // At least 2 lines: at least one test + the summary terminator
        Assert.True(lines.Count >= 2, $"expected >=2 lines, got {lines.Count}");

        // Last line must be summary
        var lastDoc = JsonDocument.Parse(lines[^1]);
        Assert.Equal("summary", lastDoc.RootElement.GetProperty("type").GetString());

        // Every non-final line must be type=test or type=progress
        for (var i = 0; i < lines.Count - 1; i++)
        {
            var doc = JsonDocument.Parse(lines[i]);
            Assert.True(doc.RootElement.TryGetProperty("type", out var t),
                $"line {i} has no `type` field: {lines[i]}");
            var typeName = t.GetString();
            Assert.True(typeName == "test" || typeName == "progress",
                $"line {i} has unexpected type {typeName}: {lines[i]}");
        }
    }

    [Fact]
    public async Task RunTests_SummaryHasProtocolVersion2()
    {
        await using var server = await CliServer.StartAsync();
        var lines = await server.SendRequestStreamingAsync(LineDirectivesRequest());
        var (_, summary) = Split(lines);

        Assert.Equal(2, summary.RootElement.GetProperty("protocolVersion").GetInt32());
    }

    [Fact]
    public async Task RunTests_FailingTestLine_HasAlSourceLineAndErrorKind()
    {
        await using var server = await CliServer.StartAsync();
        var lines = await server.SendRequestStreamingAsync(LineDirectivesRequest());
        var (events, _) = Split(lines);

        // Find the failing test event (FailingTest in the fixture)
        var failEvent = events.FirstOrDefault(e =>
            e.RootElement.GetProperty("status").GetString() == "fail");
        Assert.NotNull(failEvent);

        var root = failEvent!.RootElement;
        Assert.True(root.TryGetProperty("alSourceLine", out var line),
            "failing test event must carry alSourceLine");
        Assert.True(line.GetInt32() > 0, "alSourceLine must be 1-based positive integer");

        Assert.True(root.TryGetProperty("alSourceFile", out var file));
        Assert.EndsWith(".al", file.GetString(), StringComparison.OrdinalIgnoreCase);

        Assert.True(root.TryGetProperty("errorKind", out var errorKind));
        var kindStr = errorKind.GetString();
        Assert.False(string.IsNullOrEmpty(kindStr));
        // Lower-case enum names, per schema
        Assert.Contains(kindStr, new[] { "assertion", "runtime", "compile", "setup", "timeout", "unknown" });
    }

    [Fact]
    public async Task RunTests_TestFilterApplied()
    {
        await using var server = await CliServer.StartAsync();
        var request = LineDirectivesRequest(new
        {
            testFilter = new { procNames = new[] { "ComputeDoubles" } }
        });
        var lines = await server.SendRequestStreamingAsync(request);
        var (events, summary) = Split(lines);

        Assert.Single(events);
        Assert.Equal("ComputeDoubles", events[0].RootElement.GetProperty("name").GetString());
        Assert.Equal(1, summary.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, summary.RootElement.GetProperty("passed").GetInt32());
    }

    [Fact]
    public async Task RunTests_CoverageInSummary_WhenCoverageTrue()
    {
        await using var server = await CliServer.StartAsync();
        var request = LineDirectivesRequest(new { coverage = true });
        var lines = await server.SendRequestStreamingAsync(request);
        var (_, summary) = Split(lines);

        Assert.True(summary.RootElement.TryGetProperty("coverage", out var coverage),
            "summary must include `coverage` when coverage=true");
        Assert.Equal(JsonValueKind.Array, coverage.ValueKind);
        Assert.True(coverage.GetArrayLength() > 0,
            $"coverage array must be non-empty; got {coverage.GetRawText()}");

        // First file entry must have totalStatements > 0 and a `lines` array
        var firstFile = coverage[0];
        Assert.True(firstFile.GetProperty("totalStatements").GetInt32() > 0);
        Assert.Equal(JsonValueKind.Array, firstFile.GetProperty("lines").ValueKind);
    }

    [Fact]
    public async Task RunTests_NoCoverageInSummary_WhenCoverageOmitted()
    {
        await using var server = await CliServer.StartAsync();
        var lines = await server.SendRequestStreamingAsync(LineDirectivesRequest());
        var (_, summary) = Split(lines);

        // The serializer drops null fields, so the property should be absent entirely.
        // Tolerate (but require to be empty) the case where it is present.
        if (summary.RootElement.TryGetProperty("coverage", out var coverage))
        {
            Assert.True(
                coverage.ValueKind == JsonValueKind.Null ||
                (coverage.ValueKind == JsonValueKind.Array && coverage.GetArrayLength() == 0),
                $"coverage must be absent or empty when not requested; got {coverage.GetRawText()}");
        }
    }

    [Fact]
    public async Task RunTests_StackFramesPresent_OnFailingTest()
    {
        await using var server = await CliServer.StartAsync();
        var lines = await server.SendRequestStreamingAsync(LineDirectivesRequest());
        var (events, _) = Split(lines);

        var failEvent = events.FirstOrDefault(e =>
            e.RootElement.GetProperty("status").GetString() == "fail");
        Assert.NotNull(failEvent);

        Assert.True(failEvent!.RootElement.TryGetProperty("stackFrames", out var frames));
        Assert.Equal(JsonValueKind.Array, frames.ValueKind);
        Assert.True(frames.GetArrayLength() > 0, "stackFrames must contain at least one frame");

        // At least one frame should be a user (.al) frame with source.path ending .al
        bool foundUserFrame = false;
        foreach (var frame in frames.EnumerateArray())
        {
            if (!frame.TryGetProperty("source", out var source)) continue;
            if (source.ValueKind == JsonValueKind.Null) continue;
            if (!source.TryGetProperty("path", out var path)) continue;
            var pathStr = path.GetString();
            if (pathStr != null && pathStr.EndsWith(".al", StringComparison.OrdinalIgnoreCase))
            {
                foundUserFrame = true;
                break;
            }
        }
        Assert.True(foundUserFrame, "at least one stackFrame must reference a .al file");
    }

    [Fact]
    public async Task RunTests_CapturedMessages_InTestEvent()
    {
        await using var server = await CliServer.StartAsync();
        var request = JsonSerializer.Serialize(new
        {
            command = "runtests",
            sourcePaths = new[] { PerTestIsolationSrc, PerTestIsolationTest }
        });
        var lines = await server.SendRequestStreamingAsync(request);
        var (events, _) = Split(lines);

        Assert.Equal(2, events.Count);

        // TestA emits Message('from A'); TestB emits Message('from B').
        // Per-test isolation means TestA's messages must not leak into TestB.
        var testA = events.First(e => e.RootElement.GetProperty("name").GetString() == "TestA");
        var testB = events.First(e => e.RootElement.GetProperty("name").GetString() == "TestB");

        Assert.True(testA.RootElement.TryGetProperty("messages", out var msgsA));
        Assert.Equal(JsonValueKind.Array, msgsA.ValueKind);
        Assert.Single(msgsA.EnumerateArray());
        Assert.Equal("from A", msgsA[0].GetString());

        Assert.True(testB.RootElement.TryGetProperty("messages", out var msgsB));
        Assert.Equal(JsonValueKind.Array, msgsB.ValueKind);
        Assert.Single(msgsB.EnumerateArray());
        Assert.Equal("from B", msgsB[0].GetString());
    }

    [Fact]
    public async Task RunTests_SecondRun_ReportsCachedTrue_InSummary()
    {
        await using var server = await CliServer.StartAsync();

        var lines1 = await server.SendRequestStreamingAsync(LineDirectivesRequest());
        var (_, summary1) = Split(lines1);
        Assert.False(summary1.RootElement.GetProperty("cached").GetBoolean(),
            "first request should be a cache miss");

        var lines2 = await server.SendRequestStreamingAsync(LineDirectivesRequest());
        var (_, summary2) = Split(lines2);
        Assert.True(summary2.RootElement.GetProperty("cached").GetBoolean(),
            "second identical request should be a cache hit");

        // Result counts must match across runs.
        Assert.Equal(
            summary1.RootElement.GetProperty("total").GetInt32(),
            summary2.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task RunTests_UnknownTestFilterProc_ReturnsZeroTestEvents()
    {
        await using var server = await CliServer.StartAsync();
        var request = LineDirectivesRequest(new
        {
            testFilter = new { procNames = new[] { "NotARealTest" } }
        });
        var lines = await server.SendRequestStreamingAsync(request);
        var (events, summary) = Split(lines);

        Assert.Empty(events);
        Assert.Equal(0, summary.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(0, summary.RootElement.GetProperty("passed").GetInt32());
    }

    [Fact]
    public async Task RunTests_PassingTestEvent_HasNameStatusDuration()
    {
        // Lock the protocol-v2 schema for passing tests at the wire level.
        await using var server = await CliServer.StartAsync();
        var lines = await server.SendRequestStreamingAsync(LineDirectivesRequest());
        var (events, _) = Split(lines);

        var pass = events.First(e => e.RootElement.GetProperty("status").GetString() == "pass");
        Assert.Equal("test", pass.RootElement.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(pass.RootElement.GetProperty("name").GetString()));
        Assert.True(pass.RootElement.GetProperty("durationMs").GetInt64() >= 0);
        // A passing test must NOT carry an errorKind/alSourceLine — those are
        // populated only on fail/error per the schema.
        Assert.False(pass.RootElement.TryGetProperty("errorKind", out _),
            "errorKind must be absent on passing tests");
        Assert.False(pass.RootElement.TryGetProperty("alSourceLine", out _),
            "alSourceLine must be absent on passing tests");
    }

    [Fact]
    public async Task RunTests_MissingSourcePaths_EmitsSummaryWithError()
    {
        // sourcePaths is required; the server emits a single summary line carrying
        // error+exitCode so the client gets a uniform terminator shape.
        await using var server = await CliServer.StartAsync();
        var request = JsonSerializer.Serialize(new { command = "runtests" });
        var lines = await server.SendRequestStreamingAsync(request);

        Assert.Single(lines);
        var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("summary", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrEmpty(error.GetString()));
    }

    /// <summary>
    /// I-3: lock the test-event ordering contract. <see cref="Executor.RunTests"/> walks
    /// reflection metadata (assembly.GetTypes() → type.GetNestedTypes()), which the Roslyn
    /// transpiler emits in alphabetical-by-class-name order — so events arrive sorted by
    /// test name. This is a stable contract: no parallelism, no run-to-run shuffling. A
    /// future change that reorders the stream (e.g. introducing parallel test execution
    /// without an explicit ordering pass) would break clients that build incremental UI
    /// trees from the wire stream, so we lock the order at the protocol boundary.
    ///
    /// The line-directives fixture declares ComputeDoubles, FailingTest,
    /// ConditionalBranchExercises in that source order; reflection sorts them as
    /// ComputeDoubles, ConditionalBranchExercises, FailingTest.
    /// </summary>
    [Fact]
    public async Task RunTests_TestEvents_AreEmittedInStableOrder()
    {
        await using var server = await CliServer.StartAsync();
        var lines = await server.SendRequestStreamingAsync(LineDirectivesRequest());

        var firstRun = lines
            .Where(l => l.Contains("\"type\":\"test\""))
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("name").GetString())
            .ToList();

        // Three tests, all present, in a known fixed order.
        Assert.Equal(
            new[] { "ComputeDoubles", "ConditionalBranchExercises", "FailingTest" },
            firstRun);

        // Re-run on the same server (cache hit) and verify the order is identical —
        // run-to-run shuffling would mean clients can't rely on the wire order.
        var lines2 = await server.SendRequestStreamingAsync(LineDirectivesRequest());
        var secondRun = lines2
            .Where(l => l.Contains("\"type\":\"test\""))
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("name").GetString())
            .ToList();
        Assert.Equal(firstRun, secondRun);
    }

    /// <summary>
    /// I-2: when compilation fails, the server must still emit exactly one summary line
    /// (no test events) carrying a non-zero exitCode and protocolVersion 2. This locks
    /// the assembly==null short-circuit in HandleRunTests and proves the wire-shape
    /// contract for the compile-failure path.
    /// </summary>
    [Fact]
    public async Task RunTests_CompilationFailure_EmitsSummaryWithExitCodeNonZero()
    {
        await using var server = await CliServer.StartAsync();
        var request = JsonSerializer.Serialize(new
        {
            command = "runtests",
            sourcePaths = new[] { BrokenSyntaxSrc, BrokenSyntaxTest }
        });
        var lines = await server.SendRequestStreamingAsync(request);

        // Exactly zero test events.
        var testEventCount = lines.Count(l => l.Contains("\"type\":\"test\""));
        Assert.Equal(0, testEventCount);

        // Exactly one summary line and it terminates the stream.
        var summaryLines = lines.Where(l => l.Contains("\"type\":\"summary\"")).ToList();
        Assert.Single(summaryLines);
        Assert.Equal(summaryLines[0], lines[^1]);

        var summary = JsonDocument.Parse(summaryLines[0]).RootElement;
        Assert.Equal(2, summary.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(0, summary.GetProperty("total").GetInt32());
        Assert.Equal(0, summary.GetProperty("passed").GetInt32());
        Assert.Equal(0, summary.GetProperty("failed").GetInt32());
        Assert.NotEqual(0, summary.GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// I-5: when the request sets cobertura:true the server must write cobertura.xml in
    /// its working directory (the repo root). The file must be valid-looking XML
    /// containing a &lt;coverage&gt; root.
    /// </summary>
    [Fact]
    public async Task RunTests_CoberturaTrue_WritesCoberturaXml()
    {
        var coberturaPath = Path.Combine(CliServer.RepoRoot, "cobertura.xml");
        if (File.Exists(coberturaPath)) File.Delete(coberturaPath);
        try
        {
            await using var server = await CliServer.StartAsync();
            var request = LineDirectivesRequest(new { cobertura = true });
            var lines = await server.SendRequestStreamingAsync(request);

            // Sanity: the request itself succeeded (we got a summary terminator).
            var (_, summary) = Split(lines);
            Assert.Equal(2, summary.RootElement.GetProperty("protocolVersion").GetInt32());

            Assert.True(File.Exists(coberturaPath),
                $"Expected cobertura.xml at {coberturaPath} after cobertura:true request.");

            var xml = File.ReadAllText(coberturaPath);
            // Strip a possible UTF-8 BOM before checking the prologue.
            var trimmed = xml.TrimStart('﻿');
            Assert.StartsWith("<?xml", trimmed);
            Assert.Contains("<coverage", trimmed);
        }
        finally
        {
            if (File.Exists(coberturaPath)) File.Delete(coberturaPath);
        }
    }

    /// <summary>
    /// I-1: with the dispatch loop concurrency-aware, a cancel arriving on stdin while
    /// runtests is still streaming must be honored — observable by either:
    ///   - the summary carries cancelled:true (executor stopped between tests), OR
    ///   - the cancel ack came back with noop:false (cts was active when the cancel
    ///     was processed; the executor happened to finish before the next iteration
    ///     could observe the token).
    /// Both prove the side-channel dispatch worked. The race between "cancel signaled"
    /// and "executor completed the last test" is real but doesn't matter for this
    /// assertion — what we're locking is "cancel WAS processed during streaming."
    /// </summary>
    [Fact]
    public async Task RunTests_CancelDuringRun_AckArrivesWhileStreaming()
    {
        await using var server = await CliServer.StartAsync();
        // Use the line-directives fixture (3 tests). After the first test event we send
        // cancel; with cooperative cancellation between tests this normally lands before
        // tests 2-3 complete, but we accept either outcome.
        var request = LineDirectivesRequest();
        var (lines, ackLine) = await server.SendRequestAndCancelAfterFirstTestAsync(request);

        Assert.NotNull(ackLine); // ack MUST arrive during streaming — proves concurrent dispatch.
        var ack = JsonDocument.Parse(ackLine!).RootElement;
        Assert.Equal("ack", ack.GetProperty("type").GetString());
        Assert.Equal("cancel", ack.GetProperty("command").GetString());

        // Find the summary line.
        var summaryLine = lines.Last(l => l.Contains("\"type\":\"summary\""));
        var summary = JsonDocument.Parse(summaryLine).RootElement;

        var ackNoop = ack.GetProperty("noop").GetBoolean();
        var cancelledOnSummary =
            summary.TryGetProperty("cancelled", out var c) && c.GetBoolean();

        // Either we caught the cancel before the next test ran (cancelled:true) OR the
        // executor finished the last test concurrently with our cancel snapshot
        // (noop:false on the ack). At least one must hold; otherwise the cancel was
        // never observed by the runtests CTS, which would mean the side-channel
        // dispatch failed.
        Assert.True(cancelledOnSummary || !ackNoop,
            $"Neither cancelled:true on summary nor noop:false on ack — cancel was not observed. summary={summary.GetRawText()} ack={ack.GetRawText()}");
    }

    /// <summary>
    /// S-1: validate every emitted NDJSON line against the formal protocol-v2 schema.
    /// Exercises as many schema branches as possible (test events, summary, ack, coverage,
    /// capturedValues) by using the line-directives fixture with coverage:true and
    /// captureValues:true.
    ///
    /// This test is the canonical canary for schema/emitter drift: if Server.cs emits a
    /// field the schema doesn't permit, or omits a required field, the test catches it
    /// with an actionable per-line error message.
    /// </summary>
    [Fact]
    public async Task RunTests_AllEmittedLines_ValidateAgainstSchema()
    {
        var schemaPath = Path.Combine(RepoRoot, "protocol-v2.schema.json");
        var schemaJson = await File.ReadAllTextAsync(schemaPath);
        var schema = Newtonsoft.Json.Schema.JSchema.Parse(schemaJson);

        await using var server = await CliServer.StartAsync();

        // Use the line-directives fixture with coverage:true and captureValues:true to
        // exercise as many schema branches as possible (test events, summary, coverage
        // entries, capturedValues items).
        var request = LineDirectivesRequest(new { coverage = true, captureValues = true });
        var lines = await server.SendRequestStreamingAsync(request);

        Assert.NotEmpty(lines);
        var failures = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Newtonsoft.Json.Linq.JToken token;
            try
            {
                token = Newtonsoft.Json.Linq.JToken.Parse(line);
            }
            catch (Exception ex)
            {
                failures.Add($"Non-JSON line: {ex.Message}\nLine: {line}");
                continue;
            }
            if (!token.IsValid(schema, out IList<string> errors))
            {
                failures.Add($"Schema rejected line:\n{line}\nErrors:\n  - {string.Join("\n  - ", errors)}");
            }
        }
        Assert.True(failures.Count == 0,
            $"Schema validation found {failures.Count} failure(s):\n\n{string.Join("\n\n", failures)}");
    }
}
