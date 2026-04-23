using System.Reflection;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AlRunner;

/// <summary>
/// Compiles a dependency .app package through the AL→C#→rewrite→Roslyn pipeline
/// and produces a .dll on disk. The resulting DLL uses the same mock types
/// (AlScope, MockRecordHandle, etc.) as the main test assembly, enabling
/// cross-assembly codeunit dispatch via MockCodeunitHandle.
/// </summary>
public static class DepCompiler
{
    /// <summary>
    /// Compile a single .app dependency (or directory of AL source) to a .dll in <paramref name="outputDir"/>.
    /// Returns 0 on success, 1 on failure.
    /// </summary>
    public static int CompileDep(string appPath, string outputDir, List<string> packagePaths)
    {
        // If appPath is a directory, compile AL source files directly
        if (Directory.Exists(appPath))
            return CompileDepFromDir(appPath, outputDir, packagePaths);

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
                System.Text.RegularExpressions.RegexOptions.Compiled);
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
        return 0;
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
            // Clean up partial output
            try { File.Delete(outputPath); } catch { }
            return false;
        }

        return true;
    }

    /// <summary>
    /// Compile a directory of AL source files to a dependency .dll.
    /// </summary>
    private static int CompileDepFromDir(string srcDir, string outputDir, List<string> packagePaths)
    {
        Directory.CreateDirectory(outputDir);
        var dirName = Path.GetFileName(srcDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var dllName = SanitizeName($"{dirName}.dll");
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

        var allPackagePaths = new List<string>(packagePaths);
        var alPkg = Path.Combine(srcDir, ".alpackages");
        if (Directory.Exists(alPkg) && !allPackagePaths.Contains(alPkg))
            allPackagePaths.Add(alPkg);

        var csharpList = AlTranspiler.TranspileMulti(alSources, allPackagePaths.Count > 0 ? allPackagePaths : null, null);
        if (csharpList == null || csharpList.Count == 0)
        {
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
