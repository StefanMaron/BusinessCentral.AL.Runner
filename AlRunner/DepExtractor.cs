using System.IO.Compression;
using System.Text;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner;

/// <summary>
/// Implements <c>al-runner extract-deps</c>: walks an AL extension's source, identifies all
/// external object references, extracts the minimal reachable slice of those objects from
/// .app artifact files, and writes the extracted AL source to an output directory.
/// </summary>
public static class DepExtractor
{
    // -----------------------------------------------------------------------
    // Public entry point
    // -----------------------------------------------------------------------

    /// <summary>
    /// Main entry point for --extract-deps. Returns 0 on success, 1 on error.
    /// </summary>
    /// <param name="extensionSrcDir">Directory containing the extension's AL source files.</param>
    /// <param name="appPaths">One or more .app artifact paths to search for dependency objects.</param>
    /// <param name="outputDir">Directory to write extracted AL files.</param>
    public static int ExtractDeps(string extensionSrcDir, IEnumerable<string> appPaths, string outputDir)
    {
        if (!Directory.Exists(extensionSrcDir))
        {
            Console.Error.WriteLine($"Error: extension source directory not found: {extensionSrcDir}");
            return 1;
        }

        var appPathList = appPaths.ToList();
        var missingApps = appPathList.Where(p => !File.Exists(p)).ToList();
        if (missingApps.Count > 0)
        {
            foreach (var p in missingApps)
                Console.Error.WriteLine($"Error: .app file not found: {p}");
            return 1;
        }

        Directory.CreateDirectory(outputDir);

        // 1. Parse extension source and collect all external references
        var alFiles = Directory.GetFiles(extensionSrcDir, "*.al", SearchOption.AllDirectories);
        if (alFiles.Length == 0)
        {
            Console.Error.WriteLine($"Warning: no .al files found in {extensionSrcDir}");
        }

        var sources = alFiles.Select(File.ReadAllText).ToList();
        Console.Error.WriteLine($"Scanning {alFiles.Length} AL source file(s) in {extensionSrcDir}");

        var refs = CollectExternalReferences(sources);
        Console.Error.WriteLine($"Found external references: {refs.Tables.Count} table(s), {refs.Codeunits.Count} codeunit(s)");
        if (refs.Tables.Count > 0)
            Console.Error.WriteLine($"  Tables: {string.Join(", ", refs.Tables.OrderBy(t => t))}");
        if (refs.Codeunits.Count > 0)
            Console.Error.WriteLine($"  Codeunits: {string.Join(", ", refs.Codeunits.OrderBy(c => c))}");

        // 2. For each .app, extract all AL sources and find matching objects.
        //    We do this iteratively to find transitive dependencies.
        var allExtracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // fileName → source

        // Load all sources from all apps up front (so we can scan for event subscribers)
        var appSources = new List<(string AppPath, string FileName, string Source)>();
        foreach (var appPath in appPathList)
        {
            Console.Error.WriteLine($"Loading AL sources from {Path.GetFileName(appPath)}...");
            List<(string Name, string Source)> entries;
            try
            {
                entries = AppPackageReader.ExtractAlSources(appPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: failed to read {appPath}: {ex.Message}");
                continue;
            }
            Console.Error.WriteLine($"  {entries.Count} AL file(s) found");
            foreach (var (name, source) in entries)
                appSources.Add((appPath, name, source));
        }

        // 3. Build the reachable slice: start from the extension's direct refs,
        //    then expand transitively.
        var pendingTables = new Queue<string>(refs.Tables.Select(t => t.ToLowerInvariant()));
        var pendingCodeunits = new Queue<string>(refs.Codeunits.Select(c => c.ToLowerInvariant()));
        var visitedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedCodeunits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pendingTables.Count > 0 || pendingCodeunits.Count > 0)
        {
            // Drain tables
            while (pendingTables.Count > 0)
            {
                var tableName = pendingTables.Dequeue();
                if (!visitedTables.Add(tableName)) continue;

                var tableRefs = new ExternalRefs { Tables = { tableName } };
                foreach (var (appPath, fileName, source) in appSources)
                {
                    if (!SourceMatchesAnyRef(source, tableRefs)) continue;
                    if (allExtracted.ContainsKey(fileName)) continue;

                    allExtracted[fileName] = source;
                    Console.Error.WriteLine($"  + {fileName}");

                    // Find transitive deps in this newly added object
                    var transRefs = CollectExternalReferences(new[] { source });
                    foreach (var t in transRefs.Tables)
                        if (!visitedTables.Contains(t))
                            pendingTables.Enqueue(t.ToLowerInvariant());
                    foreach (var c in transRefs.Codeunits)
                        if (!visitedCodeunits.Contains(c))
                            pendingCodeunits.Enqueue(c.ToLowerInvariant());
                }
            }

            // Drain codeunits
            while (pendingCodeunits.Count > 0)
            {
                var cuName = pendingCodeunits.Dequeue();
                if (!visitedCodeunits.Add(cuName)) continue;

                var cuRefs = new ExternalRefs { Codeunits = { cuName } };
                foreach (var (appPath, fileName, source) in appSources)
                {
                    if (!SourceMatchesAnyRef(source, cuRefs)) continue;
                    if (allExtracted.ContainsKey(fileName)) continue;

                    allExtracted[fileName] = source;
                    Console.Error.WriteLine($"  + {fileName}");

                    var transRefs = CollectExternalReferences(new[] { source });
                    foreach (var t in transRefs.Tables)
                        if (!visitedTables.Contains(t))
                            pendingTables.Enqueue(t.ToLowerInvariant());
                    foreach (var c in transRefs.Codeunits)
                        if (!visitedCodeunits.Contains(c))
                            pendingCodeunits.Enqueue(c.ToLowerInvariant());
                }
            }
        }

        // 4. Scan ALL app source for event subscribers that subscribe to events on
        //    objects already in the slice — include them even though the extension
        //    doesn't reference them directly.
        int eventSubsAdded;
        do
        {
            eventSubsAdded = 0;
            foreach (var (appPath, fileName, source) in appSources)
            {
                if (allExtracted.ContainsKey(fileName)) continue;
                if (!IsEventSubscriberForSlice(source, visitedTables, visitedCodeunits)) continue;

                allExtracted[fileName] = source;
                Console.Error.WriteLine($"  + {fileName} (event subscriber)");
                eventSubsAdded++;

                // Its refs may expand the slice
                var transRefs = CollectExternalReferences(new[] { source });
                bool anyNew = false;
                foreach (var t in transRefs.Tables)
                    if (!visitedTables.Contains(t))
                    { pendingTables.Enqueue(t.ToLowerInvariant()); anyNew = true; }
                foreach (var c in transRefs.Codeunits)
                    if (!visitedCodeunits.Contains(c))
                    { pendingCodeunits.Enqueue(c.ToLowerInvariant()); anyNew = true; }

                if (anyNew)
                {
                    // Re-run the main expansion before continuing event-sub scan
                    while (pendingTables.Count > 0 || pendingCodeunits.Count > 0)
                    {
                        while (pendingTables.Count > 0)
                        {
                            var tableName = pendingTables.Dequeue();
                            if (!visitedTables.Add(tableName)) continue;
                            var tableRefs = new ExternalRefs { Tables = { tableName } };
                            foreach (var (ap2, fn2, src2) in appSources)
                            {
                                if (!SourceMatchesAnyRef(src2, tableRefs)) continue;
                                if (allExtracted.ContainsKey(fn2)) continue;
                                allExtracted[fn2] = src2;
                                Console.Error.WriteLine($"  + {fn2}");
                                var tr2 = CollectExternalReferences(new[] { src2 });
                                foreach (var t in tr2.Tables)
                                    if (!visitedTables.Contains(t)) pendingTables.Enqueue(t.ToLowerInvariant());
                                foreach (var c in tr2.Codeunits)
                                    if (!visitedCodeunits.Contains(c)) pendingCodeunits.Enqueue(c.ToLowerInvariant());
                            }
                        }
                        while (pendingCodeunits.Count > 0)
                        {
                            var cuName = pendingCodeunits.Dequeue();
                            if (!visitedCodeunits.Add(cuName)) continue;
                            var cuRefs = new ExternalRefs { Codeunits = { cuName } };
                            foreach (var (ap2, fn2, src2) in appSources)
                            {
                                if (!SourceMatchesAnyRef(src2, cuRefs)) continue;
                                if (allExtracted.ContainsKey(fn2)) continue;
                                allExtracted[fn2] = src2;
                                Console.Error.WriteLine($"  + {fn2}");
                                var tr2 = CollectExternalReferences(new[] { src2 });
                                foreach (var t in tr2.Tables)
                                    if (!visitedTables.Contains(t)) pendingTables.Enqueue(t.ToLowerInvariant());
                                foreach (var c in tr2.Codeunits)
                                    if (!visitedCodeunits.Contains(c)) pendingCodeunits.Enqueue(c.ToLowerInvariant());
                            }
                        }
                    }
                }
            }
        } while (eventSubsAdded > 0);

        // 5. Write extracted files to output directory
        int written = 0;
        foreach (var (fileName, source) in allExtracted)
        {
            var outPath = Path.Combine(outputDir, fileName);
            File.WriteAllText(outPath, source, Encoding.UTF8);
            written++;
        }

        Console.Error.WriteLine($"Extracted {written} AL file(s) to {outputDir}");
        return 0;
    }

