using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerCancelTests
{
    [Fact]
    public async Task Cancel_NoActiveRequest_AcksAsNoop()
    {
        await using var server = await CliServer.StartAsync();
        var response = await server.SendAsync("{\"command\":\"cancel\"}");
        var doc = JsonDocument.Parse(response);
        Assert.Equal("ack", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("cancel", doc.RootElement.GetProperty("command").GetString());
        Assert.True(doc.RootElement.GetProperty("noop").GetBoolean());
    }

    [Fact]
    public async Task Cancel_TwiceWithoutActiveRequest_BothNoop()
    {
        await using var server = await CliServer.StartAsync();
        var r1 = await server.SendAsync("{\"command\":\"cancel\"}");
        var r2 = await server.SendAsync("{\"command\":\"cancel\"}");
        Assert.True(JsonDocument.Parse(r1).RootElement.GetProperty("noop").GetBoolean());
        Assert.True(JsonDocument.Parse(r2).RootElement.GetProperty("noop").GetBoolean());
    }

    [Fact]
    public async Task Cancel_AfterRunTestsCompletes_IsNoop()
    {
        await using var server = await CliServer.StartAsync();
        // Use the protocol-v2-line-directives fixture for a realistic test run.
        var repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var srcPath = Path.Combine(repoRoot, "tests", "protocol-v2-line-directives", "src")
            .Replace("\\", "/");
        var testPath = Path.Combine(repoRoot, "tests", "protocol-v2-line-directives", "test")
            .Replace("\\", "/");
        var request = JsonSerializer.Serialize(new
        {
            command = "runtests",
            sourcePaths = new[] { srcPath, testPath }
        });
        // First, run tests synchronously — completes before we send cancel.
        // Protocol v2 streams test events + a summary terminator; drain the stream.
        var runLines = await server.SendRequestStreamingAsync(request);
        Assert.NotEmpty(runLines);
        // Now send cancel; should be noop because no request is active.
        var cancelResponse = await server.SendAsync("{\"command\":\"cancel\"}");
        Assert.True(JsonDocument.Parse(cancelResponse).RootElement
            .GetProperty("noop").GetBoolean());
    }

    [Fact]
    public async Task Cancel_WithUnknownExtraFields_StillAcks()
    {
        // Forward-compat: future protocol versions may add fields to the cancel command.
        // The server must tolerate unknown fields and still emit the ack shape.
        await using var server = await CliServer.StartAsync();
        var response = await server.SendAsync(
            "{\"command\":\"cancel\",\"reason\":\"user clicked stop\",\"requestId\":42}");
        var doc = JsonDocument.Parse(response);
        Assert.Equal("ack", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("cancel", doc.RootElement.GetProperty("command").GetString());
        Assert.True(doc.RootElement.GetProperty("noop").GetBoolean());
    }
}
