using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AlRunner;

public class PipelineOptions
{
    public List<string> InputPaths { get; set; } = new();
    public List<string> PackagePaths { get; set; } = new();
    public List<string> StubPaths { get; set; } = new();
    public string? InlineCode { get; set; }
    public bool DumpCSharp { get; set; }
    public bool DumpRewritten { get; set; }
    public bool ShowCoverage { get; set; }
    public bool Verbose { get; set; }
    public bool OutputJson { get; set; }
    public bool CaptureValues { get; set; }
    public bool IterationTracking { get; set; }
    /// <summary>Run only this specific procedure by name (e.g. "TestCalculateVAT").</summary>
    public string? RunProcedure { get; set; }
    /// <summary>If set, write a JUnit XML test report to this path after test execution.</summary>
    public string? OutputJunitPath { get; set; }
    /// <summary>When true, promote exit code 2 (runner limitations) to exit code 1 (failure).</summary>
    public bool Strict { get; set; }

    /// <summary>
    /// When true, fire standard BC lifecycle integration events (OnCompanyInitialize from
    /// Codeunit 27, OnInstallAppPerCompany from Codeunit 2) before test execution.
    /// This allows extensions with [EventSubscriber] on system events to perform
    /// setup work without the actual system codeunits being present.
    /// </summary>
    public bool InitEvents { get; set; }

    /// <summary>Configures the value returned by UserId() — defaults to "TESTUSER".</summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Controls when in-memory tables are reset between tests.
    /// Codeunit (default) — reset between test codeunits; within a codeunit all test
    /// methods share table state (BC's default TestIsolation::Codeunit behaviour).
    /// Method — reset before every individual test method (the previous runner default).
    /// </summary>
    public TestIsolation TestIsolation { get; set; } = TestIsolation.Codeunit;

    /// <summary>Directories containing pre-compiled dependency DLLs (rewritten C# with mock types).</summary>
    public List<string> DepDllPaths { get; set; } = new();

    /// <summary>
    /// Optional override for the C# rewriter step, intended for unit-testing the pipeline's
    /// rewriter-error-handling path. When set, replaces <see cref="RoslynRewriter.RewriteToTree"/>
    /// for every object. Throw to simulate a rewriter gap; return a tree with bad C# to simulate
    /// a Roslyn compilation gap.
    /// </summary>
    public Func<string, Microsoft.CodeAnalysis.SyntaxTree>? RewriterFactory { get; set; }
}

public enum TestStatus { Pass, Fail, Error }

/// <summary>
/// Controls when in-memory tables are reset between tests.
/// Matches BC's TestIsolation property on test codeunits.
/// </summary>
public enum TestIsolation
{
    /// <summary>
    /// Reset tables between test codeunits. Within the same codeunit all test
    /// methods share table state. This is BC's default TestIsolation::Codeunit.
    /// </summary>
    Codeunit,

    /// <summary>
    /// Reset tables before every individual test method. The previous runner
    /// default; useful when tests are not designed for shared state.
    /// </summary>
    Method,
}

public class TestResult
{
    public required string Name { get; init; }
    public required TestStatus Status { get; init; }
    public long DurationMs { get; init; }
    public string? Message { get; init; }
    public string? StackTrace { get; init; }
    /// <summary>AL source line where the error occurred (null for passing tests).</summary>
    public int? AlSourceLine { get; init; }
    /// <summary>
    /// AL source column where the error occurred (1-based, null for passing
    /// tests or when the underlying source-span lacks column info).
    /// </summary>
    public int? AlSourceColumn { get; init; }
    /// <summary>
    /// True when the error originates from AlRunner.Runtime mock code (a runner
    /// limitation or bug), not from user AL logic or a missing dependency injection.
    /// </summary>
    public bool IsRunnerBug { get; init; }
    /// <summary>
    /// The AL codeunit name that contains this test method. Used for grouping
    /// tests by suite in JUnit XML output.
    /// </summary>
    public string? CodeunitName { get; init; }
}

public class CapturedValue
{
    public required string ScopeName { get; init; }
    public required string ObjectName { get; init; }
    public required string VariableName { get; init; }
    public string? Value { get; init; }
    public int StatementId { get; init; }
}

public class PipelineResult
{
    public int ExitCode { get; init; }
    public List<TestResult> Tests { get; init; } = new();
    public List<CapturedValue> CapturedValues { get; init; } = new();
    public List<string> Messages { get; init; } = new();
    public int Passed => Tests.Count(t => t.Status == TestStatus.Pass);
    public int Failed => Tests.Count(t => t.Status == TestStatus.Fail);
    public int Errors => Tests.Count(t => t.Status == TestStatus.Error);
    public string? ErrorMessage { get; init; }
    public List<Runtime.IterationTracker.LoopRecord>? Iterations { get; init; }

    /// <summary>Captured stdout lines from the pipeline.</summary>
    public string StdOut { get; init; } = "";
    /// <summary>Captured stderr lines from the pipeline.</summary>
    public string StdErr { get; init; } = "";

    /// <summary>The compiled assembly (null when compilation failed).</summary>
    public Assembly? Assembly { get; init; }
    /// <summary>The collectible ALC that owns <see cref="Assembly"/>. Call Unload() to release.</summary>
    public System.Runtime.Loader.AssemblyLoadContext? LoadContext { get; init; }

    /// <summary>
    /// AL objects whose C# could not be rewritten (rewriter gap).
    /// Each entry is (ObjectName, ErrorMessage). Non-null only when at least one rewriter failure occurred.
    /// </summary>
    public List<(string Name, string Error)>? RewriterErrors { get; init; }

    /// <summary>
    /// C# compiler error messages from the Roslyn compilation stage, when compilation failed.
    /// Non-null only when compilation produced errors.
    /// </summary>
    public List<string>? CompilationErrors { get; init; }
}

public class AlRunnerPipeline
{
    private Dictionary<string, string>? _scopeToObject;
    private RoslynCompiler.CompileResult? _compileResult;
    private List<(string Name, string Error)>? _rewriterErrors;
    private List<string>? _compilationErrors;
    public RewriteCache? RewriteCache { get; set; }
    public SyntaxTreeCache? SyntaxTreeCache { get; set; }

    /// <summary>
    /// Run the full AL Runner pipeline: transpile → rewrite → compile → execute.
    /// Returns a structured result instead of writing to stdout/stderr directly.
    /// </summary>
    public PipelineResult Run(PipelineOptions options)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var testResults = new List<TestResult>();

        // Redirect Console.Out and Console.Error into the captured
        // StringWriters for the duration of the run. Parts of the pipeline
        // (AlTranspiler diagnostics) and the AL runtime (AlDialog.Message,
        // PrintResults) write directly to the process console, which would
        // otherwise corrupt the server's stdin/stdout JSON protocol and
        // make tests non-deterministic. The CLI caller is responsible for
        // forwarding result.StdOut / result.StdErr back to the real console.
        var originalConsoleError = Console.Error;
        var originalConsoleOut = Console.Out;
        Console.SetError(stderr);
        Console.SetOut(stdout);
        int exitCode;
        try
        {
            exitCode = RunCore(options, stdout, stderr, testResults);
        }
        finally
        {
            Console.SetError(originalConsoleError);
            Console.SetOut(originalConsoleOut);
        }

