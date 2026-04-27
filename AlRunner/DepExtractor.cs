using System.Text;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner;

/// <summary>
/// Implements <c>al-runner extract-deps</c>: walks an AL extension's source, identifies all
/// external object references, extracts the minimal reachable slice from .app artifacts or
/// source directories, and writes the extracted AL source to an output directory.
///
/// Supported object types: Table, TableExtension, Codeunit, Enum, EnumExtension,
/// Page, PageExtension, Report, ReportExtension, Query, XmlPort, Interface.
/// </summary>
public static class DepExtractor
{
    // All object type names the BFS tracks. Order is irrelevant; used to initialise
    // the pending/visited dictionaries.
    private static readonly string[] AllTypes =
    [
        "Table", "Codeunit", "Enum", "Page", "Report", "Query", "XmlPort", "Interface"
    ];

    // -----------------------------------------------------------------------
    // Public entry point
    // -----------------------------------------------------------------------

    /// <summary>
    /// Main entry point for --extract-deps. Returns 0 on success, 1 on error.
    /// </summary>
    /// <param name="extensionSrcDir">Directory containing the extension's AL source files.</param>
    /// <param name="depSources">
    ///   One or more dependency sources: a path to a .app artifact file,
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
        PrintRefs(refs);

        // 2. Load all AL sources from dependency sources.
        //    Each source is a .app artifact file or a directory of AL files.
        var appSources = LoadDepSources(depSourceList);

        // 3. Build a name→file index by parsing every source file once.
        //    This turns BFS lookups from O(N) scans to O(1) dictionary hits,
        //    reducing the total parse cost from O(N×slice_size) to O(N).
        Console.Error.WriteLine($"Building index from {appSources.Count} source file(s)...");
        var index = BuildIndex(appSources);
        Console.Error.WriteLine($"Index ready — {index.DefinitionCount} definitions, {index.ExtensionCount} extensions, {index.SubscriberCount} event subscribers.");

        // 4. BFS over all object types using the index.
        var pending = AllTypes.ToDictionary(t => t, _ => new Queue<string>(),     StringComparer.OrdinalIgnoreCase);
        var visited = AllTypes.ToDictionary(t => t, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var allExtracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        SeedPending(refs, pending);
        ExpandSlice(pending, visited, index, allExtracted);

        // 5. Add event subscribers targeting objects in the slice.
        //    Iterate to fixpoint: new subscribers may pull in objects that have their own subscribers.
        int eventSubsAdded;
        do
        {
            eventSubsAdded = 0;
            foreach (var targetType in new[] { "Table", "Codeunit", "Page", "Report" })
            {
                if (!index.EventSubscribers.TryGetValue(targetType, out var subsByTarget)) continue;
                foreach (var (targetName, subscribers) in subsByTarget)
                {
                    if (!visited[targetType].Contains(targetName)) continue;
                    foreach (var (fileName, source) in subscribers)
                    {
                        if (allExtracted.ContainsKey(fileName)) continue;
                        allExtracted[fileName] = source;
                        Console.Error.WriteLine($"  + {fileName} (event subscriber)");
                        eventSubsAdded++;
                        EnqueueTransitiveDeps(source, visited, pending);
                        ExpandSlice(pending, visited, index, allExtracted);
                    }
                }
            }
        } while (eventSubsAdded > 0);

        // 5. Write extracted files to output directory.
        //    DotNet-referencing procedures are replaced with Error() calls so the file
        //    compiles cleanly and fails loudly if ever reached at runtime.
        int written = 0;
        int strippedProcedures = 0;
        int skippedFiles = 0;
        foreach (var (fileName, source) in allExtracted)
        {
            var (strippedSource, stripped) = StripDotNetProcedures(source, fileName);
            if (strippedSource == null) { skippedFiles++; continue; } // pure assembly declaration, nothing callable

            var outPath = Path.Combine(outputDir, fileName);
            var dir = Path.GetDirectoryName(outPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, strippedSource, Encoding.UTF8);
            written++;
            strippedProcedures += stripped;
        }

        if (strippedProcedures > 0 || skippedFiles > 0)
            Console.Error.WriteLine($"DotNet stripping: {strippedProcedures} procedure(s) replaced with Error(), {skippedFiles} pure-assembly file(s) skipped.");

        Console.Error.WriteLine($"Extracted {written} AL file(s) to {outputDir}");
        return 0;
    }

