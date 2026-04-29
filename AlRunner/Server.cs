using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlRunner;

/// <summary>
/// Long-running server mode. Reads JSON requests from stdin, writes JSON responses to stdout.
/// Keeps the transpiler and references warm between invocations.
/// Protocol: one JSON object per line (newline-delimited JSON).
///
/// Protocol v2 (the <c>runtests</c> command): the server emits zero or more
/// <c>{"type":"test"}</c> lines as each test completes, optionally interleaved with
/// <c>{"type":"progress"}</c> lines, terminated by exactly one <c>{"type":"summary"}</c>
/// line carrying <c>protocolVersion: 2</c>. Other commands (<c>execute</c>, <c>cancel</c>,
/// <c>shutdown</c>) remain single-response.
/// </summary>
public class AlRunnerServer
{
    private readonly CompilationCache _cache = new();
    private readonly RewriteCache _rewriteCache = new();
    private readonly SyntaxTreeCache _syntaxTreeCache = new();

    /// <summary>
    /// CancellationTokenSource for the currently-active <c>runtests</c> request.
    /// Set at the start of <see cref="HandleRunTests"/> and cleared (after Dispose) in the
    /// finally block that wraps it. Read by <see cref="HandleCancel"/>.
    ///
    /// Thread-safety note: the dispatch loop in <see cref="RunAsync"/> is sequential —
    /// it reads one request line at a time and processes it to completion before reading
    /// the next. There is therefore no actual concurrent execution between a
    /// <c>runtests</c> handler and a <c>cancel</c> handler: by the time a cancel command
    /// is dispatched, <c>HandleRunTests</c> has already returned and cleared this field.
    /// No lock is needed; a simple null-or-snapshot read in <see cref="HandleCancel"/> is
    /// sufficient. The field is not <c>volatile</c> because both reads and writes happen on
    /// the same logical async continuation (same thread or, in the async case, on a
    /// continuation that has a happens-before edge with the write).
    /// </summary>
    private CancellationTokenSource? _activeRequestCts;

    /// <summary>
    /// Shared serializer options used for every protocol-v2 line. <c>WhenWritingNull</c>
    /// keeps optional fields (e.g. <c>capturedValues</c>, <c>changedFiles</c>) absent
    /// from the line when their underlying value is null, matching the schema's
    /// "field-optional unless present" contract.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        // Pre-warm: load Roslyn references once
        var refsTask = Task.Run(() => RoslynCompiler.LoadReferences(), ct);
        Kernel32Shim.EnsureRegistered();

        // Signal readiness
        await output.WriteLineAsync("{\"ready\":true}");
        await output.FlushAsync();

        while (!ct.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(ct);
            if (line == null) break; // EOF — client disconnected

            try
            {
                var request = JsonSerializer.Deserialize<ServerRequest>(line);
                if (request == null)
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(new { error = "Invalid request" }));
                    await output.FlushAsync();
                    continue;
                }

