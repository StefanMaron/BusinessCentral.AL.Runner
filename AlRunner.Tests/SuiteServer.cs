using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #5111: <see cref="RunViaServer(string[])"/> runs one <c>runTests</c> request on a warm
/// <c>--server</c> process shared by every class in the test run, instead of spawning the CLI.
/// For classes whose facts assert pass/fail, test results, and, through
/// <see cref="ServerRunResult.AssertOutputDoesNotContain"/>, what a request wrote to stderr. Which
/// classes qualify, the per-request canary and how to read a failure: docs/shared-cli-server.md#suite-server.
/// </summary>
public static class SuiteServer
{
    /// <summary>Servers alive at once. Each idle one holds a warm BC runtime (see the doc for the memory budget).</summary>
    internal const int Capacity = 2;

    /// <summary>Requests one server serves before it is replaced, which bounds its memory growth.</summary>
    internal const int RequestsPerServer = 40;

    private static readonly ServerPool Default = new(Capacity, RequestsPerServer, serverEnv: null);

    /// <summary>Server processes the shared pool has started in this test run.</summary>
    public static int SpawnCount => Default.SpawnCount;

    /// <summary>Runs the test codeunits in <paramref name="sourcePaths"/> (bundle directories) on a shared server.</summary>
    public static Task<ServerRunResult> RunViaServer(params string[] sourcePaths)
        => RunViaServer(sourcePaths, Array.Empty<string>());

    /// <summary>
    /// As <see cref="RunViaServer(string[])"/>, with the request's <c>packagePaths</c> (the CLI's
    /// <c>--packages</c>), <c>testIsolation</c> (<c>--isolation</c>), <c>test</c> (<c>--test</c> /
    /// <c>--filter</c>) and <c>excludeTests</c> (<c>--exclude-test</c>, repeated). A selection field is
    /// sent only when given, so a request without one gets the server's startup default, which for the
    /// shared server is no selection (docs/server-mode.md#test-and-excludetests).
    /// </summary>
    public static async Task<ServerRunResult> RunViaServer(string[] sourcePaths, string[] packagePaths,
        string? testIsolation = null, TimeSpan? timeout = null, string? test = null, string[]? excludeTests = null)
    {
        var fields = new Dictionary<string, object>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = sourcePaths,
            ["packagePaths"] = packagePaths,
        };
        if (testIsolation != null) fields["testIsolation"] = testIsolation;
        if (test != null) fields["test"] = test;
        if (excludeTests != null) fields["excludeTests"] = excludeTests;
        return await RunAsync(JsonSerializer.Serialize(fields), timeout, SharedServerCanary.RunAsync);
    }

    /// <summary>
    /// The shared pool's one request path. <paramref name="runCanary"/> is the canary run after the
    /// request; only a test of the canary check passes anything but <see cref="SharedServerCanary.RunAsync"/>.
    /// </summary>
    internal static Task<ServerRunResult> RunAsync(string request, TimeSpan? timeout,
        Func<CliServer, string, Task<string>> runCanary)
        => Default.RunAsync(request, timeout, runCanary);

    /// <summary>Throws, naming the request, when the canary's fingerprint after it differs from the server's first.</summary>
    internal static void CheckCanary(string request, string baseline, string after)
    {
        if (after != baseline)
            throw new InvalidOperationException(
                "SuiteServer canary: this request left server-process state behind, so the shared " +
                "server is discarded (docs/shared-cli-server.md#suite-server).\n" +
                $"request: {request}\n--- canary first ---\n{baseline}\n--- canary now ---\n{after}");
    }
}

/// <summary>
/// A set of warm <c>--server</c> processes, at most <c>capacity</c> alive at once. The shared
/// instance is <see cref="SuiteServer"/>'s; a test of the pool's own rules makes another, which can
/// start its servers with environment (a short test timeout) the shared one must never carry.
/// </summary>
internal sealed class ServerPool : IAsyncDisposable
{
    private readonly int _requestsPerServer;
    private readonly IReadOnlyDictionary<string, string>? _serverEnv;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<Pooled> _idle = new();
    private readonly ConcurrentDictionary<Pooled, byte> _live = new();
    private int _spawns;
    private int _retired;

