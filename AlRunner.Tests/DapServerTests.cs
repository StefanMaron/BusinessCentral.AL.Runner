using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlRunner;
using AlRunner.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Integration tests for the DapServer DAP (Debug Adapter Protocol) implementation.
///
/// These tests prove the DAP PoC (issue #528):
///   - initialize handshake produces capabilities + initialized event
///   - setBreakpoints acknowledges with verified status
///   - continue unblocks BreakpointManager
///
/// Full end-to-end breakpoint-hit tests require a running AL pipeline;
/// those are covered via DapServerBreakpointIntegrationTests.
/// </summary>
public class DapServerTests : IAsyncDisposable
{
    private DapServer? _server;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _serverTask;
    private readonly CancellationTokenSource _cts = new();

    private static int _nextPort = 15000;
    private static int AllocatePort() => System.Threading.Interlocked.Increment(ref _nextPort);

    private async Task<int> StartServerAsync()
    {
        BreakpointManager.Reset();
        var port = AllocatePort();
        _server = new DapServer(port);
        _server.PipelineOptions = new PipelineOptions(); // empty — no AL to run
        _serverTask = Task.Run(() => _server.RunAsync(_cts.Token));

        // Give the listener a moment to bind
        await Task.Delay(50);

        _client = new TcpClient();
        await _client.ConnectAsync("127.0.0.1", port);
        _stream = _client.GetStream();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        BreakpointManager.Continue(); // unblock any paused thread
        if (_server != null)
        {
            try { await _server.StopAsync(); }
            catch (Exception) { }
        }
        _client?.Dispose();
        if (_serverTask != null)
        {
            try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception) { }
        }
        BreakpointManager.Reset();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SendAsync(object msg)
    {
        var json = JsonSerializer.Serialize(msg);
        var payload = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _stream!.WriteAsync(header);
        await _stream!.WriteAsync(payload);
        await _stream!.FlushAsync();
    }

    private async Task<JsonObject?> ReceiveAsync()
    {
        // Read until double CRLF (end of headers)
        var headerBuilder = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            var n = await _stream!.ReadAsync(buf.AsMemory(0, 1));
            if (n == 0) return null;
            headerBuilder.Append((char)buf[0]);
            if (headerBuilder.Length >= 4 &&
                headerBuilder[^4] == '\r' && headerBuilder[^3] == '\n' &&
                headerBuilder[^2] == '\r' && headerBuilder[^1] == '\n')
                break;
        }

        var headers = headerBuilder.ToString();
        var lengthMatch = System.Text.RegularExpressions.Regex.Match(
            headers, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!lengthMatch.Success) return null;