                switch (request.Command?.ToLowerInvariant())
                {
                    case "runtests":
                        // Protocol v2: streaming response. The handler writes its own lines.
                        await HandleRunTests(request, refsTask, output);
                        break;
                    case "execute":
                    {
                        var response = HandleExecute(request, refsTask);
                        await output.WriteLineAsync(response);
                        await output.FlushAsync();
                        break;
                    }
                    case "cancel":
                    {
                        var response = HandleCancel();
                        await output.WriteLineAsync(response);
                        await output.FlushAsync();
                        break;
                    }
                    case "shutdown":
                    {
                        var response = HandleShutdown();
                        await output.WriteLineAsync(response);
                        await output.FlushAsync();
                        return; // exit the dispatch loop
                    }
                    default:
                    {
                        var response = JsonSerializer.Serialize(new { error = $"Unknown command: {request.Command}" });
                        await output.WriteLineAsync(response);
                        await output.FlushAsync();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new { error = ex.Message }));
                await output.FlushAsync();
            }
        }
    }

    /// <summary>
    /// Handle the protocol-v2 <c>runtests</c> command. Streams one
    /// <c>{"type":"test"}</c> line per completed test followed by exactly one
    /// <c>{"type":"summary"}</c> line. The summary always carries
    /// <c>protocolVersion: 2</c>.
    /// </summary>
    private async Task HandleRunTests(ServerRequest request,
        Task<List<Microsoft.CodeAnalysis.MetadataReference>> refsTask,
        TextWriter output)
    {
        if (request.SourcePaths == null || request.SourcePaths.Length == 0)
        {
            // Schema-valid summary that signals an input-validation failure.
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                type = "summary",
                error = "sourcePaths is required",
                exitCode = 2,
                passed = 0,
                failed = 0,
                errors = 0,
                total = 0,
                protocolVersion = 2,
            }, JsonOpts));
            await output.FlushAsync();
            return;
        }

        _activeRequestCts = new CancellationTokenSource();
        try
        {
            var ct = _activeRequestCts.Token;
            var fingerprint = _cache.ComputeFingerprint(request.SourcePaths);
            var cacheHit = _cache.TryGet(fingerprint);
            var filter = ConvertFilter(request.TestFilter);
            var coverageRequested = request.Coverage == true;
            var coberturaRequested = request.Cobertura == true;

            Assembly? assembly;
            Dictionary<string, List<string>>? compilationErrors;
            Dictionary<(string Scope, int StmtIndex), int>? sourceSpans;
            Dictionary<string, string>? scopeToObject;
            bool cached;
            List<string>? changedFiles = null;

            if (cacheHit != null)
            {
                assembly = cacheHit.Value.Assembly;
                compilationErrors = cacheHit.Value.CompilationErrors;
                sourceSpans = cacheHit.Value.SourceSpans;
                scopeToObject = cacheHit.Value.ScopeToObject;
                cached = true;
                Runtime.MockCodeunitHandle.CurrentAssembly = assembly;
            }
            else
            {
                changedFiles = _cache.DiffAgainstClosest();
                // Reset coverage BEFORE the pipeline runs — otherwise stale hits/totals
                // from a previous request leak into this run. The pipeline's call to
                // Executor.RegisterStatements re-populates the total set during compile.
                Runtime.AlScope.ResetCoverage();
                var options = new PipelineOptions
                {
                    OutputJson = true,
                    AlwaysComputeSourceSpans = true,
                    // Required so failing tests' StackFrames carry .al file paths via
                    // Roslyn-emitted #line directives → portable PDB.
                    EmitLineDirectives = true,
                    CancellationToken = ct,
                };
                options.InputPaths.AddRange(request.SourcePaths);
                if (request.PackagePaths != null)
                    options.PackagePaths.AddRange(request.PackagePaths);
                if (request.StubPaths != null)
                    options.StubPaths.AddRange(request.StubPaths);

                var pipeline = new AlRunnerPipeline();
                pipeline.RewriteCache = _rewriteCache;
                pipeline.SyntaxTreeCache = _syntaxTreeCache;
                var pipelineResult = pipeline.Run(options);

                assembly = pipelineResult.Assembly ?? Runtime.MockCodeunitHandle.CurrentAssembly;
                // Compilation errors (file-level exclusion was removed in #80; always empty now).
                compilationErrors = new Dictionary<string, List<string>>();
                sourceSpans = pipelineResult.SourceSpans;
                scopeToObject = pipelineResult.ScopeToObject;
                cached = false;

                // Compilation failure short-circuit: emit a summary-only response so the
                // client knows the request was processed but no tests ran.
                if (assembly == null)
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        type = "summary",
                        exitCode = pipelineResult.ExitCode,
                        passed = 0,
                        failed = 0,
                        errors = 0,
                        total = 0,
                        cached = false,
                        changedFiles = changedFiles?.Count > 0 ? changedFiles : null,
                        compilationErrors = (compilationErrors != null && compilationErrors.Count > 0)
                            ? compilationErrors.Select(kvp => new
                            {
                                file = Path.GetFileName(kvp.Key),
                                errors = kvp.Value,
                            })
                            : null,
                        protocolVersion = 2,
                    }, JsonOpts));
                    await output.FlushAsync();
                    return;
                }

                // Snapshot the total-statement set NOW (right after the pipeline ran
                // Executor.RegisterStatements) so we can restore it on a cache hit
                // when we ResetCoverage to clear stale hits. Tuple field names differ
                // (Type/Id vs Scope/StmtIndex) but the underlying value type is identical.
                var (_, totalStmtsRaw) = Runtime.AlScope.GetCoverageSets();
                var totalSnapshot = new HashSet<(string Scope, int StmtIndex)>(
                    totalStmtsRaw.Select(t => (t.Type, t.Id)));

                // Cache the compiled assembly along with its source-span / scope maps so
                // a subsequent identical request can serve coverage from the cache.
                if (pipelineResult.LoadContext != null)
                    _cache.Store(fingerprint, assembly, pipelineResult.LoadContext, compilationErrors, sourceSpans, scopeToObject, totalSnapshot);
            }

            // For cache-miss runs the pipeline already populated totals via
            // Executor.RegisterStatements before running tests, and we did
            // ResetCoverage above before that. Cache-hit runs need to:
            //   (1) reset the leftover hit set from any prior request, AND
            //   (2) restore the totals that were captured when the assembly was first compiled.
            if (cached)
            {
                Runtime.AlScope.ResetCoverage();
                if (cacheHit!.Value.TotalStatements != null)
                {
                    foreach (var (scope, stmtIdx) in cacheHit.Value.TotalStatements)
                        Runtime.AlScope.RegisterStatement(scope, stmtIdx);
                }
            }

            var allResults = new List<TestResult>();

            // Streaming callback: emit one `{"type":"test"}` line per completed test.
            // The dispatch loop is sequential, so no other handler is racing with us
            // for `output`. Sync write keeps the callback's contract simple.
            // The callback temporarily restores the real Console.Out so its line lands
            // on the server's protocol stdout channel, then re-redirects so subsequent
            // AL Message()/etc. calls stay captured.
            var realConsoleOut = Console.Out;
            var realConsoleErr = Console.Error;
            var redirectedOut = new StringWriter();
            var redirectedErr = new StringWriter();
            Console.SetOut(redirectedOut);
            Console.SetError(redirectedErr);
            try
            {
                void OnTestComplete(TestResult t)
                {
                    allResults.Add(t);
                    // Write directly to the protocol writer; Console.Out is currently
                    // redirected and so is unsafe to use here.
                    output.WriteLine(SerializeTestEvent(t));
                    output.Flush();
                }

                // Per-test isolation: the AL runtime captures Message() output via
                // TestExecutionScope (no enable flag needed — the scope is itself the
                // opt-in). Message() also calls Console.WriteLine for legacy CLI
                // consumers; we redirect Console.Out above so those legacy writes
                // don't corrupt our NDJSON stream.
                Executor.RunTests(
                    assembly!,
                    captureValues: request.CaptureValues == true,
                    filter: filter,
                    onTestComplete: OnTestComplete,
                    cancellationToken: ct);
            }
            finally
            {
                Console.SetOut(realConsoleOut);
                Console.SetError(realConsoleErr);
            }

            // Coverage emission (only when requested AND we have the source-span data).
            List<FileCoverage>? coverage = null;
            if (coverageRequested && sourceSpans != null && scopeToObject != null)
            {
                var (hitStmts, totalStmts) = Runtime.AlScope.GetCoverageSets();
                coverage = CoverageReport.ToJson(sourceSpans, hitStmts, totalStmts, scopeToObject);
            }

            if (coberturaRequested && sourceSpans != null && scopeToObject != null)
            {
                var (hitStmts, totalStmts) = Runtime.AlScope.GetCoverageSets();
                CoverageReport.WriteCobertura("cobertura.xml", sourceSpans, hitStmts, totalStmts, scopeToObject);
            }

            await output.WriteLineAsync(SerializeSummary(
                allResults,
                Executor.ExitCode(allResults),
                cached,
                changedFiles,
                compilationErrors,
                coverage,
                ct.IsCancellationRequested));
            await output.FlushAsync();
        }
        finally
        {
            _activeRequestCts?.Dispose();
            _activeRequestCts = null;
        }
    }

    /// <summary>
    /// Handle the <c>cancel</c> command. Signals the CancellationTokenSource that belongs
    /// to the currently-active <c>runtests</c> request, if any.
    ///
    /// Because the dispatch loop is sequential, a cancel command can only arrive AFTER a
    /// runtests request has fully completed (and its CTS has been disposed and nulled in
    /// the finally block). The noop=true path is therefore the common case. The noop=false
    /// path would only be reached if a future extension makes the loop non-sequential —
    /// at that point this handler already does the right thing.
    /// </summary>
    private string HandleCancel()
    {
        var cts = _activeRequestCts;
        if (cts == null || cts.IsCancellationRequested)
        {
            return JsonSerializer.Serialize(new
            {
                type = "ack",
                command = "cancel",
                noop = true
            });
        }
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Race: the runtests handler's finally-block disposed the CTS between our
            // snapshot and Cancel(). Treat as noop — the request already completed.
            return JsonSerializer.Serialize(new
            {
                type = "ack",
                command = "cancel",
                noop = true
            });
        }
        return JsonSerializer.Serialize(new
        {
            type = "ack",
            command = "cancel",
            noop = false
        });
    }

    private string HandleExecute(ServerRequest request, Task<List<Microsoft.CodeAnalysis.MetadataReference>> refsTask)
    {
        // Accept either inline code or a set of source paths (run-mode on
        // the first codeunit's OnRun trigger). One of the two must be set.
        if (string.IsNullOrWhiteSpace(request.Code) && (request.SourcePaths == null || request.SourcePaths.Length == 0))
            return JsonSerializer.Serialize(new { error = "execute requires either 'code' (inline AL) or 'sourcePaths'" });

        var options = new PipelineOptions { OutputJson = false };
        if (!string.IsNullOrWhiteSpace(request.Code))
            options.InlineCode = request.Code;
        if (request.SourcePaths != null)
            options.InputPaths.AddRange(request.SourcePaths);
        if (request.PackagePaths != null)
            options.PackagePaths.AddRange(request.PackagePaths);
        if (request.StubPaths != null)
            options.StubPaths.AddRange(request.StubPaths);
        if (request.CaptureValues == true)
            options.CaptureValues = true;

        var pipeline = new AlRunnerPipeline();
        pipeline.SyntaxTreeCache = _syntaxTreeCache;
        var result = pipeline.Run(options);

        return SerializeExecuteResponse(result);
    }

    private static string SerializeExecuteResponse(PipelineResult result)
    {
        var output = new
        {
            exitCode = result.ExitCode,
            // Tests may be empty for a pure run-mode invocation — keep the
            // field for shape consistency with runTests responses.
            tests = result.Tests.Select(t => new
            {
                name = t.Name,
                status = t.Status.ToString().ToLowerInvariant(),
                durationMs = t.DurationMs,
                message = t.Message,
                stackTrace = t.StackTrace?.TrimEnd(),
                alSourceLine = t.AlSourceLine,
                alSourceColumn = t.AlSourceColumn
            }),
            messages = result.Messages.Count > 0 ? result.Messages : null,
            capturedValues = result.CapturedValues.Count > 0
                ? result.CapturedValues.Select(c => new
                {
                    scopeName = c.ScopeName,
                    variableName = c.VariableName,
                    value = c.Value,
                    statementId = c.StatementId
                })
                : null
        };
        return JsonSerializer.Serialize(output, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }

    /// <summary>
    /// Serialize one protocol-v2 <c>{"type":"test"}</c> line. Field parity with the
    /// schema definition <c>TestEvent</c>:
    /// alSourceFile/Line/Column, errorKind, DAP-aligned stackFrames, captured messages,
    /// per-test capturedValues. Optional fields are omitted when null/empty.
    /// </summary>
    private static string SerializeTestEvent(TestResult t)
    {
        return JsonSerializer.Serialize(new
        {
            type = "test",
            name = t.Name,
            status = t.Status.ToString().ToLowerInvariant(),
            durationMs = t.DurationMs,
            message = t.Message,
            errorKind = t.ErrorKind?.ToString().ToLowerInvariant(),
            alSourceFile = t.AlSourceFile,
            alSourceLine = t.AlSourceLine,
            alSourceColumn = t.AlSourceColumn,
            stackFrames = t.StackFrames?.Select(f => new
            {
                name = f.Name,
                source = f.File != null ? new { path = f.File } : null,
                line = f.Line,
                column = f.Column,
                presentationHint = f.Hint.ToString().ToLowerInvariant(),
            }),
            stackTrace = t.StackTrace?.TrimEnd(),
            messages = (t.Messages != null && t.Messages.Count > 0) ? t.Messages : null,
            capturedValues = (t.CapturedValues != null && t.CapturedValues.Count > 0)
                ? t.CapturedValues.Select(c => new
                {
                    scopeName = c.ScopeName,
                    objectName = c.ObjectName,
                    variableName = c.VariableName,
                    value = c.Value,
                    statementId = c.StatementId,
                })
                : null,
        }, JsonOpts);
    }

    /// <summary>
    /// Serialize the terminal <c>{"type":"summary"}</c> line.
    /// Always carries <c>protocolVersion: 2</c>. Optional fields (cancelled, changedFiles,
    /// compilationErrors, coverage) are omitted when not applicable.
    /// </summary>
    private static string SerializeSummary(
        List<TestResult> tests,
        int exitCode,
        bool cached,
        List<string>? changedFiles,
        Dictionary<string, List<string>>? compilationErrors,
        List<FileCoverage>? coverage,
        bool cancelled)
    {
        return JsonSerializer.Serialize(new
        {
            type = "summary",
            exitCode,
            passed = tests.Count(t => t.Status == TestStatus.Pass),
            failed = tests.Count(t => t.Status == TestStatus.Fail),
            errors = tests.Count(t => t.Status == TestStatus.Error),
            total = tests.Count,
            cached,
            cancelled = cancelled ? (bool?)true : null,
            // Only emit changedFiles on cache miss with a non-empty diff.
            changedFiles = (cached || changedFiles == null || changedFiles.Count == 0) ? null : changedFiles,
            compilationErrors = (compilationErrors != null && compilationErrors.Count > 0)
                ? compilationErrors.Select(kvp => new
                {
                    file = Path.GetFileName(kvp.Key),
                    errors = kvp.Value,
                })
                : null,
            coverage = (coverage != null && coverage.Count > 0) ? coverage : null,
            protocolVersion = 2,
        }, JsonOpts);
    }

    private static AlRunner.TestFilter? ConvertFilter(TestFilterDto? dto)
    {
        if (dto == null) return null;
        return new AlRunner.TestFilter(
            CodeunitNames: dto.CodeunitNames != null ? new HashSet<string>(dto.CodeunitNames) : null,
            ProcNames: dto.ProcNames != null ? new HashSet<string>(dto.ProcNames) : null);
    }

    private static string HandleShutdown()
    {
        return JsonSerializer.Serialize(new { status = "shutting down" });
    }
}

