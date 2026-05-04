using System.Reflection;
using System.Security.Cryptography;
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

    /// <summary>
    /// Re-emit an app.json string, replacing unparseable version-shaped values with
    /// concrete defaults so downstream consumers (the BC compiler's NavAppManifest reader,
    /// our own <see cref="AlTranspiler.ExtractAppIdentity"/>-style readers) do not fail.
    /// Microsoft's BC source repos ship app.json files with build-time placeholders such
    /// as <c>$(app_currentVersion)</c>, <c>$(app_platformVersion)</c>, and
    /// <c>$(app_minimumVersion)</c> in the per-dependency <c>version</c> fields. When any
    /// of these reach the compiler unsubstituted, BC's manifest reader throws and the
    /// compilation falls back to a phantom "(Unknown)" extension that owns every
    /// namespace declared by the slice — producing AL0275 ambiguous-reference errors.
    ///
    /// Top-level <c>version</c> placeholder is substituted with <c>1.0.0.0</c> (the
    /// identity passed to <see cref="Microsoft.Dynamics.Nav.CodeAnalysis.Compilation.Create"/>
    /// is supplied separately, so the slice's own version is informational only).
    /// Top-level <c>platform</c>, <c>application</c>, and <c>dependencies</c> fields are
    /// DROPPED entirely if any placeholder is present — this lets the runner's "no
    /// explicit dependencies found, loading all .app packages as symbols" fallback
    /// discover concrete versions from the package directory rather than feeding the BC
    /// reference resolver synthetic versions that may not match any real binary.
    /// </summary>
    internal static string SanitizeManifestVersions(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return json;

        bool IsUnparseable(JsonElement el)
            => el.ValueKind == JsonValueKind.String
               && !Version.TryParse(el.GetString() ?? string.Empty, out _);

        bool dropPlatform = root.TryGetProperty("platform", out var pl) && IsUnparseable(pl);
        bool dropApplication = root.TryGetProperty("application", out var ap) && IsUnparseable(ap);
        bool dropDependencies = false;
        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
        {
            foreach (var dep in deps.EnumerateArray())
            {
                if (dep.ValueKind != JsonValueKind.Object) continue;
                if (dep.TryGetProperty("version", out var dv) && IsUnparseable(dv))
                { dropDependencies = true; break; }
            }
        }

        var sb = new System.Text.StringBuilder("{");
        bool first = true;
        foreach (var p in root.EnumerateObject())
        {
            if (dropPlatform && p.Name.Equals("platform", StringComparison.OrdinalIgnoreCase)) continue;
            if (dropApplication && p.Name.Equals("application", StringComparison.OrdinalIgnoreCase)) continue;
            if (dropDependencies && p.Name.Equals("dependencies", StringComparison.OrdinalIgnoreCase)) continue;

            if (!first) sb.Append(',');
            first = false;
            sb.Append(JsonSerializer.Serialize(p.Name)).Append(':');

            if (p.Name.Equals("version", StringComparison.OrdinalIgnoreCase) && IsUnparseable(p.Value))
                sb.Append(JsonSerializer.Serialize("1.0.0.0"));
            else
                sb.Append(p.Value.GetRawText());
        }
        sb.Append('}');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ //
    // SHA-256 cache freshness helpers (issue #1517)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Compute a stable SHA-256 hash over the content of a single file.
    /// Returns a lowercase 64-character hex string.
    /// </summary>
    internal static string ComputeFileHash(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = sha.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Compute a stable SHA-256 hash over all files inside <paramref name="dir"/>
    /// (sorted by relative path for determinism). Returns a lowercase 64-character hex string.
    /// </summary>
    internal static string ComputeDirectoryHash(string dir)
    {
        using var sha = SHA256.Create();
        // Sort by relative path for deterministic ordering across file systems
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => (RelPath: f.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), FullPath: f))
            .OrderBy(x => x.RelPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Feed each file's relative path + content into the running hash so that
        // renames alone (same bytes, different name) also change the hash.
        var buffer = new byte[81920];
        foreach (var (relPath, fullPath) in files)
        {
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(relPath + "\n");
            sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);

            using var fs = File.OpenRead(fullPath);
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>
    /// Path of the .sha256 sidecar for a given DLL path.
    /// </summary>
    private static string HashSidecarPath(string dllPath) => Path.ChangeExtension(dllPath, ".sha256");

    /// <summary>
    /// Returns <c>true</c> when <paramref name="dllPath"/> exists AND the stored hash
    /// matches <paramref name="currentHash"/> — the cache is fresh and compilation can
    /// be skipped. Logs the outcome to stderr.
    /// </summary>
    private static bool IsCacheFresh(string dllPath, string currentHash, string label)
    {
        if (!File.Exists(dllPath))
            return false;

        var sidecarPath = HashSidecarPath(dllPath);
        if (!File.Exists(sidecarPath))
        {
            // DLL exists but no hash file — treat as stale (pre-#1517 build)
            Console.Error.WriteLine($"Stale cache (missing .sha256): {Path.GetFileName(dllPath)} — recompiling");
            try { File.Delete(dllPath); } catch { }
            return false;
        }

        var storedHash = File.ReadAllText(sidecarPath).Trim();
        if (string.Equals(storedHash, currentHash, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Cached: {label} (hash match)");
            return true;
        }

        Console.Error.WriteLine($"Stale cache (hash changed): {Path.GetFileName(dllPath)} — recompiling");
        try { File.Delete(dllPath); } catch { }
        try { File.Delete(sidecarPath); } catch { }
        return false;
    }

    /// <summary>
    /// Write the hash sidecar next to the DLL after a successful compilation.
    /// </summary>
    private static void WriteHashSidecar(string dllPath, string hash)
    {
        var sidecarPath = HashSidecarPath(dllPath);
        try
        {
            File.WriteAllText(sidecarPath, hash);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write .sha256 sidecar {sidecarPath}: {ex.Message}");
        }
    }

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
    public static int CompileDep(string appPath, string outputDir, List<string> packagePaths,
        IEnumerable<string>? extraDefines = null)
    {
        // If appPath is a directory, compile AL source files directly
        if (Directory.Exists(appPath))
        {
            var appJsonPath = Path.Combine(appPath, "app.json");
            return File.Exists(appJsonPath)
                ? CompileDepFromDir(appPath, outputDir, packagePaths, extraDefines: extraDefines)
                : CompileDepMultiApp(appPath, outputDir, packagePaths, extraDefines: extraDefines);
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

        // 2. Cache check — skip if DLL exists AND the .app content hash matches.
        // A stale DLL (same version string, changed .app bytes) is detected and evicted here.
        var appHash = ComputeFileHash(appPath);
        if (IsCacheFresh(dllPath, appHash, dllName))
            return 0;

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
        var csharpList = AlTranspiler.TranspileMulti(compilableSources, effectivePackages, inputPaths, extraDefines: extraDefines);

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
                csharpList = AlTranspiler.TranspileMulti(compilableSources, effectivePackages, inputPaths, extraDefines: extraDefines);
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
            try { AlTranspiler.TranspileMulti(compilableSources, effectivePackages, inputPaths, extraDefines: extraDefines); }
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
            Console.Error.WriteLine(
                $"  [CompileDepMultiApp-NoOutput] (single-app) " +
                $"compilable={compilableSources.Count}/{alSources.Count} " +
                $"effectivePackages={(effectivePackages?.Count ?? 0)} " +
                $"name={name}");
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
        WriteHashSidecar(dllPath, appHash);
        return 0;
    }

    /// <summary>
    /// Compile multiple app directories (each containing app.json) under a root directory.
    /// </summary>
    public static int CompileDepMultiApp(string rootDir, string outputDir, List<string> packagePaths,
        IEnumerable<string>? extraDefines = null)
    {
        if (!Directory.Exists(rootDir))
        {
            Console.Error.WriteLine($"Error: directory not found: {rootDir}");
            return 1;
        }

        // Multi-app: top-level slice has subdirectories with app.json files. Delegate to
        // the private overload which adds topological sorting + in-memory ModuleInfo
        // chaining so apps see prior siblings' compiled symbols (issue #1521).
        var directChildAppDirs = Directory.GetDirectories(rootDir)
            .Where(d => File.Exists(Path.Combine(d, "app.json")))
            .ToList();
        if (directChildAppDirs.Count > 0)
            return CompileDepMultiApp(rootDir, directChildAppDirs, outputDir, packagePaths, extraDefines: extraDefines);

        // Fallback: single app at root, or no app.jsons anywhere.
        return CompileDepFromDir(rootDir, outputDir, packagePaths, extraDefines: extraDefines);
    }

    /// <summary>
    /// Compile syntax trees to a DLL file on disk (instead of in-memory like CompileFromTrees).
    ///
    /// When Roslyn reports CS0131 ("The left-hand side of an assignment must be a variable,
    /// property or indexer") errors, those are caused by BadExpression nodes that BC's C# emitter
    /// wrote for unresolvable codeunit references in [EventSubscriber] attributes (issue #1596).
    /// The fix: strip the containing method declarations from their syntax trees and retry once.
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

            // Issue #1596: CS0131 from BadExpression nodes injected by BC's
            // EventSubscriberAttributeEmitter when the target codeunit is not in the
            // slice.  Strip the offending method bodies and retry — these subscribers
            // will never fire at runtime anyway (their target object is absent).
            var cs0131Errors = errors.Where(d => d.Id == "CS0131").ToList();
            if (cs0131Errors.Count > 0)
            {
                var strippedTrees = StripMethodsWithCS0131(syntaxTrees, cs0131Errors);
                if (strippedTrees != null)
                {
                    Console.Error.WriteLine(
                        $"  Warning: {cs0131Errors.Count} CS0131 error(s) from BadExpression event-subscriber attribute(s) — " +
                        $"offending subscriber method(s) dropped and DLL retried (issue #1596).");

                    // Remove the non-CS0131 errors and retry with cleaned trees.
                    var nonCS0131Errors = errors.Where(d => d.Id != "CS0131").ToList();
                    if (nonCS0131Errors.Count == 0)
                    {
                        // Only CS0131 errors — retry with stripped trees
                        var retryCompilation = CSharpCompilation.Create(
                            Path.GetFileNameWithoutExtension(outputPath),
                            strippedTrees,
                            references,
                            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                                .WithAllowUnsafe(true));
                        var retryResult = retryCompilation.Emit(outputPath);
                        if (retryResult.Success)
                            return true;
                        // Retry failed — fall through to report the retry errors
                        errors = retryResult.Diagnostics
                            .Where(d => d.Severity == DiagnosticSeverity.Error)
                            .ToList();
                    }
                    else
                    {
                        // Mixed errors — CS0131 are likely from bad subscribers but there
                        // are other genuine errors too. Report the non-CS0131 ones.
                        errors = nonCS0131Errors;
                    }
                }
            }

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
    /// Given a list of CS0131 Roslyn diagnostics, determine whether any of them originate
    /// from BC's EventSubscriberAttributeEmitter — identified by being in an
    /// <see cref="Microsoft.CodeAnalysis.CSharp.Syntax.AttributeArgumentSyntax"/> node
    /// rather than in a method body statement.
    ///
    /// For those, identify the containing method declarations, replace each method body
    /// with an empty stub (so the class still compiles), and return a new list of patched
    /// trees.  Returns null if no attribute-level CS0131 errors were found.
    ///
    /// Genuine CS0131 errors (assigning to a non-lvalue in a method body) are left in the
    /// error list and cause the compilation to fail as expected.
    /// </summary>
    private static List<SyntaxTree>? StripMethodsWithCS0131(
        List<SyntaxTree> syntaxTrees,
        List<Diagnostic> cs0131Errors)
    {
        // Filter: only process CS0131 errors that are inside attribute argument lists.
        // BC's EventSubscriberAttributeEmitter generates BadExpression nodes as attribute
        // arguments; genuine CS0131 errors occur in method bodies (statement expressions).
        var attributeCS0131 = cs0131Errors.Where(d =>
        {
            var tree = d.Location.SourceTree;
            if (tree == null) return false;
            var span = d.Location.SourceSpan;
            var node = tree.GetRoot().FindNode(span, getInnermostNodeForTie: true);
            // Walk ancestors: if we reach an AttributeArgumentSyntax before a
            // StatementSyntax, this is an attribute-level error (BC-generated).
            while (node != null)
            {
                if (node is Microsoft.CodeAnalysis.CSharp.Syntax.AttributeArgumentSyntax)
                    return true;
                if (node is Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax)
                    return false;
                node = node.Parent;
            }
            return false;
        }).ToList();

        if (attributeCS0131.Count == 0) return null;

        // Group by containing syntax tree
        var errorsByTree = new Dictionary<SyntaxTree, List<Diagnostic>>(ReferenceEqualityComparer.Instance);
        foreach (var d in attributeCS0131)
        {
            var tree = d.Location.SourceTree!;
            if (!errorsByTree.TryGetValue(tree, out var list))
                errorsByTree[tree] = list = new List<Diagnostic>();
            list.Add(d);
        }

        // Patch each affected tree by removing the offending methods (those whose
        // attribute lists contain the CS0131 errors)
        var patchedTrees = new List<SyntaxTree>(syntaxTrees.Count);
        bool anyPatched = false;
        foreach (var tree in syntaxTrees)
        {
            if (!errorsByTree.TryGetValue(tree, out var treeDiags))
            {
                patchedTrees.Add(tree);
                continue;
            }

            // Collect the text spans of the error locations
            var errorSpanStarts = new HashSet<int>(treeDiags.Select(d => d.Location.SourceSpan.Start));
            var root = tree.GetRoot();

            // Find method declarations that contain one of the erroneous attribute arguments
            var methodsToStrip = root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                .Where(m => m.AttributeLists.Count > 0 &&
                    errorSpanStarts.Any(pos =>
                        pos >= m.FullSpan.Start && pos <= m.FullSpan.End))
                .ToList();

            if (methodsToStrip.Count == 0)
            {
                patchedTrees.Add(tree);
                continue;
            }

            // Replace each offending method's attribute list and body with empty stubs
            var newRoot = root.ReplaceNodes(
                methodsToStrip,
                (orig, _) => orig
                    .WithBody(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block())
                    .WithAttributeLists(
                        Microsoft.CodeAnalysis.CSharp.SyntaxFactory.List<
                            Microsoft.CodeAnalysis.CSharp.Syntax.AttributeListSyntax>()));

            patchedTrees.Add(tree.WithRootAndOptions(newRoot, tree.Options));
            anyPatched = true;
        }

        return anyPatched ? patchedTrees : null;
    }

    /// <summary>
    /// Compile a directory of AL source files to a dependency .dll.
    /// </summary>
    private record AppInfo(string Dir, Guid Id, string Name, string Publisher, string Version,
        List<Guid> DepIds, List<DepsSidecarWriter.DepEntry> DepSpecs);

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
    private static int CompileDepMultiApp(string srcDir, List<string> appDirs, string outputDir, List<string> packagePaths,
        IEnumerable<string>? extraDefines = null)
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
                var depSpecs = new List<DepsSidecarWriter.DepEntry>();

                // Platform reference: `platform: "27.0.0.0"` in app.json corresponds to the
                // Microsoft "System" platform package. Its AppId is well-known and stable
                // across BC versions (8874ed3a-…). Including it here lets the JSON loader
                // advertise the system→app dependency edge that ReferenceManager needs to
                // resolve cross-app type references (issue #1546).
                if (root.TryGetProperty("platform", out var platformProp))
                {
                    if (Version.TryParse(platformProp.GetString() ?? "", out var pv))
                        depSpecs.Add(new DepsSidecarWriter.DepEntry(
                            "Microsoft", "System", pv,
                            Guid.Parse("8874ed3a-0643-4247-9ced-7a7002f7135d")));
                }

                if (root.TryGetProperty("dependencies", out var depArr) && depArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var d in depArr.EnumerateArray())
                    {
                        Guid did = Guid.Empty;
                        if ((d.TryGetProperty("id", out var didProp) || d.TryGetProperty("appId", out didProp)) &&
                            Guid.TryParse(didProp.GetString(), out var parsedDid))
                        {
                            did = parsedDid;
                            deps.Add(parsedDid);
                        }
                        var dPub = d.TryGetProperty("publisher", out var dp) ? dp.GetString() ?? "" : "";
                        var dName = d.TryGetProperty("name", out var dn) ? dn.GetString() ?? "" : "";
                        var dVerText = d.TryGetProperty("version", out var dv) ? dv.GetString() ?? "0.0.0.0" : "0.0.0.0";
                        if (!Version.TryParse(dVerText, out var dVer)) dVer = new Version(0, 0, 0, 0);
                        if (!string.IsNullOrEmpty(dName))
                            depSpecs.Add(new DepsSidecarWriter.DepEntry(dPub, dName, dVer, did));
                    }
                Version verParsed = Version.TryParse(version, out var vp) ? vp : new Version(1, 0, 0, 0);
                apps.Add(new AppInfo(dir, id, name, publisher, version, deps, depSpecs));
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
            // Build the in-memory refs list for this app: ModuleInfo of every previously
            // compiled app in the slice — declared dep or not. Microsoft's real BC source
            // frequently references objects across apps that the consumer's app.json
            // does not declare (e.g. Tests-TestLibraries references Base Application
            // pages without a declared Base App dep). Real BC resolves these via
            // platform-wide symbol leakage; mirror that by exposing every prior compile's
            // module to the current one. Issue #1551.
            var refs = CompiledAppRefs.Values.ToList();
            if (refs.Count > 0)
                Console.Error.WriteLine($"  Chaining {refs.Count} prior ModuleInfo(s) into compile.");
            var rc = CompileDepFromDir(app.Dir, outputDir, accumPackages, appIdentity: null, extraRefs: refs, extraDefines: extraDefines);

            // Issue #1554: even when compile fails (rc != 0), AlTranspiler.LastCompilation
            // is set before the BC emit call.  The semantic model holds all type declarations
            // even though some (or all) methods failed to emit — NavTypeKind.None,
            // BadExpression in EventSubscriberAttributeEmitter, etc. cause zero captured
            // objects but do not invalidate the compilation's symbol table.
            //
            // Writing symbols.json + adding to CompiledAppRefs for the failed app allows
            // downstream apps in the same slice to resolve cross-app type references even
            // when the upstream DLL could not be produced.  This unblocks the
            // BF → Base App → Tests-TestLibraries cascade that stalled at 3/7 apps.
            var compFor = AlTranspiler.LastCompilation;
            if (rc != 0)
            {
                failed.Add(app.Name);
                Console.Error.WriteLine($"WARN: compile failed for {app.Name}; continuing with other apps");

                // Try to capture partial symbol information for downstream chaining.
                if (compFor != null)
                {
                    var moduleRef = TryGetModuleRef(compFor);
                    if (moduleRef != null)
                        CompiledAppRefs[app.Id] = moduleRef;

                    try
                    {
                        var jsonName = SanitizeName($"{app.Publisher}_{app.Name}_{app.Version}.symbols.json");
                        var jsonPath = Path.Combine(outputDir, jsonName);
                        using (var fs = File.Create(jsonPath))
                        {
                            SymbolJsonWriter.WriteSymbolJson(compFor, fs);
                            Console.Error.WriteLine($"  Partial symbols: {jsonName} ({fs.Length} bytes) — from failed compile");
                        }

                        // Sidecar deps so downstream JsonSymbolReferenceLoader can resolve identity.
                        var depsName = SanitizeName($"{app.Publisher}_{app.Name}_{app.Version}.symbols.deps.json");
                        var depsPath = Path.Combine(outputDir, depsName);
                        var appVer = Version.TryParse(app.Version, out var av2) ? av2 : new Version(1, 0, 0, 0);
                        DepsSidecarWriter.Write(depsPath, app.Publisher, app.Name, appVer, app.Id, app.DepSpecs);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  WARN: partial symbols.json emission failed for {app.Name}: {ex.Message}");
                    }
                }
            }
            else
            {
                // Capture the compiled app's module symbol for downstream chaining via the
                // in-memory ISymbolReferenceLoader path.
                var moduleRef = compFor != null ? TryGetModuleRef(compFor) : null;
                if (moduleRef != null)
                    CompiledAppRefs[app.Id] = moduleRef;

                // Emit a <App>.symbols.json next to the .dll. This is a JSON-only symbol
                // artifact (NOT a .app — no AL bytecode, cannot be deployed to a BC instance).
                // Subsequent compiles in the multi-app loop, and any future test-run that
                // recompiles user AL against this slice, resolve cross-app references via
                // the JsonSymbolReferenceLoader in TranspileMulti.
                if (compFor != null)
                {
                    try
                    {
                        var jsonName = SanitizeName($"{app.Publisher}_{app.Name}_{app.Version}.symbols.json");
                        var jsonPath = Path.Combine(outputDir, jsonName);
                        using (var fs = File.Create(jsonPath))
                        {
                            SymbolJsonWriter.WriteSymbolJson(compFor, fs);
                            Console.Error.WriteLine($"  Wrote symbols: {jsonName} ({fs.Length} bytes)");
                        }

                        // Sidecar deps file — see DepsSidecarWriter / issue #1546.
                        var depsName = SanitizeName($"{app.Publisher}_{app.Name}_{app.Version}.symbols.deps.json");
                        var depsPath = Path.Combine(outputDir, depsName);
                        var appVer = Version.TryParse(app.Version, out var av) ? av : new Version(1, 0, 0, 0);
                        DepsSidecarWriter.Write(depsPath, app.Publisher, app.Name, appVer, app.Id, app.DepSpecs);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  WARN: symbols.json emission failed for {app.Name}: {ex.Message}");
                    }
                }
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

    private static int CompileDepFromDir(string srcDir, string outputDir, List<string> packagePaths, AppJsonIdentity? appIdentity = null, List<BcSrs>? extraRefs = null, IEnumerable<string>? extraDefines = null)
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
            return CompileDepMultiApp(srcDir, appDirs, outputDir, packagePaths, extraDefines: extraDefines);

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

        // Cache check — skip if DLL exists AND directory content hash matches.
        // Detects source changes even when the version string is not bumped (issue #1517).
        var dirHash = ComputeDirectoryHash(srcDir);
        if (IsCacheFresh(dllPath, dirHash, dllName))
            return 0;

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

        // Build effective extra defines (issue #1525, #1587):
        // 1. CLI-supplied defines (extraDefines parameter).
        // 2. preprocessorSymbols from app.json in srcDir (preferred — Microsoft puts CLEANSCHEMA here).
        // 3. CLEANSCHEMA1..N from app.json "application" BC version (issue #1587 fallback table).
        //    CLEANSCHEMA-N is active for BC versions > N (the cleanup happened in version N).
        //    So for BC 27: CLEANSCHEMA1..26 are active; CLEANSCHEMA27 is in-development.
        // 4. CLEANSCHEMA1..N from app.json "runtime" version (legacy fallback, kept for compat).
        var allExtraDefines = new List<string>();
        if (extraDefines != null)
            allExtraDefines.AddRange(extraDefines);
        var appJsonDefines = AlTranspiler.ReadPreprocessorSymbolsFromAppJson(srcDir);
        foreach (var s in appJsonDefines)
            if (!allExtraDefines.Contains(s, StringComparer.OrdinalIgnoreCase))
                allExtraDefines.Add(s);
        // Auto-define CLEANSCHEMA1..N based on app.json "application" BC major version (issue #1587).
        // This is the primary fallback: "application" maps directly to the BC product version.
        var appJsonForCS = Path.Combine(srcDir, "app.json");
        if (File.Exists(appJsonForCS))
        {
            try
            {
                using var aDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appJsonForCS));
                // Try "application" field first (BC product version, e.g. "27.0.0.0").
                int csMax = 0;
                if (aDoc.RootElement.TryGetProperty("application", out var appProp)
                    && Version.TryParse(appProp.GetString() ?? "", out var appVer)
                    && appVer.Major > 0)
                {
                    csMax = AlTranspiler.GetCleanSchemaDefaultMaxForBCVersion(appVer.Major);
                }
                // Fall back to "runtime" major version for apps that only declare runtime
                // (legacy behaviour preserved for apps without "application" field).
                if (csMax <= 0 && aDoc.RootElement.TryGetProperty("runtime", out var rProp)
                    && Version.TryParse(rProp.GetString() ?? "", out var rVer))
                {
                    csMax = rVer.Major;
                }
                if (csMax > 0)
                {
                    foreach (var cs in Enumerable.Range(1, csMax).Select(n => $"CLEANSCHEMA{n}"))
                        if (!allExtraDefines.Contains(cs, StringComparer.OrdinalIgnoreCase))
                            allExtraDefines.Add(cs);
                }
            }
            catch { /* ignore malformed app.json */ }
        }
        IEnumerable<string>? effectiveExtraDefines = allExtraDefines.Count > 0 ? allExtraDefines : null;

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
        // Sanitize version-shaped fields so BC's NavAppManifest reader does not throw on
        // unsubstituted build placeholders ($(app_currentVersion), $(app_platformVersion),
        // $(app_minimumVersion)) shipped in Microsoft's BC source repos. Without this the
        // compiler falls back to a phantom "(Unknown)" extension that owns every namespace
        // declared by the slice — producing AL0275 ambiguous-reference errors.
        try { manifestJson = SanitizeManifestVersions(manifestJson); }
        catch { /* leave manifestJson unchanged on any malformed JSON */ }
        File.WriteAllText(Path.Combine(sliceManifestDir, "app.json"), manifestJson);
        var inputPaths = new List<string> { sliceManifestDir };

        var csharpList = AlTranspiler.TranspileMulti(compilableSources, effectivePackages, inputPaths, sourceFilePaths: compilableFilePaths, extraRefs: extraRefs, extraDefines: effectiveExtraDefines);

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
                csharpList = AlTranspiler.TranspileMulti(compilableSources, effectivePackages, inputPaths, sourceFilePaths: compilableFilePaths, extraRefs: extraRefs, extraDefines: effectiveExtraDefines);
            }
        }

        // Phase 3 (issue #1596): event subscriber targeting codeunit outside slice.
        //
        // When an [EventSubscriber] attribute references a codeunit whose name is not
        // in the compiled slice or packages, BC's C# emitter throws
        // "Unexpected value 'BadExpression'" during MethodCodeGenerator.EmitMethod.
        // This exception is caught in TranspileMulti (issue #1554 handler), but because
        // the exception aborts before any C# is captured, TranspileMulti returns null.
        //
        // Fix: detect AL0118 ("The name '...' does not exist") on the codeunit
        // reference inside an EventSubscriber attribute.  Strip the AL files that
        // contain those bad subscribers and retry transpilation.  The stripped
        // subscriber is logged as a warning — it will never fire at runtime anyway
        // (its target object doesn't exist in this build).
        if ((csharpList == null || csharpList.Count == 0) && compilableSources.Count > 0)
        {
            var badSubscriberFiles = FindBadEventSubscriberFiles(compilableSources, compilableFilePaths);
            if (badSubscriberFiles.Count > 0)
            {
                var badSet = new HashSet<int>(badSubscriberFiles.Select(x => x.Index));
                var retained = compilableSources
                    .Zip(compilableFilePaths, (s, p) => (s, p))
                    .Select((pair, i) => (pair.s, pair.p, i))
                    .Where(x => !badSet.Contains(x.i))
                    .ToList();

                Console.Error.WriteLine($"  Warning: {badSubscriberFiles.Count} AL file(s) contain [EventSubscriber] targeting a codeunit not in the slice — subscribers dropped (CS0131 guard, issue #1596):");
                foreach (var f in badSubscriberFiles)
                {
                    var label = f.FilePath != null ? Path.GetFileName(f.FilePath) : $"source[{f.Index}]";
                    Console.Error.WriteLine($"    {label}: {f.SubscriberName}");
                }

                compilableSources = retained.Select(x => x.s).ToList();
                compilableFilePaths = retained.Select(x => x.p).ToList();
                csharpList = AlTranspiler.TranspileMulti(compilableSources, effectivePackages, inputPaths, sourceFilePaths: compilableFilePaths, extraRefs: extraRefs, extraDefines: effectiveExtraDefines);
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
            try { AlTranspiler.TranspileMulti(compilableSources, effectivePackages, inputPaths, sourceFilePaths: compilableFilePaths, extraRefs: extraRefs, extraDefines: effectiveExtraDefines); }
            finally { Console.SetError(savedErr); Log.Verbose = prevVerbose; }
            var diagText = diagCapture.ToString();
            if (!string.IsNullOrWhiteSpace(diagText))
            {
                Console.Error.WriteLine("  Compilation diagnostics:");
                foreach (var line in diagText.Split('\n').Take(40))
                    Console.Error.WriteLine($"    {line.TrimEnd()}");
            }
            Console.Error.WriteLine(
                $"  [CompileDepMultiApp-NoOutput] (multi-app) " +
                $"compilable={compilableSources.Count}/{alSources.Count} " +
                $"effectivePackages={(effectivePackages?.Count ?? 0)} " +
                $"extraRefs={(extraRefs?.Count ?? 0)} " +
                $"dir={dirName}");
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
        WriteHashSidecar(dllPath, dirHash);
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

    /// <summary>
    /// Result record returned by <see cref="FindBadEventSubscriberFiles"/>.
    /// </summary>
    internal record BadSubscriberFile(int Index, string? FilePath, string SubscriberName);

    /// <summary>
    /// Detect AL source files that contain [EventSubscriber] attributes targeting
    /// codeunits that are not in the compiled slice (issue #1596).
    ///
    /// Strategy: after a failed TranspileMulti, query
    /// <see cref="AlTranspiler.LastCompilation"/> for AL0118 ("does not exist in
    /// the current context") declaration errors.  For each such error, extract
    /// the unresolved name and find which source files contain an [EventSubscriber]
    /// attribute that references that name.  Those files are the ones to drop and
    /// retry without.
    ///
    /// The check uses text matching (not full AST analysis) to keep it fast and
    /// side-effect free.
    /// </summary>
    internal static List<BadSubscriberFile> FindBadEventSubscriberFiles(
        List<string> sources,
        List<string?> filePaths)
    {
        var result = new List<BadSubscriberFile>();

        var lastComp = AlTranspiler.LastCompilation;
        if (lastComp == null) return result;

        // Collect unresolved names from AL0118 diagnostics.
        // Message format: The name '"Some Name"' does not exist in the current context.
        // Note: d.Severity is Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics.DiagnosticSeverity
        // (BC type), which differs from the Roslyn DiagnosticSeverity imported at top of file.
        // Use ToString() comparison to avoid namespace ambiguity.
        var al0118 = lastComp.GetDeclarationDiagnostics()
            .Where(d => d.Severity.ToString() == "Error" && d.Id == "AL0118")
            .ToList();

        if (al0118.Count == 0) return result;

        // Regex: extract the quoted name from the AL0118 message.
        // Typical message: The name '"License Management Starter"' does not exist in the current context.
        var nameRx = new System.Text.RegularExpressions.Regex(
            @"The name '""([^""]+)""' does not exist",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var unresolvedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in al0118)
        {
            var m = nameRx.Match(d.GetMessage());
            if (m.Success)
                unresolvedNames.Add(m.Groups[1].Value);
        }

        if (unresolvedNames.Count == 0) return result;

        // For each source, check if it contains an [EventSubscriber] attribute that
        // mentions one of the unresolved names.
        // AL syntax: [EventSubscriber(ObjectType::Codeunit, Codeunit::"SomeName", ...)]
        // We do a simple text search for the codeunit name next to EventSubscriber.
        for (int i = 0; i < sources.Count; i++)
        {
            var src = sources[i];
            if (!src.Contains("EventSubscriber", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var name in unresolvedNames)
            {
                // Check the combination of EventSubscriber and the unresolved name appearing
                // in the same source (exact match of the name string)
                if (src.Contains($"\"{name}\"", StringComparison.OrdinalIgnoreCase))
                {
                    var fp = i < filePaths.Count ? filePaths[i] : null;
                    result.Add(new BadSubscriberFile(i, fp, name));
                    break; // one match per file is enough
                }
            }
        }

        return result;
    }
}
