using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Symbols;

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
    /// <summary>
    /// When set, run the OnRun trigger of the named codeunit explicitly instead of
    /// executing test codeunits. Codeunits with TableNo set are skipped (they require
    /// a record to be passed in and cannot run standalone).
    /// </summary>
    public string? RunCodeunit { get; set; }
    /// <summary>If set, write a JUnit XML test report to this path after test execution.</summary>
    public string? OutputJunitPath { get; set; }
    /// <summary>When true, promote exit code 2 (runner limitations) to exit code 1 (failure).</summary>
    public bool Strict { get; set; }
    /// <summary>Per-test timeout in seconds. Tests exceeding this are reported as errors
    /// and the runner moves to the next test. Default: 5 seconds. Set to 0 to disable.</summary>
    public int TestTimeoutSeconds { get; set; } = 5;

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
    /// Codeunit IDs that were auto-stubbed (return defaults, no real implementation).
    /// Used by the test output to annotate failures involving stub calls.
    /// Maps codeunit ID → name.
    /// </summary>
    public static Dictionary<int, string> AutoStubbedCodeunits { get; } = new();

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
        Runtime.QueryFieldRegistry.Clear();
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
                Runtime.QueryFieldRegistry.ParseAndRegister(text);
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

        foreach (var src in alSources) Runtime.QueryFieldRegistry.ParseAndRegister(src);
        if (options.InlineCode != null) Runtime.QueryFieldRegistry.ParseAndRegister(options.InlineCode);

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

        // Pre-compile check: detect duplicate extension object names within the same
        // app.json scope. These are genuine AL0197 errors that the runner must enforce
        // because when all source files are compiled together in a single BC compilation
        // pass, the BC compiler assigns ONE extension identity to all objects and cannot
        // distinguish same-extension from cross-extension duplicates. We detect them here
        // using the actual app.json file structure instead.
        var sameExtDups = DetectSameExtensionDuplicates(sourceFilePaths, alSources);
        if (sameExtDups.Count > 0)
        {
            stderr.WriteLine($"AL compile error (AL0197): duplicate extension object name within the same extension ({sameExtDups.Count} error(s)):");
            foreach (var msg in sameExtDups)
                stderr.WriteLine($"  {msg}");
            return 3;
        }

        // Step 1: Transpile
        Timer.StartStage("AL transpilation");
        var generatedCSharpList = Transpile(options, alSources, inputGroups, inputPaths, stubSources, stderr,
            syntaxTreeCache: SyntaxTreeCache, sourceFilePaths: sourceFilePaths);
        if (generatedCSharpList == null)
        {
            Timer.EndStage("AL transpilation");
            return 3;
        }

        // Save the main compilation for symbol table queries (auto-stub generation).
        // Subsequent TranspileMulti calls (for assert stubs) overwrite LastCompilation.
        var mainCompilation = AlTranspiler.LastCompilation;
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

        // Auto-stub: scan the generated C# for ALL referenced object IDs that were
        // NOT compiled from source, generate minimal empty AL stubs for them, and
        // compile those stubs in a SECOND BC pass using the same packages. This
        // produces proper C# classes with correct scope classes, member IDs, and
        // default return values — covering codeunits, tables, pages, and all other
        // object types from system, base app, and test library packages.
        var autoStubTrees = GenerateAndCompileAutoStubs(
            generatedCSharpList, options.PackagePaths, inputPaths, stderr, mainCompilation);
        if (autoStubTrees.Count > 0)
            rewrittenTreeList.AddRange(autoStubTrees);

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
        bool explicitRunCodeunit = options.RunCodeunit != null;
        bool isInlineMode = options.InlineCode != null;

        int exitCode;
        if (hasTests || explicitRunCodeunit || isInlineMode)
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
            if (explicitRunCodeunit || isInlineMode)
            {
                if (explicitRunCodeunit)
                    exitCode = Executor.RunOnRun(assembly, options.RunCodeunit!, captureValues: options.CaptureValues);
                else
                    exitCode = Executor.RunOnRun(assembly, captureValues: options.CaptureValues);
                runSw.Stop();
                if (options.CaptureValues) Runtime.ValueCapture.Disable();
                if (options.IterationTracking) Runtime.IterationTracker.Disable();
                Runtime.MessageCapture.Disable();
                if (options.IterationTracking || options.ShowCoverage)
                    _scopeToObject = CoverageReport.BuildScopeToObjectMap(generatedCSharpList!);
            }
            else
            {
                var results = Executor.RunTests(assembly, captureValues: options.CaptureValues, runProcedure: options.RunProcedure, initEvents: options.InitEvents, testIsolation: options.TestIsolation, testTimeoutSeconds: options.TestTimeoutSeconds);
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
                    scopeToObject = CoverageReport.BuildScopeToObjectMap(generatedCSharpList!);

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
        }
        else
        {
            stderr.WriteLine("No test codeunits found (Subtype = Test). Source compiled successfully.");
            stderr.WriteLine("To run a specific codeunit\u0027s OnRun trigger, use: --run-codeunit <CodunitName>");
            exitCode = 1;
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

    // Regex to detect extension object declarations: captures type and name.
    // Matches: pageextension, tableextension, reportextension, enumextension,
    //          permissionsetextension, pageCustomization (all extension-type objects).
    // Pattern for numbered extension objects: type ID "Name" extends Target
    // Covers: pageextension, tableextension, reportextension, enumextension,
    //         permissionsetextension, pagecustomization.
    private static readonly Regex ExtensionObjectDeclPattern = new(
        @"^\s*(?<type>pageextension|tableextension|reportextension|enumextension|permissionsetextension|pagecustomization)\s+\d+\s+""(?<name>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    // Pattern for profileextension (no numeric ID; unquoted identifier name):
    //   profileextension MyProfileExt extends SomeProfile { ... }
    private static readonly Regex ProfileExtensionDeclPattern = new(
        @"^\s*(?<type>profileextension)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s+extends\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    // Canonical type names matching the BC compiler's AL0197 message format.
    private static readonly Dictionary<string, string> ExtensionTypeDisplayNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "pageextension",          "PageExtension" },
            { "tableextension",         "TableExtension" },
            { "reportextension",        "ReportExtension" },
            { "enumextension",          "EnumExtension" },
            { "permissionsetextension", "PermissionSetExtension" },
            { "pagecustomization",      "PageCustomization" },
            { "profileextension",       "ProfileExtension" },
        };

    /// <summary>
    /// Walks up from <paramref name="filePath"/> to find the nearest app.json.
    /// Returns the app.json path, or null if none found.
    /// </summary>
    private static string? FindOwningAppJson(string filePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "app.json");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// Scans source files for same-extension duplicate extension object names.
    /// Groups files by owning app.json; within each group, if two files declare an
    /// extension object (pageextension, tableextension, etc.) with the same name,
    /// that is a genuine AL0197 error — not a cross-extension collision.
    /// Returns a list of error messages, or an empty list if no duplicates detected.
    /// </summary>
    private static List<string> DetectSameExtensionDuplicates(
        List<string?> sourceFilePaths,
        List<string> alSources)
    {
        var errors = new List<string>();

        // Group file paths by their owning app.json.
        // Files with no owning app.json are grouped under a null key (skip them).
        var appJsonToFiles = new Dictionary<string, List<(string FilePath, string Source)>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sourceFilePaths.Count && i < alSources.Count; i++)
        {
            var fp = sourceFilePaths[i];
            if (fp == null) continue;
            var appJson = FindOwningAppJson(fp);
            if (appJson == null) continue;
            if (!appJsonToFiles.TryGetValue(appJson, out var list))
            {
                list = new List<(string, string)>();
                appJsonToFiles[appJson] = list;
            }
            list.Add((fp, alSources[i]));
        }

        // For each app.json group, detect duplicate extension object names.
        foreach (var (appJson, files) in appJsonToFiles)
        {
            // Map of "type:name" (case-insensitive) → first declaring file path
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (filePath, source) in files)
            {
                // Match numbered extension objects (pageextension, tableextension, etc.)
                foreach (Match m in ExtensionObjectDeclPattern.Matches(source))
                {
                    var rawType  = m.Groups["type"].Value;
                    var displayType = ExtensionTypeDisplayNames.TryGetValue(rawType, out var dt) ? dt : rawType;
                    var objectName = m.Groups["name"].Value;
                    var key = $"{rawType}:{objectName}";
                    if (seen.TryGetValue(key, out var firstFile))
                    {
                        errors.Add(
                            $"error AL0197: An application object of type '{displayType}' " +
                            $"with name '{objectName}' is already declared in " +
                            $"'{Path.GetFileName(firstFile)}' " +
                            $"(same extension: {Path.GetFileName(appJson)})");
                    }
                    else
                    {
                        seen[key] = filePath;
                    }
                }

                // Match profileextension objects (no numeric ID; unquoted identifier name)
                foreach (Match m in ProfileExtensionDeclPattern.Matches(source))
                {
                    var rawType  = m.Groups["type"].Value;
                    var displayType = ExtensionTypeDisplayNames.TryGetValue(rawType, out var dt) ? dt : rawType;
                    var objectName = m.Groups["name"].Value;
                    var key = $"{rawType}:{objectName}";
                    if (seen.TryGetValue(key, out var firstFile))
                    {
                        errors.Add(
                            $"error AL0197: An application object of type '{displayType}' " +
                            $"with name '{objectName}' is already declared in " +
                            $"'{Path.GetFileName(firstFile)}' " +
                            $"(same extension: {Path.GetFileName(appJson)})");
                    }
                    else
                    {
                        seen[key] = filePath;
                    }
                }
            }
        }

        return errors;
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
        // Handle stub replacement before transpilation.
        // All stubs are included in the main BC compilation pass:
        // - Source-replacing stubs: remove the matching source object and add the stub.
        // - Dependency stubs: add the stub directly; the AL0275 reactive conflict
        //   resolver in TranspileMulti drops the conflicting package so the stub wins.
        if (stubSources.Count > 0)
        {
            foreach (var stub in stubSources)
            {
                var match = Regex.Match(stub,
                    @"(?:codeunit|table|page|report|xmlport|query|enum|interface)\s+(\d+)",
                    RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    // No object ID — add as-is; the BC compiler will report any errors.
                    alSources.Add(stub);
                    sourceFilePaths?.Add(null);
                    continue;
                }

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
                    // Dependency stub: include in the main compilation pass.
                    // The AL0275 reactive conflict resolver drops any conflicting package.
                    alSources.Add(stub);
                    sourceFilePaths?.Add(null);
                    Log.Info($"Stub for dependency object: {stubId} (included in main pass)");
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
    /// Scans generated C# for ALL referenced object IDs (codeunits, tables, pages, etc.)
    /// that don't have a compiled class. Generates minimal empty AL stubs for each missing
    /// object and compiles them in a second BC pass using the same packages. The BC compiler
    /// produces proper C# with correct scope classes, member IDs, and default return values.
    /// </summary>
    private static List<(string Name, Microsoft.CodeAnalysis.SyntaxTree Tree)> GenerateAndCompileAutoStubs(
        List<(string Name, string Code)> generatedCSharpList,
        List<string> packagePaths, List<string> inputPaths,
        TextWriter stderr, Compilation? mainCompilation)
    {
        var result = new List<(string Name, Microsoft.CodeAnalysis.SyntaxTree Tree)>();

        // 1. Collect object IDs already compiled from user source
        var alreadyPresent = new HashSet<(string Type, int Id)>();
        var classPattern = new Regex(@"class\s+(Codeunit|Record|Page|Report|XmlPort|Query)(\d+)\b", RegexOptions.Compiled);
        foreach (var (_, code) in generatedCSharpList)
        {
            foreach (Match m in classPattern.Matches(code))
            {
                if (int.TryParse(m.Groups[2].Value, out int id))
                    alreadyPresent.Add((m.Groups[1].Value, id));
            }
        }

        // 2. Scan generated C# for ALL referenced object IDs
        //    Pattern: new NavCodeunitHandle(this, ID) — codeunits
        //    Pattern: new NavRecordHandle(this, ID, ...) — tables/records
        var cuPattern = new Regex(@"NavCodeunitHandle\(\s*\w+\s*,\s*(\d+)\s*\)", RegexOptions.Compiled);
        var recPattern = new Regex(@"NavRecordHandle\(\s*\w+\s*,\s*(\d+)\s*,", RegexOptions.Compiled);

        var missingCodeunits = new HashSet<int>();
        var missingTables = new HashSet<int>();

        // Codeunits with runner-native mock implementations — never stub
        var nativeMockIds = new HashSet<int> { 130, 131, 130000, 130002, 130440, 130500, 131004, 131100, 132250 };

        foreach (var (_, code) in generatedCSharpList)
        {
            foreach (Match m in cuPattern.Matches(code))
            {
                if (int.TryParse(m.Groups[1].Value, out int id)
                    && !nativeMockIds.Contains(id)
                    && !alreadyPresent.Contains(("Codeunit", id)))
                    missingCodeunits.Add(id);
            }
            foreach (Match m in recPattern.Matches(code))
            {
                if (int.TryParse(m.Groups[1].Value, out int id)
                    && !alreadyPresent.Contains(("Record", id)))
                    missingTables.Add(id);
            }
        }

        if (missingCodeunits.Count == 0 && missingTables.Count == 0)
            return result;


        // 3. Generate AL stubs with full method signatures from the BC compiler's
        //    symbol table. The main compilation already resolved all symbols — we
        //    query it to get method names, parameters, and return types.
        var alStubs = new List<string>();
        var compilation = mainCompilation;

        foreach (var id in missingCodeunits.OrderBy(x => x))
        {
            var stubAl = compilation != null
                ? RenderCodeunitStubFromSymbols(compilation, id)
                : null;
            alStubs.Add(stubAl ?? $"codeunit {id} \"AutoStub{id}\" {{ }}");
        }
        foreach (var id in missingTables.OrderBy(x => x))
        {
            var stubAl = compilation != null
                ? RenderTableStubFromSymbols(compilation, id)
                : null;
            alStubs.Add(stubAl ?? $"table {id} \"AutoStub{id}\" {{ fields {{ field(1; PK; Integer) {{ }} }} keys {{ key(PK; PK) {{ Clustered = true; }} }} }}");
        }

        // Register table stubs with field/init-value registries so runtime mocks
        // see auto-stubbed tables the same way as source-AL tables (e.g. so
        // AutoIncrement on a packaged table reaches the auto-increment path).
        foreach (var stubAl in alStubs)
        {
            if (!stubAl.TrimStart().StartsWith("table ", StringComparison.OrdinalIgnoreCase)) continue;
            Runtime.TableFieldRegistry.ParseAndRegister(stubAl);
            Runtime.TableInitValueRegistry.ParseAndRegister(stubAl);
        }

        // 4. Compile stubs in a second BC pass (same packages → proper scope classes)
        var savedErr = Console.Error;
        Console.SetError(TextWriter.Null);
        List<(string Name, string Code)>? stubCSharp;
        try
        {
            // Compile stubs WITHOUT package references to avoid AL0197 conflicts
            // (stubs define the same codeunit IDs as the packages). Stubs only use
            // built-in AL types (Integer, Text, Variant, etc.) so no packages needed.
            stubCSharp = AlTranspiler.TranspileMulti(alStubs);
        }
        finally { Console.SetError(savedErr); }

        // 5. Rewrite and collect compiled stubs
        int rewriteOk = 0, rewriteFail = 0;
        if (stubCSharp != null)
        {
            foreach (var (name, code) in stubCSharp)
            {
                try
                {
                    var tree = RoslynRewriter.RewriteToTree(code);
                    if (tree != null)
                    {
                        result.Add((name, tree));
                        rewriteOk++;
                    }
                }
                catch
                {
                    rewriteFail++;
                    // Generate minimal fallback class so other code can reference it
                    var classMatch = classPattern.Match(code);
                    if (classMatch.Success)
                    {
                        var className = $"{classMatch.Groups[1].Value}{classMatch.Groups[2].Value}";
                        var fallback = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                            $"namespace Microsoft.Dynamics.Nav.BusinessApplication {{ public class {className} {{ }} }}");
                        result.Add(($"AutoStub_{className}", fallback));
                    }
                }
            }
        }

        // 6. For any IDs that failed BC compilation (missing transitive deps),
        //    generate minimal C# classes as fallback
        var compiledIds = new HashSet<int>();
        var compiledClassPattern = new Regex(@"class\s+(?:Codeunit|Record)(\d+)", RegexOptions.Compiled);
        if (stubCSharp != null)
        {
            foreach (var (_, code) in stubCSharp)
                foreach (Match m in compiledClassPattern.Matches(code))
                    if (int.TryParse(m.Groups[1].Value, out int id))
                        compiledIds.Add(id);
        }
        foreach (var id in missingCodeunits.Where(id => !compiledIds.Contains(id)))
        {
            var fallback = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                $"namespace Microsoft.Dynamics.Nav.BusinessApplication {{ public class Codeunit{id} {{ }} }}",
                path: $"AutoStub_Codeunit{id}.cs");
            result.Add(($"AutoStub_Codeunit{id}", fallback));
        }
        foreach (var id in missingTables.Where(id => !compiledIds.Contains(id)))
        {
            var fallback = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                $"namespace Microsoft.Dynamics.Nav.BusinessApplication {{ public class Record{id} {{ }} }}",
                path: $"AutoStub_Record{id}.cs");
            result.Add(($"AutoStub_Record{id}", fallback));
        }

        // Track for test output annotation
        foreach (var id in missingCodeunits)
            AutoStubbedCodeunits.TryAdd(id, $"Codeunit {id}");
        foreach (var id in missingTables)
            AutoStubbedCodeunits.TryAdd(id, $"Table {id}");

        // 7. Report
        int total = missingCodeunits.Count + missingTables.Count;
        int compiled = stubCSharp?.Count ?? 0;
        stderr.WriteLine($"\nAuto-stubbed {total} dependency object(s) — methods return defaults:");
        if (missingCodeunits.Count > 0)
            stderr.WriteLine($"  Codeunits: {missingCodeunits.Count} ({compiled} compiled via BC, {missingCodeunits.Count - compiledIds.Intersect(missingCodeunits).Count()} fallback)");
        if (missingTables.Count > 0)
            stderr.WriteLine($"  Tables:    {missingTables.Count}");
        stderr.WriteLine($"Stubbed methods return default values. Provide real implementations via:");
        stderr.WriteLine($"  --stubs ./stubs     (AL stub files)");
        stderr.WriteLine($"  --dep-dlls .deps    (compiled dependency DLLs)");
        stderr.WriteLine();

        return result;
    }

    /// <summary>
    /// Query the BC compiler's symbol table for a codeunit by ID. If found, render
    /// an AL stub with all its method signatures. Uses reflection since the BC
    /// CodeAnalysis API types are internal and vary across versions.
    /// Returns null if the codeunit is not found.
    /// </summary>
    private static string? RenderCodeunitStubFromSymbols(Compilation compilation, int codeunitId)
    {
        try
        {
            // Look up the codeunit by ID using the BC compiler's symbol table.
            // GetApplicationObjectTypeSymbolsByIdAcrossModules is not in the public API
            // for all BC versions, so we call it via reflection.
            var lookupMethod = compilation.GetType().GetMethod(
                "GetApplicationObjectTypeSymbolsByIdAcrossModules",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(SymbolKind), typeof(int) }, null);
            if (lookupMethod == null)
                return null;

            var symbolsObj = lookupMethod.Invoke(compilation, new object[] { SymbolKind.Codeunit, codeunitId });
            if (symbolsObj == null) return null;

            // The result is ImmutableArray<IApplicationObjectTypeSymbol> — access via indexer
            var lengthProp = symbolsObj.GetType().GetProperty("Length");
            if (lengthProp == null || (int)lengthProp.GetValue(symbolsObj)! == 0) return null;

            var indexer = symbolsObj.GetType().GetProperty("Item");
            if (indexer == null) return null;
            var found = indexer.GetValue(symbolsObj, new object[] { 0 })!;
            var foundName = found.GetType().GetProperty("Name")?.GetValue(found)?.ToString() ?? $"AutoStub{codeunitId}";

            var sb = new System.Text.StringBuilder();
            var cuName = foundName.Replace("\"", "\"\"");
            sb.AppendLine($"codeunit {codeunitId} \"{cuName}\"");
            sb.AppendLine("{");

            var getMembersMethod = found.GetType().GetMethod("GetMembers", Type.EmptyTypes);
            if (getMembersMethod == null) { sb.AppendLine("}"); return sb.ToString(); }

            var members = getMembersMethod.Invoke(found, null) as System.Collections.IEnumerable;
            if (members == null) { sb.AppendLine("}"); return sb.ToString(); }

            var emittedSigs = new HashSet<string>();

            foreach (var member in members)
            {
                var memberKind = member.GetType().GetProperty("Kind")?.GetValue(member);
                if (memberKind?.ToString() != "Method") continue;

                var methodName = member.GetType().GetProperty("Name")?.GetValue(member)?.ToString();
                if (methodName == null) continue;

                // Get parameters
                var paramsProp = member.GetType().GetProperty("Parameters");
                var paramParts = new List<string>();
                if (paramsProp != null)
                {
                    var parms = paramsProp.GetValue(member) as System.Collections.IEnumerable;
                    if (parms != null)
                    {
                        foreach (var p in parms)
                        {
                            var pName = p.GetType().GetProperty("Name")?.GetValue(p)?.ToString() ?? "p";
                            // Escape AL keywords used as parameter names
                            if (IsAlKeyword(pName)) pName = $"\"{pName}\"";
                            else if (pName.Contains(" ") || pName.Contains(".")) pName = $"\"{pName}\"";
                            var pIsVar = p.GetType().GetProperty("IsVar")?.GetValue(p) is true;
                            var pType = p.GetType().GetProperty("ParameterType")?.GetValue(p)
                                     ?? p.GetType().GetProperty("Type")?.GetValue(p);
                            var typeName = pType != null ? RenderSymbolTypeViaReflection(pType) : "Variant";
                            // Strip "var" from primitive numeric/boolean params in auto-stubs.
                            // "var Integer" → ByRef<int> and "var Boolean" → ByRef<bool> in the
                            // generated C#.  When the stub has ByRef<int> but the caller passes
                            // ByRef<Decimal18> (because the real codeunit has a Decimal overload),
                            // Roslyn rejects the call with CS1503.  Auto-stubs have empty bodies
                            // so they never actually use by-reference semantics; stripping "var"
                            // makes the stub use value params and avoids the ByRef mismatch.
                            var effectiveIsVar = pIsVar && !IsCSharpPrimitiveMappedType(typeName);
                            var prefix = effectiveIsVar ? "var " : "";
                            paramParts.Add($"{prefix}{pName}: {typeName}");
                        }
                    }
                }

                var paramStr = string.Join("; ", paramParts);

                // Get return type
                var returnPart = "";
                var retType = member.GetType().GetProperty("ReturnValueType")?.GetValue(member)
                           ?? member.GetType().GetProperty("ReturnType")?.GetValue(member);
                if (retType != null)
                {
                    var navTypeKind = retType.GetType().GetProperty("NavTypeKind")?.GetValue(retType);
                    if (navTypeKind != null && navTypeKind.ToString() != "None" && navTypeKind.ToString() != "Void")
                    {
                        var retTypeName = RenderSymbolTypeViaReflection(retType);
                        returnPart = $": {retTypeName}";
                    }
                }

                // Dedup by (name, paramCount): overloaded methods with different
                // interface/complex types all collapse to Variant in the stub, producing
                // identical C# signatures and Roslyn CS0121 ambiguity errors.
                var sig = $"{methodName}/{paramParts.Count}";
                if (!emittedSigs.Add(sig)) continue;

                sb.AppendLine($"    procedure {methodName}({paramStr}){returnPart}");
                sb.AppendLine("    begin");
                sb.AppendLine("    end;");
                sb.AppendLine();
            }

            sb.AppendLine("}");

            // Track the name for reporting
            AutoStubbedCodeunits[codeunitId] = foundName;

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }


    private static readonly HashSet<string> _alKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "field", "var", "begin", "end", "if", "then", "else", "repeat", "until",
        "while", "do", "for", "to", "downto", "case", "of", "with", "exit", "trigger",
        "procedure", "local", "internal", "protected", "true", "false", "not", "and", "or",
        "xor", "div", "mod", "in", "array", "record", "codeunit", "page", "report", "query",
        "xmlport", "table", "enum", "interface", "temporary", "database"
    };
    private static bool IsAlKeyword(string name) => _alKeywords.Contains(name);

    /// <summary>
    /// Generate an AL table stub with fields and methods from the BC compiler's symbol table.
    /// Tables need fields + keys to compile. Methods (procedures defined on the table) are
    /// included so that calls like Record.SomeMethod() dispatch correctly.
    /// </summary>
    private static string? RenderTableStubFromSymbols(Compilation compilation, int tableId)
    {
        try
        {
            var lookupMethod = compilation.GetType().GetMethod(
                "GetApplicationObjectTypeSymbolsByIdAcrossModules",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(SymbolKind), typeof(int) }, null);
            if (lookupMethod == null) return null;

            var symbolsObj = lookupMethod.Invoke(compilation, new object[] { SymbolKind.Table, tableId });
            if (symbolsObj == null) return null;
            var lengthProp = symbolsObj.GetType().GetProperty("Length");
            if (lengthProp == null || (int)lengthProp.GetValue(symbolsObj)! == 0) return null;
            var indexer = symbolsObj.GetType().GetProperty("Item");
            if (indexer == null) return null;
            var found = indexer.GetValue(symbolsObj, new object[] { 0 })!;
            var foundName = found.GetType().GetProperty("Name")?.GetValue(found)?.ToString() ?? $"AutoStub{tableId}";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"table {tableId} \"{foundName.Replace("\"", "\"\"")}\"");
            sb.AppendLine("{");

            // Preserve the real PK shape so Insert collisions and AutoIncrement
            // work correctly. BC fires AutoIncrement on every Insert overload
            // (RecordRef.Insert docs).
            var pkFields = ExtractPrimaryKeyFields(found);

            sb.AppendLine("    fields");
            sb.AppendLine("    {");
            if (pkFields.Count > 0)
            {
                foreach (var (_, fieldSymbol) in pkFields)
                {
                    var rendered = RenderPrimaryKeyFieldDecl(fieldSymbol);
                    if (rendered != null)
                        sb.AppendLine(rendered);
                }
            }
            else
            {
                sb.AppendLine("        field(1; PK; Integer) { }");
            }
            sb.AppendLine("    }");
            sb.AppendLine("    keys");
            sb.AppendLine("    {");
            if (pkFields.Count > 0)
            {
                var keyList = string.Join(", ", pkFields.Select(p => $"\"{p.Name.Replace("\"", "\"\"")}\""));
                sb.AppendLine($"        key(PK; {keyList}) {{ Clustered = true; }}");
            }
            else
            {
                sb.AppendLine("        key(PK; PK) { Clustered = true; }");
            }
            sb.AppendLine("    }");

            // Methods — same approach as codeunit stubs
            var getMembersMethod = found.GetType().GetMethod("GetMembers", Type.EmptyTypes);
            if (getMembersMethod != null)
            {
                var members = getMembersMethod.Invoke(found, null) as System.Collections.IEnumerable;
                if (members != null)
                {
                    var emittedSigs = new HashSet<string>();
                    foreach (var member in members)
                    {
                        var memberKind = member.GetType().GetProperty("Kind")?.GetValue(member);
                        if (memberKind?.ToString() != "Method") continue;

                        var methodName = member.GetType().GetProperty("Name")?.GetValue(member)?.ToString();
                        if (methodName == null) continue;

                        var paramsProp = member.GetType().GetProperty("Parameters");
                        var paramParts = new List<string>();
                        if (paramsProp != null)
                        {
                            var parms = paramsProp.GetValue(member) as System.Collections.IEnumerable;
                            if (parms != null)
                            {
                                foreach (var p in parms)
                                {
                                    var pName = p.GetType().GetProperty("Name")?.GetValue(p)?.ToString() ?? "p";
                                    if (IsAlKeyword(pName)) pName = $"\"{pName}\"";
                                    else if (pName.Contains(" ") || pName.Contains(".")) pName = $"\"{pName}\"";
                                    var pIsVar = p.GetType().GetProperty("IsVar")?.GetValue(p) is true;
                                    var pType = p.GetType().GetProperty("ParameterType")?.GetValue(p)
                                             ?? p.GetType().GetProperty("Type")?.GetValue(p);
                                    var typeName = pType != null ? RenderSymbolTypeViaReflection(pType) : "Variant";
                                    var prefix = pIsVar ? "var " : "";
                                    paramParts.Add($"{prefix}{pName}: {typeName}");
                                }
                            }
                        }
                        var paramStr = string.Join("; ", paramParts);

                        var returnPart = "";
                        var retType = member.GetType().GetProperty("ReturnValueType")?.GetValue(member)
                                   ?? member.GetType().GetProperty("ReturnType")?.GetValue(member);
                        if (retType != null)
                        {
                            var navTypeKind = retType.GetType().GetProperty("NavTypeKind")?.GetValue(retType);
                            if (navTypeKind != null && navTypeKind.ToString() != "None" && navTypeKind.ToString() != "Void")
                                returnPart = $": {RenderSymbolTypeViaReflection(retType)}";
                        }

                        var sig = $"{methodName}/{paramParts.Count}";
                        if (!emittedSigs.Add(sig)) continue;

                        sb.AppendLine($"    procedure {methodName}({paramStr}){returnPart}");
                        sb.AppendLine("    begin");
                        sb.AppendLine("    end;");
                        sb.AppendLine();
                    }
                }
            }

            sb.AppendLine("}");
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Read a property whether it is declared on the runtime type, a base type,
    /// or an interface. BC symbol contracts (e.g. <c>ITableTypeSymbol.PrimaryKey</c>)
    /// live on interfaces, so plain <c>GetType().GetProperty</c> on a concrete
    /// reference symbol misses them.
    /// </summary>
    private static object? GetSymbolPropertyValue(object obj, string propertyName)
    {
        var t = obj.GetType();
        for (var cur = t; cur != null; cur = cur.BaseType)
        {
            var prop = cur.GetProperty(propertyName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop != null) return prop.GetValue(obj);
        }
        foreach (var iface in t.GetInterfaces())
        {
            var prop = iface.GetProperty(propertyName);
            if (prop != null) return prop.GetValue(obj);
        }
        return null;
    }

    /// <summary>
    /// Read the primary-key FieldSymbols (with names) from a TableTypeSymbol in
    /// PK order. Falls back to an empty list when the symbol shape is unfamiliar
    /// so callers can use a synthesized PK field.
    /// </summary>
    private static List<(string Name, object Symbol)> ExtractPrimaryKeyFields(object tableSymbol)
    {
        var result = new List<(string Name, object Symbol)>();
        try
        {
            var pk = GetSymbolPropertyValue(tableSymbol, "PrimaryKey");
            if (pk == null) return result;
            var fieldsObj = GetSymbolPropertyValue(pk, "Fields");
            if (fieldsObj is not System.Collections.IEnumerable fields) return result;
            foreach (var f in fields)
            {
                if (f == null) continue;
                var name = GetSymbolPropertyValue(f, "Name")?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                result.Add((name, f));
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Render a single primary-key field declaration with the field's real id,
    /// name, type, length, and AutoIncrement so the stub preserves enough
    /// schema for Insert(true) to work like base BC.
    /// </summary>
    private static string? RenderPrimaryKeyFieldDecl(object fieldSymbol)
    {
        try
        {
            var idObj = GetSymbolPropertyValue(fieldSymbol, "Id");
            var nameObj = GetSymbolPropertyValue(fieldSymbol, "Name");
            if (idObj is not int id || nameObj is not string name || string.IsNullOrEmpty(name))
                return null;

            var typeObj = GetSymbolPropertyValue(fieldSymbol, "Type");
            var typeText = RenderFieldTypeForStub(fieldSymbol, typeObj);

            var autoInc = ReadAutoIncrement(fieldSymbol);
            var props = autoInc ? " AutoIncrement = true; " : " ";

            return $"        field({id}; \"{name.Replace("\"", "\"\"")}\"; {typeText}) {{{props}}}";
        }
        catch { return null; }
    }

    /// <summary>Render a stub field's type, preserving Text[N]/Code[N] length when available.</summary>
    private static string RenderFieldTypeForStub(object fieldSymbol, object? typeSymbol)
    {
        if (typeSymbol == null) return "Variant";
        var navTypeKind = GetSymbolPropertyValue(typeSymbol, "NavTypeKind")?.ToString();
        if (navTypeKind is "Text" or "Code")
        {
            var hasLengthObj = GetSymbolPropertyValue(fieldSymbol, "HasLength");
            var lengthObj = GetSymbolPropertyValue(fieldSymbol, "Length");
            if (hasLengthObj is true && lengthObj is int length && length > 0)
                return $"{navTypeKind}[{length}]";
            return navTypeKind == "Code" ? "Code[20]" : "Text[250]";
        }
        return RenderSymbolTypeViaReflection(typeSymbol);
    }

    /// <summary>Returns true when the FieldSymbol carries AutoIncrement = true.</summary>
    private static bool ReadAutoIncrement(object fieldSymbol)
    {
        try
        {
            var propsObj = GetSymbolPropertyValue(fieldSymbol, "Properties");
            if (propsObj is not System.Collections.IEnumerable props) return false;

            foreach (var p in props)
            {
                var kind = GetSymbolPropertyValue(p, "PropertyKind")?.ToString();
                if (kind != "AutoIncrement") continue;

                // The presence of an AutoIncrement property in the Properties
                // array means the field declared it explicitly; AL syntax only
                // ever sets AutoIncrement = true (the default is false). Read
                // the value when available, otherwise treat presence as truth.
                var value = GetSymbolPropertyValue(p, "Value");
                if (value is bool b) return b;
                if (value != null && bool.TryParse(value.ToString(), out var parsed)) return parsed;

                var valueText = GetSymbolPropertyValue(p, "ValueText")?.ToString();
                if (valueText != null && bool.TryParse(valueText, out var parsedText)) return parsedText;

                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Render an AL type from a BC symbol type object using reflection.</summary>
    private static string RenderSymbolTypeViaReflection(object typeSymbol)
    {
        var navTypeKind = typeSymbol.GetType().GetProperty("NavTypeKind")?.GetValue(typeSymbol)?.ToString();
        if (navTypeKind == null) return "Variant";

        // For complex types (Record, Codeunit, Enum, etc.) the symbol's ToString()
        // includes the full qualified name. Use Variant to avoid syntax issues.
        return navTypeKind switch
        {
            "Integer" => "Integer",
            "BigInteger" => "BigInteger",
            "Decimal" => "Decimal",
            "Boolean" => "Boolean",
            "Date" => "Date",
            "Time" => "Time",
            "DateTime" => "DateTime",
            "Duration" => "Duration",
            "Guid" => "Guid",
            "Char" => "Char",
            "Byte" => "Byte",
            "Option" => "Option",
            "Enum" => "Option",       // Enum args arrive as NavOption at runtime; Option compiles without package refs
            "Variant" => "Variant",
            "RecordId" => "RecordId",
            "DateFormula" => "DateFormula",
            "JsonObject" => "JsonObject",
            "JsonArray" => "JsonArray",
            "JsonToken" => "JsonToken",
            "JsonValue" => "JsonValue",
            "HttpClient" => "HttpClient",
            "HttpHeaders" => "HttpHeaders",
            "HttpContent" => "HttpContent",
            "HttpRequestMessage" => "HttpRequestMessage",
            "HttpResponseMessage" => "HttpResponseMessage",
            "SecretText" => "SecretText",
            "Text" => "Text",
            "Code" => "Code[20]",
            "Label" => "Text",
            "TextConst" => "Text",
            "InStream" => "InStream",
            "OutStream" => "OutStream",
            "BigText" => "BigText",
            "Blob" => "BigText",
            "XmlDocument" => "XmlDocument",
            _ => "Variant", // Use Variant for complex/unknown types to avoid syntax errors
        };
    }

    /// <summary>
    /// Returns true for AL type names that map to C# primitive types (int, bool, etc.).
    /// These types produce <c>ByRef&lt;int&gt;</c>/<c>ByRef&lt;bool&gt;</c> when used as
    /// <c>var</c> parameters, which causes CS1503 when the caller passes a
    /// <c>ByRef&lt;Decimal18&gt;</c> or other concrete BC value type.  Auto-stubs have
    /// empty bodies so stripping <c>var</c> from these types is safe.
    /// </summary>
    private static bool IsCSharpPrimitiveMappedType(string typeName) => typeName is
        "Integer" or "BigInteger" or "Boolean" or "Char" or "Byte";

}