    // -----------------------------------------------------------------------
    // BFS expansion
    // -----------------------------------------------------------------------

    private static void ExpandSlice(
        Dictionary<string, Queue<string>> pending,
        Dictionary<string, HashSet<string>> visited,
        DepIndex index,
        Dictionary<string, string> allExtracted)
    {
        bool anyWork;
        do
        {
            anyWork = false;
            foreach (var typeName in AllTypes)
            {
                while (pending[typeName].Count > 0)
                {
                    var name = pending[typeName].Dequeue();
                    if (!visited[typeName].Add(name)) continue;
                    anyWork = true;

                    // O(1) definition lookup
                    if (index.Definitions.TryGetValue(typeName, out var defs) &&
                        defs.TryGetValue(name, out var def) &&
                        !allExtracted.ContainsKey(def.FileName))
                    {
                        allExtracted[def.FileName] = def.Source;
                        Console.Error.WriteLine($"  + {def.FileName}");
                        EnqueueTransitiveDeps(def.Source, visited, pending);
                    }

                    // O(1) extension lookup — includes all tableextensions, pageextensions, etc.
                    if (index.Extensions.TryGetValue(typeName, out var exts) &&
                        exts.TryGetValue(name, out var extList))
                    {
                        foreach (var (fileName, source) in extList)
                        {
                            if (allExtracted.ContainsKey(fileName)) continue;
                            allExtracted[fileName] = source;
                            Console.Error.WriteLine($"  + {fileName}");
                            EnqueueTransitiveDeps(source, visited, pending);
                        }
                    }
                }
            }
        } while (anyWork);
    }

    private static void EnqueueTransitiveDeps(
        string source,
        Dictionary<string, HashSet<string>> visited,
        Dictionary<string, Queue<string>> pending)
    {
        var transRefs = CollectExternalReferences(new[] { source });
        EnqueueNewNames(transRefs.Tables,     "Table",     visited, pending);
        EnqueueNewNames(transRefs.Codeunits,  "Codeunit",  visited, pending);
        EnqueueNewNames(transRefs.Enums,      "Enum",      visited, pending);
        EnqueueNewNames(transRefs.Pages,      "Page",      visited, pending);
        EnqueueNewNames(transRefs.Reports,    "Report",    visited, pending);
        EnqueueNewNames(transRefs.Queries,    "Query",     visited, pending);
        EnqueueNewNames(transRefs.XmlPorts,   "XmlPort",   visited, pending);
        EnqueueNewNames(transRefs.Interfaces, "Interface", visited, pending);
    }

    private static void EnqueueNewNames(
        HashSet<string> names,
        string typeName,
        Dictionary<string, HashSet<string>> visited,
        Dictionary<string, Queue<string>> pending)
    {
        foreach (var n in names)
            if (!visited[typeName].Contains(n))
                pending[typeName].Enqueue(n);
    }

    // -----------------------------------------------------------------------
    // Index — built once from all dep sources, used for O(1) BFS lookups
    // -----------------------------------------------------------------------

    private static DepIndex BuildIndex(List<(string Origin, string FileName, string Source)> appSources)
    {
        var index = new DepIndex();
        int i = 0;
        foreach (var (_, fileName, source) in appSources)
        {
            i++;
            if (i % 1000 == 0)
                Console.Error.WriteLine($"  Indexing {i}/{appSources.Count}...");

            try
            {
                var tree = SyntaxTree.ParseObjectText(source);
                var root = tree.GetRoot();
                if (root is not CompilationUnitSyntax cu) continue;

                foreach (var obj in cu.Objects)
                    IndexObject(obj, fileName, source, index);

                IndexEventSubscribers(root, fileName, source, index);
            }
            catch { /* skip unparseable files */ }
        }
        return index;
    }

