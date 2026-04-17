using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlRunner.Runtime;

namespace AlRunner;

/// <summary>
/// Minimal DAP (Debug Adapter Protocol) server for al-runner.
///
/// Implements enough of the protocol for a single-breakpoint proof-of-concept:
///   initialize → capabilities + initialized event
///   launch / attach → acknowledge, run AL on a background thread
///   setBreakpoints → translate (file, line) to (scopeName, stmtId) and register
///   configurationDone → release the execution thread to start
///   threads → single "Main Thread"
///   stackTrace → current paused location
///   scopes → "Locals" scope
///   variables → captured variable values from ValueCapture
///   continue → release BreakpointManager
///   disconnect → clean up
///
/// Protocol framing: Content-Length: N\r\n\r\n{json}
/// Transport: TCP (default port 4711, the standard DAP port).
/// </summary>
public sealed class DapServer : IDisposable
{
    private readonly TcpListener _listener;
    private int _seq;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource _cts = new();
    private Task? _runTask;

    // Breakpoint source file → list of (file, line) registrations (for
    // returning verified breakpoints in the setBreakpoints response).
    private readonly Dictionary<string, List<(string File, int Line)>> _requestedBreakpoints = new();

    // Semaphore that gates AL execution start until configurationDone is received.
    private readonly SemaphoreSlim _configDoneSemaphore = new(0, 1);

    /// <summary>
    /// Options for the AL pipeline that the DAP server will run.
    /// Set these before calling <see cref="StartAsync"/>.
    /// </summary>
    public PipelineOptions PipelineOptions { get; set; } = new();

    public DapServer(int port = 4711)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>
    /// Start listening and handle one client connection asynchronously.
    /// Returns when the client disconnects or <see cref="StopAsync"/> is called.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        _listener.Start();
        _client = await _listener.AcceptTcpClientAsync(ct);
        _stream = _client.GetStream();

        // Subscribe to BreakpointHit so we can send the stopped event to the IDE.
        BreakpointManager.BreakpointHit += args =>
        {
            // Fire-and-forget: this runs on the AL execution thread just before it
            // blocks on the semaphore, so we must not block here.
            _ = SendEventAsync("stopped", new
            {
                reason = "breakpoint",
                threadId = 1,
                allThreadsStopped = true
            });
        };

        var readTask = ReadLoopAsync(ct);

        // Run AL pipeline on a background thread after configurationDone
        _runTask = Task.Run(async () =>
        {
            // Wait for configurationDone before running
            await _configDoneSemaphore.WaitAsync(ct);
            if (ct.IsCancellationRequested) return;

            BreakpointManager.Enable();
            try
            {
                var pipeline = new AlRunnerPipeline();
                pipeline.Run(PipelineOptions);
            }
            finally
            {
                BreakpointManager.Disable();
                BreakpointManager.BreakpointHit = null;
                try
                {
                    // Signal termination after pipeline finishes
                    await SendEventAsync("terminated", new { });
                    await SendEventAsync("exited", new { exitCode = 0 });
                }
                catch (IOException) { }
                catch (InvalidOperationException) { }
            }
        }, ct);

