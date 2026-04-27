using System.Text;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner;

/// <summary>
/// Implements <c>al-runner extract-deps</c>: walks an AL extension's source, identifies all
/// external object references, extracts the minimal reachable slice of those objects from
/// .app artifacts or source directories, and writes the extracted AL source to an output directory.
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
    /// <param name="depSources">
    ///   One or more dependency sources: either a path to a .app artifact file,
    ///   or a path to a directory containing AL source files.
    /// </param>
    /// <param name="outputDir">Directory to write extracted AL files.</param>
    public static int ExtractDeps(string extensionSrcDir, IEnumerable<string> depSources, string outputDir)
    {
        if (!Directory.Exists(extensionSrcDir))
        {
            Console.Error.WriteLine($"Error: extension source directory not found: {extensionSrcDir}");
            return 1;
        }

        var depSourceList = depSources.ToList();
        foreach (var src in depSourceList)
        {
            if (!File.Exists(src) && !Directory.Exists(src))
            {
                Console.Error.WriteLine($"Error: dependency source not found: {src}");
                return 1;
            }
        }

        Directory.CreateDirectory(outputDir);

        // 1. Parse extension source and collect all external references.
        var alFiles = Directory.GetFiles(extensionSrcDir, "*.al", SearchOption.AllDirectories);
        if (alFiles.Length == 0)
            Console.Error.WriteLine($"Warning: no .al files found in {extensionSrcDir}");

        var sources = alFiles.Select(File.ReadAllText).ToList();
        Console.Error.WriteLine($"Scanning {alFiles.Length} AL file(s) in {extensionSrcDir}");

        var refs = CollectExternalReferences(sources);
        Console.Error.WriteLine($"Found external refs: {refs.Tables.Count} table(s), {refs.Codeunits.Count} codeunit(s), {refs.Enums.Count} enum(s)");
        if (refs.Tables.Count > 0)
            Console.Error.WriteLine($"  Tables:   {string.Join(", ", refs.Tables.OrderBy(t => t))}");
        if (refs.Codeunits.Count > 0)
            Console.Error.WriteLine($"  Codeunits:{string.Join(", ", refs.Codeunits.OrderBy(c => c))}");
        if (refs.Enums.Count > 0)
            Console.Error.WriteLine($"  Enums:    {string.Join(", ", refs.Enums.OrderBy(e => e))}");

        // 2. Load all AL sources from all dependency sources up front.
        //    Each source can be a .app artifact file or a directory of AL files.
        var appSources = new List<(string Origin, string FileName, string Source)>();
        foreach (var depSource in depSourceList)
        {
            if (File.Exists(depSource))
            {
                Console.Error.WriteLine($"Loading from .app: {Path.GetFileName(depSource)}");
                List<(string Name, string Source)> entries;
                try
                {
                    entries = AppPackageReader.ExtractAlSources(depSource);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: failed to read {depSource}: {ex.Message}");
                    continue;
                }
                Console.Error.WriteLine($"  {entries.Count} AL file(s) found");
                foreach (var (name, source) in entries)
                    appSources.Add((depSource, name, source));
            }
            else
            {
                // Directory — read AL files directly (ISV source directories)
                Console.Error.WriteLine($"Loading from directory: {depSource}");
                var dirFiles = Directory.GetFiles(depSource, "*.al", SearchOption.AllDirectories);
                Console.Error.WriteLine($"  {dirFiles.Length} AL file(s) found");
                foreach (var f in dirFiles)
                {
                    try
                    {
                        appSources.Add((depSource, Path.GetRelativePath(depSource, f), File.ReadAllText(f)));
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Warning: failed to read {f}: {ex.Message}");
                    }
                }
            }
        }

        // 3. Build the reachable slice using BFS over tables, codeunits and enums.
        var pendingTables = new Queue<string>(refs.Tables);
        var pendingCodeunits = new Queue<string>(refs.Codeunits);
        var pendingEnums = new Queue<string>(refs.Enums);
        var visitedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedCodeunits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedEnums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allExtracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        ExpandSlice(pendingTables, pendingCodeunits, pendingEnums,
                    visitedTables, visitedCodeunits, visitedEnums,
                    appSources, allExtracted);

        // 4. Scan ALL source for event subscribers targeting objects in the slice.
        //    Includes: table events (OnAfterInsert, etc.), integration events on
        //    codeunits in the slice, and global subscribers.
        //    Iterate to fixpoint: new subscribers may pull in new objects that
        //    themselves have subscribers.
        int eventSubsAdded;
        do
        {
            eventSubsAdded = 0;
            foreach (var (origin, fileName, source) in appSources)
            {
                if (allExtracted.ContainsKey(fileName)) continue;
                if (!IsEventSubscriberForSlice(source, visitedTables, visitedCodeunits)) continue;

                allExtracted[fileName] = source;
                Console.Error.WriteLine($"  + {fileName} (event subscriber)");
                eventSubsAdded++;

                // New subscriber may reference new objects — expand the slice.
                var transRefs = CollectExternalReferences(new[] { source });
                bool anyNew = false;
                foreach (var t in transRefs.Tables)
                    if (!visitedTables.Contains(t)) { pendingTables.Enqueue(t); anyNew = true; }
                foreach (var c in transRefs.Codeunits)
                    if (!visitedCodeunits.Contains(c)) { pendingCodeunits.Enqueue(c); anyNew = true; }
                foreach (var e in transRefs.Enums)
                    if (!visitedEnums.Contains(e)) { pendingEnums.Enqueue(e); anyNew = true; }

                if (anyNew)
                    ExpandSlice(pendingTables, pendingCodeunits, pendingEnums,
                                visitedTables, visitedCodeunits, visitedEnums,
                                appSources, allExtracted);
            }
        } while (eventSubsAdded > 0);

        // 5. Write extracted files to output directory.
        int written = 0;
        foreach (var (fileName, source) in allExtracted)
        {
            var outPath = Path.Combine(outputDir, fileName);
            var dir = Path.GetDirectoryName(outPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, source, Encoding.UTF8);
            written++;
        }

        Console.Error.WriteLine($"Extracted {written} AL file(s) to {outputDir}");
        return 0;
    }

    // -----------------------------------------------------------------------
    // BFS expansion — extract all objects reachable from pending queues
    // -----------------------------------------------------------------------

    private static void ExpandSlice(
        Queue<string> pendingTables,
        Queue<string> pendingCodeunits,
        Queue<string> pendingEnums,
        HashSet<string> visitedTables,
        HashSet<string> visitedCodeunits,
        HashSet<string> visitedEnums,
        List<(string Origin, string FileName, string Source)> appSources,
        Dictionary<string, string> allExtracted)
    {
        while (pendingTables.Count > 0 || pendingCodeunits.Count > 0 || pendingEnums.Count > 0)
        {
            while (pendingTables.Count > 0)
            {
                var name = pendingTables.Dequeue();
                if (!visitedTables.Add(name)) continue;

                var lookup = new ExternalRefs { Tables = { name } };
                foreach (var (_, fileName, source) in appSources)
                {
                    if (allExtracted.ContainsKey(fileName)) continue;
                    if (!SourceMatchesAnyRef(source, lookup)) continue;

                    allExtracted[fileName] = source;
                    Console.Error.WriteLine($"  + {fileName}");
                    EnqueueTransitiveDeps(source, visitedTables, visitedCodeunits, visitedEnums,
                                         pendingTables, pendingCodeunits, pendingEnums);
                }
            }

            while (pendingCodeunits.Count > 0)
            {
                var name = pendingCodeunits.Dequeue();
                if (!visitedCodeunits.Add(name)) continue;

                var lookup = new ExternalRefs { Codeunits = { name } };
                foreach (var (_, fileName, source) in appSources)
                {
                    if (allExtracted.ContainsKey(fileName)) continue;
                    if (!SourceMatchesAnyRef(source, lookup)) continue;

                    allExtracted[fileName] = source;
                    Console.Error.WriteLine($"  + {fileName}");
                    EnqueueTransitiveDeps(source, visitedTables, visitedCodeunits, visitedEnums,
                                         pendingTables, pendingCodeunits, pendingEnums);
                }
            }

            while (pendingEnums.Count > 0)
            {
                var name = pendingEnums.Dequeue();
                if (!visitedEnums.Add(name)) continue;

                var lookup = new ExternalRefs { Enums = { name } };
                foreach (var (_, fileName, source) in appSources)
                {
                    if (allExtracted.ContainsKey(fileName)) continue;
                    if (!SourceMatchesAnyRef(source, lookup)) continue;

                    allExtracted[fileName] = source;
                    Console.Error.WriteLine($"  + {fileName}");
                    EnqueueTransitiveDeps(source, visitedTables, visitedCodeunits, visitedEnums,
                                         pendingTables, pendingCodeunits, pendingEnums);
                }
            }
        }
    }

    private static void EnqueueTransitiveDeps(
        string source,
        HashSet<string> visitedTables,
        HashSet<string> visitedCodeunits,
        HashSet<string> visitedEnums,
        Queue<string> pendingTables,
        Queue<string> pendingCodeunits,
        Queue<string> pendingEnums)
    {
        var transRefs = CollectExternalReferences(new[] { source });
        foreach (var t in transRefs.Tables)
            if (!visitedTables.Contains(t)) pendingTables.Enqueue(t);
        foreach (var c in transRefs.Codeunits)
            if (!visitedCodeunits.Contains(c)) pendingCodeunits.Enqueue(c);
        foreach (var e in transRefs.Enums)
            if (!visitedEnums.Contains(e)) pendingEnums.Enqueue(e);
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
            try { CollectRefsFromSource(source, result); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: failed to parse source for reference collection: {ex.Message}");
            }
        }
        return result;
    }

    private static void CollectRefsFromSource(string source, ExternalRefs result)
    {
        var tree = SyntaxTree.ParseObjectText(source);
        var root = tree.GetRoot();

        // Record variable declarations: var x: Record "Sales Header"
        foreach (var recRef in root.DescendantNodes().OfType<RecordTypeReferenceSyntax>())
        {
            var name = ExtractNameFromDataType(recRef.DataType);
            if (!string.IsNullOrEmpty(name))
                result.Tables.Add(name);
        }

        // Enum field/variable types: field(1; Status; Enum "Sales Line Type")
        foreach (var enumDt in root.DescendantNodes().OfType<EnumDataTypeSyntax>())
        {
            var name = UnquoteIdentifier(enumDt.EnumTypeName?.ToFullString().Trim());
            if (!string.IsNullOrEmpty(name))
                result.Enums.Add(name);
        }

        // EventSubscriber attributes — declare which object must be in the slice.
        // Covers table events (platform-triggered OnAfterInsert etc.) and integration
        // events on codeunits in the slice.
        foreach (var attr in root.DescendantNodes().OfType<MemberAttributeSyntax>())
        {
            if (attr.Name?.Identifier.ValueText?.Equals("EventSubscriber", StringComparison.OrdinalIgnoreCase) != true)
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

        // Codeunit::"Name" option-access expressions
        foreach (var optAccess in root.DescendantNodes().OfType<OptionAccessExpressionSyntax>())
        {
            var prefix = optAccess.Expression?.ToFullString().Trim() ?? "";
            if (!prefix.Equals("Codeunit", StringComparison.OrdinalIgnoreCase)) continue;
            var cuName = UnquoteIdentifier(optAccess.Name?.ToFullString().Trim());
            if (!string.IsNullOrEmpty(cuName))
                result.Codeunits.Add(cuName);
        }
    }

    // -----------------------------------------------------------------------
    // Object matching — does a source file define an object in the refs set?
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns true if the given AL source file defines an object (table, tableextension,
    /// codeunit, enum, or enumextension) whose name or base object appears in <paramref name="refs"/>.
    /// </summary>
    public static bool SourceMatchesAnyRef(string source, ExternalRefs refs)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (refs.Tables.Count == 0 && refs.Codeunits.Count == 0 && refs.Enums.Count == 0) return false;

        try
        {
            var tree = SyntaxTree.ParseObjectText(source);
            var root = tree.GetRoot();
            var cu = root as CompilationUnitSyntax;
            if (cu == null) return false;

            foreach (var obj in cu.Objects)
            {
                switch (obj.Kind)
                {
                    case SyntaxKind.TableObject:
                        if (refs.Tables.Count > 0 && refs.Tables.Contains(GetObjectName(obj) ?? ""))
                            return true;
                        break;

                    case SyntaxKind.TableExtensionObject:
                        // Include tableextensions OF tables in the slice — this ensures
                        // OnValidate triggers and field additions from BA reach the slice.
                        if (refs.Tables.Count > 0 && obj is TableExtensionSyntax tableExt)
                        {
                            var baseTable = UnquoteIdentifier(tableExt.BaseObject?.ToFullString().Trim());
                            if (baseTable != null && refs.Tables.Contains(baseTable))
                                return true;
                        }
                        break;

                    case SyntaxKind.CodeunitObject:
                        if (refs.Codeunits.Count > 0 && refs.Codeunits.Contains(GetObjectName(obj) ?? ""))
                            return true;
                        break;

                    case SyntaxKind.EnumType:
                        if (refs.Enums.Count > 0 && obj is EnumTypeSyntax enumObj)
                        {
                            var name = UnquoteIdentifier(enumObj.Name.Identifier.ValueText);
                            if (name != null && refs.Enums.Contains(name))
                                return true;
                        }
                        break;

                    case SyntaxKind.EnumExtensionType:
                        // Include enumextensions OF enums in the slice.
                        if (refs.Enums.Count > 0 && obj is EnumExtensionTypeSyntax enumExt)
                        {
                            var baseEnum = UnquoteIdentifier(enumExt.BaseObject?.ToFullString().Trim());
                            if (baseEnum != null && refs.Enums.Contains(baseEnum))
                                return true;
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // On parse failure, skip the file entirely.
            // DO NOT fall back to text search — text search matches files that *reference*
            // the object name (e.g. call Customer.Get()) rather than files that *define* it,
            // which would silently pull incorrect objects into the slice.
            Console.Error.WriteLine($"Warning: failed to parse source file for matching (skipped): {ex.Message}");
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // Event subscriber scan
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns true if this source file contains an EventSubscriber that subscribes to
    /// an event on a table or codeunit already in the slice. Covers:
    /// - Platform-triggered implicit table events (OnAfterInsert, OnBeforeModify, etc.)
    /// - Integration/business events published by codeunits in the slice
    /// - Global event subscribers
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

    private static ExternalRefs CollectEventSubscriberTargets(string source)
    {
        var result = new ExternalRefs();
        var tree = SyntaxTree.ParseObjectText(source);
        var root = tree.GetRoot();

        foreach (var attr in root.DescendantNodes().OfType<MemberAttributeSyntax>())
        {
            if (attr.Name?.Identifier.ValueText?.Equals("EventSubscriber", StringComparison.OrdinalIgnoreCase) != true)
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

    private static string? ExtractNameFromDataType(DataTypeSyntax dataType)
    {
        if (dataType is SubtypedDataTypeSyntax subtyped)
            return UnquoteIdentifier(subtyped.Subtype?.Identifier?.ToFullString()?.Trim());
        return null;
    }

    private static string? ExtractNameFromAttributeArg(AttributeArgumentSyntax arg)
    {
        var fullText = arg.ToFullString().Trim();
        var colonColon = fullText.IndexOf("::", StringComparison.Ordinal);
        if (colonColon >= 0)
            return UnquoteIdentifier(fullText[(colonColon + 2)..].Trim());
        return UnquoteIdentifier(fullText.Trim('\'', '"'));
    }

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

    private static string? UnquoteIdentifier(string? raw)
    {
        if (raw == null) return null;
        raw = raw.Trim();
        if (raw.StartsWith('"') && raw.EndsWith('"') && raw.Length > 1)
            return raw[1..^1];
        return raw;
    }
}

/// <summary>
/// Collected set of external object references from an AL extension's source.
/// All names are stored in their original casing; comparisons are case-insensitive.
/// </summary>
public class ExternalRefs
{
    public HashSet<string> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Codeunits { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Enums { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => Tables.Count == 0 && Codeunits.Count == 0 && Enums.Count == 0;
}