    private static void IndexObject(SyntaxNode obj, string fileName, string source, DepIndex index)
    {
        switch (obj.Kind)
        {
            case SyntaxKind.TableObject:
                index.AddDefinition("Table", GetObjectName(obj), fileName, source); break;
            case SyntaxKind.CodeunitObject:
                index.AddDefinition("Codeunit", GetObjectName(obj), fileName, source); break;
            case SyntaxKind.EnumType:
                if (obj is EnumTypeSyntax et)
                    index.AddDefinition("Enum", UnquoteIdentifier(et.Name.Identifier.ValueText), fileName, source);
                break;
            case SyntaxKind.PageObject:
                index.AddDefinition("Page", GetObjectName(obj), fileName, source); break;
            case SyntaxKind.ReportObject:
                index.AddDefinition("Report", GetObjectName(obj), fileName, source); break;
            case SyntaxKind.QueryObject:
                index.AddDefinition("Query", GetObjectName(obj), fileName, source); break;
            case SyntaxKind.XmlPortObject:
                index.AddDefinition("XmlPort", GetObjectName(obj), fileName, source); break;
            case SyntaxKind.Interface:
                index.AddDefinition("Interface", GetObjectName(obj), fileName, source); break;

            // Extension objects: indexed by the base object they extend
            case SyntaxKind.TableExtensionObject:
                if (obj is ApplicationObjectExtensionSyntax te)
                    index.AddExtension("Table", UnquoteIdentifier(te.BaseObject?.ToFullString().Trim()), fileName, source);
                break;
            case SyntaxKind.EnumExtensionType:
                if (obj is ApplicationObjectExtensionSyntax ee)
                    index.AddExtension("Enum", UnquoteIdentifier(ee.BaseObject?.ToFullString().Trim()), fileName, source);
                break;
            case SyntaxKind.PageExtensionObject:
                if (obj is ApplicationObjectExtensionSyntax pe)
                    index.AddExtension("Page", UnquoteIdentifier(pe.BaseObject?.ToFullString().Trim()), fileName, source);
                break;
            case SyntaxKind.ReportExtensionObject:
                if (obj is ApplicationObjectExtensionSyntax re)
                    index.AddExtension("Report", UnquoteIdentifier(re.BaseObject?.ToFullString().Trim()), fileName, source);
                break;
        }
    }

    private static void IndexEventSubscribers(SyntaxNode root, string fileName, string source, DepIndex index)
    {
        foreach (var attr in root.DescendantNodes().OfType<MemberAttributeSyntax>())
        {
            if (attr.Name?.Identifier.ValueText?.Equals("EventSubscriber", StringComparison.OrdinalIgnoreCase) != true)
                continue;

            var args = attr.ArgumentList?.Arguments ?? default;
            if (args.Count < 2) continue;

            var objectTypeText = args[0].ToFullString().Trim();
            var objectNameText = ExtractNameFromAttributeArg(args[1]);
            if (string.IsNullOrEmpty(objectNameText)) continue;

            string? targetType = null;
            if      (objectTypeText.Contains("Table",    StringComparison.OrdinalIgnoreCase)) targetType = "Table";
            else if (objectTypeText.Contains("Codeunit", StringComparison.OrdinalIgnoreCase)) targetType = "Codeunit";
            else if (objectTypeText.Contains("Page",     StringComparison.OrdinalIgnoreCase)) targetType = "Page";
            else if (objectTypeText.Contains("Report",   StringComparison.OrdinalIgnoreCase)) targetType = "Report";

            if (targetType != null)
                index.AddEventSubscriber(targetType, objectNameText, fileName, source);
        }
    }

    private static void SeedPending(ExternalRefs refs, Dictionary<string, Queue<string>> pending)
    {
        foreach (var n in refs.Tables)     pending["Table"].Enqueue(n);
        foreach (var n in refs.Codeunits)  pending["Codeunit"].Enqueue(n);
        foreach (var n in refs.Enums)      pending["Enum"].Enqueue(n);
        foreach (var n in refs.Pages)      pending["Page"].Enqueue(n);
        foreach (var n in refs.Reports)    pending["Report"].Enqueue(n);
        foreach (var n in refs.Queries)    pending["Query"].Enqueue(n);
        foreach (var n in refs.XmlPorts)   pending["XmlPort"].Enqueue(n);
        foreach (var n in refs.Interfaces) pending["Interface"].Enqueue(n);
    }

    // -----------------------------------------------------------------------
    // Reference collection (via BC AL syntax tree)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parse AL source(s) and return all external object references.
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