/// <summary>
/// Multi-slot LRU cache of compiled assemblies keyed by a per-file-content
/// fingerprint. Also tracks the per-file hashes of the most recently served
/// request so callers can diff a new request and learn which files changed.
/// </summary>
public class CompilationCache
{
    // Small LRU — enough to cover "bounce between a handful of projects" usage
    // from IDE integrations without pinning large amounts of memory.
    private const int MaxSlots = 8;
    private readonly LinkedList<CacheEntry> _lru = new();

    /// <summary>Per-file hashes captured by the most recent ComputeFingerprint call.</summary>
    public IReadOnlyDictionary<string, string> LastFileHashes { get; private set; } =
        new Dictionary<string, string>();

    private sealed class CacheEntry
    {
        public required string Fingerprint { get; init; }
        public required Dictionary<string, string> FileHashes { get; init; }
        public required Assembly Assembly { get; init; }
        public required System.Runtime.Loader.AssemblyLoadContext LoadContext { get; init; }
        public required Dictionary<string, List<string>> CompilationErrors { get; init; }
        public Dictionary<(string Scope, int StmtIndex), int>? SourceSpans { get; init; }
        public Dictionary<string, string>? ScopeToObject { get; init; }
        /// <summary>
        /// Snapshot of the executable-statement set as registered by
        /// <see cref="Executor.RegisterStatements"/> at compile time. Used on a cache
        /// hit to restore the global <c>AlScope</c> total-set after a hit-only reset.
        /// </summary>
        public HashSet<(string Scope, int StmtIndex)>? TotalStatements { get; init; }
    }