        // Collect captured values
        var capturedValues = new List<CapturedValue>();
        if (options.CaptureValues)
        {
            foreach (var (scopeName, objectName, variableName, value, stmtId) in Runtime.ValueCapture.GetCaptures())
            {
                capturedValues.Add(new CapturedValue
                {
                    ScopeName = scopeName,
                    ObjectName = objectName,
                    VariableName = variableName,
                    Value = value,
                    StatementId = stmtId
                });
            }
        }

        // Collect messages
        var messages = Runtime.MessageCapture.GetMessages();

        // Collect iteration data
        List<Runtime.IterationTracker.LoopRecord>? iterationLoops = null;
        if (options.IterationTracking)
        {
            iterationLoops = Runtime.IterationTracker.GetLoops();
        }

        var stdoutStr = stdout.ToString();
        if (options.OutputJson)
        {
            // Always emit JSON when --output-json is set, even when there are no tests,
            // so that tooling can observe compilation/rewriter gap errors via exitCode and errors fields.
            IReadOnlyDictionary<string, List<string>>? compilationErrorsDict = null;
            if (_compilationErrors?.Count > 0)
            {
                compilationErrorsDict = new Dictionary<string, List<string>>
                {
                    ["roslyn"] = _compilationErrors
                };
            }
            stdoutStr = SerializeJsonOutput(testResults, exitCode, capturedValues: capturedValues, messages: messages, iterations: iterationLoops, compilationErrors: compilationErrorsDict, scopeToObject: _scopeToObject);
        }

        if (options.OutputJunitPath != null && testResults.Count > 0)
        {
            JUnitReport.WriteJUnit(options.OutputJunitPath, testResults);
        }

