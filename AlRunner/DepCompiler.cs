using System.Reflection;
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

        // 3. Extract AL sources from .app
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

        // 6. Transpile AL → C#
        var csharpList = AlTranspiler.TranspileMulti(alSources, allPackagePaths.Count > 0 ? allPackagePaths : null, null);
        if (csharpList == null || csharpList.Count == 0)
        {
            Console.Error.WriteLine($"  AL transpilation produced no output for {name}");
            return 1;
        }
        Console.Error.WriteLine($"  Transpiled {csharpList.Count} C# objects");

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
}