    /// <summary>The result of a successful cache lookup.</summary>
    public readonly struct CacheHit
    {
        public Assembly Assembly { get; init; }
        public System.Runtime.Loader.AssemblyLoadContext LoadContext { get; init; }
        public Dictionary<string, List<string>> CompilationErrors { get; init; }
        /// <summary>
        /// Per-statement source-span map captured when the assembly was first compiled
        /// (cache miss path). Null when the original compile didn't request always-compute
        /// source spans. Protocol-v2 uses this to emit coverage on a cache hit without
        /// having to re-run the rewrite/compile pipeline.
        /// </summary>
        public Dictionary<(string Scope, int StmtIndex), int>? SourceSpans { get; init; }
        /// <summary>
        /// Scope class name → AL object name map captured alongside <see cref="SourceSpans"/>.
        /// Used to resolve coverage rows back to user-visible source files.
        /// </summary>
        public Dictionary<string, string>? ScopeToObject { get; init; }
        /// <summary>
        /// Snapshot of the executable-statement set so cache hits can restore the
        /// global AlScope total set after resetting hit counts for a new run.
        /// </summary>
        public HashSet<(string Scope, int StmtIndex)>? TotalStatements { get; init; }
    }

    /// <summary>
    /// Compute a stable fingerprint for the set of .al files reachable from
    /// the given paths, and update <see cref="LastFileHashes"/> as a side
    /// effect so the caller can diff against the previously served request.
    /// </summary>
    public string ComputeFingerprint(string[] sourcePaths)
    {
        var fileHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateFiles(sourcePaths))
        {
            using var sha = SHA256.Create();
            var bytes = File.ReadAllBytes(path);
            fileHashes[path] = Convert.ToHexString(sha.ComputeHash(bytes));
        }