        return new PipelineResult
        {
            ExitCode = exitCode,
            Tests = testResults,
            CapturedValues = capturedValues,
            Messages = messages,
            Iterations = iterationLoops,
            StdOut = stdoutStr,
            StdErr = stderr.ToString(),
            Assembly = _compileResult?.Assembly,
            LoadContext = _compileResult?.LoadContext,
            RewriterErrors = _rewriterErrors,
            CompilationErrors = _compilationErrors
        };
    }

    public static string SerializeJsonOutput(
        List<TestResult> tests, int exitCode, bool indented = true,
        List<CapturedValue>? capturedValues = null, List<string>? messages = null,
        List<Runtime.IterationTracker.LoopRecord>? iterations = null,
        IReadOnlyDictionary<string, List<string>>? compilationErrors = null,
        Dictionary<string, string>? scopeToObject = null)
    {
        object? capturedValuesObj = capturedValues?.Count > 0
            ? capturedValues.Select(c => new
            {
                scopeName = c.ScopeName,
                sourceFile = SourceFileMapper.GetFile(c.ObjectName),
                variableName = c.VariableName,
                value = c.Value,
                statementId = c.StatementId
            })
            : null;

        object? compilationErrorsObj = compilationErrors?.Count > 0
            ? compilationErrors.Select(kvp => new
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
                stackTrace = t.StackTrace?.TrimEnd(),
                alSourceLine = t.AlSourceLine,
                alSourceColumn = t.AlSourceColumn,
                isRunnerBug = t.IsRunnerBug ? (bool?)true : null
            }),
            passed = tests.Count(t => t.Status == TestStatus.Pass),
            failed = tests.Count(t => t.Status == TestStatus.Fail),
            errors = tests.Count(t => t.Status == TestStatus.Error),
            total = tests.Count,
            exitCode,
            compilationErrors = compilationErrorsObj,
            capturedValues = capturedValuesObj,
            messages = messages?.Count > 0 ? messages : null,
            iterations = iterations?.Count > 0
                ? iterations.Select(loop => new
                {
                    loopId = $"L{loop.LoopId}",
                    sourceFile = scopeToObject != null
                        ? SourceFileMapper.GetFileForScope(loop.ScopeName, scopeToObject)
                        : null,
                    loopLine = SourceLineMapper.GetAlLineFromStatement(loop.ScopeName, loop.SourceStartLine) ?? loop.SourceStartLine,
                    loopEndLine = SourceLineMapper.GetAlLineFromStatement(loop.ScopeName, loop.SourceEndLine) ?? loop.SourceEndLine,
                    parentLoopId = loop.ParentLoopId.HasValue ? $"L{loop.ParentLoopId}" : (string?)null,
                    parentIteration = loop.ParentIteration,
                    iterationCount = loop.IterationCount,
                    steps = loop.Steps.Select(step => new
                    {
                        iteration = step.Iteration,
                        capturedValues = step.CapturedValues.Select(cv => new
                        {
                            variableName = cv.VariableName,
                            value = cv.Value
                        }),
                        messages = step.Messages.Count > 0 ? step.Messages : null,
                        linesExecuted = step.LinesExecuted
                            .Select(id => SourceLineMapper.GetAlLineFromStatement(loop.ScopeName, id) ?? id)
                            .Distinct()
                            .OrderBy(l => l)
                            .ToList()
                    })
                })
                : null
        };

        return JsonSerializer.Serialize(output, new JsonSerializerOptions
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }

    private int RunCore(PipelineOptions options, TextWriter stdout, TextWriter stderr, List<TestResult> testResults)
    {
        if (options.Verbose)
            Log.Verbose = true;

        // Apply configurable session properties
        Runtime.AlScope.UserId = options.UserId ?? "TESTUSER";

        var alSources = new List<string>();
        var sourceFilePaths = new List<string?>(); // parallel to alSources: file path or null
        var stubSources = new List<string>();
        var assertStubSources = new List<string>();
        var inputPaths = new List<string>();
        var inputGroups = new List<(string Path, List<string> Sources)>();

        // Handle inline code
        if (options.InlineCode != null)
        {
            var code = PrepareSourceForStandalone(options.InlineCode);
            if (!code.TrimStart().StartsWith("codeunit", StringComparison.OrdinalIgnoreCase) &&
                !code.TrimStart().StartsWith("table", StringComparison.OrdinalIgnoreCase))
            {
                code = $"codeunit 99 __Inline {{ trigger OnRun() begin {code} end; }}";
            }
            alSources.Add(code);
            sourceFilePaths.Add(null); // inline code has no file path
        }

        // Reset per-run state that the AL parsing populates.
        Runtime.EnumRegistry.Clear();
        Runtime.TableInitValueRegistry.Clear();
        Runtime.CodeunitNameRegistry.Clear();
        Runtime.CalcFormulaRegistry.Clear();
        Runtime.TableFieldRegistry.Clear();
        Runtime.MockNumberSequence.Reset();
        SourceFileMapper.Clear();

        // Load stubs
        foreach (var stubPath in options.StubPaths)
        {
            if (!Directory.Exists(stubPath))
            {
                stderr.WriteLine($"Error: stubs directory not found: {stubPath}");
                return 1;
            }
            Log.HasStubs = true;
            var stubFiles = Directory.GetFiles(stubPath, "*.al", SearchOption.AllDirectories).OrderBy(f => f).ToList();
            Log.Info($"Loading {stubFiles.Count} stub files from {stubPath}");
            foreach (var sf in stubFiles)
            {
                var text = PrepareSourceForStandalone(File.ReadAllText(sf));
                stubSources.Add(text);
                StubIndex.Record(sf, text);
                Runtime.EnumRegistry.ParseAndRegister(text);
                Runtime.TableInitValueRegistry.ParseAndRegister(text);
                Runtime.CodeunitNameRegistry.ParseAndRegister(text);
                Runtime.CalcFormulaRegistry.ParseAndRegister(text);
                Runtime.TableFieldRegistry.ParseAndRegister(text);
            }
        }

        // Load input paths
        foreach (var path in options.InputPaths)
        {
            if (path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                var extracted = AppPackageReader.ExtractAlSources(path);
                if (extracted.Count == 0)
                {
                    stderr.WriteLine($"Error: no .al files found in app package {path}");
                    return 1;
                }
                Log.Info($"Loading {extracted.Count} AL files from {Path.GetFileName(path)}");
                var groupSources = new List<string>();
                foreach (var (name, source) in extracted)
                {
                    var preparedSource = PrepareSourceForStandalone(source);
                    Log.Info($"  {name}");
                    alSources.Add(preparedSource);
                    sourceFilePaths.Add(null); // .app extracts have no disk file path
                    groupSources.Add(preparedSource);

                    // Register extracted objects with SourceFileMapper using the .app-relative name
                    foreach (var objName in SourceFileMapper.ParseObjectDeclarations(preparedSource))
                        SourceFileMapper.Register(objName, name);
                }
                var fullPath = Path.GetFullPath(path);
                inputPaths.Add(fullPath);
                inputGroups.Add((fullPath, groupSources));
            }
            else if (Directory.Exists(path))
            {
                var alFiles = Directory.GetFiles(path, "*.al", SearchOption.AllDirectories)
                    .OrderBy(f => f).ToList();
                if (alFiles.Count == 0)
                {
                    stderr.WriteLine($"Error: no .al files found in directory {path}");
                    return 1;
                }
                Log.Info($"Loading {alFiles.Count} AL files from {path}");
                var groupSources = new List<string>();
                foreach (var f in alFiles)
                {
                    Log.Info($"  {Path.GetFileName(f)}");
                    var src = PrepareSourceForStandalone(File.ReadAllText(f));
                    alSources.Add(src);
                    sourceFilePaths.Add(Path.GetFullPath(f));
                    groupSources.Add(src);

                    var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), f);
                    foreach (var objName in SourceFileMapper.ParseObjectDeclarations(src))
                        SourceFileMapper.Register(objName, relativePath);
                }
                var fullPath = Path.GetFullPath(path);
                inputPaths.Add(fullPath);
                inputGroups.Add((fullPath, groupSources));
            }
            else if (File.Exists(path))
            {
                var src = PrepareSourceForStandalone(File.ReadAllText(path));
                alSources.Add(src);
                sourceFilePaths.Add(Path.GetFullPath(path));
                var fullPath = Path.GetFullPath(Path.GetDirectoryName(path)!);
                inputPaths.Add(fullPath);
                inputGroups.Add((fullPath, new List<string> { src }));

                var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
                foreach (var objName in SourceFileMapper.ParseObjectDeclarations(src))
                    SourceFileMapper.Register(objName, relativePath);
            }
            else
            {
                stderr.WriteLine($"Error: file or directory not found: {path}");
                return 1;
            }
        }

        if (alSources.Count == 0)
        {
            stderr.WriteLine("Error: no AL source provided");
            return 1;
        }

        // Parse all AL sources for enum declarations, table InitValue
        // defaults, and codeunit name-to-ID mappings so runtime
        // Ordinals()/Names(), Rec.Init(), and enum-to-interface dispatch
        // can resolve without reflecting over generated C#.
        foreach (var src in alSources)
        {
            Runtime.EnumRegistry.ParseAndRegister(src);
            Runtime.TableInitValueRegistry.ParseAndRegister(src);
            Runtime.CodeunitNameRegistry.ParseAndRegister(src);
            Runtime.CalcFormulaRegistry.ParseAndRegister(src);
            Runtime.TableFieldRegistry.ParseAndRegister(src);
        }
        if (options.InlineCode != null)
        {
            Runtime.EnumRegistry.ParseAndRegister(options.InlineCode);
            Runtime.TableInitValueRegistry.ParseAndRegister(options.InlineCode);
            Runtime.CodeunitNameRegistry.ParseAndRegister(options.InlineCode);
            Runtime.CalcFormulaRegistry.ParseAndRegister(options.InlineCode);
            Runtime.TableFieldRegistry.ParseAndRegister(options.InlineCode);
        }

        // Auto-discover dependency .app files from --packages directories.
        // Note: dependency/stub sources are intentionally NOT registered with SourceFileMapper —
        // they are external code whose captured values and coverage should not appear in user files.
        if (options.PackagePaths.Count > 0 && inputGroups.Any(g => g.Path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)))
        {
            AutoDiscoverDependencies(options.PackagePaths, inputGroups, inputPaths, alSources);
        }

        // Kernel32 shim
        Kernel32Shim.EnsureRegistered();

        // Auto-include AL stubs (Assert, etc.)
        LoadAssertStubs(options.PackagePaths, inputPaths, inputGroups, alSources, assertStubSources);
        // Pad sourceFilePaths with nulls for any stubs added by LoadAssertStubs
        while (sourceFilePaths.Count < alSources.Count)
            sourceFilePaths.Add(null);

        // Step 1: Transpile
        Timer.StartStage("AL transpilation");
        var generatedCSharpList = Transpile(options, alSources, inputGroups, inputPaths, stubSources, stderr,
            syntaxTreeCache: SyntaxTreeCache, sourceFilePaths: sourceFilePaths);
        if (generatedCSharpList == null)
        {
            Timer.EndStage("AL transpilation");
            return 3;
        }

        // Compile dependency stubs separately
        var separateStubSources = GetSeparateStubSources(stubSources, alSources);
        if (separateStubSources.Count > 0)
        {
            var depStubCSharp = AlTranspiler.TranspileMulti(separateStubSources, options.PackagePaths, inputPaths);
            if (depStubCSharp != null && depStubCSharp.Count > 0)
            {
                generatedCSharpList.AddRange(depStubCSharp);
                Log.Info($"Added {depStubCSharp.Count} dependency stub(s) for runtime dispatch");
            }
        }

        // Assert stubs compiled separately when Assert.app found in packages
        if (assertStubSources.Count > 0)
        {
            // Compile runtime-only built-in stubs in isolation so they don't
            // collide with the real BC test library symbols already referenced
            // by the main compilation.
            var stubCSharp = AlTranspiler.TranspileMulti(assertStubSources);
            if (stubCSharp != null)
            {
                generatedCSharpList.AddRange(stubCSharp);
                Log.Info($"Added {stubCSharp.Count} Assert stub(s) for runtime dispatch");
            }
        }

        // Register C# class names → AL object names for captured value resolution
        var outerClassPattern = new System.Text.RegularExpressions.Regex(@"^\s*public\s+(?:\w+\s+)*class\s+(\w+)", System.Text.RegularExpressions.RegexOptions.Multiline);
        foreach (var (name, code) in generatedCSharpList)
        {
            var classMatch = outerClassPattern.Match(code);
            if (classMatch.Success)
                SourceFileMapper.RegisterClass(classMatch.Groups[1].Value, name);
        }

        Log.Info($"\nTranspiled {generatedCSharpList.Count} AL objects to C#");

        if (options.DumpCSharp)
        {
            foreach (var (name, code) in generatedCSharpList)
            {
                stdout.WriteLine($"=== Generated C# for {name} (before rewriting) ===");
                stdout.WriteLine(code);
                stdout.WriteLine($"=== End {name} ===\n");
            }
        }
        Timer.EndStage("AL transpilation");

        // Step 2: Rewrite
        Timer.StartStage("Roslyn rewriting");
        var refsTask = Task.Run(() => RoslynCompiler.LoadReferences());

        var rewrittenTrees = new (string Name, Microsoft.CodeAnalysis.SyntaxTree Tree)[generatedCSharpList.Count];
        int rewriteHits = 0;
        var rewriteFailures = new System.Collections.Concurrent.ConcurrentBag<(string Name, string Error)>();
        // Regex to extract the first class name from generated C# — used to build
        // minimal fallback classes that preserve the type name for dependent objects.
        var classNamePattern = new System.Text.RegularExpressions.Regex(
            @"^\s*public\s+(?:\w+\s+)*class\s+(\w+)",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        Parallel.For(0, generatedCSharpList.Count, i =>
        {
            var (name, code) = generatedCSharpList[i];

            // Check rewrite cache — if C# output is unchanged, reuse prior tree
            var cached = RewriteCache?.TryGet(name, code, options.IterationTracking);
            if (cached != null)
            {
                rewrittenTrees[i] = (name, cached);
                Interlocked.Increment(ref rewriteHits);
                return;
            }

            try
            {
                // Use the injected rewriter factory if provided (for testing), otherwise the real rewriter.
                var rewriterFn = options.RewriterFactory ?? RoslynRewriter.RewriteToTree;
                var tree = rewriterFn(code);
                // Second pass: inject per-statement ValueCapture.Capture calls.
                // The capture function no-ops when ValueCapture.Enabled is false,
                // so we can run this unconditionally and avoid a separate code
                // path for capture vs non-capture runs.
                var injectedRoot = ValueCaptureInjector.Inject(tree.GetRoot(), name);
                if (options.IterationTracking)
                    injectedRoot = IterationInjector.Inject(injectedRoot);
                tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.Create((Microsoft.CodeAnalysis.CSharp.CSharpSyntaxNode)injectedRoot);
                rewrittenTrees[i] = (name, tree);

                // Store in cache for next run (skip when using an injected factory)
                if (options.RewriterFactory == null)
                    RewriteCache?.Store(name, code, tree, options.IterationTracking);
            }
            catch (Exception ex)
            {
                // Generate a minimal fallback class that preserves the type name so
                // that dependent objects can still reference it during Roslyn compilation.
                // This avoids silently dropping the object and causing a cascade of
                // "type not found" errors in unrelated objects.
                var classMatch = classNamePattern.Match(code);
                var className = classMatch.Success ? classMatch.Groups[1].Value : $"FallbackClass_{i}";
                var fallback = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                    $"// Rewriter failed: {ex.Message}\npublic class {className} {{ }}");
                rewrittenTrees[i] = (name, fallback);
                var msg = $"{ex.GetType().Name}: {ex.Message}";
                rewriteFailures.Add((name, msg));
                if (Log.Verbose)
                    stderr.WriteLine($"  rewriter exception for '{name}': {ex}");
            }
        });

        // Track rewriter exit code but do NOT bail out early — continue with fallback
        // trees so that dependent objects can still compile and their tests can run.
        int rewriterExitCode = 0;
        if (rewriteFailures.Count > 0)
        {
            var failures = rewriteFailures.OrderBy(f => f.Name).ToList();
            stderr.WriteLine();
            stderr.WriteLine($"ERROR: C# rewriting failed for {failures.Count} AL object(s)");
            stderr.WriteLine("  ⚑ These objects contain AL constructs not yet handled by the runner's rewriter.");
            foreach (var (failName, failMsg) in failures)
                stderr.WriteLine($"    × {failName}: {failMsg}");
            stderr.WriteLine();
            stderr.WriteLine("  To debug: run with --dump-csharp to see the generated C# before rewriting.");
            stderr.WriteLine("  You may be prompted to report this via telemetry in interactive mode (run with --no-telemetry to opt out).");
            _rewriterErrors = failures;
            rewriterExitCode = options.Strict ? 1 : 2;
        }

        if (rewriteHits > 0)
            Log.Info($"Rewrite cache: {rewriteHits}/{generatedCSharpList.Count} hits");
        // All objects now have trees (failures use a minimal fallback class), so no filtering needed.
        var rewrittenTreeList = rewrittenTrees
            .Select(t => (t.Name, t.Tree))
            .ToList();

        List<(string Name, string Code)>? _rewrittenStringList = null;
        List<(string Name, string Code)> GetRewrittenStrings()
        {
            if (_rewrittenStringList == null)
            {
                _rewrittenStringList = rewrittenTreeList
                    .Select(t => (t.Name, t.Tree.GetRoot().ToFullString()))
                    .ToList();
            }
            return _rewrittenStringList;
        }

        if (options.DumpRewritten)
        {
            foreach (var (name, code) in GetRewrittenStrings())
            {
                stdout.WriteLine($"=== Rewritten C# for {name} ===");
                stdout.WriteLine(code);
                stdout.WriteLine($"=== End {name} ===\n");
            }
        }
        Timer.EndStage("Roslyn rewriting");

        // Inject minimal C# stub classes for test-toolkit codeunits (130000–139999) that
        // are referenced in the generated C# but were NOT compiled from source.
        // This allows MockCodeunitHandle.FindCodeunitType to find them at runtime and
        // execute as no-ops (no OnRun method → InvokeOnRun silently returns) instead of
        // throwing InvalidOperationException.
        // Auto-stub test-toolkit codeunits (130000-139999) as empty classes so
        // they compile. Methods on these stubs will be no-ops (return defaults).
        // For proper method implementations, users should run:
        //   al-runner --generate-stubs .alpackages ./stubs ./src ./test
        var testToolkitStubs = GenerateTestToolkitStubs(generatedCSharpList, options.PackagePaths, inputPaths);
        if (testToolkitStubs.Count > 0)
        {
            rewrittenTreeList.AddRange(testToolkitStubs);
            stderr.WriteLine($"Auto-stubbed {testToolkitStubs.Count} dependency codeunit(s) — methods return defaults");
            stderr.WriteLine($"  Tip: for proper stubs run: al-runner --generate-stubs .alpackages ./stubs ./src ./test");
            Log.Info($"  IDs: {string.Join(", ", testToolkitStubs.Select(s => s.Name))}");

        }

        // Step 3: Compile
        Timer.StartStage("Roslyn compilation");
        var mapperTask = Task.Run(() =>
            SourceLineMapper.Build(generatedCSharpList, GetRewrittenStrings()));
        var preloadedRefs = refsTask.Result;

        // Load dependency DLLs as MetadataReferences for Roslyn compilation
        var depDllAssemblyPaths = new List<string>();
        foreach (var depDir in options.DepDllPaths)
        {
            if (!Directory.Exists(depDir)) continue;
            foreach (var dll in Directory.GetFiles(depDir, "*.dll"))
            {
                try
                {
                    preloadedRefs.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(dll));
                    depDllAssemblyPaths.Add(Path.GetFullPath(dll));
                    Log.Info($"Added dep DLL reference: {Path.GetFileName(dll)}");
                }
                catch (Exception ex) { Log.Info($"Skipping dep DLL {dll}: {ex.Message}"); }
            }
        }

        var compilationErrorSink = new List<string>();
        var compileResult = RoslynCompiler.Compile(rewrittenTreeList, preloadedRefs, compilationErrorSink);
        mapperTask.Wait();
        if (compileResult == null)
        {
            if (compilationErrorSink.Count > 0)
                _compilationErrors = compilationErrorSink;
            if (!options.DumpRewritten && options.Verbose)
            {
                stderr.WriteLine("\n--- Rewritten C# (for debugging compilation failure) ---");
                foreach (var (name, code) in GetRewrittenStrings())
                {
                    stderr.WriteLine($"=== {name} ===");
                    stderr.WriteLine(code);
                }
            }
            Timer.EndStage("Roslyn compilation");
            return options.Strict ? 1 : 2;
        }
        _compileResult = compileResult;
        Timer.EndStage("Roslyn compilation");

        // Step 4: Execute
        Timer.StartStage("Test execution");
        var assembly = compileResult.Assembly;
        Runtime.MockCodeunitHandle.CurrentAssembly = assembly;

        // Load dependency DLLs into the same ALC for runtime type resolution
        var depAssemblies = new List<Assembly>();
        foreach (var dllPath in depDllAssemblyPaths)
        {
            try
            {
                var depAsm = compileResult.LoadContext.LoadFromAssemblyPath(dllPath);
                depAssemblies.Add(depAsm);
                Log.Info($"Loaded dep assembly: {depAsm.GetName().Name}");
            }
            catch (Exception ex) { Log.Info($"Failed to load dep assembly {dllPath}: {ex.Message}"); }
        }
        Runtime.MockCodeunitHandle.DependencyAssemblies = depAssemblies.Count > 0 ? depAssemblies : null;

        Executor.RegisterStatements(GetRewrittenStrings());

        bool hasTests = alSources.Any(s => s.Contains("Subtype = Test"));

        int exitCode;
        if (hasTests)
        {
            Runtime.MessageCapture.Reset();
            Runtime.MessageCapture.Enable();

            if (options.CaptureValues)
            {
                Runtime.ValueCapture.Reset();
                Runtime.ValueCapture.Enable();
            }

            if (options.IterationTracking)
            {
                Runtime.IterationTracker.Reset();
                Runtime.IterationTracker.Enable();
            }

            var runSw = System.Diagnostics.Stopwatch.StartNew();
            var results = Executor.RunTests(assembly, captureValues: options.CaptureValues, runProcedure: options.RunProcedure, initEvents: options.InitEvents, testIsolation: options.TestIsolation);
            runSw.Stop();
            testResults.AddRange(results);
            if (results.Count == 0 && options.RunProcedure != null)
                stderr.WriteLine($"Error: Procedure '{options.RunProcedure}' not found in the generated code.");
            if (!options.OutputJson)
                Executor.PrintResults(results, runSw.ElapsedMilliseconds, options.Verbose, options.Strict);
            exitCode = Executor.ExitCode(results, options.Strict);

            if (options.CaptureValues)
                Runtime.ValueCapture.Disable();
            if (options.IterationTracking)
                Runtime.IterationTracker.Disable();
            Runtime.MessageCapture.Disable();

            Dictionary<string, string>? scopeToObject = null;
            if (options.IterationTracking || options.ShowCoverage)
            {
                scopeToObject = CoverageReport.BuildScopeToObjectMap(generatedCSharpList!);
            }

            _scopeToObject = scopeToObject;

            if (options.ShowCoverage)
            {
                Executor.PrintCoverageReport();

                var sourceSpans = CoverageReport.ParseSourceSpans(generatedCSharpList!);
                var (hitStmts, totalStmts) = Runtime.AlScope.GetCoverageSets();

                CoverageReport.WriteCobertura("cobertura.xml", sourceSpans, hitStmts, totalStmts, scopeToObject!);
                Log.Info("Coverage report: cobertura.xml");
            }
        }
        else
        {
            Runtime.MessageCapture.Reset();
            Runtime.MessageCapture.Enable();
            if (options.CaptureValues)
            {
                Runtime.ValueCapture.Reset();
                Runtime.ValueCapture.Enable();
            }
            if (options.IterationTracking)
            {
                Runtime.IterationTracker.Reset();
                Runtime.IterationTracker.Enable();
            }
            exitCode = Executor.RunOnRun(assembly, captureValues: options.CaptureValues);
            if (options.CaptureValues)
                Runtime.ValueCapture.Disable();
            if (options.IterationTracking)
                Runtime.IterationTracker.Disable();
            Runtime.MessageCapture.Disable();

            if (options.IterationTracking || options.ShowCoverage)
            {
                _scopeToObject = CoverageReport.BuildScopeToObjectMap(generatedCSharpList!);
            }
        }
        Timer.EndStage("Test execution");
        Timer.Print();

        // If rewriting failed for some objects, ensure the exit code reflects the
        // limitation.  We distinguish two cases:
        //   • No test/run results (testResults.Count == 0): the "exit 1" from the
        //     executor just means "nothing ran", which is itself a consequence of the
        //     rewriter failure — so the rewriterExitCode (2 or 1 strict) governs.
        //   • Real test results exist and they failed (exitCode == 1): a genuine test
        //     assertion failure is more specific, so exit code 1 is kept as-is.
        if (rewriterExitCode != 0)
        {
            if (exitCode == 0 || testResults.Count == 0)
                exitCode = rewriterExitCode;
            // If exitCode is already 1 and testResults.Count > 0, keep exit code 1
            // (real test failures take precedence over the soft limitation flag).
        }

        return exitCode;
    }

    private static void AutoDiscoverDependencies(
        List<string> packagePaths,
        List<(string Path, List<string> Sources)> inputGroups,
        List<string> inputPaths,
        List<string> alSources)
    {
        var appIndex = new Dictionary<Guid, string>();
        foreach (var pkgDir in packagePaths)
        {
            foreach (var appFile in Directory.GetFiles(pkgDir, "*.app", SearchOption.AllDirectories))
            {
                try
                {
                    var doc = AlTranspiler.LoadNavxManifest(appFile);
                    if (doc == null) continue;
                    XNamespace ns = "http://schemas.microsoft.com/navx/2015/manifest";
                    var appElement = doc.Root?.Element(ns + "App");
                    var idStr = appElement?.Attribute("Id")?.Value;
                    if (idStr != null && Guid.TryParse(idStr, out var appGuid))
                    {
                        if (!appIndex.ContainsKey(appGuid))
                            appIndex[appGuid] = appFile;
                    }
                }
                catch { }
            }
        }

        var inputAppGuids = new HashSet<Guid>();
        foreach (var group in inputGroups)
        {
            if (!group.Path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var doc = AlTranspiler.LoadNavxManifest(group.Path);
                if (doc == null) continue;
                XNamespace ns = "http://schemas.microsoft.com/navx/2015/manifest";
                var idStr = doc.Root?.Element(ns + "App")?.Attribute("Id")?.Value;
                if (idStr != null && Guid.TryParse(idStr, out var appGuid))
                    inputAppGuids.Add(appGuid);
            }
            catch { }
        }

        var toProcess = new Queue<Guid>();
        var discovered = new HashSet<Guid>(inputAppGuids);

        foreach (var group in inputGroups)
        {
            if (!group.Path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var depGuid in AlTranspiler.GetDependencyGuids(group.Path))
            {
                if (discovered.Add(depGuid))
                    toProcess.Enqueue(depGuid);
            }
        }

        var autoDiscovered = new List<(Guid Id, string Path)>();
        while (toProcess.Count > 0)
        {
            var depGuid = toProcess.Dequeue();
            if (!appIndex.TryGetValue(depGuid, out var depAppPath)) continue;
            autoDiscovered.Add((depGuid, depAppPath));

            foreach (var transDepGuid in AlTranspiler.GetDependencyGuids(depAppPath))
            {
                if (discovered.Add(transDepGuid))
                    toProcess.Enqueue(transDepGuid);
            }
        }

        if (autoDiscovered.Count > 0)
        {
            Log.Info($"\nAuto-discovered {autoDiscovered.Count} dependency app(s) for transpilation:");
            foreach (var (id, depPath) in autoDiscovered)
            {
                var fileName = Path.GetFileName(depPath);
                Log.Info($"  {fileName}");

                var extracted = AppPackageReader.ExtractAlSources(depPath);
                if (extracted.Count == 0) continue;

                var groupSources = new List<string>();
                foreach (var (name, source) in extracted)
                {
                    var prepared = PrepareSourceForStandalone(source);
                    alSources.Add(prepared);
                    groupSources.Add(prepared);
                }
                var fullPath = Path.GetFullPath(depPath);
                inputPaths.Add(fullPath);
                inputGroups.Add((fullPath, groupSources));
            }
        }
    }

    private static void LoadAssertStubs(
        List<string> packagePaths,
        List<string> inputPaths,
        List<(string Path, List<string> Sources)> inputGroups,
        List<string> alSources,
        List<string> assertStubSources)
    {
        if (!NeedsBuiltInTestStubs(alSources))
        {
            Log.Info("Skipping built-in test stubs (no test-library usage detected)");
            return;
        }

        bool packagesHaveAssert = packagePaths.Any(p =>
            Directory.Exists(p) &&
            Directory.GetFiles(p, "*.app", SearchOption.AllDirectories)
                .Any(f => Path.GetFileName(f).Contains("Assert", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetFileName(f).Contains("TestLibraries", StringComparison.OrdinalIgnoreCase)));

        if (!packagesHaveAssert)
        {
            foreach (var ip in inputPaths)
            {
                var dir = Directory.Exists(ip) ? ip : Path.GetDirectoryName(ip);
                while (dir != null)
                {
                    var alPkgs = Path.Combine(dir, ".alpackages");
                    if (Directory.Exists(alPkgs) &&
                        Directory.GetFiles(alPkgs, "*.app", SearchOption.AllDirectories)
                            .Any(f => Path.GetFileName(f).Contains("Assert", StringComparison.OrdinalIgnoreCase)))
                    {
                        packagesHaveAssert = true;
                        break;
                    }
                    var parent = Path.GetDirectoryName(dir);
                    if (parent == dir) break;
                    dir = parent;
                }
                if (packagesHaveAssert) break;
            }
        }

        if (!packagesHaveAssert)
        {
            var stubsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "stubs");
            if (!Directory.Exists(stubsDir))
                stubsDir = Path.Combine(AppContext.BaseDirectory, "stubs");
            if (Directory.Exists(stubsDir))
            {
                Log.Info("Loading Assert stubs (no Assert.app found in packages)");
                foreach (var stubFile in Directory.GetFiles(stubsDir, "*.al", SearchOption.TopDirectoryOnly).OrderBy(f => f))
                {
                    var src = File.ReadAllText(stubFile);
                    alSources.Add(src);
                    foreach (var group in inputGroups)
                        group.Sources.Add(src);
                }
            }
        }
        else
        {
            var stubsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "stubs");
            if (!Directory.Exists(stubsDir))
                stubsDir = Path.Combine(AppContext.BaseDirectory, "stubs");
            if (Directory.Exists(stubsDir))
            {
                foreach (var stubFile in Directory.GetFiles(stubsDir, "*.al", SearchOption.TopDirectoryOnly).OrderBy(f => f))
                    assertStubSources.Add(File.ReadAllText(stubFile));
            }
            Log.Info("Skipping Assert stubs for AL compilation (real Assert.app found in packages)");
        }
    }

    private static bool NeedsBuiltInTestStubs(List<string> alSources)
    {
        foreach (var src in alSources)
        {
            if (src.Contains("Subtype = Test", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("Subtype = TestRunner", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("\"Library - Variable Storage\"", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("\"Library Assert\"", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("Codeunit Assert", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string PrepareSourceForStandalone(string source)
    {
        // Strip rendering blocks and DefaultRenderingLayout from any source that
        // contains a report or reportextension declaration — not just when the
        // declaration is at the very start. This handles BOM, header comments,
        // multi-object files, and other common AL file layouts.
        if (Regex.IsMatch(source, @"\breport(extension)?\s+\d+", RegexOptions.IgnoreCase))
        {
            source = StripNamedBlock(source, "rendering");
            // Remove DefaultRenderingLayout property — it references layout names
            // defined inside the rendering block which we just stripped.
            source = Regex.Replace(source,
                @"(?im)^\s*DefaultRenderingLayout\s*=\s*[^;]+;\s*$",
                "");
        }

        // Strip addfirst(GroupName; Fields...) inside tableextension fieldgroups.
        // The runner's BC compiler version does not recognise addfirst in fieldgroups
        // (AL0104). Fieldgroup modifications have no runtime effect in the runner, so
        // removing them is safe. The distinguishing syntax is a semicolon inside the
        // parens: addfirst(GroupName; Field1, Field2) vs page-style addfirst(group).
        if (Regex.IsMatch(source, @"\btableextension\s+\d+", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(source, @"\baddfirst\s*\(", RegexOptions.IgnoreCase))
        {
            source = Regex.Replace(source,
                @"(?im)^\s*addfirst\s*\([^;)]+;[^)]*\)\s*$",
                "");
        }

        // Strip fileupload(Name) { ... } action declarations inside page / pageextension
        // action areas. fileupload() was introduced after BC 16 and is not recognised by
        // the runner's AL compiler version. The action is UI-only (no runtime logic), so
        // removing it entirely is safe.
        if (Regex.IsMatch(source, @"\bpage(extension)?\s+\d+", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(source, @"\bfileupload\s*\(", RegexOptions.IgnoreCase))
        {
            source = StripPatternedBlock(source, @"\bfileupload\s*\([^)]*\)");
        }

        return source;
    }

    /// <summary>
    /// Strips all blocks whose opening header matches <paramref name="headerPattern"/>.
    /// The block extends from the header start to the matching closing brace (inclusive).
    /// The entire block — header and body — is replaced with an empty string.
    /// </summary>
    private static string StripPatternedBlock(string source, string headerPattern)
    {
        int searchIndex = 0;
        while (searchIndex < source.Length)
        {
            var match = Regex.Match(
                source.Substring(searchIndex),
                headerPattern + @"\s*\{",
                RegexOptions.IgnoreCase);
            if (!match.Success)
                break;

            int blockStart = searchIndex + match.Index;
            int openBrace = source.IndexOf('{', blockStart + match.Length - 1);
            if (openBrace < 0)
                break;

            // Brace-depth counting to find the matching closing brace.
            int depth = 0;
            int i = openBrace;
            for (; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        break;
                }
            }

            if (i >= source.Length)
                break;

            source = source.Substring(0, blockStart) + source.Substring(i + 1);
            // Don't advance searchIndex so we catch multiple consecutive blocks.
        }

        return source;
    }

    private static string StripNamedBlock(string source, string blockName)
    {
        int searchIndex = 0;
        while (searchIndex < source.Length)
        {
            var match = Regex.Match(
                source.Substring(searchIndex),
                $@"(?im)^\s*{Regex.Escape(blockName)}\s*\{{");
            if (!match.Success)
                break;

            int blockStart = searchIndex + match.Index;
            int openBrace = source.IndexOf('{', blockStart);
            if (openBrace < 0)
                break;

            // Simple brace-depth counting. Does not skip braces inside
            // single-quoted AL string literals or comments, so a caption like
            //   Caption = 'Some {brace} text'
            // would confuse the counter. This is acceptable for `rendering`
            // blocks which don't contain string literals with braces.
            int depth = 0;
            int i = openBrace;
            for (; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        break;
                }
            }

            if (i >= source.Length)
                break;

            var replacement = $"{blockName}{Environment.NewLine}{{{Environment.NewLine}}}";
            source = source.Substring(0, blockStart) + replacement + source.Substring(i + 1);
            searchIndex = blockStart + replacement.Length;
        }

        return source;
    }

    private static List<(string Name, string Code)>? Transpile(
        PipelineOptions options,
        List<string> alSources,
        List<(string Path, List<string> Sources)> inputGroups,
        List<string> inputPaths,
        List<string> stubSources,
        TextWriter stderr,
        SyntaxTreeCache? syntaxTreeCache = null,
        List<string?>? sourceFilePaths = null)
    {
        // Handle stub replacement before transpilation
        var separateStubSources = new List<string>();
        if (stubSources.Count > 0)
        {
            var stubObjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var stub in stubSources)
            {
                var match = Regex.Match(stub,
                    @"(?:codeunit|table|page|report|xmlport|query|enum|interface)\s+(\d+)",
                    RegexOptions.IgnoreCase);
                if (match.Success)
                    stubObjectIds.Add(match.Value.ToLowerInvariant());
            }

            foreach (var stub in stubSources)
            {
                var match = Regex.Match(stub,
                    @"(?:codeunit|table|page|report|xmlport|query|enum|interface)\s+(\d+)",
                    RegexOptions.IgnoreCase);
                if (!match.Success) { separateStubSources.Add(stub); continue; }

                var stubId = match.Value.ToLowerInvariant();
                bool foundInSource = alSources.Any(src =>
                {
                    var srcMatch = Regex.Match(src,
                        @"(?:codeunit|table|page|report|xmlport|query|enum|interface)\s+(\d+)",
                        RegexOptions.IgnoreCase);
                    return srcMatch.Success && srcMatch.Value.ToLowerInvariant() == stubId;
                });

                if (foundInSource)
                {
                    // Remove matching sources and their parallel sourceFilePaths entries
                    for (int ri = alSources.Count - 1; ri >= 0; ri--)
                    {
                        var srcMatch = Regex.Match(alSources[ri],
                            @"(?:codeunit|table|page|report|xmlport|query|enum|interface)\s+(\d+)",
                            RegexOptions.IgnoreCase);
                        if (srcMatch.Success && srcMatch.Value.ToLowerInvariant() == stubId)
                        {
                            alSources.RemoveAt(ri);
                            if (sourceFilePaths != null && ri < sourceFilePaths.Count)
                                sourceFilePaths.RemoveAt(ri);
                        }
                    }
                    alSources.Add(stub);
                    sourceFilePaths?.Add(null); // stubs have no disk file path
                    Log.Info($"Stub replaces source object: {stubId}");
                }
                else
                {
                    separateStubSources.Add(stub);
                    Log.Info($"Stub for dependency object: {stubId} (compiled separately)");
                }
            }
        }

        bool hasExplicitPackages = options.PackagePaths.Count > 0;
        bool hasAppInputs = inputGroups.Any(g => g.Path.EndsWith(".app", StringComparison.OrdinalIgnoreCase));

        List<(string Name, string Code)>? generatedCSharpList;

        if (hasExplicitPackages && inputGroups.Count > 1 && hasAppInputs)
        {
            generatedCSharpList = new List<(string Name, string Code)>();
            for (int gi = 0; gi < inputGroups.Count; gi++)
            {
                var group = inputGroups[gi];
                Log.Info($"\n--- Transpiling group {gi + 1}/{inputGroups.Count}: {Path.GetFileName(group.Path)} ---");

                var groupPackagePaths = new List<string>(options.PackagePaths);
                for (int oi = 0; oi < inputGroups.Count; oi++)
                {
                    if (oi == gi) continue;
                    var otherPath = inputGroups[oi].Path;
                    if (otherPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                    {
                        var parentDir = Path.GetDirectoryName(otherPath)!;
                        if (!groupPackagePaths.Contains(parentDir))
                            groupPackagePaths.Add(parentDir);
                    }
                    else
                    {
                        if (!groupPackagePaths.Contains(otherPath))
                            groupPackagePaths.Add(otherPath);
                    }
                }

                var groupInputPaths = new List<string> { group.Path };
                var groupResult = AlTranspiler.TranspileMulti(group.Sources, groupPackagePaths, groupInputPaths);
                if (groupResult != null && groupResult.Count > 0)
                {
                    Log.Info($"  Transpiled {groupResult.Count} AL objects");
                    generatedCSharpList.AddRange(groupResult);
                }
                else
                {
                    stderr.WriteLine($"  Warning: no C# generated for {Path.GetFileName(group.Path)}");
                }
            }

            if (generatedCSharpList.Count == 0)
            {
                stderr.WriteLine("Error: no C# code generated from any input group");
                return null;
            }
        }
        else
        {
            generatedCSharpList = AlTranspiler.TranspileMulti(alSources, options.PackagePaths, inputPaths,
                treeCache: syntaxTreeCache, sourceFilePaths: sourceFilePaths);
            if (generatedCSharpList == null || generatedCSharpList.Count == 0)
                return null;
        }

        return generatedCSharpList;
    }

    /// <summary>
    /// Scans the generated C# for test-toolkit codeunit IDs (130000–139999) referenced
    /// via <c>NavCodeunit.RunCodeunit</c> or <c>new NavCodeunitHandle(..., id)</c> patterns,
    /// then returns a minimal rewritten C# syntax tree for each ID that does NOT already
    /// have a compiled class in <paramref name="generatedCSharpList"/>. The generated stub
    /// classes are empty (no methods) so that <see cref="AlRunner.Runtime.MockCodeunitHandle.FindCodeunitType"/>
    /// finds them at runtime and treats any call as a silent no-op instead of throwing.
    /// Codeunits with runner-native mock implementations (130/131/130000, 131004, 131100) are
    /// excluded — they are never looked up by <c>FindCodeunitType</c>.
    /// </summary>
    internal static List<(string Name, Microsoft.CodeAnalysis.SyntaxTree Tree)> GenerateTestToolkitStubs(
        List<(string Name, string Code)> generatedCSharpList,
        List<string>? packagePaths = null, List<string>? inputPaths = null)
    {
        // IDs already compiled from source — no stub needed.
        var alreadyPresent = new HashSet<int>();
        var classPattern = new Regex(@"class\s+Codeunit(\d+)", RegexOptions.Compiled);
        foreach (var (_, code) in generatedCSharpList)
        {
            foreach (Match m in classPattern.Matches(code))
            {
                if (int.TryParse(m.Groups[1].Value, out int id))
                    alreadyPresent.Add(id);
            }
        }

        // Codeunits that have dedicated runner-native mock implementations
        var nativeMockIds = new HashSet<int> { 130, 131, 130000, 130002, 130440, 130500, 131004, 131100, 132250 };

        // Scan all generated C# for test-toolkit ID literals (130000-139999)
        var idPattern = new Regex(@"(?:,\s*|[\(\s])1(3[0-9]{4})\b", RegexOptions.Compiled);
        var referencedIds = new HashSet<int>();
        foreach (var (_, code) in generatedCSharpList)
        {
            foreach (Match m in idPattern.Matches(code))
            {
                if (int.TryParse("1" + m.Groups[1].Value, out int id)
                    && id is >= 130000 and <= 139999
                    && !nativeMockIds.Contains(id))
                {
                    referencedIds.Add(id);
                }
            }
        }

        // Build a map of codeunit ID → method signatures from .app packages
        var symbolMap = new Dictionary<int, StubGenerator.CodeunitSymbol>();
        var allPackageDirs = new List<string>(packagePaths ?? new());
        // Also scan .alpackages near input paths
        if (inputPaths != null)
        {
            foreach (var p in inputPaths)
            {
                var dir = Directory.Exists(p) ? p : Path.GetDirectoryName(p);
                if (dir == null) continue;
                var alPkg = Path.Combine(dir, ".alpackages");
                if (Directory.Exists(alPkg) && !allPackageDirs.Contains(alPkg))
                    allPackageDirs.Add(alPkg);
            }
        }
        foreach (var pkgDir in allPackageDirs)
        {
            if (!Directory.Exists(pkgDir)) continue;
            foreach (var appFile in Directory.GetFiles(pkgDir, "*.app"))
            {
                try
                {
                    var (codeunits, _, _) = StubGenerator.ReadCodeunitsFromApp(appFile);
                    foreach (var cu in codeunits)
                        symbolMap.TryAdd(cu.Id, cu);
                }
                catch { /* skip unreadable packages */ }
            }
        }

        var missingIds = referencedIds.Where(id => !alreadyPresent.Contains(id)).OrderBy(id => id).ToList();
        var stubs = new List<(string Name, Microsoft.CodeAnalysis.SyntaxTree Tree)>();

        // --- Phase 1: Compile rich stubs as AL through the BC compiler ---
        // This produces proper scope classes with deterministic member IDs that match
        // the caller's generated code, fixing dispatch for codeunits with many methods
        // that share the same parameter count (issue #1150).
        var compiledIds = new HashSet<int>();
        var alStubSources = new List<string>();
        var alStubIdOrder = new List<int>();

        foreach (var id in missingIds)
        {
            if (symbolMap.TryGetValue(id, out var cuSymbol) && cuSymbol.Methods.Count > 0)
            {
                alStubSources.Add(StubGenerator.RenderCodeunit(cuSymbol, "auto-stub"));
                alStubIdOrder.Add(id);
            }
        }

        if (alStubSources.Count > 0)
        {
            // Suppress stderr from the auto-stub compilation — errors are expected
            // when methods reference types not available in the packages.
            var savedErr = Console.Error;
            Console.SetError(System.IO.TextWriter.Null);
            try
            {
                var csharpList = AlTranspiler.TranspileMulti(
                    alStubSources,
                    packagePaths != null && packagePaths.Count > 0 ? packagePaths : null,
                    inputPaths);
                if (csharpList != null)
                {
                    foreach (var (name, code) in csharpList)
                    {
                        try
                        {
                            var tree = RoslynRewriter.RewriteToTree(code);
                            if (tree != null)
                            {
                                stubs.Add((name, tree));
                                // Track which IDs were successfully compiled
                                foreach (Match m in classPattern.Matches(code))
                                {
                                    if (int.TryParse(m.Groups[1].Value, out int compiledId))
                                        compiledIds.Add(compiledId);
                                }
                            }
                        }
                        catch
                        {
                            // Rewriter failed — fall through to minimal C# stub below
                        }
                    }
                }
            }
            finally
            {
                Console.SetError(savedErr);
            }
        }

        // --- Phase 2: Minimal C# stubs for anything not compiled from AL ---
        foreach (var id in missingIds)
        {
            if (compiledIds.Contains(id)) continue;
            var stubCode = $"namespace Microsoft.Dynamics.Nav.BusinessApplication {{ public class Codeunit{id} {{ }} }}";
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                stubCode, path: $"TestToolkitStub{id}.cs");
            stubs.Add(($"TestToolkitStub{id}", tree));
        }

        return stubs;
    }

    private static List<string> GetSeparateStubSources(List<string> stubSources, List<string> alSources)
    {
        var result = new List<string>();
        foreach (var stub in stubSources)
        {
            var match = Regex.Match(stub,
                @"(?:codeunit|table|page|report|xmlport|query|enum|interface)\s+(\d+)",
                RegexOptions.IgnoreCase);
            if (!match.Success) { result.Add(stub); continue; }

            var stubId = match.Value.ToLowerInvariant();
            bool foundInSource = alSources.Any(src =>
            {
                var srcMatch = Regex.Match(src,
                    @"(?:codeunit|table|page|report|xmlport|query|enum|interface)\s+(\d+)",
                    RegexOptions.IgnoreCase);
                return srcMatch.Success && srcMatch.Value.ToLowerInvariant() == stubId;
            });

            if (!foundInSource)
                result.Add(stub);
        }
        return result;
    }
}