        // Page/Report/Query/XmlPort/Interface variable declarations:
        //   var P: Page "Customer Card"; var R: Report "Sales Invoice"; etc.
        // Also catches "Enum "Sales Line Type"" field types via SubtypedDataTypeSyntax
        // (EnumDataTypeSyntax is handled separately below for clarity).
        foreach (var subtyped in root.DescendantNodes().OfType<SubtypedDataTypeSyntax>())
        {
            var typeName = subtyped.TypeName.ToFullString().Trim();
            var name = UnquoteIdentifier(subtyped.Subtype?.ToFullString().Trim());
            if (string.IsNullOrEmpty(name)) continue;

            switch (typeName)
            {
                case "Page":      result.Pages.Add(name);      break;
                case "Report":    result.Reports.Add(name);    break;
                case "Query":     result.Queries.Add(name);    break;
                case "XmlPort":   result.XmlPorts.Add(name);   break;
                case "Interface": result.Interfaces.Add(name); break;
                // "Record" is covered by RecordTypeReferenceSyntax above.
                // "Enum" is covered by EnumDataTypeSyntax below.
            }
        }

        // Enum field/variable types: field(1; Status; Enum "Sales Line Type")
        foreach (var enumDt in root.DescendantNodes().OfType<EnumDataTypeSyntax>())
        {
            var name = UnquoteIdentifier(enumDt.EnumTypeName?.ToFullString().Trim());
            if (!string.IsNullOrEmpty(name))
                result.Enums.Add(name);
        }

        // EventSubscriber attributes — declare which object must be in the slice.
        // Covers Table events (platform-triggered OnAfterInsert etc.),
        // Codeunit integration events, Page events, and Report events.
        foreach (var attr in root.DescendantNodes().OfType<MemberAttributeSyntax>())
        {
            if (attr.Name?.Identifier.ValueText?.Equals("EventSubscriber", StringComparison.OrdinalIgnoreCase) != true)
                continue;

            var args = attr.ArgumentList?.Arguments ?? default;
            if (args.Count < 2) continue;

            var objectTypeText = args[0].ToFullString().Trim();
            var objectNameText = ExtractNameFromAttributeArg(args[1]);
            if (string.IsNullOrEmpty(objectNameText)) continue;

            if      (objectTypeText.Contains("Table",    StringComparison.OrdinalIgnoreCase)) result.Tables.Add(objectNameText);
            else if (objectTypeText.Contains("Codeunit", StringComparison.OrdinalIgnoreCase)) result.Codeunits.Add(objectNameText);
            else if (objectTypeText.Contains("Page",     StringComparison.OrdinalIgnoreCase)) result.Pages.Add(objectNameText);
            else if (objectTypeText.Contains("Report",   StringComparison.OrdinalIgnoreCase)) result.Reports.Add(objectNameText);
        }