        LastFileHashes = fileHashes;

        // Fingerprint is a hash of the sorted (path, file-hash) pairs so two
        // runs with the same files in the same state produce the same key.
        using var agg = SHA256.Create();
        foreach (var kv in fileHashes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var line = System.Text.Encoding.UTF8.GetBytes(kv.Key + "|" + kv.Value + "\n");
            agg.TransformBlock(line, 0, line.Length, line, 0);
        }
        agg.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(agg.Hash!);
    }

    /// <summary>
    /// Return the cached assembly and compilation errors for the given fingerprint,
    /// promoting the entry to the front of the LRU, or null on miss.
    /// </summary>
    public CacheHit? TryGet(string fingerprint)
    {
        var node = _lru.First;
        while (node != null)
        {
            if (node.Value.Fingerprint == fingerprint)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return new CacheHit
                {
                    Assembly = node.Value.Assembly,
                    LoadContext = node.Value.LoadContext,
                    CompilationErrors = node.Value.CompilationErrors,
                    SourceSpans = node.Value.SourceSpans,
                    ScopeToObject = node.Value.ScopeToObject,
                    TotalStatements = node.Value.TotalStatements
                };
            }
            node = node.Next;
        }
        return null;
    }

    /// <summary>
    /// Store an assembly and its compilation errors under the given fingerprint, evicting
    /// the LRU tail if full. Optional <paramref name="sourceSpans"/>,
    /// <paramref name="scopeToObject"/>, and <paramref name="totalStatements"/> are stashed
    /// alongside the entry so coverage can be served on a cache hit.
    /// </summary>
    public void Store(string fingerprint, Assembly assembly,
        System.Runtime.Loader.AssemblyLoadContext loadContext,
        Dictionary<string, List<string>> compilationErrors,
        Dictionary<(string Scope, int StmtIndex), int>? sourceSpans = null,
        Dictionary<string, string>? scopeToObject = null,
        HashSet<(string Scope, int StmtIndex)>? totalStatements = null)
    {
        var entry = new CacheEntry
        {
            Fingerprint = fingerprint,
            FileHashes = new Dictionary<string, string>(LastFileHashes, StringComparer.OrdinalIgnoreCase),
            Assembly = assembly,
            LoadContext = loadContext,
            CompilationErrors = compilationErrors,
            SourceSpans = sourceSpans,
            ScopeToObject = scopeToObject,
            TotalStatements = totalStatements
        };
        _lru.AddFirst(entry);
        while (_lru.Count > MaxSlots)
        {
            var evicted = _lru.Last!.Value;
            _lru.RemoveLast();
            evicted.LoadContext.Unload();
        }
    }

    /// <summary>
    /// Compare the currently-captured file hashes against the closest cached
    /// entry (by largest overlap) and return the list of files that differ
    /// — added, removed, or modified. Used to populate the response
    /// <c>changedFiles</c> field on a cache miss.
    /// </summary>
    public List<string> DiffAgainstClosest()
    {
        if (_lru.Count == 0 || LastFileHashes.Count == 0)
            return LastFileHashes.Keys.Select(Path.GetFileName).OfType<string>().ToList();

        CacheEntry? best = null;
        int bestOverlap = -1;
        foreach (var entry in _lru)
        {
            int overlap = 0;
            foreach (var kv in LastFileHashes)
            {
                if (entry.FileHashes.TryGetValue(kv.Key, out var prev) && prev == kv.Value)
                    overlap++;
            }
            if (overlap > bestOverlap)
            {
                best = entry;
                bestOverlap = overlap;
            }
        }

        var changed = new List<string>();
        if (best is null)
        {
            foreach (var path in LastFileHashes.Keys)
                changed.Add(Path.GetFileName(path) ?? path);
            return changed;
        }

        // Added or modified
        foreach (var kv in LastFileHashes)
        {
            if (!best.FileHashes.TryGetValue(kv.Key, out var prev) || prev != kv.Value)
                changed.Add(Path.GetFileName(kv.Key) ?? kv.Key);
        }
        // Removed
        foreach (var kv in best.FileHashes)
        {
            if (!LastFileHashes.ContainsKey(kv.Key))
                changed.Add(Path.GetFileName(kv.Key) ?? kv.Key);
        }
        return changed;
    }

    private static IEnumerable<string> EnumerateFiles(string[] sourcePaths)
    {
        foreach (var path in sourcePaths)
        {
            if (Directory.Exists(path))
            {
                foreach (var f in Directory.GetFiles(path, "*.al", SearchOption.AllDirectories).OrderBy(f => f))
                    yield return f;
            }
            else if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    // Back-compat for callers still using the old name.
    public string ComputeHash(string[] sourcePaths) => ComputeFingerprint(sourcePaths);
}

public class ServerRequest
{
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("sourcePaths")]
    public string[]? SourcePaths { get; set; }

    [JsonPropertyName("packagePaths")]
    public string[]? PackagePaths { get; set; }

    [JsonPropertyName("stubPaths")]
    public string[]? StubPaths { get; set; }

    /// <summary>Inline AL source (used by the <c>execute</c> command).</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>Opt-in to variable capture on <c>execute</c>.</summary>
    [JsonPropertyName("captureValues")]
    public bool? CaptureValues { get; set; }

    /// <summary>Protocol-v2 test filter (codeunit and/or procedure names).</summary>
    [JsonPropertyName("testFilter")]
    public TestFilterDto? TestFilter { get; set; }

    /// <summary>Protocol-v2: when true, emit per-file coverage in the summary.</summary>
    [JsonPropertyName("coverage")]
    public bool? Coverage { get; set; }

    /// <summary>Protocol-v2: when true, also write a Cobertura XML coverage report.</summary>
    [JsonPropertyName("cobertura")]
    public bool? Cobertura { get; set; }

    /// <summary>Optional negotiated protocol version (currently informational).</summary>
    [JsonPropertyName("protocolVersion")]
    public int? ProtocolVersion { get; set; }
}

/// <summary>
/// Wire-format DTO for the request-side <c>testFilter</c> object. Converted to the
/// internal <see cref="AlRunner.TestFilter"/> record before being passed to the executor.
/// </summary>
public class TestFilterDto
{
    [JsonPropertyName("codeunitNames")]
    public List<string>? CodeunitNames { get; set; }

    [JsonPropertyName("procNames")]
    public List<string>? ProcNames { get; set; }
}
