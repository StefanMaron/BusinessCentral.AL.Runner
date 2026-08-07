using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlRunner;

/// <summary>
/// Wire types and (de)serialization for <c>--server</c> mode — the
/// newline-delimited JSON protocol the VS Code extension depends on.
///
/// One JSON object per line. stdin = requests, stdout = responses.
///   request : {command, sourcePaths[], packagePaths[], stubPaths[], code, captureValues, testIsolation}
///   runTests: STREAMING (protocol-v2.schema.json — see #1641) — zero or more
///             {"type":"test", name, status, durationMs, message, stackTrace}
///             lines, one per completed test as it finishes, followed by exactly
///             one terminal {"type":"summary", exitCode, passed, failed, errors,
///             total, cached, changedFiles|omitted, compilationErrors|omitted,
///             protocolVersion:2} line. `errorKind`/`stackFrames` on the test
///             event and `cancelled` on the summary are schema-defined but not
///             yet populated — separate follow-up slices of #1641.
///   execute : {exitCode, tests:[{name,status,durationMs,message,stackTrace}],
///              messages|null, compilationErrors|null} — single response, not
///              streamed (matches v1: only runTests streams).
///   error   : {error}
///   shutdown: {status}
/// </summary>
public sealed class ServerRequest
{
    [JsonPropertyName("command")] public string? Command { get; set; }
    [JsonPropertyName("sourcePaths")] public string[]? SourcePaths { get; set; }
    [JsonPropertyName("packagePaths")] public string[]? PackagePaths { get; set; }
    // v1 carried AL stub paths; v2 has no stubs layer. Accepted and ignored.
    [JsonPropertyName("stubPaths")] public string[]? StubPaths { get; set; }
    /// <summary>Inline AL source (used by the <c>execute</c> command).</summary>
    [JsonPropertyName("code")] public string? Code { get; set; }
    /// <summary>Opt-in to variable capture on <c>execute</c> (v1 field; not yet supported in v2).</summary>
    [JsonPropertyName("captureValues")] public bool? CaptureValues { get; set; }
    /// <summary>
    /// "codeunit" (default) | "test"/"method" | "disabled" — see <see cref="TestIsolationParser"/>.
    /// Null = the server's existing default (TestIsolation.Codeunit), matching the
    /// CLI's own default. Threaded into PipelineOptions.TestIsolation-equivalent
    /// (TestExecutor.Isolation) before RunTests/execute — see #1616: without this
    /// field, --server had no way to ask for per-method isolation, so tests that
    /// depend on per-method reset cross-pollute under --server even though the
    /// identical CLI invocation with --test-isolation method passes.
    /// </summary>
    [JsonPropertyName("testIsolation")] public string? TestIsolation { get; set; }
}

/// <summary>A file-grouped compilation error block, matching v1's response shape.</summary>
public sealed record CompilationErrorGroup(string File, IReadOnlyList<string> Errors);

/// <summary>Per-request run outcome carried from the server run path to the protocol serializer.</summary>
public sealed record ServerRunResult(
    IReadOnlyList<TestResult> Tests,
    int ExitCode,
    bool Cached,
    IReadOnlyList<CompilationErrorGroup>? CompileErrors,
    Dictionary<string, string> FileHashes)
{
    public static ServerRunResult Failure(int exitCode, string file, string message, Dictionary<string, string> hashes)
        => new(Array.Empty<TestResult>(), exitCode, false,
               new List<CompilationErrorGroup> { new(file, new List<string> { message }) }, hashes);
}

public static class ServerProtocol
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ServerRequest? Parse(string line)
        => JsonSerializer.Deserialize<ServerRequest>(line);

    public static string Error(string message)
        => JsonSerializer.Serialize(new { error = message }, Opts);

    public static string Shutdown()
        => JsonSerializer.Serialize(new { status = "shutting down" }, Opts);

    /// <summary>
    /// Serialize one protocol-v2 <c>test</c> NDJSON line for a single completed
    /// test (the streaming <c>runTests</c> shape — see #1641 / protocol-v2.schema.json's
    /// <c>TestEvent</c>). <c>errorKind</c>/<c>stackFrames</c> are schema-defined
    /// but not populated here — that wiring is a separate follow-up slice of #1641.
    /// </summary>
    public static string TestEvent(TestResult t)
    {
        var payload = new
        {
            type = "test",
            name = $"{t.Codeunit}.{t.Method}",
            status = t.Outcome.ToString().ToLowerInvariant(),
            durationMs = (long)t.Duration.TotalMilliseconds,
            message = t.Message,
            stackTrace = (t.AlCallStack ?? t.FullException)?.TrimEnd(),
        };
        return JsonSerializer.Serialize(payload, Opts);
    }

    /// <summary>
    /// Serialize the single terminal <c>summary</c> NDJSON line that ends a
    /// streaming <c>runTests</c> response (protocol-v2.schema.json's <c>Summary</c>).
    /// <paramref name="changedFiles"/> is only emitted on a cache miss (cache hits
    /// have no diff). <paramref name="compilationErrors"/> is omitted when there
    /// were none. <c>cancelled</c> is schema-defined but not populated here — the
    /// <c>cancel</c> command is a separate follow-up slice of #1641.
    /// </summary>
    public static string Summary(
        IReadOnlyList<TestResult> tests,
        int exitCode,
        bool cached,
        IReadOnlyList<string>? changedFiles = null,
        IReadOnlyList<CompilationErrorGroup>? compilationErrors = null)
    {
        var payload = new
        {
            type = "summary",
            exitCode,
            passed = tests.Count(t => t.Outcome == TestOutcome.Pass),
            failed = tests.Count(t => t.Outcome == TestOutcome.Fail),
            errors = tests.Count(t => t.Outcome == TestOutcome.Error),
            total = tests.Count,
            cached,
            changedFiles = cached ? null : changedFiles,
            compilationErrors = compilationErrors is { Count: > 0 }
                ? compilationErrors.Select(g => new { file = g.File, errors = g.Errors })
                : null,
            protocolVersion = 2,
        };
        return JsonSerializer.Serialize(payload, Opts);
    }

    /// <summary>Serialize an execute response (run-mode / inline code).</summary>
    public static string Execute(
        IReadOnlyList<TestResult> tests,
        int exitCode,
        IReadOnlyList<string>? messages = null,
        IReadOnlyList<CompilationErrorGroup>? compilationErrors = null)
    {
        var payload = new
        {
            exitCode,
            tests = tests.Select(ToWire),
            messages = messages is { Count: > 0 } ? messages : null,
            compilationErrors = compilationErrors is { Count: > 0 }
                ? compilationErrors.Select(g => new { file = g.File, errors = g.Errors })
                : null,
        };
        return JsonSerializer.Serialize(payload, Opts);
    }

    // A single test result on the wire. stackTrace prefers the AL call stack
    // (meaningful for AL-originated errors) and falls back to the raw C#
    // exception for runner-internal failures — see
    // .claude rule al_stack_vs_csharp_stack.
    private static object ToWire(TestResult t) => new
    {
        name = $"{t.Codeunit}.{t.Method}",
        status = t.Outcome.ToString().ToLowerInvariant(),
        durationMs = (long)t.Duration.TotalMilliseconds,
        message = t.Message,
        stackTrace = (t.AlCallStack ?? t.FullException)?.TrimEnd(),
    };
}
