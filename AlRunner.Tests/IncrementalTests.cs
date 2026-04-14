using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class IncrementalTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub) =>
        Path.Combine(CliRunner.FindTestCase(testCase), sub);

    [Fact]
    public async Task Server_SecondRun_SameFiles_UsesCachedAssembly()
    {
        await using var server = await CliServer.StartAsync();

        var request = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") }
        });

        // First run — cold
        var response1 = await server.SendAsync(request);
        var doc1 = JsonDocument.Parse(response1);
        Assert.Equal(6, doc1.RootElement.GetProperty("passed").GetInt32());
        Assert.False(doc1.RootElement.TryGetProperty("cached", out var c1) && c1.GetBoolean());

        // Second run — same paths, should report cache hit
        var response2 = await server.SendAsync(request);
        var doc2 = JsonDocument.Parse(response2);
        Assert.Equal(6, doc2.RootElement.GetProperty("passed").GetInt32());
        Assert.True(doc2.RootElement.TryGetProperty("cached", out var c2) && c2.GetBoolean(),
            "Expected second run to report cached=true");
    }

    [Fact]
    public async Task Server_DifferentFiles_DoesNotUseStalCache()
    {
        await using var server = await CliServer.StartAsync();

        // Run test case 01
        var request1 = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") }
        });
        var response1 = await server.SendAsync(request1);
        var doc1 = JsonDocument.Parse(response1);
        Assert.Equal(6, doc1.RootElement.GetProperty("passed").GetInt32());

        // Run test case 04 — different files, must not return cached results
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
    public async Task Server_MultiSlotCache_ReturnsToFirstProjectCachedAfterSwitching()
    {
        // With a 1-slot LRU, bouncing between projects A → B → A rebuilds A.
        // With a multi-slot LRU, the second run of A hits the cache.
        await using var server = await CliServer.StartAsync();

        var requestA = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") }
        });
        var requestB = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { TestPath("04-asserterror", "src"), TestPath("04-asserterror", "test") }
        });

        // Cold: A runs full compile
        var r1 = JsonDocument.Parse(await server.SendAsync(requestA));
        Assert.False(r1.RootElement.TryGetProperty("cached", out var c1) && c1.GetBoolean());

        // B runs full compile (different inputs)
        var r2 = JsonDocument.Parse(await server.SendAsync(requestB));
        Assert.False(r2.RootElement.TryGetProperty("cached", out var c2) && c2.GetBoolean());

        // Back to A: must be a cache hit (proves multi-slot cache, not single slot)
        var r3 = JsonDocument.Parse(await server.SendAsync(requestA));
        Assert.True(r3.RootElement.TryGetProperty("cached", out var c3) && c3.GetBoolean(),
            "Multi-slot LRU should return cached=true for A after bouncing through B");
    }

    [Fact]
    public async Task Server_ExecuteInlineCode_ReturnsCapturedMessages()
    {
        await using var server = await CliServer.StartAsync();

        var request = JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "Message('from server execute');"
        });

        var response = await server.SendAsync(request);
        var doc = JsonDocument.Parse(response);

        Assert.Equal(0, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("messages", out var msgs), "execute response must include messages");
        Assert.True(msgs.GetArrayLength() > 0, "execute should capture at least one Message()");
        Assert.Contains("from server execute", msgs[0].GetString());
    }

    [Fact]
    public async Task Server_ExecuteSourcePaths_RunsOnRunTrigger()
    {
        // tests/01-pure-function/ is a codeunit with procedures but no OnRun;
        // BC auto-generates an empty OnRun so the runner can still invoke it.
        // Using a fixture we know has a Message in its OnRun would be cleaner,
        // but the existing fixtures don't expose one; the test here just
        // asserts the dispatch succeeds and returns a structured response.
        await using var server = await CliServer.StartAsync();

        var request = JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "codeunit 98 __X { trigger OnRun() begin Message('ran %1', 'ok'); end; }"
        });

        var response = await server.SendAsync(request);
        var doc = JsonDocument.Parse(response);

        Assert.Equal(0, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("messages", out var msgs));
        Assert.True(msgs.GetArrayLength() > 0);
        Assert.Contains("ran ok", msgs[0].GetString());
    }

    [Fact]
    public async Task Server_ExecuteRejectsMissingCodeAndPaths()
    {
        await using var server = await CliServer.StartAsync();

        var request = JsonSerializer.Serialize(new { command = "execute" });
        var response = await server.SendAsync(request);
        var doc = JsonDocument.Parse(response);

        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.Contains("code", err.GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Server_Response_IncludesChangedFilesOnCacheMiss()
    {
        // On a cache miss, response should report which files caused the
        // miss so IDE integrations can show change-aware feedback.
        await using var server = await CliServer.StartAsync();

        var req1 = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") }
        });
        var req2 = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { TestPath("02-record-operations", "src"), TestPath("02-record-operations", "test") }
        });

        // First run: no prior state — changedFiles should be absent or empty.
        var r1 = JsonDocument.Parse(await server.SendAsync(req1));
        Assert.False(r1.RootElement.TryGetProperty("cached", out var c1) && c1.GetBoolean());

        // Second run, different project: should report a non-empty changedFiles
        // list (every file is new relative to the prior compile).
        var r2 = JsonDocument.Parse(await server.SendAsync(req2));
        Assert.False(r2.RootElement.TryGetProperty("cached", out var c2) && c2.GetBoolean());
        Assert.True(r2.RootElement.TryGetProperty("changedFiles", out var changed),
            "cache miss response must include a changedFiles field");
        Assert.True(changed.GetArrayLength() > 0, "changedFiles must list at least one file on a cold miss");
    }
}
