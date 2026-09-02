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
/// These spawn the real runner and need BC artifacts provisioned (see TestArtifacts);
/// when absent they report Skipped with a reason, not Passed.
///
/// See DefineFlagIntegrationTests for why this used to be
/// [Collection("server-serial")] and no longer is — #1809.
///
/// #1804: all three facts share ONE server process via SharedCliServer instead
/// of spawning their own. None needs a different --cache/startup flag from the
/// others and none tears the process down (conditions (a)/(b) of
/// SharedCliServer's doc comment). Condition (c) — distinct app IDs — is
/// enforced by giving <see cref="MakeMixedBundle"/> a <c>variant</c>: a shared
/// server process caches a compiled module by AppId for the process's whole
/// lifetime (<c>DependencyLoader.TryGetByAppId</c>) and reuses it for ANY later
/// request whose bundle reports the same AppId at a different SourcePath,
/// regardless of content — so two facts calling this bundle generator with the
/// SAME AppId would (harmlessly, while the content happens to stay identical,
/// but not safely in general) share one compiled module instead of each
/// genuinely compiling its own. See ServerTestIsolationTests' class doc
/// comment for the same point made at more length.
/// </summary>
public class ServerStreamingTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public ServerStreamingTests(SharedCliServer fixture) => _fixture = fixture;

    // One passing test, one failing test (with a recognisable Error() message) —
    // exercises both status branches of the streamed `test` event shape.
    // `variant` gives each call site its own AppId (last hex digit) and object
    // ID (offset by variant*10 from 60200) — see the class doc comment.
    private static string MakeMixedBundle(int variant)
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-server-streaming", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var baseId = 60200 + variant * 10;
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "a1b2c3d4-e5f6-4708-a9ba-cbdcedfe0f1{{variant:x1}}",
          "name": "Runner Extras - Server Streaming Probe {{variant}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "StreamingTest.Codeunit.al"), $$"""
        codeunit {{baseId}} "Server Streaming Probe SX {{variant}}"
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

    [SkippableFact]
    public async Task RunTests_StreamsOneTestEventPerTest_ThenSummary()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = MakeMixedBundle(variant: 0);
        var server = await _fixture.GetAsync();

        var lines = await server.SendRequestStreamingAsync(RunTestsReq(bundle));

        // Exactly 2 test events (proves streaming happened per-test, not a
        // single batch line), each BEFORE the summary terminator.
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        Assert.Equal(2, events.Count);
        Assert.Equal("summary", JsonSerializer.Deserialize<JsonElement>(lines[^1]).GetProperty("type").GetString());

        var pass = events.Single(e => e.GetProperty("name").GetString()!.EndsWith("PassingTest"));
        Assert.Equal("pass", pass.GetProperty("status").GetString());
        Assert.True(pass.GetProperty("durationMs").GetInt64() >= 0);
        // A pass carries no diagnostics fields at all (#1641).
        Assert.False(pass.TryGetProperty("errorKind", out _));
        Assert.False(pass.TryGetProperty("stackFrames", out _));

        var fail = events.Single(e => e.GetProperty("name").GetString()!.EndsWith("FailingTest"));
        Assert.Equal("fail", fail.GetProperty("status").GetString());
        Assert.Contains("streaming-probe-boom", fail.GetProperty("message").GetString());

        // #1641 errorKind + stackFrames, end-to-end through the real server rather
        // than through ServerProtocol.TestEvent alone: an AL Error() inside a [Test]
        // body classifies as `runtime`, and the captured AL call stack arrives as
        // ONE structured frame naming the AL object and the in-procedure line.
        Assert.Equal("runtime", fail.GetProperty("errorKind").GetString());
        var frames = fail.GetProperty("stackFrames");
        Assert.Equal(1, frames.GetArrayLength());
        Assert.Equal("\"Server Streaming Probe SX 0\"(CodeUnit 60200).FailingTest",
                     frames[0].GetProperty("name").GetString());
        Assert.Equal(2, frames[0].GetProperty("line").GetInt32());
        Assert.Equal("normal", frames[0].GetProperty("presentationHint").GetString());

        // Summary carries the protocol-v2 contract and matches the streamed events.
        Assert.Equal(2, summary.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(1, summary.GetProperty("passed").GetInt32());
        Assert.Equal(1, summary.GetProperty("failed").GetInt32());
        Assert.Equal(0, summary.GetProperty("errors").GetInt32());
        Assert.Equal(2, summary.GetProperty("total").GetInt32());
        Assert.Equal(1, summary.GetProperty("exitCode").GetInt32());
    }

    [SkippableFact]
    public async Task RunTests_MissingSourcePaths_ReturnsSingleErrorLine_NoTestOrSummaryLines()
    {
        TestArtifacts.SkipIfMissing();

        var server = await _fixture.GetAsync();
        var lines = await server.SendRequestStreamingAsync(
            JsonSerializer.Serialize(new { command = "runTests" }));

        Assert.Single(lines);
        var d = JsonSerializer.Deserialize<JsonElement>(lines[0]);
        Assert.True(d.TryGetProperty("error", out var err));
        Assert.Contains("sourcePaths", err.GetString());
        Assert.False(d.TryGetProperty("type", out _));
    }

    [SkippableFact]
    public async Task Execute_StillReturnsSingleLine_NotStreamed()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = MakeMixedBundle(variant: 1);
        var server = await _fixture.GetAsync();

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