        await readTask;
        if (_runTask != null)
        {
            try { await _runTask; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            JsonObject? msg;
            try
            {
                msg = await ReadMessageAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
            catch (Exception) { break; }

            if (msg == null) break;

            var type = msg["type"]?.GetValue<string>();
            if (type == "request")
                await HandleRequestAsync(msg, ct);
        }
    }

    private async Task HandleRequestAsync(JsonObject msg, CancellationToken ct)
    {
        var seq = msg["seq"]?.GetValue<int>() ?? 0;
        var command = msg["command"]?.GetValue<string>() ?? "";
        var args = msg["arguments"] as JsonObject;

        switch (command)
        {
            case "initialize":
                await RespondAsync(seq, command, success: true, body: BuildCapabilities());
                await SendEventAsync("initialized", new { });
                break;

            case "launch":
            case "attach":
                await RespondAsync(seq, command, success: true);
                break;

            case "setBreakpoints":
                var bpResponse = HandleSetBreakpoints(args);
                await RespondAsync(seq, command, success: true, body: bpResponse);
                break;

            case "configurationDone":
                await RespondAsync(seq, command, success: true);
                _configDoneSemaphore.Release();
                break;

            case "threads":
                await RespondAsync(seq, command, success: true, body: new
                {
                    threads = new[] { new { id = 1, name = "Main Thread" } }
                });
                break;

            case "stackTrace":
                await RespondAsync(seq, command, success: true, body: BuildStackTrace());
                break;

            case "scopes":
                await RespondAsync(seq, command, success: true, body: new
                {
                    scopes = new[]
                    {
                        new { name = "Locals", variablesReference = 1, expensive = false }
                    }
                });
                break;

            case "variables":
                await RespondAsync(seq, command, success: true, body: BuildVariables());
                break;

            case "continue":
                await RespondAsync(seq, command, success: true, body: new { allThreadsContinued = true });
                BreakpointManager.Continue();
                break;

            case "next":
            case "stepIn":
            case "stepOut":
                // Simplified: treat step as continue
                await RespondAsync(seq, command, success: true);
                BreakpointManager.Continue();
                break;

            case "disconnect":
                await RespondAsync(seq, command, success: true);
                _cts.Cancel();
                BreakpointManager.Continue(); // unblock any paused thread
                break;

            default:
                await RespondAsync(seq, command, success: true);
                break;
        }
    }

    private object HandleSetBreakpoints(JsonObject? args)
    {
        BreakpointManager.ClearBreakpoints();
        _requestedBreakpoints.Clear();

        if (args == null)
            return new { breakpoints = Array.Empty<object>() };

        var sourceNode = args["source"] as JsonObject;
        var sourceFile = sourceNode?["path"]?.GetValue<string>()
            ?? sourceNode?["name"]?.GetValue<string>()
            ?? "";

        var bpArray = args["breakpoints"] as JsonArray ?? new JsonArray();
        var verified = new List<object>();

        foreach (var bp in bpArray)
        {
            var line = bp?["line"]?.GetValue<int>() ?? 0;

            // Translate (sourceFile, line) → (scopeName, stmtId) pairs
            var stmts = SourceLineMapper.FindStatementsForAlLine(sourceFile, line);
            foreach (var (scopeName, stmtId) in stmts)
                BreakpointManager.RegisterBreakpoint(scopeName, stmtId);

            var isVerified = stmts.Count > 0;
            verified.Add(new
            {
                verified = isVerified,
                line,
                message = isVerified ? null : "No executable statement at this line"
            });

            if (!_requestedBreakpoints.TryGetValue(sourceFile, out var list))
                _requestedBreakpoints[sourceFile] = list = new();
            list.Add((sourceFile, line));
        }

        return new { breakpoints = verified };
    }

    private static object BuildCapabilities()
    {
        return new
        {
            supportsConfigurationDoneRequest = true,
            supportsSetBreakpointsRequest = true,
            supportsTerminateRequest = false,
            supportsRestartRequest = false,
            supportsVariableType = true,
        };
    }

    private static object BuildStackTrace()
    {
        var paused = AlScope.LastStatementHit;
        if (paused == null)
        {
            return new
            {
                stackFrames = Array.Empty<object>(),
                totalFrames = 0
            };
        }

        var (typeName, stmtId) = paused.Value;
        var pos = SourceLineMapper.GetAlPositionFromStatement(typeName, stmtId);
        var objectName = SourceFileMapper.GetObjectForClass(typeName);
        var file = SourceFileMapper.GetFile(objectName) ?? $"{objectName}.al";

        return new
        {
            stackFrames = new[]
            {
                new
                {
                    id = 1,
                    name = objectName,
                    source = new { name = Path.GetFileName(file), path = file },
                    line = pos?.Line ?? 0,
                    column = pos?.Column ?? 0,
                }
            },
            totalFrames = 1
        };
    }

    private static object BuildVariables()
    {
        var captures = ValueCapture.GetCaptures();
        var vars = captures.Select(c => new
        {
            name = c.VariableName,
            value = c.Value ?? "",
            variablesReference = 0
        }).ToArray();

        return new { variables = vars };
    }

    // ── DAP framing ───────────────────────────────────────────────────────────

    private async Task RespondAsync(int requestSeq, string command, bool success, object? body = null)
    {
        var msg = new Dictionary<string, object?>
        {
            ["seq"] = Interlocked.Increment(ref _seq),
            ["type"] = "response",
            ["request_seq"] = requestSeq,
            ["success"] = success,
            ["command"] = command,
        };
        if (body != null)
            msg["body"] = body;

        await WriteMessageAsync(msg);
    }

    private async Task SendEventAsync(string eventName, object body)
    {
        var msg = new Dictionary<string, object>
        {
            ["seq"] = Interlocked.Increment(ref _seq),
            ["type"] = "event",
            ["event"] = eventName,
            ["body"] = body,
        };
        await WriteMessageAsync(msg);
    }

    private async Task WriteMessageAsync(object msg)
    {
        var json = JsonSerializer.Serialize(msg);
        var payload = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");

        if (_stream == null) return;
        await _stream.WriteAsync(header);
        await _stream.WriteAsync(payload);
        await _stream.FlushAsync();
    }

    private async Task<JsonObject?> ReadMessageAsync(CancellationToken ct)
    {
        if (_stream == null) return null;

        // Read headers
        var headerBuilder = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            var n = await _stream.ReadAsync(buf.AsMemory(0, 1), ct);
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
            var n = await _stream.ReadAsync(body.AsMemory(read, length - read), ct);
            if (n == 0) return null;
            read += n;
        }

        return JsonSerializer.Deserialize<JsonObject>(body);
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        BreakpointManager.Continue();
        _listener.Stop();
        if (_runTask != null)
        {
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client?.Dispose();
        _listener.Stop();
    }
}
