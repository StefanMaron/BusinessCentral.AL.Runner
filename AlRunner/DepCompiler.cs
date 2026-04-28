using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using BcSrs = Microsoft.Dynamics.Nav.CodeAnalysis.SymbolReferenceSpecification;

namespace AlRunner;

/// <summary>
/// Compiles a dependency .app package through the AL→C#→rewrite→Roslyn pipeline
/// and produces a .dll on disk. The resulting DLL uses the same mock types
/// (AlScope, MockRecordHandle, etc.) as the main test assembly, enabling
/// cross-assembly codeunit dispatch via MockCodeunitHandle.
/// </summary>
public static class DepCompiler
{
    private static readonly Version DefaultAppVersion = new(1, 0, 0, 0);
    private static bool _loggedVersionFallback;

    private record AppJsonIdentity(string Publisher, string Name, string VersionRaw, Version Version, Guid AppId);

    private static Version ParseVersionTolerant(string? raw, Version fallback)
    {
        var text = raw?.Trim() ?? string.Empty;
        if (Version.TryParse(text, out var parsed))
            return parsed;

        if (!_loggedVersionFallback)
        {
            _loggedVersionFallback = true;
            Console.Error.WriteLine($"App version unparseable ('{text}'): falling back to {fallback}");
        }

        return fallback;
    }

    private static bool TryReadAppJsonIdentity(string appJsonPath, out AppJsonIdentity identity)
    {
        identity = default!;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(appJsonPath));
            var root = json.RootElement;

            var fallbackName = Path.GetFileName(Path.GetDirectoryName(appJsonPath)) ?? "Unknown";
            var publisher = root.TryGetProperty("publisher", out var publisherProp)
                ? publisherProp.GetString() ?? "Unknown"
                : "Unknown";
            var name = root.TryGetProperty("name", out var nameProp)
                ? nameProp.GetString() ?? fallbackName
                : fallbackName;
            var rawVersion = root.TryGetProperty("version", out var versionProp)
                ? versionProp.GetString() ?? string.Empty
                : string.Empty;
            var version = ParseVersionTolerant(rawVersion, DefaultAppVersion);

            var appId = Guid.Empty;
            if (root.TryGetProperty("id", out var idProp))
            {
                var idRaw = idProp.GetString();
                if (idRaw != null && Guid.TryParse(idRaw, out var parsed))
                    appId = parsed;
            }