        // Option-access expressions: Codeunit::"Name", Page::"Name", Report::, etc.
        foreach (var optAccess in root.DescendantNodes().OfType<OptionAccessExpressionSyntax>())
        {
            var prefix = optAccess.Expression?.ToFullString().Trim() ?? "";
            var name = UnquoteIdentifier(optAccess.Name?.ToFullString().Trim());
            if (string.IsNullOrEmpty(name)) continue;

            switch (prefix)
            {
                case "Codeunit": result.Codeunits.Add(name); break;
                case "Page":     result.Pages.Add(name);     break;
                case "Report":   result.Reports.Add(name);   break;
                case "Query":    result.Queries.Add(name);   break;
                case "XmlPort":  result.XmlPorts.Add(name);  break;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Object matching — does a source file define an object in the refs set?
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns true if the given AL source file defines an object whose name (or, for
    /// extension objects, whose base object name) appears in <paramref name="refs"/>.
    /// Handles all object types: Table/Extension, Codeunit, Enum/Extension,
    /// Page/Extension, Report/Extension, Query, XmlPort, Interface.
    /// </summary>
    public static bool SourceMatchesAnyRef(string source, ExternalRefs refs)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (refs.IsEmpty) return false;

        try
        {
            var tree = SyntaxTree.ParseObjectText(source);
            var root = tree.GetRoot();
            if (root is not CompilationUnitSyntax cu) return false;

            foreach (var obj in cu.Objects)
            {
                switch (obj.Kind)
                {
                    // --- Definitions: match by object name ---
                    case SyntaxKind.TableObject:
                        if (refs.Tables.Count > 0 && refs.Tables.Contains(GetObjectName(obj) ?? "")) return true;
                        break;
                    case SyntaxKind.CodeunitObject:
                        if (refs.Codeunits.Count > 0 && refs.Codeunits.Contains(GetObjectName(obj) ?? "")) return true;
                        break;
                    case SyntaxKind.EnumType:
                        if (refs.Enums.Count > 0 && obj is EnumTypeSyntax enumObj)
                        {
                            var name = UnquoteIdentifier(enumObj.Name.Identifier.ValueText);
                            if (name != null && refs.Enums.Contains(name)) return true;
                        }
                        break;
                    case SyntaxKind.PageObject:
                        if (refs.Pages.Count > 0 && refs.Pages.Contains(GetObjectName(obj) ?? "")) return true;
                        break;
                    case SyntaxKind.ReportObject:
                        if (refs.Reports.Count > 0 && refs.Reports.Contains(GetObjectName(obj) ?? "")) return true;
                        break;
                    case SyntaxKind.QueryObject:
                        if (refs.Queries.Count > 0 && refs.Queries.Contains(GetObjectName(obj) ?? "")) return true;
                        break;
                    case SyntaxKind.XmlPortObject:
                        if (refs.XmlPorts.Count > 0 && refs.XmlPorts.Contains(GetObjectName(obj) ?? "")) return true;
                        break;
                    case SyntaxKind.Interface:
                        if (refs.Interfaces.Count > 0 && refs.Interfaces.Contains(GetObjectName(obj) ?? "")) return true;
                        break;

                    // --- Extensions: match by BaseObject (the object being extended) ---
                    // This ensures all BA extensions of slice objects are included so
                    // triggers and additional fields/methods reach the compiled DLL.
                    case SyntaxKind.TableExtensionObject:
                        if (refs.Tables.Count > 0 && obj is ApplicationObjectExtensionSyntax tableExt1)
                        {
                            var baseObj = UnquoteIdentifier(tableExt1.BaseObject?.ToFullString().Trim());
                            if (baseObj != null && refs.Tables.Contains(baseObj)) return true;
                        }
                        break;
                    case SyntaxKind.EnumExtensionType:
                        if (refs.Enums.Count > 0 && obj is ApplicationObjectExtensionSyntax enumExt1)
                        {
                            var baseObj = UnquoteIdentifier(enumExt1.BaseObject?.ToFullString().Trim());
                            if (baseObj != null && refs.Enums.Contains(baseObj)) return true;
                        }
                        break;
                    case SyntaxKind.PageExtensionObject:
                        if (refs.Pages.Count > 0 && obj is ApplicationObjectExtensionSyntax pageExt1)
                        {
                            var baseObj = UnquoteIdentifier(pageExt1.BaseObject?.ToFullString().Trim());
                            if (baseObj != null && refs.Pages.Contains(baseObj)) return true;
                        }
                        break;
                    case SyntaxKind.ReportExtensionObject:
                        if (refs.Reports.Count > 0 && obj is ApplicationObjectExtensionSyntax reportExt1)
                        {
                            var baseObj = UnquoteIdentifier(reportExt1.BaseObject?.ToFullString().Trim());
                            if (baseObj != null && refs.Reports.Contains(baseObj)) return true;
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // On parse failure, skip entirely.
            // DO NOT fall back to text search — that would match files that *reference*
            // the object rather than files that *define* it.
            Console.Error.WriteLine($"Warning: failed to parse source for matching (skipped): {ex.Message}");
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // Event subscriber scan
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns true if this source file contains an EventSubscriber targeting a
    /// table, codeunit, page, or report already in the slice.
    /// Covers platform-triggered implicit events (OnAfterInsert, OnBeforeModify, etc.)
    /// and integration/business events on objects in the slice.
    /// </summary>
    private static bool IsEventSubscriberForSlice(
        string source,
        Dictionary<string, HashSet<string>> visited)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        try
        {
            var refs = CollectEventSubscriberTargets(source);
            if (refs.Tables.Any(t     => visited["Table"].Contains(t)))     return true;
            if (refs.Codeunits.Any(c  => visited["Codeunit"].Contains(c)))  return true;
            if (refs.Pages.Any(p      => visited["Page"].Contains(p)))      return true;
            if (refs.Reports.Any(r    => visited["Report"].Contains(r)))    return true;
        }
        catch { }
        return false;
    }

    private static ExternalRefs CollectEventSubscriberTargets(string source)
    {
        var result = new ExternalRefs();
        var tree = SyntaxTree.ParseObjectText(source);

        foreach (var attr in tree.GetRoot().DescendantNodes().OfType<MemberAttributeSyntax>())
        {
            if (attr.Name?.Identifier.ValueText?.Equals("EventSubscriber", StringComparison.OrdinalIgnoreCase) != true)
                continue;

            var args = attr.ArgumentList?.Arguments ?? default;
            if (args.Count < 2) continue;

            var objectTypeText = args[0].ToFullString().Trim();
            var objectNameText = ExtractNameFromAttributeArg(args[1]);
            if (string.IsNullOrEmpty(objectNameText)) continue;

            if      (objectTypeText.Contains("Table",    StringComparison.OrdinalIgnoreCase)) result.Tables.Add(objectNameText);
            else if (objectTypeText.Contains("Codeunit", StringComparison.OrdinalIgnoreCase)) result.Codeunits.Add(objectNameText);
            else if (objectTypeText.Contains("Page",     StringComparison.OrdinalIgnoreCase)) result.Pages.Add(objectNameText);
            else if (objectTypeText.Contains("Report",   StringComparison.OrdinalIgnoreCase)) result.Reports.Add(objectNameText);
        }
        return result;
    }

    // -----------------------------------------------------------------------
    // DotNet stripping
    // -----------------------------------------------------------------------

    /// <summary>
    /// Replaces bodies of procedures that reference DotNet types with an <c>Error()</c> call.
    /// Returns (strippedSource, count) where strippedSource is null if the entire file
    /// is a pure assembly declaration with nothing callable.
    /// </summary>
    private static (string? Source, int Stripped) StripDotNetProcedures(string source, string fileName)
    {
        // Fast path: no DotNet keyword, nothing to do.
        if (!source.Contains("DotNet", StringComparison.Ordinal))
            return (source, 0);

        try
        {
            var tree = SyntaxTree.ParseObjectText(source);
            var root = tree.GetRoot();

            // Collect (start, end, replacement) for each DotNet-referencing procedure.
            var replacements = new List<(int Start, int End, string Text)>();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var hasDotNet = method.DescendantNodes()
                    .OfType<SubtypedDataTypeSyntax>()
                    .Any(s => s.TypeName.ToFullString().Trim()
                        .Equals("DotNet", StringComparison.OrdinalIgnoreCase));

                if (!hasDotNet) continue;

                var methodName = method.Name?.Identifier.ValueText ?? "Unknown";
                var varSection = method.ChildNodes().OfType<VarSectionSyntax>().FirstOrDefault();
                var block = method.ChildNodes().OfType<BlockSyntax>().FirstOrDefault();
                if (block == null) continue;

                // Replace from start of var section (or block if no var) through end of block.
                var replaceStart = varSection?.Span.Start ?? block.Span.Start;
                var replaceEnd = block.Span.End;
                var escaped = methodName.Replace("'", "''");
                var errorBody =
                    "\n    begin\n" +
                    $"        Error('AL Runner: ''{escaped}'' uses DotNet interop — not supported in standalone mode. Add this object to your compiled dependency slice.');\n" +
                    "    end;";

                replacements.Add((replaceStart, replaceEnd, errorBody));
            }

            if (replacements.Count == 0)
                return (source, 0);

            // Apply replacements from end to start to preserve offsets.
            var chars = source.ToCharArray();
            var result = source;
            foreach (var (start, end, text) in replacements.OrderByDescending(r => r.Start))
                result = result[..start] + text + result[end..];

            return (result, replacements.Count);
        }
        catch
        {
            // On parse failure, return source unchanged rather than losing the file.
            return (source, 0);
        }
    }

    // -----------------------------------------------------------------------
    // Dependency source loading
    // -----------------------------------------------------------------------

    private static List<(string Origin, string FileName, string Source)> LoadDepSources(
        List<string> depSources)
    {
        var result = new List<(string, string, string)>();
        foreach (var depSource in depSources)
        {
            if (File.Exists(depSource))
            {
                Console.Error.WriteLine($"Loading from .app: {Path.GetFileName(depSource)}");
                List<(string Name, string Source)> entries;
                try { entries = AppPackageReader.ExtractAlSources(depSource); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: failed to read {depSource}: {ex.Message}");
                    continue;
                }
                Console.Error.WriteLine($"  {entries.Count} AL file(s) found");
                foreach (var (name, src) in entries)
                    result.Add((depSource, name, src));
            }
            else
            {
                Console.Error.WriteLine($"Loading from directory: {depSource}");
                var dirFiles = Directory.GetFiles(depSource, "*.al", SearchOption.AllDirectories);
                Console.Error.WriteLine($"  {dirFiles.Length} AL file(s) found");
                foreach (var f in dirFiles)
                {
                    try { result.Add((depSource, Path.GetRelativePath(depSource, f), File.ReadAllText(f))); }
                    catch (Exception ex) { Console.Error.WriteLine($"Warning: failed to read {f}: {ex.Message}"); }
                }
            }
        }
        return result;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static ExternalRefs SingleRef(string typeName, string name)
    {
        var refs = new ExternalRefs();
        switch (typeName)
        {
            case "Table":     refs.Tables.Add(name);     break;
            case "Codeunit":  refs.Codeunits.Add(name);  break;
            case "Enum":      refs.Enums.Add(name);      break;
            case "Page":      refs.Pages.Add(name);      break;
            case "Report":    refs.Reports.Add(name);    break;
            case "Query":     refs.Queries.Add(name);    break;
            case "XmlPort":   refs.XmlPorts.Add(name);   break;
            case "Interface": refs.Interfaces.Add(name); break;
        }
        return refs;
    }

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

    private static void PrintRefs(ExternalRefs refs)
    {
        void Print(string label, HashSet<string> set) {
            if (set.Count > 0)
                Console.Error.WriteLine($"  {label,-12}{string.Join(", ", set.OrderBy(x => x))}");
        }
        Console.Error.WriteLine($"Found external refs:");
        Print("Tables:",     refs.Tables);
        Print("Codeunits:",  refs.Codeunits);
        Print("Enums:",      refs.Enums);
        Print("Pages:",      refs.Pages);
        Print("Reports:",    refs.Reports);
        Print("Queries:",    refs.Queries);
        Print("XmlPorts:",   refs.XmlPorts);
        Print("Interfaces:", refs.Interfaces);
    }
}

/// <summary>
/// Pre-built lookup index over all dependency source files.
/// Parsed once upfront; BFS lookups are O(1) dictionary hits rather than O(N) file scans.
/// </summary>
internal class DepIndex
{
    // typeName -> objectName -> (fileName, source)
    public Dictionary<string, Dictionary<string, (string FileName, string Source)>> Definitions { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    // baseTypeName -> baseObjectName -> list of (fileName, source)
    public Dictionary<string, Dictionary<string, List<(string FileName, string Source)>>> Extensions { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    // targetTypeName -> targetObjectName -> list of (fileName, source)
    public Dictionary<string, Dictionary<string, List<(string FileName, string Source)>>> EventSubscribers { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public int DefinitionCount  => Definitions.Values.Sum(d => d.Count);
    public int ExtensionCount   => Extensions.Values.Sum(d => d.Values.Sum(l => l.Count));
    public int SubscriberCount  => EventSubscribers.Values.Sum(d => d.Values.Sum(l => l.Count));

    public void AddDefinition(string? typeName, string? objectName, string fileName, string source)
    {
        if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(objectName)) return;
        if (!Definitions.TryGetValue(typeName, out var byName))
            Definitions[typeName] = byName = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        byName.TryAdd(objectName, (fileName, source));
    }

    public void AddExtension(string? baseTypeName, string? baseObjectName, string fileName, string source)
    {
        if (string.IsNullOrEmpty(baseTypeName) || string.IsNullOrEmpty(baseObjectName)) return;
        if (!Extensions.TryGetValue(baseTypeName, out var byBase))
            Extensions[baseTypeName] = byBase = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        if (!byBase.TryGetValue(baseObjectName, out var list))
            byBase[baseObjectName] = list = [];
        list.Add((fileName, source));
    }

    public void AddEventSubscriber(string? targetType, string? targetName, string fileName, string source)
    {
        if (string.IsNullOrEmpty(targetType) || string.IsNullOrEmpty(targetName)) return;
        if (!EventSubscribers.TryGetValue(targetType, out var byTarget))
            EventSubscribers[targetType] = byTarget = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        if (!byTarget.TryGetValue(targetName, out var list))
            byTarget[targetName] = list = [];
        list.Add((fileName, source));
    }
}

/// <summary>
/// Collected set of external object references from an AL extension's source.
/// All names are stored in their original casing; comparisons are case-insensitive.
/// </summary>
public class ExternalRefs
{
    public HashSet<string> Tables     { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Codeunits  { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Enums      { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Pages      { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Reports    { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Queries    { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> XmlPorts   { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Interfaces { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty =>
        Tables.Count == 0 && Codeunits.Count == 0 && Enums.Count == 0 &&
        Pages.Count == 0 && Reports.Count == 0 && Queries.Count == 0 &&
        XmlPorts.Count == 0 && Interfaces.Count == 0;
}