    // -----------------------------------------------------------------------
    // Reference collection (via BC AL syntax tree)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parse AL source(s) and return a deduplicated set of external object references.
    /// </summary>
    public static ExternalRefs CollectExternalReferences(IEnumerable<string> sources)
    {
        var result = new ExternalRefs();

        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            try
            {
                CollectRefsFromSource(source, result);
            }
            catch
            {
                // Silently skip unparseable files — we don't want one bad file
                // to block extraction of everything else.
            }
        }

        return result;
    }

    private static void CollectRefsFromSource(string source, ExternalRefs result)
    {
        var tree = SyntaxTree.ParseObjectText(source);
        var root = tree.GetRoot();

        // Collect Record-type variable declarations: var x: Record "Sales Header"
        foreach (var recRef in root.DescendantNodes().OfType<RecordTypeReferenceSyntax>())
        {
            var name = ExtractObjectNameFromDataType(recRef.DataType);
            if (!string.IsNullOrEmpty(name))
                result.Tables.Add(name);
        }

        // Collect EventSubscriber attributes — these declare which object must be in the slice
        // [EventSubscriber(ObjectType::Table, Database::"Sales Header", 'EventName', ...)]
        foreach (var attr in root.DescendantNodes().OfType<MemberAttributeSyntax>())
        {
            if (!attr.Name.Identifier.ValueText.Equals("EventSubscriber", StringComparison.OrdinalIgnoreCase))
                continue;

            var args = attr.ArgumentList?.Arguments ?? default;
            // args[0] = ObjectType::Table / ObjectType::Codeunit
            // args[1] = Database::"Sales Header" / Codeunit::"Sales-Post"
            if (args.Count < 2) continue;

            var objectTypeArg = args[0];
            var objectNameArg = args[1];

            var objectTypeText = objectTypeArg.ToFullString().Trim();
            var objectNameText = ExtractNameFromAttributeArg(objectNameArg);
            if (string.IsNullOrEmpty(objectNameText)) continue;

            if (objectTypeText.Contains("Table", StringComparison.OrdinalIgnoreCase))
                result.Tables.Add(objectNameText);
            else if (objectTypeText.Contains("Codeunit", StringComparison.OrdinalIgnoreCase))
                result.Codeunits.Add(objectNameText);
        }

        // Collect Codeunit::"Name" option-access expressions
        foreach (var optAccess in root.DescendantNodes().OfType<OptionAccessExpressionSyntax>())
        {
            var prefix = optAccess.Expression?.ToFullString().Trim() ?? "";
            if (!prefix.Equals("Codeunit", StringComparison.OrdinalIgnoreCase)) continue;
            var cuName = optAccess.Name?.ToFullString().Trim().Trim('"');
            if (!string.IsNullOrEmpty(cuName))
                result.Codeunits.Add(cuName);
        }
    }

    /// <summary>
    /// Extract the unquoted object name from a DataTypeSyntax node.
    /// For "Record Customer" the DataType is SubtypedDataTypeSyntax with Subtype.Identifier.
    /// </summary>
    private static string? ExtractObjectNameFromDataType(DataTypeSyntax dataType)
    {
        if (dataType is SubtypedDataTypeSyntax subtyped)
        {
            var raw = subtyped.Subtype?.Identifier?.ToFullString()?.Trim();
            return UnquoteIdentifier(raw);
        }
        return null;
    }

    /// <summary>
    /// Extract the object name string from an EventSubscriber attribute argument.
    /// Handles Database::"Sales Header" and Codeunit::"Sales-Post" forms.
    /// </summary>
    private static string? ExtractNameFromAttributeArg(AttributeArgumentSyntax arg)
    {
        var fullText = arg.ToFullString().Trim();
        // Database::"Sales Header" → extract after "::"
        var colonColon = fullText.IndexOf("::", StringComparison.Ordinal);
        if (colonColon >= 0)
        {
            var after = fullText[(colonColon + 2)..].Trim().Trim('"');
            return UnquoteIdentifier(after);
        }
        // May be a plain identifier or string literal
        return UnquoteIdentifier(fullText.Trim('\'', '"'));
    }

    /// <summary>Strip surrounding double-quotes from an AL identifier.</summary>
    private static string? UnquoteIdentifier(string? raw)
    {
        if (raw == null) return null;
        raw = raw.Trim();
        if (raw.StartsWith('"') && raw.EndsWith('"') && raw.Length > 1)
            return raw[1..^1];
        return raw;
    }

    // -----------------------------------------------------------------------
    // Object matching — does a source file define an object in the refs set?
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns true if the given AL source file defines an object (table / codeunit)
    /// whose name appears in <paramref name="refs"/>.
    /// Uses regex-based name matching to avoid a full parse per source file.
    /// </summary>
    public static bool SourceMatchesAnyRef(string source, ExternalRefs refs)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (refs.Tables.Count == 0 && refs.Codeunits.Count == 0) return false;

        try
        {
            var tree = SyntaxTree.ParseObjectText(source);
            var root = tree.GetRoot();
            var compilationUnit = root as CompilationUnitSyntax;
            if (compilationUnit == null) return false;

            foreach (var obj in compilationUnit.Objects)
            {
                var objName = GetObjectName(obj);
                if (objName == null) continue;

                var kind = obj.Kind;
                if (kind == SyntaxKind.TableObject)
                {
                    if (refs.Tables.Contains(objName)) return true;
                }
                else if (kind == SyntaxKind.CodeunitObject)
                {
                    if (refs.Codeunits.Contains(objName)) return true;
                }
            }
        }
        catch
        {
            // On parse failure, fall back to a simple text search
            foreach (var t in refs.Tables)
                if (source.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var c in refs.Codeunits)
                if (source.Contains(c, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // Event subscriber scan
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns true if this source file contains an EventSubscriber that subscribes to
    /// an event on a table or codeunit that is already in the current slice.
    /// </summary>
    private static bool IsEventSubscriberForSlice(
        string source,
        HashSet<string> sliceTables,
        HashSet<string> sliceCodeunits)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        try
        {
            var refs = CollectEventSubscriberTargets(source);
            foreach (var t in refs.Tables)
                if (sliceTables.Contains(t)) return true;
            foreach (var c in refs.Codeunits)
                if (sliceCodeunits.Contains(c)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Extract only the EventSubscriber targets (ObjectType + object name) from a source file.
    /// </summary>
    private static ExternalRefs CollectEventSubscriberTargets(string source)
    {
        var result = new ExternalRefs();
        var tree = SyntaxTree.ParseObjectText(source);
        var root = tree.GetRoot();

        foreach (var attr in root.DescendantNodes().OfType<MemberAttributeSyntax>())
        {
            if (!attr.Name.Identifier.ValueText.Equals("EventSubscriber", StringComparison.OrdinalIgnoreCase))
                continue;

            var args = attr.ArgumentList?.Arguments ?? default;
            if (args.Count < 2) continue;

            var objectTypeText = args[0].ToFullString().Trim();
            var objectNameText = ExtractNameFromAttributeArg(args[1]);
            if (string.IsNullOrEmpty(objectNameText)) continue;

            if (objectTypeText.Contains("Table", StringComparison.OrdinalIgnoreCase))
                result.Tables.Add(objectNameText);
            else if (objectTypeText.Contains("Codeunit", StringComparison.OrdinalIgnoreCase))
                result.Codeunits.Add(objectNameText);
        }
        return result;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string? GetObjectName(SyntaxNode obj)
    {
        if (obj is ApplicationObjectSyntax appObj)
            return UnquoteIdentifier(appObj.Name?.Identifier.ValueText);
        if (obj is ApplicationObjectExtensionSyntax extObj)
            return UnquoteIdentifier(extObj.Name?.Identifier.ValueText);
        if (obj is ObjectSyntax plain)
            return UnquoteIdentifier(plain.Name?.Identifier.ValueText);
        return null;
    }
}

/// <summary>
/// Collected set of external object references from an AL extension's source.
/// All names are stored in their original casing (comparisons should be case-insensitive).
/// </summary>
public class ExternalRefs
{
    public HashSet<string> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Codeunits { get; } = new(StringComparer.OrdinalIgnoreCase);
}
