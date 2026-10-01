using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #5111: <see cref="RunViaServer(string[])"/> runs one <c>runTests</c> request on a warm
/// <c>--server</c> process shared by every class in the test run, instead of spawning the CLI.
/// For classes whose facts assert only pass/fail and test results. Which classes qualify, the
/// per-request canary and how to read a failure: docs/shared-cli-server.md#suite-server.
/// </summary>
public static class SuiteServer
{
    /// <summary>Servers alive at once. Each idle one holds a warm BC runtime (see the doc for the memory budget).</summary>
    internal const int Capacity = 2;

    /// <summary>Requests one server serves before it is replaced, which bounds its memory growth.</summary>
    internal const int RequestsPerServer = 40;

    private static readonly SemaphoreSlim Slots = new(Capacity, Capacity);
    private static readonly ConcurrentBag<Pooled> Idle = new();
    private static readonly ConcurrentDictionary<Pooled, byte> Live = new();
    private static int _spawns;

    /// <summary>Server processes this pool has started in this test run.</summary>
    public static int SpawnCount => _spawns;

    static SuiteServer()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var p in Live.Keys)
                try { p.Server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)); } catch { }
        };
    }

    private sealed class Pooled
    {
        public required CliServer Server { get; init; }
        public required string CanaryBundle { get; init; }
        public required string CanaryBaseline { get; init; }
        public int Requests;
    }

    /// <summary>Runs the test codeunits in <paramref name="sourcePaths"/> (bundle directories) on a shared server.</summary>
    public static Task<ServerRunResult> RunViaServer(params string[] sourcePaths)
        => RunViaServer(sourcePaths, Array.Empty<string>());

    /// <summary>
    /// As <see cref="RunViaServer(string[])"/>, with the request's <c>packagePaths</c> (the CLI's
    /// <c>--packages</c>) and <c>testIsolation</c> (the CLI's <c>--isolation</c>).
    /// </summary>
    public static async Task<ServerRunResult> RunViaServer(string[] sourcePaths, string[] packagePaths,
        string? testIsolation = null, TimeSpan? timeout = null)
    {
        var fields = new Dictionary<string, object>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = sourcePaths,
            ["packagePaths"] = packagePaths,
        };
        if (testIsolation != null) fields["testIsolation"] = testIsolation;
        var request = JsonSerializer.Serialize(fields);

        await Slots.WaitAsync();
        Pooled? pooled = null;
        var keep = false;
        try
        {
            pooled = Idle.TryTake(out var idle) ? idle : await StartAsync();
            var mark = pooled.Server.StdErrMark;
            var lines = await pooled.Server.SendRequestStreamingAsync(request, timeout ?? TimeSpan.FromSeconds(300));
            var result = ServerRunResult.Parse(lines, pooled.Server.StdErrSince(mark));
            pooled.Requests++;

            // The canary after every request names the request that left state behind.
            CheckCanary(request, pooled.CanaryBaseline,
                await SharedServerCanary.RunAsync(pooled.Server, pooled.CanaryBundle));

            keep = pooled.Requests < RequestsPerServer;
            return result;
        }
        finally
        {
            if (pooled != null)
            {
                if (keep) Idle.Add(pooled);
                else await RetireAsync(pooled);
            }
            Slots.Release();
        }
    }

    /// <summary>Throws, naming the request, when the canary's fingerprint after it differs from the server's first.</summary>
    internal static void CheckCanary(string request, string baseline, string after)
    {
        if (after != baseline)
            throw new InvalidOperationException(
                "SuiteServer canary: this request left server-process state behind, so the shared " +
                "server is discarded (docs/shared-cli-server.md#suite-server).\n" +
                $"request: {request}\n--- canary first ---\n{baseline}\n--- canary now ---\n{after}");
    }

    private static async Task<Pooled> StartAsync()
    {
        var args = new List<string>();
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) { args.Add("--package-cache"); args.Add(platformApps); }
        var server = await CliServer.StartAsync(args);
        Interlocked.Increment(ref _spawns);
        try
        {
            var canary = SharedServerCanary.WriteBundle();
            var baseline = await SharedServerCanary.RunAsync(server, canary);
            SharedServerCanary.AssertBaseline(baseline);
            var pooled = new Pooled { Server = server, CanaryBundle = canary, CanaryBaseline = baseline };
            Live[pooled] = 0;
            return pooled;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    private static async Task RetireAsync(Pooled pooled)
    {
        Live.TryRemove(pooled, out _);
        await pooled.Server.DisposeAsync();
        try { Directory.Delete(pooled.CanaryBundle, recursive: true); } catch { }
    }
}

/// <summary>One <c>runTests</c> request's answer, read from the protocol-v2 stream.</summary>
public sealed class ServerRunResult
{
    public required int ExitCode { get; init; }
    public required int Total { get; init; }
    public required int Passed { get; init; }
    public required int Failed { get; init; }
    public required int Errors { get; init; }
    public required IReadOnlyList<(string Name, string Status, string Message)> Tests { get; init; }
    public required IReadOnlyList<string> CompilationErrors { get; init; }

    /// <summary>Every protocol line plus this request's stderr — for assertion messages, never for assertions.</summary>
    public required string Transcript { get; init; }

    public override string ToString() => Transcript;

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

    internal static ServerRunResult Parse(IReadOnlyList<string> lines, string stderr)
    {
        var tests = new List<(string, string, string)>();
        var compileErrors = new List<string>();
        JsonElement? summary = null;
        foreach (var line in lines)
        {
            JsonElement el;
            try { el = JsonSerializer.Deserialize<JsonElement>(line); }
            catch (JsonException) { continue; }
            var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "test")
                tests.Add((Str(el, "name"), Str(el, "status"), Str(el, "message")));
            else if (type == "summary")
            {
                summary = el;
                if (el.TryGetProperty("compilationErrors", out var groups) && groups.ValueKind == JsonValueKind.Array)
                    foreach (var g in groups.EnumerateArray())
                        if (g.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                            compileErrors.AddRange(errs.EnumerateArray().Select(e => e.GetString() ?? ""));
            }
        }
        var transcript = string.Join("\n", lines) + "\n--- server stderr for this request ---\n" + stderr;
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
            Transcript = transcript,
        };
    }

    private static string Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Num(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
}