            identity = new AppJsonIdentity(publisher, name, rawVersion, version, appId);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to parse {appJsonPath}: {ex.Message}");
            return false;
        }
    }

    private static void WriteDepSidecar(string dllPath, string publisher, string name, string versionRaw, Guid appId)
    {
        var version = ParseVersionTolerant(versionRaw, DefaultAppVersion);
        var sidecarPath = Path.ChangeExtension(dllPath, ".app.json");
        try
        {
            var payload = new
            {
                id = appId,
                name,
                publisher,
                version = version.ToString()
            };
            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write dep sidecar {sidecarPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Compile a single .app dependency (or directory of AL source) to a .dll in <paramref name="outputDir"/>.
    /// Returns 0 on success, 1 on failure.
    /// </summary>
    public static int CompileDep(string appPath, string outputDir, List<string> packagePaths)
    {
        // If appPath is a directory, compile AL source files directly
        if (Directory.Exists(appPath))
        {
            var appJsonPath = Path.Combine(appPath, "app.json");
            return File.Exists(appJsonPath)
                ? CompileDepFromDir(appPath, outputDir, packagePaths)
                : CompileDepMultiApp(appPath, outputDir, packagePaths);
        }

        if (!File.Exists(appPath))
        {
            Console.Error.WriteLine($"Error: .app file or directory not found: {appPath}");
            return 1;
        }

        Directory.CreateDirectory(outputDir);

        // 1. Read manifest for naming
        var (publisher, name, version) = ReadAppIdentity(appPath);
        var dllName = SanitizeName($"{publisher}_{name}_{version}.dll");
        var dllPath = Path.Combine(outputDir, dllName);

        // 2. Cache check — skip if DLL already exists
        if (File.Exists(dllPath))
        {
            Console.Error.WriteLine($"Cached: {dllName} (already exists in {outputDir})");
            return 0;
        }

        Console.Error.WriteLine($"Compiling dependency: {name} by {publisher} v{version}");

        // 3. Validate that all declared dependencies are available in packages
        var missingDeps = ValidateDependencies(appPath, packagePaths);
        if (missingDeps.Count > 0)
        {
            Console.Error.WriteLine($"  Cannot compile \"{name}\": {missingDeps.Count} required dependenc{(missingDeps.Count == 1 ? "y" : "ies")} not found in --packages:");
            foreach (var dep in missingDeps)
                Console.Error.WriteLine($"    ✗ \"{dep.Name}\" by {dep.Publisher} (>= {dep.MinVersion})");
            if (packagePaths.Count > 0)
            {
                Console.Error.WriteLine($"  Package directories searched:");
                foreach (var p in packagePaths)
                    Console.Error.WriteLine($"    {p}");
            }
            else
            {
                Console.Error.WriteLine($"  No --packages directories specified.");
            }
            Console.Error.WriteLine($"  To fix: download the missing .app file(s) and add them to your packages directory,");
            Console.Error.WriteLine($"  or add --packages pointing to where they live.");
            return 1;
        }

        // 4. Extract AL sources from .app
        List<(string Name, string Source)> alEntries;
        try
        {
            alEntries = AppPackageReader.ExtractAlSources(appPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error extracting AL from {appPath}: {ex.Message}");
            return 1;
        }

        if (alEntries.Count == 0)
        {
            Console.Error.WriteLine($"Warning: no AL source found in {appPath} — skipping");
            return 0;
        }

        var alSources = alEntries.Select(e => e.Source).ToList();
        Console.Error.WriteLine($"  Extracted {alSources.Count} AL files");

        // 4. Register metadata (enums, tables, etc.)
        foreach (var src in alSources)
        {
            Runtime.EnumRegistry.ParseAndRegister(src);
            Runtime.TableInitValueRegistry.ParseAndRegister(src);
            Runtime.CodeunitNameRegistry.ParseAndRegister(src);
            Runtime.CalcFormulaRegistry.ParseAndRegister(src);
            Runtime.TableFieldRegistry.ParseAndRegister(src);
            Runtime.QueryFieldRegistry.ParseAndRegister(src);
        }

        // 5. Resolve package paths — include the .app's own .alpackages and any explicit paths
        var allPackagePaths = new List<string>(packagePaths);
        var appDir = Path.GetDirectoryName(Path.GetFullPath(appPath));
        if (appDir != null)
        {
            var alPkg = Path.Combine(appDir, ".alpackages");
            if (Directory.Exists(alPkg) && !allPackagePaths.Contains(alPkg))
                allPackagePaths.Add(alPkg);
        }
        // Also add output dir as package path so dep-on-dep chains resolve
        if (Directory.Exists(outputDir))
        {
            foreach (var existingDll in Directory.GetFiles(outputDir, "*.app"))
            {
                var dir = Path.GetDirectoryName(existingDll);
                if (dir != null && !allPackagePaths.Contains(dir))
                    allPackagePaths.Add(dir);
            }
        }

        // 6. Create a temp app.json so TranspileMulti can identify the app being
        //    compiled and exclude it from package references (avoids self-duplicate
        //    AL0275 errors and, crucially, prevents the .app's SymbolReference.json
        //    from pulling in DotNet types that the source files may not need).
        var appGuid = ReadAppGuid(appPath);
        string? tempManifestDir = null;
        List<string>? inputPaths = null;
        if (appGuid != Guid.Empty)
        {
            tempManifestDir = Path.Combine(Path.GetTempPath(), $"alrunner-dep-{appGuid:N}");
            Directory.CreateDirectory(tempManifestDir);
            var manifest = $"{{\"id\":\"{appGuid}\",\"name\":\"{name}\",\"publisher\":\"{publisher}\",\"version\":\"{version}\"}}";
            File.WriteAllText(Path.Combine(tempManifestDir, "app.json"), manifest);
            inputPaths = new List<string> { tempManifestDir };
        }

        // 7. Transpile AL → C#
        // The BC compiler refuses to emit ANY objects when unresolvable declaration
        // errors exist (e.g. DotNet types, missing codeunits from other apps).
        // DotNet types fundamentally don't work in the runner (no BC service tier),
        // so excluding files that use them is correct, not a workaround.
        //
        // Strategy: try full compilation first. If zero output, iteratively remove
        // files with unresolvable dependencies and retry until we get output.
        var compilableSources = new List<string>(alSources);
        var effectivePackages = allPackagePaths.Count > 0 ? allPackagePaths : null;
        var csharpList = AlTranspiler.TranspileMulti(compilableSources, effectivePackages, inputPaths);

        if ((csharpList == null || csharpList.Count == 0) && compilableSources.Count > 0)
        {
            // Phase 1: Remove files with DotNet type references (unsupported in runner).
            // DotNet interop requires the BC service tier and .NET assembly loading —
            // these types fundamentally cannot work in standalone mode.
            // AL syntax: `var x: DotNet StreamReader;` or `DotNet "System.IO.StreamReader"`
            var dotnetPattern = new System.Text.RegularExpressions.Regex(
                @":\s*DotNet\b|\bDotNet\s+[""A-Z]",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var dotnetCount = compilableSources.Count(s => dotnetPattern.IsMatch(s));
            if (dotnetCount > 0)
            {
                compilableSources = compilableSources.Where(s => !dotnetPattern.IsMatch(s)).ToList();
                Console.Error.WriteLine($"  Excluded {dotnetCount} file(s) using DotNet interop (unsupported in runner)");

                // Retry without DotNet files
                csharpList = AlTranspiler.TranspileMulti(compilableSources, effectivePackages, inputPaths);
            }
        }

        // Phase 2: If still failing after DotNet removal, report unresolved symbols clearly.
        // All declared dependencies passed validation, so remaining failures indicate
        // version mismatches or undeclared transitive dependencies.
        if ((csharpList == null || csharpList.Count == 0) && compilableSources.Count > 0)
        {
            // Capture diagnostics to extract the specific unresolved symbols
            var savedErr = Console.Error;
            var diagCapture = new System.IO.StringWriter();
            Console.SetError(diagCapture);
            try { AlTranspiler.TranspileMulti(compilableSources, effectivePackages, inputPaths); }
            finally { Console.SetError(savedErr); }

            var diagOutput = diagCapture.ToString();
            var missingPattern = new System.Text.RegularExpressions.Regex(
                @"((?:Codeunit|Table|Page|Enum|Interface|Report|PermissionSet|DotNet)\s+'[^']+'\s+is missing)");
            var missingSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in missingPattern.Matches(diagOutput))
                missingSymbols.Add(m.Groups[1].Value);

            if (missingSymbols.Count > 0)
            {
                Console.Error.WriteLine($"  Compilation failed — {missingSymbols.Count} unresolved symbol(s):");
                foreach (var sym in missingSymbols.OrderBy(s => s))
                    Console.Error.WriteLine($"    ✗ {sym}");
                Console.Error.WriteLine();
                Console.Error.WriteLine("  These symbols come from .app packages not included in --packages.");
                Console.Error.WriteLine("  To fix: find the .app file(s) that contain these objects and add them");
                Console.Error.WriteLine("  to your packages directory, then retry.");
            }
        }

        // Clean up temp manifest
        if (tempManifestDir != null)
            try { Directory.Delete(tempManifestDir, true); } catch { }

        if (csharpList == null || csharpList.Count == 0)
        {
            Console.Error.WriteLine($"  AL transpilation produced no output for {name}");
            return 1;
        }
        Console.Error.WriteLine($"  Transpiled {csharpList.Count} C# objects (from {compilableSources.Count}/{alSources.Count} AL files)");

        // 7. Rewrite C# (BC types → mock types — same rewriter as main pipeline)
        var rewrittenTrees = new List<SyntaxTree>();
        int rewriteErrors = 0;
        foreach (var item in csharpList)
        {
            try
            {
                var tree = RoslynRewriter.RewriteToTree(item.Code);
                if (tree != null)
                    rewrittenTrees.Add(tree);
            }
            catch
            {
                rewriteErrors++;
                // Generate minimal stub so other types can still reference it
                var className = ExtractClassName(item.Name);
                if (className != null)
                {
                    var stubCode = $"namespace Microsoft.Dynamics.Nav.BusinessApplication {{ public class {className} {{ }} }}";
                    rewrittenTrees.Add(CSharpSyntaxTree.ParseText(stubCode, path: $"{className}_stub.cs"));
                }
            }
        }
        if (rewriteErrors > 0)
            Console.Error.WriteLine($"  {rewriteErrors} rewrite error(s) — stub classes generated for those");

        if (rewrittenTrees.Count == 0)
        {
            Console.Error.WriteLine($"  No rewritten C# trees — cannot compile");
            return 1;
        }

        // 8. Compile to DLL on disk
        var references = RoslynCompiler.LoadReferences();
        // Add any existing dep DLLs in outputDir as references (for dep-on-dep chains)
        foreach (var dll in Directory.GetFiles(outputDir, "*.dll"))
        {
            try { references.Add(MetadataReference.CreateFromFile(dll)); }
            catch { /* skip unloadable */ }
        }

        var errors = new List<string>();
        bool success = CompileToDisk(rewrittenTrees, references, dllPath, errors);

        if (!success)
        {
            Console.Error.WriteLine($"  Roslyn compilation failed ({errors.Count} errors):");
            foreach (var err in errors.Take(20))
                Console.Error.WriteLine($"    {err}");
            return 1;
        }

        Console.Error.WriteLine($"  Compiled: {dllName}");
        WriteDepSidecar(dllPath, publisher, name, version, appGuid);
        return 0;
    }

    /// <summary>
    /// Compile multiple app directories (each containing app.json) under a root directory.
    /// </summary>
    public static int CompileDepMultiApp(string rootDir, string outputDir, List<string> packagePaths)
    {
        if (!Directory.Exists(rootDir))
        {
            Console.Error.WriteLine($"Error: directory not found: {rootDir}");
            return 1;
        }

        var appJsonPaths = Directory.GetFiles(rootDir, "app.json", SearchOption.AllDirectories);
        if (appJsonPaths.Length == 0)
            return CompileDepFromDir(rootDir, outputDir, packagePaths);

        var appDirs = appJsonPaths
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int failures = 0;
        foreach (var appDir in appDirs)
        {
            AppJsonIdentity? identity = null;
            var appJsonPath = Path.Combine(appDir!, "app.json");
            if (File.Exists(appJsonPath) && TryReadAppJsonIdentity(appJsonPath, out var parsed))
                identity = parsed;

            var result = CompileDepFromDir(appDir!, outputDir, packagePaths, identity);
            if (result != 0)
                failures++;
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Compile syntax trees to a DLL file on disk (instead of in-memory like CompileFromTrees).
    /// </summary>
    internal static bool CompileToDisk(List<SyntaxTree> syntaxTrees, List<MetadataReference> references,
        string outputPath, IList<string>? errorSink = null)
    {
        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(outputPath),
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithAllowUnsafe(true));

        var result = compilation.Emit(outputPath);

        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            foreach (var d in errors)
                errorSink?.Add(d.ToString());
            // DEBUG: dump rewritten trees on failure
            if (System.Environment.GetEnvironmentVariable("ALRUNNER_DUMP_REWRITTEN") == "1")
            {
                var dumpDir = Path.Combine(System.IO.Path.GetTempPath(), "alrunner_debug_" + Path.GetFileNameWithoutExtension(outputPath));
                Directory.CreateDirectory(dumpDir);
                int idx = 0;
                foreach (var tree in syntaxTrees)
                {
                    var fp = tree.FilePath;
                    var fn = (!string.IsNullOrEmpty(fp) ? Path.GetFileName(fp) : null) ?? $"tree_{idx}";
                    if (string.IsNullOrEmpty(fn)) fn = $"tree_{idx}";
                    File.WriteAllText(Path.Combine(dumpDir, fn + ".cs"), tree.GetRoot().ToFullString());
                    idx++;
                }
                Console.Error.WriteLine($"  DEBUG: rewritten trees dumped to {dumpDir}");
            }
            // Clean up partial output
            try { File.Delete(outputPath); } catch { }
            return false;
        }

        return true;
    }

    /// <summary>
    /// Compile a directory of AL source files to a dependency .dll.
    /// </summary>
    private record AppInfo(string Dir, Guid Id, string Name, string Publisher, string Version, List<Guid> DepIds);

    /// <summary>
    /// Per-app compiled module-info cache for in-memory symbol chaining. Avoids needing
    /// to emit a synthetic .app file alongside each .dll — the BC compiler can resolve
    /// downstream apps' references against the upstream app's compiled module symbol
    /// directly.
    /// </summary>
    private static readonly Dictionary<Guid, BcSrs> CompiledAppRefs = new();

    /// <summary>
    /// Wrap a compiled BC <c>Compilation</c>'s module symbol in a SymbolReferenceSpecification
    /// using the <c>(IModuleSymbol)</c> ctor — the BC compiler resolves downstream references
    /// against the live module without needing to call out to ISymbolReferenceLoader.
    /// <c>CompiledModule</c> is internal so we access it via reflection.
    /// </summary>
    /// <summary>
    /// Emit a BC .app symbol package alongside the per-app DLL so subsequent compiles in
    /// the multi-app loop can resolve cross-app references via the default PackageScanner.
    /// Uses the public <c>PackageModuleOutputter</c>. Failures are non-fatal (logged at the
    /// caller) since the .app is only needed for chaining; runtime execution uses the .dll.
    /// </summary>
    private static void EmitSymbolApp(Microsoft.Dynamics.Nav.CodeAnalysis.Compilation comp, AppInfo app, string outputDir)
    {
        var version = Version.TryParse(app.Version, out var v) ? v : new Version(1, 0, 0, 0);
        var manifest = new Microsoft.Dynamics.Nav.CodeAnalysis.Packaging.NavAppManifest
        {
            AppId = app.Id,
            AppName = app.Name,
            AppPublisher = app.Publisher,
            AppVersion = version,
            AppDescription = $"{app.Name} (slice)",
            AppBrief = app.Name,
            Target = Microsoft.Dynamics.Nav.CodeAnalysis.CompilationTarget.OnPrem,
            Runtime = new Version(15, 0),
            ContextSensitiveHelpUrl = "https://learn.microsoft.com/en-us/dynamics365/business-central/",
            AppHelpBaseUrl = "https://learn.microsoft.com/en-us/dynamics365/business-central/",
        };
        var appPath = Path.Combine(outputDir, SanitizeName($"{app.Publisher}_{app.Name}_{app.Version}.app"));
        var emitOptions = new Microsoft.Dynamics.Nav.CodeAnalysis.EmitOptions();
        using var stream = File.Create(appPath);
        var outputter = new Microsoft.Dynamics.Nav.CodeAnalysis.Packaging.PackageModuleOutputter(
            manifest, stream, comp, emitOptions, projectRoot: app.Dir);
        var result = comp.Emit(emitOptions, outputter, default);
        if (!result.Success)
        {
            stream.Close();
            try { File.Delete(appPath); } catch { }
            var firstErr = result.Diagnostics.FirstOrDefault(d => d.Severity == Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics.DiagnosticSeverity.Error);
            throw new Exception($".app emission failed: {firstErr?.GetMessage() ?? "(no error message)"}");
        }
        Console.Error.WriteLine($"  Emitted symbol package: {Path.GetFileName(appPath)}");
    }

    private static BcSrs? TryGetModuleRef(Microsoft.Dynamics.Nav.CodeAnalysis.Compilation comp)
    {
        var prop = comp.GetType().GetProperty("CompiledModule",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var moduleObj = prop?.GetValue(comp);
        if (moduleObj == null) return null;
        var imsType = comp.GetType().Assembly.GetType("Microsoft.Dynamics.Nav.CodeAnalysis.IModuleSymbol");
        if (imsType == null || !imsType.IsAssignableFrom(moduleObj.GetType())) return null;
        var ctor = typeof(BcSrs).GetConstructor(new[] { imsType });
        if (ctor == null) return null;
        return (BcSrs)ctor.Invoke(new[] { moduleObj });
    }

    /// <summary>
    /// Compile a multi-app slice: each subdirectory of <paramref name="srcDir"/> is a
    /// separate app with its own app.json. Build a dependency graph from the manifests,
    /// topologically sort, and compile each app to its own DLL — accumulating prior
    /// outputs as <c>--packages</c> for later compiles. Mirrors BC's actual packaging.
    /// </summary>
    private static int CompileDepMultiApp(string srcDir, List<string> appDirs, string outputDir, List<string> packagePaths)
    {
        var apps = new List<AppInfo>();
        foreach (var dir in appDirs)
        {
            var manPath = Path.Combine(dir, "app.json");
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manPath));
                var root = doc.RootElement;
                var id = Guid.Parse(root.GetProperty("id").GetString()!);
                var name = root.GetProperty("name").GetString() ?? Path.GetFileName(dir);
                var publisher = root.GetProperty("publisher").GetString() ?? "Unknown";
                var version = root.GetProperty("version").GetString() ?? "1.0.0.0";
                var deps = new List<Guid>();
                if (root.TryGetProperty("dependencies", out var depArr) && depArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var d in depArr.EnumerateArray())
                        if (d.TryGetProperty("id", out var didProp) || d.TryGetProperty("appId", out didProp))
                            if (Guid.TryParse(didProp.GetString(), out var did))
                                deps.Add(did);
                apps.Add(new AppInfo(dir, id, name, publisher, version, deps));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: failed to read {manPath}: {ex.Message}");
            }
        }

        Console.Error.WriteLine($"Multi-app compile: {apps.Count} apps detected");

        // Topological sort: an app comes after all dependencies it has *that are also
        // present in our slice*. Dependencies on apps not in the slice are satisfied
        // by the user-provided --packages binaries.
        var slicedIds = apps.Select(a => a.Id).ToHashSet();
        var ordered = new List<AppInfo>();
        var done = new HashSet<Guid>();
        var maxIters = apps.Count * apps.Count + 1;
        while (ordered.Count < apps.Count && maxIters-- > 0)
        {
            foreach (var app in apps)
            {
                if (done.Contains(app.Id)) continue;
                if (app.DepIds.Where(slicedIds.Contains).All(done.Contains))
                {
                    ordered.Add(app);
                    done.Add(app.Id);
                }
            }
        }
        if (ordered.Count < apps.Count)
        {
            Console.Error.WriteLine($"Error: dependency cycle in {apps.Count - ordered.Count} apps");
            return 1;
        }

        Directory.CreateDirectory(outputDir);
        var accumPackages = new List<string>(packagePaths);
        var outputDirAbs = Path.GetFullPath(outputDir);
        if (!accumPackages.Contains(outputDirAbs)) accumPackages.Add(outputDirAbs);

        // Reset the compiled-app symbol-ref cache for this build. Each app accumulates
        // ModuleInfo from its declared deps that are also in our slice — no .app
        // serialization needed.
        CompiledAppRefs.Clear();

        var failed = new List<string>();
        foreach (var app in ordered)
        {
            Console.Error.WriteLine($"=== Compiling app: {app.Name} by {app.Publisher} v{app.Version} ===");
            // Build the in-memory refs list for this app: ModuleInfo of every declared
            // dependency that's also in our slice and was built successfully.
            var refs = app.DepIds
                .Where(CompiledAppRefs.ContainsKey)
                .Select(id => CompiledAppRefs[id])
                .ToList();
            if (refs.Count > 0)
                Console.Error.WriteLine($"  Chaining {refs.Count} prior ModuleInfo(s) into compile.");
            var rc = CompileDepFromDir(app.Dir, outputDir, accumPackages, refs);
            if (rc != 0)
            {
                failed.Add(app.Name);
                Console.Error.WriteLine($"WARN: compile failed for {app.Name}; continuing with other apps");
            }
            else
            {
                // Emit a .app symbol package so downstream apps can resolve cross-app
                // references. Uses BC's PackageModuleOutputter (public API). The .app sits
                // alongside the .dll in outputDir; outputDir is already in accumPackages,
                // so the next compile's PackageScanner picks it up automatically.
                var compFor = AlTranspiler.LastCompilation;
                if (compFor != null)
                {
                    try { EmitSymbolApp(compFor, app, outputDir); }
                    catch (Exception ex) { Console.Error.WriteLine($"  WARN: .app emission failed for {app.Name}: {ex.Message}"); }
                }
                // Also record the in-memory ModuleInfo for downstream chaining via
                // SymbolReferenceSpecification (kept as fallback, not strictly needed
                // once .app emission works).
                var moduleRef = compFor != null ? TryGetModuleRef(compFor) : null;
                if (moduleRef != null)
                    CompiledAppRefs[app.Id] = moduleRef;
            }
        }
        var built = ordered.Count - failed.Count;
        Console.Error.WriteLine($"Multi-app compile: {built}/{ordered.Count} app(s) compiled to {outputDir}");
        if (failed.Count > 0)
        {
            Console.Error.WriteLine($"  Failed apps ({failed.Count}): {string.Join(", ", failed)}");
            return 1;
        }
        return 0;
    }

    private static int CompileDepFromDir(string srcDir, string outputDir, List<string> packagePaths, AppJsonIdentity? appIdentity = null, List<BcSrs>? extraRefs = null)
    {
        Directory.CreateDirectory(outputDir);

        // Multi-app detection: if subdirectories have app.json files, treat each as a
        // separate app and produce one DLL per app. This mirrors BC's actual packaging:
        // each app carries its real identity (id/name/publisher) so InternalsVisibleTo
        // grants resolve correctly per-DLL, and cross-app object name conflicts that
        // exist legitimately in BC source (different apps shipping the same FQN) are
        // dissolved at the compile-unit boundary.
        var appDirs = Directory.GetDirectories(srcDir)
            .Where(d => File.Exists(Path.Combine(d, "app.json")))
            .ToList();
        if (appDirs.Count > 0)
            return CompileDepMultiApp(srcDir, appDirs, outputDir, packagePaths);

        var dirName = Path.GetFileName(srcDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (appIdentity == null)
        {
            var appJsonPath = Path.Combine(srcDir, "app.json");
            if (File.Exists(appJsonPath) && TryReadAppJsonIdentity(appJsonPath, out var parsed))
                appIdentity = parsed;
        }

        var dllName = appIdentity != null
            ? SanitizeName($"{appIdentity.Publisher}_{appIdentity.Name}_{appIdentity.Version}.dll")
            : SanitizeName($"{dirName}.dll");
        var dllPath = Path.Combine(outputDir, dllName);

        if (File.Exists(dllPath))
        {
            Console.Error.WriteLine($"Cached: {dllName}");
            return 0;
        }

        var alFiles = Directory.GetFiles(srcDir, "*.al", SearchOption.AllDirectories);
        if (alFiles.Length == 0)
        {
            Console.Error.WriteLine($"No .al files found in {srcDir}");
            return 1;
        }

        var alSources = alFiles.Select(File.ReadAllText).ToList();
        Console.Error.WriteLine($"Compiling dependency from directory: {dirName} ({alSources.Count} AL files)");

        foreach (var src in alSources)
        {
            Runtime.EnumRegistry.ParseAndRegister(src);
            Runtime.TableInitValueRegistry.ParseAndRegister(src);
            Runtime.CodeunitNameRegistry.ParseAndRegister(src);
            Runtime.CalcFormulaRegistry.ParseAndRegister(src);
            Runtime.TableFieldRegistry.ParseAndRegister(src);
            Runtime.QueryFieldRegistry.ParseAndRegister(src);
        }

        // Route through ResolvePackagePaths so individual .app files are isolated to a temp
        // directory rather than exposing every other .app in the same .alpackages folder
        // (which would pull in System Application as a binary symbol while we already have
        // its source in the slice — produces AL0275 ambiguous-reference errors).
        var allPackagePaths = AlTranspiler.ResolvePackagePaths(packagePaths, null);
        var alPkg = Path.Combine(srcDir, ".alpackages");
        if (Directory.Exists(alPkg) && !allPackagePaths.Contains(alPkg))
            allPackagePaths.Add(alPkg);

        // Pass file paths so parse-error messages include the source filename.
        var filePaths = alFiles.Select(f => (string?)f).ToList();
        var effectivePackages = allPackagePaths.Count > 0 ? allPackagePaths : null;
        var compilableSources = alSources;
        var compilableFilePaths = filePaths;

        // Use the app's real app.json if present (per-app DLL mode); otherwise synthesize
        // a stub one. Either way we inject contextSensitiveHelpUrl/helpBaseUrl so Microsoft
        // pages with `ContextSensitiveHelpPage` don't fail AL0543. Real identity preserves
        // InternalsVisibleTo grants, which a synthetic identity cannot inherit.
        var sliceManifestDir = Path.Combine(Path.GetTempPath(), $"alrunner-slice-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sliceManifestDir);
        var realManifest = Path.Combine(srcDir, "app.json");
        string manifestJson;
        if (File.Exists(realManifest))
        {
            // Inject the help URLs into the real manifest. We parse and re-emit minimally;
            // the BC compiler tolerates extra properties.
            var raw = File.ReadAllText(realManifest);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var sb = new System.Text.StringBuilder("{");
                bool first = true;
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Name.Equals("contextSensitiveHelpUrl", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Equals("helpBaseUrl", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!first) sb.Append(',');
                    sb.Append(System.Text.Json.JsonSerializer.Serialize(p.Name)).Append(':').Append(p.Value.GetRawText());
                    first = false;
                }
                if (!first) sb.Append(',');
                sb.Append("\"contextSensitiveHelpUrl\":\"https://learn.microsoft.com/en-us/dynamics365/business-central/\",")
                  .Append("\"helpBaseUrl\":\"https://learn.microsoft.com/en-us/dynamics365/business-central/\"}");
                manifestJson = sb.ToString();
            }
            catch
            {
                manifestJson = raw; // fallback: use as-is
            }
        }
        else
        {
            manifestJson = $"{{\"id\":\"{Guid.NewGuid()}\",\"name\":\"{dirName}\",\"publisher\":\"AlRunner\",\"version\":\"1.0.0.0\"," +
                "\"contextSensitiveHelpUrl\":\"https://learn.microsoft.com/en-us/dynamics365/business-central/\"," +
                "\"helpBaseUrl\":\"https://learn.microsoft.com/en-us/dynamics365/business-central/\"}";
        }
        File.WriteAllText(Path.Combine(sliceManifestDir, "app.json"), manifestJson);
        var inputPaths = new List<string> { sliceManifestDir };

        var csharpList = AlTranspiler.TranspileMulti(compilableSources, effectivePackages, inputPaths, sourceFilePaths: compilableFilePaths, extraRefs: extraRefs);

        if ((csharpList == null || csharpList.Count == 0) && compilableSources.Count > 0)
        {
            // Remove files with residual DotNet type references not caught by extract-time stripping.
            // Uses AST-based detection (not regex) to avoid false positives from DotNet appearing
            // in string literals, Obsolete attributes, or codeunit names.
            static bool HasDotNetTypeRef(string src) {
                try {
                    return Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.SyntaxTree
                        .ParseObjectText(src).GetRoot().DescendantNodes()
                        .OfType<Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.SubtypedDataTypeSyntax>()
                        .Any(s => s.TypeName.ToFullString().Trim()
                            .Equals("DotNet", StringComparison.OrdinalIgnoreCase));
                } catch { return false; }
            }
            var pairs = compilableSources.Zip(compilableFilePaths, (s, p) => (s, p)).ToList();
            var cleaned = pairs.Where(x => !HasDotNetTypeRef(x.s)).ToList();
            var dotnetCount = pairs.Count - cleaned.Count;
            if (dotnetCount > 0)
            {
                compilableSources = cleaned.Select(x => x.s).ToList();
                compilableFilePaths = cleaned.Select(x => x.p).ToList();
                Console.Error.WriteLine($"  Excluded {dotnetCount} file(s) with residual DotNet interop (unsupported in runner)");
                csharpList = AlTranspiler.TranspileMulti(compilableSources, effectivePackages, inputPaths, sourceFilePaths: compilableFilePaths, extraRefs: extraRefs);
            }
        }

        if (csharpList == null || csharpList.Count == 0)
        {
            // Re-run capturing stderr so we can surface specific compilation diagnostics
            // (otherErrors, parse errors) that TranspileMulti suppresses by default.
            var savedErr = Console.Error;
            var diagCapture = new System.IO.StringWriter();
            Console.SetError(diagCapture);
            var prevVerbose = Log.Verbose;
            Log.Verbose = true;
            try { AlTranspiler.TranspileMulti(compilableSources, effectivePackages, inputPaths, sourceFilePaths: compilableFilePaths, extraRefs: extraRefs); }
            finally { Console.SetError(savedErr); Log.Verbose = prevVerbose; }
            var diagText = diagCapture.ToString();
            if (!string.IsNullOrWhiteSpace(diagText))
            {
                Console.Error.WriteLine("  Compilation diagnostics:");
                foreach (var line in diagText.Split('\n').Take(40))
                    Console.Error.WriteLine($"    {line.TrimEnd()}");
            }
            Console.Error.WriteLine($"  AL transpilation produced no output");
            return 1;
        }

        var rewrittenTrees = new List<Microsoft.CodeAnalysis.SyntaxTree>();
        foreach (var item in csharpList)
        {
            try
            {
                var tree = RoslynRewriter.RewriteToTree(item.Code);
                if (tree != null) rewrittenTrees.Add(tree);
            }
            catch
            {
                var className = ExtractClassName(item.Name);
                if (className != null)
                {
                    var stubCode = $"namespace Microsoft.Dynamics.Nav.BusinessApplication {{ public class {className} {{ }} }}";
                    rewrittenTrees.Add(CSharpSyntaxTree.ParseText(stubCode));
                }
            }
        }

        var references = RoslynCompiler.LoadReferences();
        foreach (var dll in Directory.GetFiles(outputDir, "*.dll"))
        {
            try { references.Add(MetadataReference.CreateFromFile(dll)); }
            catch { }
        }

        var errors = new List<string>();
        if (!CompileToDisk(rewrittenTrees, references, dllPath, errors))
        {
            Console.Error.WriteLine($"  Compilation failed ({errors.Count} errors):");
            foreach (var err in errors.Take(10))
                Console.Error.WriteLine($"    {err}");
            return 1;
        }

        Console.Error.WriteLine($"  Compiled: {dllName}");
        if (appIdentity != null)
            WriteDepSidecar(dllPath, appIdentity.Publisher, appIdentity.Name, appIdentity.VersionRaw, appIdentity.AppId);
        return 0;
    }

    private static Guid ReadAppGuid(string appPath)
    {
        try
        {
            var manifest = AlTranspiler.LoadNavxManifest(appPath);
            if (manifest != null)
            {
                var ns = System.Xml.Linq.XNamespace.Get("http://schemas.microsoft.com/navx/2015/manifest");
                var app = manifest.Root?.Element(ns + "App") ?? manifest.Root;
                var idStr = app?.Attribute("Id")?.Value;
                if (idStr != null && Guid.TryParse(idStr, out var id))
                    return id;
            }
        }
        catch { }
        return Guid.Empty;
    }

    private static (string Publisher, string Name, string Version) ReadAppIdentity(string appPath)
    {
        try
        {
            var manifest = AlTranspiler.LoadNavxManifest(appPath);
            if (manifest != null)
            {
                var ns = System.Xml.Linq.XNamespace.Get("http://schemas.microsoft.com/navx/2015/manifest");
                // Root is <Package>, <App> is a child element
                var app = manifest.Root?.Element(ns + "App") ?? manifest.Root;
                if (app != null)
                {
                    var publisher = app.Attribute("Publisher")?.Value ?? "Unknown";
                    var name = app.Attribute("Name")?.Value ?? "Unknown";
                    var version = app.Attribute("Version")?.Value ?? "0.0.0.0";
                    return (publisher, name, version);
                }
            }
        }
        catch { }
        return ("Unknown", Path.GetFileNameWithoutExtension(appPath), "0.0.0.0");
    }

    private static string SanitizeName(string name)
    {
        return string.Concat(name.Select(c =>
            char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_'));
    }

    private static string? ExtractClassName(string objectName)
    {
        // Object names are like "MyCodeunit", "Record12345", etc.
        // Clean to valid C# identifier
        var clean = new string(objectName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        return string.IsNullOrEmpty(clean) ? null : clean;
    }

    private record MissingDependency(string Publisher, string Name, string MinVersion, Guid AppId);

    /// <summary>
    /// Read the .app manifest's declared dependencies and check each against available packages.
    /// Returns a list of dependencies that are NOT found in any package directory.
    /// </summary>
    private static List<MissingDependency> ValidateDependencies(string appPath, List<string> packagePaths)
    {
        var missing = new List<MissingDependency>();
        try
        {
            var doc = AlTranspiler.LoadNavxManifest(appPath);
            if (doc == null) return missing;

            XNamespace ns = "http://schemas.microsoft.com/navx/2015/manifest";
            var depsElement = doc.Root?.Element(ns + "Dependencies");
            if (depsElement == null) return missing;

            // Scan all package directories to find available .app files by GUID
            var availableGuids = new HashSet<Guid>();
            foreach (var pkgDir in packagePaths)
            {
                if (!Directory.Exists(pkgDir)) continue;
                var specs = PackageScanner.ScanForSpecs(new[] { pkgDir });
                foreach (var spec in specs)
                    availableGuids.Add(spec.AppId);
            }

            // Check each declared dependency
            foreach (var dep in depsElement.Elements(ns + "Dependency"))
            {
                var idStr = dep.Attribute("Id")?.Value;
                if (idStr == null || !Guid.TryParse(idStr, out var depGuid)) continue;

                // Platform and Application dependencies are resolved differently (by version, not GUID)
                // — they're always available via the BC DLLs. Skip them.
                var depPublisher = dep.Attribute("Publisher")?.Value ?? "Unknown";
                var depName = dep.Attribute("Name")?.Value ?? "Unknown";
                if (depPublisher == "Microsoft" && depName is "System" or "Application")
                    continue;

                if (!availableGuids.Contains(depGuid))
                {
                    var depVersion = dep.Attribute("MinVersion")?.Value ?? dep.Attribute("Version")?.Value ?? "0.0.0.0";
                    missing.Add(new MissingDependency(depPublisher, depName, depVersion, depGuid));
                }
            }
        }
        catch { }
        return missing;
    }
}
