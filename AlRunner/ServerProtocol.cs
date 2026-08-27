using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlRunner;

/// <summary>
/// Wire types and (de)serialization for <c>--server</c> mode — the
/// newline-delimited JSON protocol the VS Code extension depends on.
///
/// One JSON object per line. stdin = requests, stdout = responses.
///   request : {command, sourcePaths[], packagePaths[], stubPaths[], code, captureValues, coverage, testIsolation}
///   runTests: STREAMING (protocol-v2.schema.json — see #1641) — zero or more
///             {"type":"test", name, status, durationMs, message, errorKind,
///             stackFrames, stackTrace} lines, one per completed test as it
///             finishes, followed by exactly one terminal
///             {"type":"summary", exitCode, passed, failed, errors,
///             total, cached, cancelled|omitted, changedFiles|omitted,
///             compilationErrors|omitted, coverage|omitted, wallSeconds|omitted,
///             protocolVersion:2} line.
///             `cancelled` (true) is present only when a concurrent `cancel`
///             command actually stopped the run before every test ran; omitted
///             (never `false`) otherwise — see the `cancel` command below.
///   cancel  : side-channel command (#1641/v1 #1613) — NOT dispatched through the
///             normal sequential command queue. A dedicated stdin-reader thread
///             (see Program.cs's RunServerLoop) recognises `cancel` the instant it
///             is read and answers immediately, even while a `runtests` request is
///             still streaming on the main dispatch thread — that concurrency is
///             the entire point: cooperative cancellation only helps if the signal
///             can arrive mid-run. Response: {"type":"ack","command":"cancel",
///             "noop":bool}. `noop:true` when there was no active run (or it had
///             already finished) at the moment the cancel was processed — the v1
///             shape (#1613/#1614), reused verbatim rather than inventing a new one.
///   execute : {exitCode, tests:[{name,status,durationMs,message,stackTrace,
///              capturedValues|omitted}], messages|null, compilationErrors|null,
///              coverage|omitted} —
///              single response, not streamed (matches v1: only runTests streams).
///              `capturedValues` (#1640) is present per test only when the request
///              set `captureValues:true`; each entry is {scopeName, variableName,
///              value, statementId, captureError|omitted} — see AlValueCapture.
///              `captureError` (#2043) is present, non-null, only when the field
///              read or its ToString() threw; `value` is null in that case too but
///              MUST NOT be read as "genuinely null" — a genuinely null AL variable
///              has `captureError` absent.
///   `coverage` (#2042, on BOTH `runTests`' summary and `execute`'s response) is
///   present only when the request set `coverage:true`: one entry per AL source file,
///   {file, statements:[{id, scope, line, column, endLine, endColumn, hits}]}. `id` is
///   the SAME id-space as `capturedValues[].statementId` for the same `scope` — see
///   AlStatementTableTests. Supersedes the schema-only v1 `FileCoverage{file, lines[],
///   totalStatements, hitStatements}` shape (protocol-v2.schema.json never had a
///   working implementation of it, so there is no compatibility break): per-statement
///   detail with positions strictly subsumes a line-hit rollup, which a caller can
///   still derive client-side by grouping `statements` on `line`.
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
    /// <summary>
    /// Opt-in to variable capture on <c>execute</c> (v1 field; #1640 second slice —
    /// --coverage was the first, #1922). When true, each response test entry's
    /// <c>capturedValues</c> carries the top-level AL scope's locals as of the last
    /// statement that ran (see AlValueCapture). Null/false = unchanged behaviour,
    /// field omitted from the response.
    /// </summary>
    [JsonPropertyName("captureValues")] public bool? CaptureValues { get; set; }
    /// <summary>
    /// Opt-in to per-statement hit counts + a position table on `runTests`/`execute`
    /// (issue #2042 — the id/position half `captureValues`' `statementId` needed to be
    /// placeable in an editor, per SShadowS/ALchemist#1). When true, the response's
    /// `coverage[]` carries one entry per AL source file; each entry's `statements[]`
    /// gives every BC-instrumented statement's `id` (the SAME id-space as
    /// `capturedValues[].statementId` for the SAME `scope` — see
    /// AlStatementTableTests.CapturedValueStatementId_MatchesStatementTableScopeAndId), the
    /// owning AL member name (`scope`), the 1-based start/end line+column, and this
    /// run's hit count. Per statement, never per line: two statements sharing a line
    /// are two separate entries, not one summed count. Reuses AlCoverageTracker's
    /// existing StmtHit hook (#1922) — no new instrumentation. Null/false = unchanged
    /// behaviour, `coverage` omitted from the response.
    /// </summary>
    [JsonPropertyName("coverage")] public bool? Coverage { get; set; }
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
    /// Serialize a protocol-v2 <c>{"type":"ack"}</c> line (protocol-v2.schema.json's
    /// <c>Ack</c>) — the response to a side-channel command, currently only
    /// <c>cancel</c> (#1641/v1 #1613). <paramref name="noop"/> is true when the
    /// command had nothing to act on (no active run, or the run already finished
    /// by the time the cancel was processed) — the v1 shape, reused verbatim.
    /// </summary>
    public static string Ack(string command, bool noop)
        => JsonSerializer.Serialize(new { type = "ack", command, noop }, Opts);

    /// <summary>
    /// Serialize one protocol-v2 <c>test</c> NDJSON line for a single completed
    /// test (the streaming <c>runTests</c> shape — see #1641 / protocol-v2.schema.json's
    /// <c>TestEvent</c>), including the two structured-diagnostics fields:
    /// <c>errorKind</c> (<see cref="ErrorClassifier"/>) and <c>stackFrames</c>
    /// (<see cref="StackFrameMapper"/>'s parse of the captured AL call stack).
    /// Both are OMITTED rather than emitted empty when there is nothing to say —
    /// a pass has no error kind, and a failure with no captured AL stack has no
    /// frames (an empty array would claim "AL stack captured, zero frames deep").
    /// <c>capturedValues</c>/<c>alSourceFile</c>/<c>alSourceLine</c> stay
    /// schema-only: they need the Cecil instrumentation pass tracked on #1640.
    /// </summary>
    public static string TestEvent(TestResult t)
    {
        var frames = StackFrameMapper.Walk(t.AlCallStack);
        var payload = new
        {
            type = "test",
            name = $"{t.Codeunit}.{t.Method}",
            status = t.Outcome.ToString().ToLowerInvariant(),
            durationMs = (long)t.Duration.TotalMilliseconds,
            message = t.Message,
            errorKind = ErrorClassifier.Classify(t)?.ToString().ToLowerInvariant(),
            stackFrames = frames.Count > 0 ? frames.Select(ToWire) : null,
            stackTrace = (t.AlCallStack ?? t.FullException)?.TrimEnd(),
        };
        return JsonSerializer.Serialize(payload, Opts);
    }

    // One AL stack frame on the wire, in protocol-v2.schema.json's `AlStackFrame`
    // shape. `source` is only emitted when the frame actually carries a file —
    // BC's service-tier call-stack format does not include one, so it is normally
    // absent rather than invented (.claude/rules/loud-failures.md). Line/column are
    // likewise null-omitted, so a frame BC gave no line for does not gain a fake 0.
    private static object ToWire(AlStackFrame f) => new
    {
        name = f.Name,
        source = f.File != null ? new { path = f.File, name = Path.GetFileName(f.File) } : null,
        line = f.Line,
        column = f.Column,
        presentationHint = f.Hint.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Serialize the single terminal <c>summary</c> NDJSON line that ends a
    /// streaming <c>runTests</c> response (protocol-v2.schema.json's <c>Summary</c>).
    /// <paramref name="changedFiles"/> is only emitted on a cache miss (cache hits
    /// have no diff). <paramref name="compilationErrors"/> is omitted when there
    /// were none. <paramref name="cancelled"/> (see #1641/v1 #1613-#1614) is
    /// carried as <c>true</c> only when a concurrent <c>cancel</c> command actually
    /// stopped the run before every test ran; omitted entirely otherwise — never
    /// emitted as a literal <c>false</c>, matching every other optional field on
    /// this line (WhenWritingNull serialization; "not cancelled" and "cancellation
    /// wasn't asked for" both read as "absent").
    /// <paramref name="wallSeconds"/> (#1936) is the real wall-clock duration of
    /// THIS request — set by the caller from a <c>Stopwatch</c> started when
    /// <c>runtests</c> was received — not the process's total uptime (a warm server
    /// serves many requests, so "since process start" would be meaningless past the
    /// first one). Omitted (never 0) when the caller does not supply it, same
    /// null-omission convention as every other optional field here.
    /// </summary>
    /// <paramref name="statementTable"/> (#2042) is the run's aggregated per-statement
    /// hit-count + position table (see AlCoverageTracker.CollectStatementTable), passed
    /// only when the request set `coverage:true`. Null omits `coverage` entirely
    /// (WhenWritingNull); a non-null EMPTY list still serializes as `coverage:[]` —
    /// "asked, nothing instrumented" is a real, distinct answer from "didn't ask",
    /// same convention `capturedValues` already uses for `captureValues`.
    public static string Summary(
        IReadOnlyList<TestResult> tests,
        int exitCode,
        bool cached,
        IReadOnlyList<string>? changedFiles = null,
        IReadOnlyList<CompilationErrorGroup>? compilationErrors = null,
        bool cancelled = false,
        double? wallSeconds = null,
        IReadOnlyList<Infrastructure.AlCoverageTracker.AlStatementRecord>? statementTable = null)
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
            cancelled = cancelled ? (bool?)true : null,
            changedFiles = cached ? null : changedFiles,
            compilationErrors = compilationErrors is { Count: > 0 }
                ? compilationErrors.Select(g => new { file = g.File, errors = g.Errors })
                : null,
            coverage = ToStatementTableWire(statementTable),
            wallSeconds,
            protocolVersion = 2,
        };
        return JsonSerializer.Serialize(payload, Opts);
    }

    /// <summary>Serialize an execute response (run-mode / inline code). <paramref
    /// name="statementTable"/> — see Summary's doc comment; identical `coverage`
    /// shape and null-vs-empty convention.</summary>
    public static string Execute(
        IReadOnlyList<TestResult> tests,
        int exitCode,
        IReadOnlyList<string>? messages = null,
        IReadOnlyList<CompilationErrorGroup>? compilationErrors = null,
        IReadOnlyList<Infrastructure.AlCoverageTracker.AlStatementRecord>? statementTable = null)
    {
        var payload = new
        {
            exitCode,
            tests = tests.Select(ToWire),
            messages = messages is { Count: > 0 } ? messages : null,
            compilationErrors = compilationErrors is { Count: > 0 }
                ? compilationErrors.Select(g => new { file = g.File, errors = g.Errors })
                : null,
            coverage = ToStatementTableWire(statementTable),
        };
        return JsonSerializer.Serialize(payload, Opts);
    }

    // Groups a flat statement list into the wire's per-file shape (issue #2042):
    // {file, statements:[{id, scope, line, column, endLine, endColumn, hits}]}.
    // Null in -> null out (coverage omitted); a non-null empty list in -> an empty
    // (but present) enumerable out, so Summary/Execute's WhenWritingNull only ever
    // strips the field for "not requested", never for "requested, found nothing".
    // Ordered (file, then line, then column) so repeated calls against the same run
    // are byte-identical — reflection's assembly/type enumeration order is not a
    // contract callers should have to tolerate drifting.
    private static IEnumerable<object>? ToStatementTableWire(
        IReadOnlyList<Infrastructure.AlCoverageTracker.AlStatementRecord>? statements)
    {
        if (statements == null) return null;
        return statements
            .GroupBy(s => s.FilePath)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (object)new
            {
                file = g.Key,
                statements = g.OrderBy(s => s.Line).ThenBy(s => s.Column).ThenBy(s => s.StatementId)
                    .Select(s => new
                    {
                        id = s.StatementId,
                        scope = s.ScopeName,
                        line = s.Line,
                        column = s.Column,
                        endLine = s.EndLine,
                        endColumn = s.EndColumn,
                        hits = s.HitCount,
                    }),
            });
    }

    // A single test result on the wire. stackTrace prefers the AL call stack
    // (meaningful for AL-originated errors) and falls back to the raw C#
    // exception for runner-internal failures — see
    // .claude rule al_stack_vs_csharp_stack.
    // capturedValues (#1640) is null-omitted (WhenWritingNull) when the request
    // didn't set captureValues:true — t.CapturedValues is null in that case
    // (RunFirstCodeunitOnRun only populates it when AlValueCapture.Enabled).
    // When captureValues WAS requested it is present even as an empty array
    // ("captured, zero AL locals" — a real, distinct answer from "not asked").
    private static object ToWire(TestResult t) => new
    {
        name = $"{t.Codeunit}.{t.Method}",
        status = t.Outcome.ToString().ToLowerInvariant(),
        durationMs = (long)t.Duration.TotalMilliseconds,
        message = t.Message,
        stackTrace = (t.AlCallStack ?? t.FullException)?.TrimEnd(),
        capturedValues = t.CapturedValues?.Select(ToWire),
    };

    // One captured AL local on the wire — the shape protocol-v2.schema.json already
    // reserves for TestEvent.capturedValues (see the schema's top-level description),
    // reused here for execute's own (schema-independent) response.
    // captureError (#2043) is null-omitted (WhenWritingNull) on the common path — only
    // present when the field read or its ToString() threw, so it never gets confused
    // with a genuinely null AL variable (which has value:null and no captureError key).
    private static object ToWire(Infrastructure.AlCapturedValue v) => new
    {
        scopeName = v.ScopeName,
        variableName = v.VariableName,
        value = v.Value,
        statementId = v.StatementId,
        captureError = v.CaptureError,
    };
}
