using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #1641 (streaming runTests slice): --server's <c>runTests</c> now emits the
/// protocol-v2 NDJSON shape (<c>protocol-v2.schema.json</c>) — one
/// <c>{"type":"test"}</c> line per completed test, then exactly one
/// <c>{"type":"summary", protocolVersion:2}</c> terminator — instead of a single
/// v1-shaped response object. <c>execute</c>/<c>shutdown</c>/unknown-command
/// responses are unaffected (still one line).
///
/// These spawn the real runner and need the BC artifact caches present
/// (~/.bcartifacts.cache); when absent, they skip rather than fail.
/// </summary>
[Collection("server-serial")]
public class ServerStreamingTests
{
    private static bool ArtifactsPresent()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home)) return false;
        return Directory.Exists(Path.Combine(home, ".bcartifacts.cache", "sandbox"));
    }

    // One passing test, one failing test (with a recognisable Error() message) —
    // exercises both status branches of the streamed `test` event shape.
    private static string MakeMixedBundle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-server-streaming", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "a1b2c3d4-e5f6-4708-a9ba-cbdcedfe0f11",
          "name": "Runner Extras - Server Streaming Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 60200, "to": 60209 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "StreamingTest.Codeunit.al"), """
        codeunit 60200 "Server Streaming Probe SX"
        {
            Subtype = Test;

            [Test]
            procedure PassingTest()
            begin
            end;

            [Test]
            procedure FailingTest()
            begin
                Error('streaming-probe-boom');
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

    [Fact]
    public async Task RunTests_StreamsOneTestEventPerTest_ThenSummary()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifact cache not present"); return; }

        var bundle = MakeMixedBundle();
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-server-streaming-cache", Guid.NewGuid().ToString("N"));
        await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir });

        var lines = await server.SendRequestStreamingAsync(RunTestsReq(bundle));

        // Exactly 2 test events (proves streaming happened per-test, not a
        // single batch line), each BEFORE the summary terminator.
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        Assert.Equal(2, events.Count);
        Assert.Equal("summary", JsonSerializer.Deserialize<JsonElement>(lines[^1]).GetProperty("type").GetString());

        var pass = events.Single(e => e.GetProperty("name").GetString()!.EndsWith("PassingTest"));
        Assert.Equal("pass", pass.GetProperty("status").GetString());
        Assert.True(pass.GetProperty("durationMs").GetInt64() >= 0);

        var fail = events.Single(e => e.GetProperty("name").GetString()!.EndsWith("FailingTest"));
        Assert.Equal("fail", fail.GetProperty("status").GetString());
        Assert.Contains("streaming-probe-boom", fail.GetProperty("message").GetString());

        // Summary carries the protocol-v2 contract and matches the streamed events.
        Assert.Equal(2, summary.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(1, summary.GetProperty("passed").GetInt32());
        Assert.Equal(1, summary.GetProperty("failed").GetInt32());
        Assert.Equal(0, summary.GetProperty("errors").GetInt32());
        Assert.Equal(2, summary.GetProperty("total").GetInt32());
        Assert.Equal(1, summary.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task RunTests_MissingSourcePaths_ReturnsSingleErrorLine_NoTestOrSummaryLines()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifact cache not present"); return; }

        await using var server = await CliServer.StartAsync();
        var lines = await server.SendRequestStreamingAsync(
            JsonSerializer.Serialize(new { command = "runTests" }));

        Assert.Single(lines);
        var d = JsonSerializer.Deserialize<JsonElement>(lines[0]);
        Assert.True(d.TryGetProperty("error", out var err));
        Assert.Contains("sourcePaths", err.GetString());
        Assert.False(d.TryGetProperty("type", out _));
    }

    [Fact]
    public async Task Execute_StillReturnsSingleLine_NotStreamed()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifact cache not present"); return; }

        var bundle = MakeMixedBundle();
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-server-streaming-exec-cache", Guid.NewGuid().ToString("N"));
        await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir });

        // execute (run-mode) is unaffected by the streaming change — still one
        // v1-shaped response line, no "type" discriminator.
        var lines = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            sourcePaths = new[] { bundle },
            packagePaths = Array.Empty<string>(),
        }));

        Assert.Single(lines);
        var d = JsonSerializer.Deserialize<JsonElement>(lines[0]);
        Assert.True(d.TryGetProperty("exitCode", out _));
        Assert.False(d.TryGetProperty("type", out _));
    }
}
