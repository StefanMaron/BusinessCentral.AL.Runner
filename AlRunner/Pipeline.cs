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
}

public enum TestStatus { Pass, Fail, Error }

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
}

public class CapturedValue
{
    public required string ScopeName { get; init; }
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
}

public class AlRunnerPipeline
{
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
            foreach (var (scopeName, variableName, value, stmtId) in Runtime.ValueCapture.GetCaptures())
            {
                capturedValues.Add(new CapturedValue
                {
                    ScopeName = scopeName,
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
        if (options.OutputJson && (testResults.Count > 0 || messages.Count > 0))
        {
            var compilationErrors = RoslynCompiler.ExcludedFiles.Count > 0 ? RoslynCompiler.ExcludedFiles : null;
            stdoutStr = SerializeJsonOutput(testResults, exitCode, capturedValues: capturedValues, messages: messages, iterations: iterationLoops, compilationErrors: compilationErrors);
        }

        return new PipelineResult
        {
            ExitCode = exitCode,
            Tests = testResults,
            CapturedValues = capturedValues,
            Messages = messages,
            Iterations = iterationLoops,
            StdOut = stdoutStr,
            StdErr = stderr.ToString()
        };
    }

    public static string SerializeJsonOutput(
        List<TestResult> tests, int exitCode, bool indented = true,
        List<CapturedValue>? capturedValues = null, List<string>? messages = null,
        List<Runtime.IterationTracker.LoopRecord>? iterations = null,
        IReadOnlyDictionary<string, List<string>>? compilationErrors = null)
    {
        object? capturedValuesObj = capturedValues?.Count > 0
            ? capturedValues.Select(c => new
            {
                scopeName = c.ScopeName,
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

        var alSources = new List<string>();
        var stubSources = new List<string>();
        var assertStubSources = new List<string>();
        var inputPaths = new List<string>();
        var inputGroups = new List<(string Path, List<string> Sources)>();

        // Handle inline code
        if (options.InlineCode != null)
        {
            var code = options.InlineCode;
            if (!code.TrimStart().StartsWith("codeunit", StringComparison.OrdinalIgnoreCase) &&
                !code.TrimStart().StartsWith("table", StringComparison.OrdinalIgnoreCase))
            {
                code = $"codeunit 99 __Inline {{ trigger OnRun() begin {code} end; }}";
            }
            alSources.Add(code);
        }

        // Reset per-run state that the AL parsing populates.
        Runtime.EnumRegistry.Clear();
        Runtime.TableInitValueRegistry.Clear();
        Runtime.CodeunitNameRegistry.Clear();
        Runtime.CalcFormulaRegistry.Clear();
        Runtime.TableFieldRegistry.Clear();
        Runtime.MockNumberSequence.Reset();

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
                var text = File.ReadAllText(sf);
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
                    Log.Info($"  {name}");
                    alSources.Add(source);
                    groupSources.Add(source);
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
                    var src = File.ReadAllText(f);
                    alSources.Add(src);
                    groupSources.Add(src);
                }
                var fullPath = Path.GetFullPath(path);
                inputPaths.Add(fullPath);
                inputGroups.Add((fullPath, groupSources));
            }
            else if (File.Exists(path))
            {
                var src = File.ReadAllText(path);
                alSources.Add(src);
                var fullPath = Path.GetFullPath(Path.GetDirectoryName(path)!);
                inputPaths.Add(fullPath);
                inputGroups.Add((fullPath, new List<string> { src }));
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

        // Auto-discover dependency .app files from --packages directories
        if (options.PackagePaths.Count > 0 && inputGroups.Any(g => g.Path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)))
        {
            AutoDiscoverDependencies(options.PackagePaths, inputGroups, inputPaths, alSources);
        }

        // Kernel32 shim
        Kernel32Shim.EnsureRegistered();

        // Auto-include AL stubs (Assert, etc.)
        LoadAssertStubs(options.PackagePaths, inputPaths, inputGroups, alSources, assertStubSources);

        // Step 1: Transpile
        Timer.StartStage("AL transpilation");
        var generatedCSharpList = Transpile(options, alSources, inputGroups, inputPaths, stubSources, stderr);
        if (generatedCSharpList == null)
        {
            Timer.EndStage("AL transpilation");
            return 1;
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
            var stubCSharp = AlTranspiler.TranspileMulti(assertStubSources, options.PackagePaths, inputPaths);
            if (stubCSharp != null)
            {
                generatedCSharpList.AddRange(stubCSharp);
                Log.Info($"Added {stubCSharp.Count} Assert stub(s) for runtime dispatch");
            }
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
        Parallel.For(0, generatedCSharpList.Count, i =>
        {
            var (name, code) = generatedCSharpList[i];
            var tree = RoslynRewriter.RewriteToTree(code);
            // Second pass: inject per-statement ValueCapture.Capture calls.
            // The capture function no-ops when ValueCapture.Enabled is false,
            // so we can run this unconditionally and avoid a separate code
            // path for capture vs non-capture runs.
            var injectedRoot = ValueCaptureInjector.Inject(tree.GetRoot());
            if (options.IterationTracking)
                injectedRoot = IterationInjector.Inject(injectedRoot);
            tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.Create((Microsoft.CodeAnalysis.CSharp.CSharpSyntaxNode)injectedRoot);
            rewrittenTrees[i] = (name, tree);
        });
        var rewrittenTreeList = rewrittenTrees.ToList();

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

        // Step 3: Compile
        Timer.StartStage("Roslyn compilation");
        var mapperTask = Task.Run(() =>
            SourceLineMapper.Build(generatedCSharpList, GetRewrittenStrings()));
        var preloadedRefs = refsTask.Result;
        var assembly = RoslynCompiler.Compile(rewrittenTreeList, preloadedRefs);
        mapperTask.Wait();
        if (assembly == null)
        {
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
            return 1;
        }
        Timer.EndStage("Roslyn compilation");

        // Step 4: Execute
        Timer.StartStage("Test execution");
        Runtime.MockCodeunitHandle.CurrentAssembly = assembly;
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

            var results = Executor.RunTests(assembly, captureValues: options.CaptureValues, runProcedure: options.RunProcedure);
            testResults.AddRange(results);
            if (results.Count == 0 && options.RunProcedure != null)
                stderr.WriteLine($"Error: Procedure '{options.RunProcedure}' not found in the generated code.");
            if (!options.OutputJson)
                Executor.PrintResults(results);
            exitCode = Executor.ExitCode(results);

            if (options.CaptureValues)
                Runtime.ValueCapture.Disable();
            if (options.IterationTracking)
                Runtime.IterationTracker.Disable();
            Runtime.MessageCapture.Disable();
            if (options.ShowCoverage)
            {
                Executor.PrintCoverageReport();

                var sourceSpans = CoverageReport.ParseSourceSpans(generatedCSharpList!);
                var (hitStmts, totalStmts) = Runtime.AlScope.GetCoverageSets();

                var alFilePaths = new List<string>();
                foreach (var inputPath in inputPaths)
                {
                    if (Directory.Exists(inputPath))
                        alFilePaths.AddRange(Directory.GetFiles(inputPath, "*.al", SearchOption.AllDirectories));
                    else if (File.Exists(inputPath))
                        alFilePaths.Add(inputPath);
                }

                var objectToFile = CoverageReport.MapObjectsToFiles(generatedCSharpList!, alFilePaths);
                var scopeToObject = CoverageReport.BuildScopeToObjectMap(generatedCSharpList!);
                CoverageReport.WriteCobertura("cobertura.xml", sourceSpans, hitStmts, totalStmts, objectToFile, scopeToObject);
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
        }
        Timer.EndStage("Test execution");
        Timer.Print();

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
                    alSources.Add(source);
                    groupSources.Add(source);
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

    private static List<(string Name, string Code)>? Transpile(
        PipelineOptions options,
        List<string> alSources,
        List<(string Path, List<string> Sources)> inputGroups,
        List<string> inputPaths,
        List<string> stubSources,
        TextWriter stderr)
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
                    alSources.RemoveAll(src =>
                    {
                        var srcMatch = Regex.Match(src,
                            @"(?:codeunit|table|page|report|xmlport|query|enum|interface)\s+(\d+)",
                            RegexOptions.IgnoreCase);
                        return srcMatch.Success && srcMatch.Value.ToLowerInvariant() == stubId;
                    });
                    alSources.Add(stub);
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
            generatedCSharpList = AlTranspiler.TranspileMulti(alSources, options.PackagePaths, inputPaths);
            if (generatedCSharpList == null || generatedCSharpList.Count == 0)
                return null;
        }

        return generatedCSharpList;
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