    public ServerPool(int capacity, int requestsPerServer, IReadOnlyDictionary<string, string>? serverEnv)
    {
        _requestsPerServer = requestsPerServer;
        _serverEnv = serverEnv;
        _slots = new SemaphoreSlim(capacity, capacity);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var p in _live.Keys)
                try { p.Server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)); } catch { }
        };
    }

    /// <summary>Stops every server this pool started and has not discarded. For a pool a test made itself.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var p in _live.Keys)
        {
            _live.TryRemove(p, out _);
            await p.Server.DisposeAsync();
            try { Directory.Delete(p.CanaryBundle, recursive: true); } catch { }
        }
    }

    /// <summary>Server processes this pool has started.</summary>
    public int SpawnCount => Volatile.Read(ref _spawns);

    /// <summary>Servers this pool has discarded: by request count, after a timeout, or after a failed request.</summary>
    public int RetiredCount => Volatile.Read(ref _retired);

    private sealed class Pooled
    {
        public required CliServer Server { get; init; }
        public required string CanaryBundle { get; init; }
        public required string CanaryBaseline { get; init; }
        public int Requests;
    }

    public async Task<ServerRunResult> RunAsync(string request, TimeSpan? timeout,
        Func<CliServer, string, Task<string>> runCanary)
    {
        await _slots.WaitAsync();
        Pooled? pooled = null;
        var keep = false;
        try
        {
            pooled = _idle.TryTake(out var idle) ? idle : await StartAsync();
            var lines = await pooled.Server.SendRequestStreamingAsync(request, timeout ?? TimeSpan.FromSeconds(300));
            // Read up to the request's own end marker (docs/server-mode.md#stderr-request-marker), or
            // throw: stdout and stderr are two pipes, so the summary line says nothing about how much of
            // this request's stderr has arrived. Before the canary, which is a request of its own.
            var stderr = await pooled.Server.StdErrOfLastRequestAsync();
            var result = ServerRunResult.Parse(lines, stderr);
            pooled.Requests++;

            // The canary after every request names the request that left state behind.
            SuiteServer.CheckCanary(request, pooled.CanaryBaseline, await runCanary(pooled.Server, pooled.CanaryBundle));

            keep = Reusable(result, pooled.Requests, _requestsPerServer);
            return result;
        }
        finally
        {
            if (pooled != null)
            {
                if (keep) _idle.Add(pooled);
                else await RetireAsync(pooled);
            }
            _slots.Release();
        }
    }

    /// <summary>
    /// Whether a server may serve another request. A request in which a test timed out leaves that
    /// test's thread running (#5171), so the server may write into any later request's stderr slice
    /// and a slice taken from it could be wrong in either direction. The server is discarded rather
    /// than handed out again.
    /// </summary>
    internal static bool Reusable(ServerRunResult result, int requestsServed, int requestsPerServer)
        => requestsServed < requestsPerServer && !result.TimedOut;

    private async Task<Pooled> StartAsync()
    {
        var args = new List<string>();
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) { args.Add("--package-cache"); args.Add(platformApps); }
        var server = await CliServer.StartAsync(args, extraEnv: _serverEnv);
        Interlocked.Increment(ref _spawns);
        try
        {
            var canary = SharedServerCanary.WriteBundle();
            var baseline = await SharedServerCanary.RunAsync(server, canary);
            SharedServerCanary.AssertBaseline(baseline);
            var pooled = new Pooled { Server = server, CanaryBundle = canary, CanaryBaseline = baseline };
            _live[pooled] = 0;
            return pooled;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    private async Task RetireAsync(Pooled pooled)
    {
        Interlocked.Increment(ref _retired);
        _live.TryRemove(pooled, out _);
        await pooled.Server.DisposeAsync();
        try { Directory.Delete(pooled.CanaryBundle, recursive: true); } catch { }
    }
}

/// <summary>One <c>runTests</c> request's answer, read from the protocol-v2 stream.</summary>
public sealed class ServerRunResult
{
    private string? _stderr;

    public required int ExitCode { get; init; }
    public required int Total { get; init; }
    public required int Passed { get; init; }
    public required int Failed { get; init; }
    public required int Errors { get; init; }
    public required IReadOnlyList<(string Name, string Status, string Message)> Tests { get; init; }
    public required IReadOnlyList<string> CompilationErrors { get; init; }

    /// <summary>The summary's <c>warnings</c> (field notes, the <c>test-selection:</c> note); empty when it carries none.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// A test of this request hit the per-test timeout (<c>errorKind: "timeout"</c>). The runner abandons
    /// such a test's thread, which keeps running and may write after the request's end marker (#5171).
    /// </summary>
    public required bool TimedOut { get; init; }

    /// <summary>The protocol lines, one per line, as read.</summary>
    public required string ProtocolText { get; init; }

    /// <summary>Every protocol line plus this request's stderr — for assertion messages, never for assertions.</summary>
    public required string Transcript { get; init; }

    public override string ToString() => Transcript;

    /// <summary>
    /// What the server wrote to stderr during this request, up to its end marker. Throws, rather than
    /// answering, when that is not known: no slice was read, or a test timed out and its abandoned thread
    /// may have written after the marker (#5171).
    /// </summary>
    public string StdErr
    {
        get
        {
            if (_stderr == null)
                throw new InvalidOperationException(
                    "SuiteServer: no stderr slice was read up to this request's end marker, so no claim about " +
                    "its stderr can be made.\n" + Transcript);
            if (TimedOut)
                throw new InvalidOperationException(
                    "SuiteServer: a test in this request timed out. The runner abandons that test's thread, which can " +
                    "write to stderr after the request's end marker, so this slice may be missing lines and is not " +
                    "handed out (#5171).\n" + Transcript);
            return _stderr;
        }
    }

