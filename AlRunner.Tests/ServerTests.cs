using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub) =>
        Path.Combine(RepoRoot, "tests", testCase, sub);

    [Fact]
    public async Task Server_RunTests_ReturnsJsonResult()
    {
        await using var server = await CliServer.StartAsync();

        var request = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") }
        });

        var response = await server.SendAsync(request);
        var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("tests", out var tests));
        Assert.Equal(6, tests.GetArrayLength());
        Assert.True(root.TryGetProperty("passed", out var passed));
        Assert.Equal(6, passed.GetInt32());
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
        var response1 = await server.SendAsync(request1);
        var doc1 = JsonDocument.Parse(response1);
        Assert.Equal(6, doc1.RootElement.GetProperty("passed").GetInt32());

        // Second request — should work without cold start
        var request2 = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { TestPath("04-asserterror", "src"), TestPath("04-asserterror", "test") }
        });
        var response2 = await server.SendAsync(request2);
        var doc2 = JsonDocument.Parse(response2);
        Assert.Equal(7, doc2.RootElement.GetProperty("passed").GetInt32());
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
        var response1 = await server.SendAsync(request);
        var doc1 = JsonDocument.Parse(response1);
        Assert.False(doc1.RootElement.GetProperty("cached").GetBoolean(), "first request should be a cache miss");
        var passed1 = doc1.RootElement.GetProperty("passed").GetInt32();
        Assert.True(passed1 > 0, "first request must have passing tests");

        // Cache hit — identical request
        var response2 = await server.SendAsync(request);
        var doc2 = JsonDocument.Parse(response2);
        Assert.True(doc2.RootElement.GetProperty("cached").GetBoolean(), "second request should be a cache hit");
        Assert.Equal(passed1, doc2.RootElement.GetProperty("passed").GetInt32());
    }
}
