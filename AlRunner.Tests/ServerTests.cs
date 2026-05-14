using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub) =>
        Path.Combine(CliRunner.FindTestCase(testCase), sub);

    /// <summary>
    /// Find the terminal <c>{"type":"summary"}</c> line in a streaming response.
    /// </summary>
    private static JsonElement ParseSummary(IReadOnlyList<string> lines)
    {
        var summary = lines.LastOrDefault();
        Assert.NotNull(summary);
        var doc = JsonDocument.Parse(summary!);
        Assert.Equal("summary", doc.RootElement.GetProperty("type").GetString());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Server_RunTests_ReturnsJsonResult()
    {
        // Protocol v2: runtests streams one `type=test` line per completed test
        // followed by a single `type=summary` terminator line.
        await using var server = await CliServer.StartAsync();

        var request = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") }
        });

        var lines = await server.SendRequestStreamingAsync(request);
        var summary = ParseSummary(lines);

        Assert.Equal(6, summary.GetProperty("total").GetInt32());
        Assert.Equal(6, summary.GetProperty("passed").GetInt32());
        // 6 test events + 1 summary
        Assert.Equal(7, lines.Count);
        Assert.Equal(2, summary.GetProperty("protocolVersion").GetInt32());
    }

    [Fact]
    public async Task Server_Shutdown_Exits()
    {
        await using var server = await CliServer.StartAsync();

        var request = JsonSerializer.Serialize(new { command = "shutdown" });
        await server.SendAsync(request);

        // Process should exit cleanly
        var exited = await server.WaitForExitAsync(timeout: TimeSpan.FromSeconds(5));
        Assert.True(exited);
        Assert.Equal(0, server.ExitCode);
    }

    [Fact]
    public async Task Server_MultipleRequests_ReuseWarmState()
    {
        await using var server = await CliServer.StartAsync();

        // First request
        var request1 = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") }
        });
        var lines1 = await server.SendRequestStreamingAsync(request1);
        var summary1 = ParseSummary(lines1);
        Assert.Equal(6, summary1.GetProperty("passed").GetInt32());

        // Second request — should work without cold start
        var request2 = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { TestPath("04-asserterror", "src"), TestPath("04-asserterror", "test") }
        });
        var lines2 = await server.SendRequestStreamingAsync(request2);
        var summary2 = ParseSummary(lines2);
        Assert.Equal(7, summary2.GetProperty("passed").GetInt32());
    }

    [Fact]
    public async Task Server_InvalidCommand_ReturnsError()
    {
        await using var server = await CliServer.StartAsync();

        var request = JsonSerializer.Serialize(new { command = "bogus" });
        var response = await server.SendAsync(request);
        var doc = JsonDocument.Parse(response);

        Assert.True(doc.RootElement.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrEmpty(error.GetString()));
    }

    [Fact]
    public async Task Server_CacheHit_ReturnsCachedTrue()
    {
        // The first request compiles and runs tests (cache miss, cached: false).
        // An identical second request should be served from the assembly cache
        // (cache hit, cached: true) and return the same test results.
        // Partial compilation / compilationErrors was removed in #80.
        await using var server = await CliServer.StartAsync();

        var request = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { TestPath("84-json-compilation-error", "src"), TestPath("84-json-compilation-error", "test") }
        });

        // Cache miss — first request
        var lines1 = await server.SendRequestStreamingAsync(request);
        var summary1 = ParseSummary(lines1);
        Assert.False(summary1.GetProperty("cached").GetBoolean(), "first request should be a cache miss");
        var passed1 = summary1.GetProperty("passed").GetInt32();
        Assert.True(passed1 > 0, "first request must have passing tests");

        // Cache hit — identical request
        var lines2 = await server.SendRequestStreamingAsync(request);
        var summary2 = ParseSummary(lines2);
        Assert.True(summary2.GetProperty("cached").GetBoolean(), "second request should be a cache hit");
        Assert.Equal(passed1, summary2.GetProperty("passed").GetInt32());
    }
}