        var length = int.Parse(lengthMatch.Groups[1].Value);
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await _stream!.ReadAsync(body.AsMemory(read, length - read));
            if (n == 0) return null;
            read += n;
        }

        return JsonSerializer.Deserialize<JsonObject>(body);
    }

    private async Task<JsonObject?> ReceiveWithTimeout(int ms = 2000)
    {
        using var cts = new CancellationTokenSource(ms);
        try
        {
            return await ReceiveAsync().WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Initialize_ReturnsCapabilitiesAndInitializedEvent()
    {
        await StartServerAsync();

        await SendAsync(new
        {
            seq = 1,
            type = "request",
            command = "initialize",
            arguments = new { clientName = "test", adapterID = "al-runner" }
        });

        // Should receive an initialize response
        var response = await ReceiveWithTimeout();
        Assert.NotNull(response);
        Assert.Equal("response", response["type"]?.GetValue<string>());
        Assert.Equal("initialize", response["command"]?.GetValue<string>());
        Assert.True(response["success"]?.GetValue<bool>(), "initialize must succeed");

        // Body must include capabilities
        var body = response["body"] as JsonObject;
        Assert.NotNull(body);
        Assert.True(body.ContainsKey("supportsConfigurationDoneRequest"),
            "capabilities must include supportsConfigurationDoneRequest");

        // Should also receive an 'initialized' event
        var evt = await ReceiveWithTimeout();
        Assert.NotNull(evt);
        Assert.Equal("event", evt["type"]?.GetValue<string>());
        Assert.Equal("initialized", evt["event"]?.GetValue<string>());
    }

    [Fact]
    public async Task Launch_ReturnsSuccess()
    {
        await StartServerAsync();

        // Initialize first
        await SendAsync(new { seq = 1, type = "request", command = "initialize", arguments = new { } });
        await ReceiveWithTimeout(); // response
        await ReceiveWithTimeout(); // initialized event

        // Launch
        await SendAsync(new { seq = 2, type = "request", command = "launch", arguments = new { } });
        var response = await ReceiveWithTimeout();
        Assert.NotNull(response);
        Assert.Equal("response", response["type"]?.GetValue<string>());
        Assert.True(response["success"]?.GetValue<bool>(), "launch must succeed");
    }

    [Fact]
    public async Task SetBreakpoints_NoMappedStatements_ReturnsUnverified()
    {
        await StartServerAsync();

        await SendAsync(new { seq = 1, type = "request", command = "initialize", arguments = new { } });
        await ReceiveWithTimeout();
        await ReceiveWithTimeout();

        // Set a breakpoint at a line that has no source mapping
        await SendAsync(new
        {
            seq = 2,
            type = "request",
            command = "setBreakpoints",
            arguments = new
            {
                source = new { name = "NonExistent.al", path = "NonExistent.al" },
                breakpoints = new[] { new { line = 999 } }
            }
        });

        var response = await ReceiveWithTimeout();
        Assert.NotNull(response);
        Assert.Equal("response", response["type"]?.GetValue<string>());
        Assert.True(response["success"]?.GetValue<bool>());

        var body = response["body"] as JsonObject;
        var bps = body?["breakpoints"] as JsonArray;
        Assert.NotNull(bps);
        Assert.Equal(1, bps.Count);

        var bp = bps[0] as JsonObject;
        Assert.False(bp?["verified"]?.GetValue<bool>(),
            "Breakpoint at unmapped line must not be verified");
    }

    [Fact]
    public async Task Threads_ReturnsSingleMainThread()
    {
        await StartServerAsync();

        await SendAsync(new { seq = 1, type = "request", command = "initialize", arguments = new { } });
        await ReceiveWithTimeout();
        await ReceiveWithTimeout();

        await SendAsync(new { seq = 2, type = "request", command = "threads" });
        var response = await ReceiveWithTimeout();
        Assert.NotNull(response);

        var body = response?["body"] as JsonObject;
        var threads = body?["threads"] as JsonArray;
        Assert.NotNull(threads);
        Assert.Equal(1, threads.Count);
        Assert.Equal("Main Thread", (threads[0] as JsonObject)?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task Continue_ReleasesBreakpointManager()
    {
        await StartServerAsync();

        BreakpointManager.Enable();
        BreakpointManager.RegisterBreakpoint("SomeScope_Scope_abc", 1);

        await SendAsync(new { seq = 1, type = "request", command = "initialize", arguments = new { } });
        await ReceiveWithTimeout(); // initialize response
        await ReceiveWithTimeout(); // initialized event

        bool resumed = false;
        var pausedTask = Task.Run(() =>
        {
            BreakpointManager.CheckHit("SomeScope_Scope_abc", 1);
            resumed = true;
        });

        await Task.Delay(60);
        Assert.True(BreakpointManager.IsPaused, "Must be paused before continue");

        // Send continue via DAP
        await SendAsync(new { seq = 2, type = "request", command = "continue", arguments = new { threadId = 1 } });

        // The server may send a 'stopped' event before the continue response.
        // Read messages until we find the continue response.
        JsonObject? continueResponse = null;
        for (int i = 0; i < 5; i++)
        {
            var msg = await ReceiveWithTimeout();
            if (msg == null) break;
            if (msg["type"]?.GetValue<string>() == "response" &&
                msg["command"]?.GetValue<string>() == "continue")
            {
                continueResponse = msg;
                break;
            }
            // Skip events (e.g. 'stopped' fired by the BreakpointHit handler)
        }

        Assert.NotNull(continueResponse);
        Assert.True(continueResponse["success"]?.GetValue<bool>());

        await pausedTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(resumed, "Execution must resume after DAP continue");
    }
}