    /// <summary>The CLI's whole output as a test reads it: the protocol lines and this request's stderr.</summary>
    public string Output => ProtocolText + "\n" + StdErr;

    /// <summary>The status of the test named <paramref name="name"/> (<c>Codeunit&lt;id&gt;.&lt;method&gt;</c>), or null when none ran.</summary>
    public string? StatusOf(string name)
        => Tests.Where(t => t.Name == name).Select(t => t.Status).FirstOrDefault();

    /// <summary>The CLI's <c>PASS  Codeunit&lt;id&gt;.&lt;method&gt;</c> line, read from the protocol.</summary>
    public void AssertPassed(string name)
        => Xunit.Assert.True(StatusOf(name) == "pass", $"expected {name} to pass, got '{StatusOf(name) ?? "not run"}'\n{Transcript}");

    /// <summary>The CLI's "no FAIL anywhere in the output": no compile error, and every test that ran passed.</summary>
    public void AssertNoFailures()
        => Xunit.Assert.True(Failed == 0 && Errors == 0 && CompilationErrors.Count == 0 && Tests.All(t => t.Status == "pass"),
            $"expected no failure of any kind\n{Transcript}");

    /// <summary>The CLI's per-bundle <c>&lt;P&gt;P/&lt;F&gt;F/&lt;E&gt;E</c> line, for a request of one bundle.</summary>
    public void AssertCounts(int passed, int failed, int errors)
        => Xunit.Assert.True(Passed == passed && Failed == failed && Errors == errors,
            $"expected {passed}P/{failed}F/{errors}E, got {Passed}P/{Failed}F/{Errors}E\n{Transcript}");

    /// <summary>
    /// The CLI's <c>Assert.DoesNotContain(text, stdout + stderr)</c>: <paramref name="text"/> is in neither a
    /// protocol line nor this request's stderr. It throws, and never passes, when the slice is not known
    /// (see <see cref="StdErr"/>). An empty slice that was read up to the end marker is an answer — a
    /// clean request writes nothing to stderr — while one never read is not, and <see cref="StdErr"/>
    /// tells the two apart.
    /// </summary>
    public void AssertOutputDoesNotContain(string text, StringComparison comparison = StringComparison.Ordinal)
        => Xunit.Assert.False(Output.Contains(text, comparison), $"expected the output not to contain '{text}'\n{Transcript}");

    /// <summary>The CLI's <c>Assert.Contains(text, stdout + stderr)</c>.</summary>
    public void AssertOutputContains(string text, StringComparison comparison = StringComparison.Ordinal)
        => Xunit.Assert.True(Output.Contains(text, comparison), $"expected the output to contain '{text}'\n{Transcript}");

    internal static ServerRunResult Parse(IReadOnlyList<string> lines, string? stderr)
    {
        var tests = new List<(string, string, string)>();
        var compileErrors = new List<string>();
        var warnings = new List<string>();
        var timedOut = false;
        JsonElement? summary = null;
        foreach (var line in lines)
        {
            JsonElement el;
            try { el = JsonSerializer.Deserialize<JsonElement>(line); }
            catch (JsonException) { continue; }
            var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "test")
            {
                tests.Add((Str(el, "name"), Str(el, "status"), Str(el, "message")));
                if (Str(el, "errorKind") == "timeout") timedOut = true;
            }
            else if (type == "summary")
            {
                summary = el;
                if (el.TryGetProperty("warnings", out var ws) && ws.ValueKind == JsonValueKind.Array)
                    warnings.AddRange(ws.EnumerateArray().Select(w => w.GetString() ?? ""));
                if (el.TryGetProperty("compilationErrors", out var groups) && groups.ValueKind == JsonValueKind.Array)
                    foreach (var g in groups.EnumerateArray())
                        if (g.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                            compileErrors.AddRange(errs.EnumerateArray().Select(e => e.GetString() ?? ""));
            }
        }
        var protocol = string.Join("\n", lines);
        var transcript = protocol + "\n--- server stderr for this request ---\n" + (stderr ?? "(not read)");
        if (summary is not { } s)
            throw new InvalidOperationException("SuiteServer: the request ended without a summary line.\n" + transcript);
        return new ServerRunResult
        {
            ExitCode = Num(s, "exitCode"),
            Total = Num(s, "total"),
            Passed = Num(s, "passed"),
            Failed = Num(s, "failed"),
            Errors = Num(s, "errors"),
            Tests = tests,
            CompilationErrors = compileErrors,
            Warnings = warnings,
            TimedOut = timedOut,
            ProtocolText = protocol,
            Transcript = transcript,
            _stderr = stderr,
        };
    }

    private static string Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Num(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
}
