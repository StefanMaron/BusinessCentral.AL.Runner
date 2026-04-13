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
/// </summary>
public class AlRunnerServer
{
    private readonly CompilationCache _cache = new();
    private readonly RewriteCache _rewriteCache = new();

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

            string response;
            try
            {
                var request = JsonSerializer.Deserialize<ServerRequest>(line);
                if (request == null)
                {
                    response = JsonSerializer.Serialize(new { error = "Invalid request" });
                }
                else
                {
                    response = request.Command?.ToLowerInvariant() switch
                    {
                        "runtests" => HandleRunTests(request, refsTask),
                        "execute" => HandleExecute(request, refsTask),
                        "shutdown" => HandleShutdown(),
                        _ => JsonSerializer.Serialize(new { error = $"Unknown command: {request.Command}" })
                    };

                    if (request.Command?.ToLowerInvariant() == "shutdown")
                    {
                        await output.WriteLineAsync(response);
                        await output.FlushAsync();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                response = JsonSerializer.Serialize(new { error = ex.Message });
            }

            await output.WriteLineAsync(response);
            await output.FlushAsync();
        }
    }

    private string HandleRunTests(ServerRequest request, Task<List<Microsoft.CodeAnalysis.MetadataReference>> refsTask)
    {
        if (request.SourcePaths == null || request.SourcePaths.Length == 0)
            return JsonSerializer.Serialize(new { error = "sourcePaths is required" });

        // Fingerprint the request — also updates per-file hashes used for the diff.
        var fingerprint = _cache.ComputeFingerprint(request.SourcePaths);
        var cacheHit = _cache.TryGet(fingerprint);

        if (cacheHit != null)
        {
            // Cache hit — re-run tests on the cached assembly, returning the
            // compilation errors that were seen when the assembly was first compiled.
            Runtime.MockCodeunitHandle.CurrentAssembly = cacheHit.Value.Assembly;
            var results = Executor.RunTests(cacheHit.Value.Assembly);
            return SerializeServerResponse(results, Executor.ExitCode(results), cached: true,
                compilationErrors: cacheHit.Value.CompilationErrors);
        }

        // Cache miss — diff BEFORE storing the new entry so the report
        // reflects "what changed since the closest previous run".
        var changedFiles = _cache.DiffAgainstClosest();

        var options = new PipelineOptions { OutputJson = true };
        options.InputPaths.AddRange(request.SourcePaths);
        if (request.PackagePaths != null)
            options.PackagePaths.AddRange(request.PackagePaths);
        if (request.StubPaths != null)
            options.StubPaths.AddRange(request.StubPaths);

        var pipeline = new AlRunnerPipeline();
        pipeline.RewriteCache = _rewriteCache;
        var result = pipeline.Run(options);

        // Compilation errors (file-level exclusion was removed in #80; always empty now).
        var compilationErrors = new Dictionary<string, List<string>>();

        // Cache the compiled assembly (with its compilation errors) if available.
        if (result.ExitCode == 0 || result.Tests.Count > 0)
        {
            var assembly = Runtime.MockCodeunitHandle.CurrentAssembly;
            if (assembly != null)
                _cache.Store(fingerprint, assembly, compilationErrors);
        }

        return SerializeServerResponse(result.Tests, result.ExitCode, cached: false,
            changedFiles: changedFiles, compilationErrors: compilationErrors);
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

    private static string SerializeServerResponse(List<TestResult> tests, int exitCode, bool cached,
        List<string>? changedFiles = null, Dictionary<string, List<string>>? compilationErrors = null)
    {
        // compilationErrors is passed in explicitly — either from the live compilation (cache miss)
        // or from the stored cache entry (cache hit).
        var compilationErrorsObj = compilationErrors != null && compilationErrors.Count > 0
            ? (object?)compilationErrors.Select(kvp => new
            {
                file = System.IO.Path.GetFileName(kvp.Key),
                errors = kvp.Value
            })
            : null;

        var output = new
        {
            tests = tests.Select(t => new
            {
                name = t.Name,
                status = t.Status.ToString().ToLowerInvariant(),
                durationMs = t.DurationMs,
                message = t.Message,
                stackTrace = t.StackTrace?.TrimEnd()
            }),
            passed = tests.Count(t => t.Status == TestStatus.Pass),
            failed = tests.Count(t => t.Status == TestStatus.Fail),
            errors = tests.Count(t => t.Status == TestStatus.Error),
            total = tests.Count,
            exitCode,
            compilationErrors = compilationErrorsObj,
            cached,
            // Only emit changedFiles on cache miss — cache hits have no diff.
            changedFiles = cached ? null : changedFiles
        };

        return JsonSerializer.Serialize(output, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
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
        public required Dictionary<string, List<string>> CompilationErrors { get; init; }
    }

    /// <summary>The result of a successful cache lookup.</summary>
    public readonly struct CacheHit
    {
        public Assembly Assembly { get; init; }
        public Dictionary<string, List<string>> CompilationErrors { get; init; }
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
                    CompilationErrors = node.Value.CompilationErrors
                };
            }
            node = node.Next;
        }
        return null;
    }

    /// <summary>Store an assembly and its compilation errors under the given fingerprint, evicting the LRU tail if full.</summary>
    public void Store(string fingerprint, Assembly assembly, Dictionary<string, List<string>> compilationErrors)
    {
        var entry = new CacheEntry
        {
            Fingerprint = fingerprint,
            FileHashes = new Dictionary<string, string>(LastFileHashes, StringComparer.OrdinalIgnoreCase),
            Assembly = assembly,
            CompilationErrors = compilationErrors
        };
        _lru.AddFirst(entry);
        while (_lru.Count > MaxSlots)
            _lru.RemoveLast();
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
}
