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

    // Stub object IDs start at 1_999_900_000 to avoid clashing with real BC object IDs.
    private const int StubIdBase = 1_999_900_000;

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
    /// <param name="packagePaths">
    ///   Optional symbol packages (.app files or directories containing .app files).
    ///   When supplied, the packages are loaded during every trial-compile round so
    ///   that platform tables and other objects declared via package DLLs or embedded
    ///   AL source are not reported as missing. Objects still missing after all rounds
    ///   are stub-generated; any that remain in the package-declared set (from
    ///   SymbolReference.json) are additionally filtered to prevent AL0197 collisions.
    /// </param>
    public static int ExtractDeps(string extensionSrcDir, IEnumerable<string> depSources, string outputDir,
        List<string>? packagePaths = null)
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

        // Tracks BFS targets that have no definition in the dep-source index.
        // Without this set, ExpandSlice silently dropped pending names whose target
        // was external/add-on (e.g. DATABASE::"X" referencing a table whose source
        // lives in an app not present in dep-sources). Such names slipped past
        // extract-deps and surfaced at compile-dep time as AL0118 "the name does
        // not exist in the current context" — which TrialCompileMissing's
        // AL0185-only pattern does not capture, so the fixup loop missed them too.
        // We feed unresolved targets into finalMissingObjects so the existing
        // blank-shell stub generator emits stubs for them.
        var unresolved = new HashSet<(string TypeName, string Name)>(
            EqualityComparer<(string, string)>.Create(
                (a, b) => string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase),
                x => StringComparer.OrdinalIgnoreCase.GetHashCode(x.Item1)
                   ^ StringComparer.OrdinalIgnoreCase.GetHashCode(x.Item2)));

        SeedPending(refs, pending);
        ExpandSlice(pending, visited, index, allExtracted, unresolved);

        // 5. Add event subscribers targeting objects in the slice.
        //    Same-app rule: only pull subscribers whose host file lives in the same app
        //    as the target object. Cross-app subscribers are a fidelity gap tracked in #1545.
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
                    var targetApps = GetAppsForObject(index, targetType, targetName);
                    foreach (var (fileName, source) in subscribers)
                    {
                        if (allExtracted.ContainsKey(fileName)) continue;
                        if (targetApps.Count > 0 && !targetApps.Contains(GetAppOfFile(fileName))) continue;
                        allExtracted[fileName] = source;
                        Console.Error.WriteLine($"  + {fileName} (event subscriber)");
                        eventSubsAdded++;
                        EnqueueTransitiveDeps(source, visited, pending);
                        ExpandSlice(pending, visited, index, allExtracted, unresolved);
                    }
                }
            }
        } while (eventSubsAdded > 0);

        // 5b. Cross-app extension fixup. The same-app filter inside ExpandSlice keeps
        //     truly out-of-scope foreign extensions out, but it also drops legitimate
        //     extensions whose host app is *itself* in the slice (because the consumer
        //     references some other object from that app). Such extensions add values
        //     and members the slice transitively uses — without them the slice fails
        //     to compile (AL0132 missing enum value, AL0280 missing event publisher).
        //     Iterate to fixpoint: pulling an extension may bring in transitive deps
        //     that pull in new apps, which may unlock more extensions.
        ExpandCrossAppExtensions(index, visited, pending, allExtracted, unresolved);

        // 6. Compiler-driven fixup: trial-compile the current slice, parse missing symbols,
        //    look them up in the index, add to the slice, repeat.
        //    This catches object references that static pattern-matching misses (page parts,
        //    platform tables, edge-case reference patterns). The index makes each resolution
        //    pass O(1); only the trial compile is expensive, and we run it at most ~5 times.
        //    Resolve package paths once (ResolvePackagePaths may create isolated temp dirs
        //    for individual .app file inputs; doing it once avoids accumulating temp dirs).
        var resolvedPkgDirs = (packagePaths is { Count: > 0 })
            ? AlTranspiler.ResolvePackagePaths(packagePaths, null)
            : null;

        int fixupRound = 0;
        int fixupAdded;
        List<(string TypeName, string Name)> finalMissingObjects = [];
        do
        {
            fixupAdded = 0;
            fixupRound++;
            Console.Error.WriteLine($"Compiler fixup round {fixupRound}: trial-compiling {allExtracted.Count} file(s)...");

            var missing = TrialCompileMissing(allExtracted.Values.ToList(), resolvedPkgDirs);
            if (missing.Count == 0) { Console.Error.WriteLine("  No missing objects — slice is complete."); break; }

            Console.Error.WriteLine($"  Compiler reports {missing.Count} missing object(s), resolving from index...");
            foreach (var (t, n) in missing)
            {
                if (visited.TryGetValue(t, out var vis) && vis.Contains(n)) continue;
                if (pending.TryGetValue(t, out var q)) q.Enqueue(n);
                fixupAdded++;
            }

            if (fixupAdded > 0)
            {
                ExpandSlice(pending, visited, index, allExtracted, unresolved);
                // Re-run event subscriber scan for any newly added objects
                int subsAdded2;
                do {
                    subsAdded2 = 0;
                    foreach (var targetType in new[] { "Table", "Codeunit", "Page", "Report" })
                    {
                        if (!index.EventSubscribers.TryGetValue(targetType, out var subsByTarget2)) continue;
                        foreach (var (targetName, subscribers) in subsByTarget2)
                        {
                            if (!visited[targetType].Contains(targetName)) continue;
                            var targetApps = GetAppsForObject(index, targetType, targetName);
                            foreach (var (fn, src) in subscribers)
                            {
                                if (allExtracted.ContainsKey(fn)) continue;
                                if (targetApps.Count > 0 && !targetApps.Contains(GetAppOfFile(fn))) continue;
                                allExtracted[fn] = src;
                                subsAdded2++;
                                EnqueueTransitiveDeps(src, visited, pending);
                                ExpandSlice(pending, visited, index, allExtracted, unresolved);
                            }
                        }
                    }
                } while (subsAdded2 > 0);
                ExpandCrossAppExtensions(index, visited, pending, allExtracted, unresolved);
                Console.Error.WriteLine($"  Added {fixupAdded} object(s) from index ({allExtracted.Count} total).");
            }
            else
            {
                finalMissingObjects = missing; // capture for stub-generation pass
                Console.Error.WriteLine($"  {missing.Count} missing object(s) have no source in the index (platform tables, external deps).");
                break;
            }
        } while (fixupAdded > 0 && fixupRound < 10);

        // Merge BFS-unresolved targets into finalMissingObjects so the stub
        // generator emits blank shells for them. Dedup against names the
        // trial-compile fixup may already have captured.
        if (unresolved.Count > 0)
        {
            var alreadyCaptured = new HashSet<(string, string)>(
                finalMissingObjects.Select(m => (m.TypeName, m.Name)),
                EqualityComparer<(string, string)>.Create(
                    (a, b) => string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase),
                    x => StringComparer.OrdinalIgnoreCase.GetHashCode(x.Item1)
                       ^ StringComparer.OrdinalIgnoreCase.GetHashCode(x.Item2)));
            int addedFromBfs = 0;
            foreach (var item in unresolved)
            {
                if (alreadyCaptured.Add(item))
                {
                    finalMissingObjects.Add(item);
                    addedFromBfs++;
                }
            }
            if (addedFromBfs > 0)
                Console.Error.WriteLine($"  + {addedFromBfs} BFS-unresolved object(s) queued for stub generation.");
        }

        // Filter pure-numeric names (`Codeunit::25` syntax yields the literal "25" as
        // a "name" — it is an object id, not an object name) and root namespace
        // identifiers (e.g. "Microsoft", "Azure") that the BFS extractor mistakenly
        // captured from namespace-qualified expressions like
        // `Record Microsoft.X.Y."Some Table"`. Stubbing the namespace identifier as
        // a top-level table named "Microsoft" causes AL0275 ambiguous-reference
        // errors against the merged Microsoft namespace from real BC source.
        if (finalMissingObjects.Count > 0)
        {
            var rootNamespaceIdents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nsRegex = new System.Text.RegularExpressions.Regex(
                @"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_]*)\b",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            foreach (var src in allExtracted.Values)
                foreach (System.Text.RegularExpressions.Match m in nsRegex.Matches(src))
                    rootNamespaceIdents.Add(m.Groups[1].Value);

            var before = finalMissingObjects.Count;
            finalMissingObjects = finalMissingObjects
                .Where(m =>
                    !string.IsNullOrEmpty(m.Name)
                    && !m.Name.All(char.IsDigit)
                    && !rootNamespaceIdents.Contains(m.Name))
                .ToList();
            var dropped = before - finalMissingObjects.Count;
            if (dropped > 0)
                Console.Error.WriteLine($"  Dropped {dropped} bogus stub target(s) (numeric ids or root namespace identifiers).");
        }

        // Stub-generation pass: write blank-shell AL stubs for each truly-unresolvable
        // missing object. Without stubs, consumer locals bind at NavTypeKind.None and
        // crash EmitFieldInitializer in the BC emitter.
        // First, exclude objects already declared by the symbol packages — generating stubs
        // for those would cause AL0197 duplicate-declaration errors at compile-dep time.
        if (finalMissingObjects.Count > 0 && packagePaths is { Count: > 0 })
        {
            var packageProvided = CollectPackageDeclaredObjects(packagePaths);
            if (packageProvided.Count > 0)
            {
                var before = finalMissingObjects.Count;
                finalMissingObjects = finalMissingObjects
                    .Where(m => !packageProvided.Contains((m.TypeName, m.Name)))
                    .ToList();
                var excluded = before - finalMissingObjects.Count;
                if (excluded > 0)
                    Console.Error.WriteLine($"  Excluded {excluded} object(s) already declared by symbol packages.");
            }
        }

        if (finalMissingObjects.Count > 0)
        {
            var stubsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int stubIdCounter = StubIdBase;
            int stubCount = 0;
            foreach (var (typeName, objectName) in finalMissingObjects)
            {
                var dedupeKey = $"{typeName}|{objectName}";
                if (!stubsSeen.Add(dedupeKey)) continue;

                // Defensive: an object name that still contains a `"` is malformed
                // (a parsing bug fed it through). Emitting a stub for it produces
                // syntactically broken AL (`""Foo"`), so drop it.
                if (string.IsNullOrEmpty(objectName) || objectName.Contains('"')) continue;

                // Strategy A for #1521: emit one stub per consumer namespace.
                // BC name resolution for unqualified references searches the file's
                // declared namespace + `using` namespaces — NOT the global root. So
                // a single root-level stub is invisible to consumers that declared
                // `namespace X;`. Emitting a copy of the stub inside each consumer
                // namespace makes the unqualified reference resolve.
                var consumerScopes = FindConsumerNamespaces(allExtracted, objectName);
                if (consumerScopes.Count == 0)
                {
                    var fallbackApp = FindFirstConsumerApp(allExtracted, objectName);
                    if (string.IsNullOrEmpty(fallbackApp)) continue;
                    consumerScopes = new List<(string, string)> { (fallbackApp, "") };
                }

                foreach (var (consumerApp, ns) in consumerScopes)
                {
                    int stubId = stubIdCounter++;
                    var stubBody = GenerateStub(typeName, objectName, stubId, allExtracted);
                    var stubSource = string.IsNullOrEmpty(ns)
                        ? stubBody
                        : $"namespace {ns};\n{stubBody}";
                    var safeName = MakeSafeName(objectName) + $"_{stubId}";
                    var stubKey = $"{consumerApp}/__GeneratedStubs__/{safeName}.al";
                    allExtracted[stubKey] = stubSource;
                    stubCount++;
                }
            }

            if (stubCount > 0)
                Console.Error.WriteLine($"Generated {stubCount} stub(s) for missing platform objects.");
        }

        // Namespace stub-generation pass:
        //
        // For every `using <ns>;` directive in the extracted slice that has no corresponding
        // `namespace <ns>;` declaration, emit a blank-shell interface file so the BC compiler
        // can resolve the namespace reference (AL0791).  The scan runs to a fixed point so that
        // namespace declarations pulled in from the dep-source index also get their own `using`
        // directives resolved.
        {
            var usingRegex = new System.Text.RegularExpressions.Regex(
                @"^\s*using\s+([\w.]+)\s*;",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            var nsStatementRegex = new System.Text.RegularExpressions.Regex(
                @"^\s*namespace\s+([\w.]+)\s*[;{]",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            // Reverse-lookup: namespace name → dep source file.
            var nsDeclaredInDepSource = new Dictionary<string, (string FileName, string Source)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, depFileName, depSource) in appSources)
            {
                foreach (System.Text.RegularExpressions.Match m in nsStatementRegex.Matches(depSource))
                {
                    var ns = m.Groups[1].Value;
                    if (!nsDeclaredInDepSource.ContainsKey(ns))
                        nsDeclaredInDepSource[ns] = (depFileName, depSource);
                }
            }

            var nsStubsSeen      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nsScannedKeys    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nsPendingUsings  = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var nsDeclared       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int nsStubCount      = 0;

            // Scans any files in allExtracted not yet processed and generates namespace stubs.
            // Returns true if any new stubs were emitted or new files were pulled in.
            bool RunNamespaceScan()
            {
                bool changed = false;
                foreach (var (fileName, source) in allExtracted.ToList()) // snapshot to allow mutation
                {
                    if (!nsScannedKeys.Add(fileName)) continue;
                    changed = true;
                    var app = GetAppOfFile(fileName);
                    foreach (System.Text.RegularExpressions.Match m in usingRegex.Matches(source))
                    {
                        var ns = m.Groups[1].Value;
                        if (!nsPendingUsings.TryGetValue(ns, out var set))
                            nsPendingUsings[ns] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (!string.IsNullOrEmpty(app)) set.Add(app);
                    }
                    foreach (System.Text.RegularExpressions.Match m in nsStatementRegex.Matches(source))
                        nsDeclared.Add(m.Groups[1].Value);
                }

                foreach (var (ns, appsUsing) in nsPendingUsings)
                {
                    if (nsDeclared.Contains(ns)) continue;

                    if (nsDeclaredInDepSource.TryGetValue(ns, out var entry))
                    {
                        if (!allExtracted.ContainsKey(entry.FileName))
                        {
                            allExtracted[entry.FileName] = entry.Source;
                            Console.Error.WriteLine($"  + {entry.FileName} (namespace anchor for '{ns}')");
                            // Follow the anchor's own object references — without this,
                            // any object the anchor file references (page parts, RunObject
                            // targets, table relations, etc.) is silently dropped, which
                            // surfaces downstream as "AL transpilation: no C# code was
                            // generated" with no diagnostic. Re-run BFS so the new refs
                            // are resolved against the index.
                            EnqueueTransitiveDeps(entry.Source, visited, pending);
                            ExpandSlice(pending, visited, index, allExtracted);
                            changed = true;
                        }
                        nsDeclared.Add(ns);
                        continue;
                    }

                    if (!nsStubsSeen.Add(ns)) continue;

                    var consumerApp = appsUsing.FirstOrDefault() ?? "";
                    if (string.IsNullOrEmpty(consumerApp))
                    {
                        foreach (var fn in allExtracted.Keys)
                        {
                            consumerApp = GetAppOfFile(fn);
                            if (!string.IsNullOrEmpty(consumerApp)) break;
                        }
                    }
                    if (string.IsNullOrEmpty(consumerApp)) continue;

                    var safeSuffix  = System.Text.RegularExpressions.Regex.Replace(ns, @"[^A-Za-z0-9]", "");
                    var stubContent = $"namespace {ns};\ninterface \"__StubNamespaceAnchor_{safeSuffix}\" {{ }}\n";
                    var stubKey     = $"{consumerApp}/__GeneratedStubs__/_namespaces/{safeSuffix}.al";
                    if (allExtracted.TryAdd(stubKey, stubContent))
                    {
                        nsStubCount++;
                        changed = true;
                    }
                }
                return changed;
            }

            // Initial namespace scan to fixed point (handles cascading using deps).
            bool nsProgress = true;
            while (nsProgress) nsProgress = RunNamespaceScan();

            if (nsStubCount > 0)
                Console.Error.WriteLine($"Generated {nsStubCount} namespace stub(s) for unresolved using directives.");

        }


        // 7. Write extracted files to output directory.
        //    DotNet-referencing procedures are replaced with Error() calls so the file
        //    compiles cleanly and fails loudly if ever reached at runtime.
        int written = 0;
        int strippedProcedures = 0;
        int skippedFiles = 0;
        int strippedControlAddInFiles = 0;
        int strippedUserControlHostPages = 0;
        int strippedUserControls = 0;
        int strippedHelpProps = 0;
        int strippedLayoutProps = 0;
        foreach (var (fileName, source) in allExtracted)
        {
            // 1. ControlAddIn objects need a JS/HTML resource sidecar that the runner cannot
            //    serve (no browser, no service tier). Skip the file entirely.
            if (IsControlAddInObjectFile(source))
            {
                strippedControlAddInFiles++;
                continue;
            }

            // 2. UserControlHost pages must contain exactly one usercontrol (AL0874).
            //    After usercontrol stripping the page is invalid — drop the whole file.
            //    Anything calling Page.Run on it will surface a missing-page error,
            //    which is the right signal: that page never could have run here.
            if (IsUserControlHostPage(source))
            {
                strippedUserControlHostPages++;
                continue;
            }

            // 3. Strip PageUserControl blocks and their dependent event triggers.
            var (uncontrolledSource, controlsRemoved) = StripPageUserControls(source);
            strippedUserControls += controlsRemoved;

            // 4. Strip ContextSensitiveHelpPage property from all pages (AL0543 trigger).
            var (helpStrippedSource, helpRemoved) = StripContextSensitiveHelpPage(uncontrolledSource);
            strippedHelpProps += helpRemoved;

            // 5. Strip report layout sections — Windows backslash paths in LayoutFile
            //    trip AL0363 on Linux, and the runner has no RDLC subsystem anyway.
            var (layoutStrippedSource, layoutsRemoved) = StripReportLayouts(helpStrippedSource);
            strippedLayoutProps += layoutsRemoved;

            var (strippedSource, stripped) = StripDotNetProcedures(layoutStrippedSource, fileName);
            if (strippedSource == null) { skippedFiles++; continue; } // pure assembly declaration, nothing callable

            var outPath = Path.Combine(outputDir, fileName);
            var dir = Path.GetDirectoryName(outPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, strippedSource, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            written++;
            strippedProcedures += stripped;
        }

        if (strippedProcedures > 0 || skippedFiles > 0)
            Console.Error.WriteLine($"DotNet stripping: {strippedProcedures} procedure(s) replaced with Error(), {skippedFiles} pure-assembly file(s) skipped.");
        if (strippedControlAddInFiles > 0 || strippedUserControls > 0 || strippedUserControlHostPages > 0)
            Console.Error.WriteLine($"ControlAddIn stripping: {strippedControlAddInFiles} controladdin file(s) dropped, {strippedUserControlHostPages} UserControlHost page(s) dropped, {strippedUserControls} usercontrol block(s) removed.");
        if (strippedHelpProps > 0)
            Console.Error.WriteLine($"ContextSensitiveHelpPage stripping: {strippedHelpProps} propertie(s) removed.");
        if (strippedLayoutProps > 0)
            Console.Error.WriteLine($"Report layout stripping: {strippedLayoutProps} layout block(s)/properties removed.");

        // Copy each extracted app's app.json so compile-dep can use the real per-app
        // identity (id/name/publisher/version) instead of a synthetic one. Per-app
        // identity is required for InternalsVisibleTo grants and for resolving cross-app
        // object name conflicts the way real BC does.
        int manifestsCopied = 0;
        foreach (var appDir in Directory.GetDirectories(outputDir))
        {
            var appName = Path.GetFileName(appDir);
            foreach (var depSource in depSourceList)
            {
                if (!Directory.Exists(depSource)) continue;
                var srcManifest = Path.Combine(depSource, appName, "app.json");
                if (File.Exists(srcManifest))
                {
                    // Sanitize unparseable build placeholders (e.g. $(app_currentVersion))
                    // when copying. Microsoft's BC source repos ship app.json with these
                    // placeholders unsubstituted; if any reach the BC compiler's
                    // NavAppManifest reader they trigger a parse exception and the AL files
                    // get attributed to a phantom "(Unknown)" extension that collides with
                    // the slice's namespace declarations (AL0275). See issue #1521.
                    try
                    {
                        var raw = File.ReadAllText(srcManifest);
                        var sanitized = DepCompiler.SanitizeManifestVersions(raw);
                        File.WriteAllText(Path.Combine(appDir, "app.json"), sanitized);
                    }
                    catch
                    {
                        File.Copy(srcManifest, Path.Combine(appDir, "app.json"), overwrite: true);
                    }
                    manifestsCopied++;
                    break;
                }
            }
        }
        if (manifestsCopied > 0)
            Console.Error.WriteLine($"Per-app manifests: copied {manifestsCopied} app.json file(s).");

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
        Dictionary<string, string> allExtracted,
        HashSet<(string TypeName, string Name)>? unresolved = null)
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

                    // O(1) definition lookup. A name can resolve to multiple files when
                    // the same object is redefined across modules (obsolete + new
                    // location after a CLEANSCHEMA migration). Pull every occurrence —
                    // the compiler with preprocessor symbols selects the active one.
                    var baseApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bool foundDef = false;
                    if (index.Definitions.TryGetValue(typeName, out var defs) &&
                        defs.TryGetValue(name, out var defList))
                    {
                        foundDef = true;
                        foreach (var (fileName, source) in defList)
                        {
                            baseApps.Add(GetAppOfFile(fileName));
                            if (allExtracted.ContainsKey(fileName)) continue;
                            allExtracted[fileName] = source;
                            Console.Error.WriteLine($"  + {fileName}");
                            EnqueueTransitiveDeps(source, visited, pending);
                        }
                    }

                    // Pull only same-app extensions. An extension that lives in a different
                    // app from the base it extends is treated as out-of-scope — pulling it
                    // would drag in that foreign app's transitive deps. See issue #1544.
                    // Fidelity gap (cross-app extension side-effects) tracked in #1545.
                    if (index.Extensions.TryGetValue(typeName, out var exts) &&
                        exts.TryGetValue(name, out var extList))
                    {
                        foreach (var (fileName, source) in extList)
                        {
                            if (allExtracted.ContainsKey(fileName)) continue;
                            if (baseApps.Count > 0 && !baseApps.Contains(GetAppOfFile(fileName))) continue;
                            allExtracted[fileName] = source;
                            Console.Error.WriteLine($"  + {fileName}");
                            EnqueueTransitiveDeps(source, visited, pending);
                        }
                    }

                    // No base definition found in any dep app. Track for stub generation —
                    // without a stub the consumer's reference will fail at compile-dep
                    // time as AL0118 (which TrialCompileMissing's AL0185-only pattern
                    // does not capture, so the fixup loop misses it).
                    if (!foundDef && unresolved is not null)
                        unresolved.Add((typeName, name));
                }
            }
        } while (anyWork);
    }

    /// <summary>
    /// Pulls in extensions whose host app is itself part of the slice (i.e. has at
    /// least one extracted file already) but whose base object lives in a different
    /// app. The same-app guard inside <see cref="ExpandSlice"/> would otherwise drop
    /// these, even though their host app is in scope and the consumer transitively
    /// uses values/members the extension contributes.
    /// </summary>
    private static void ExpandCrossAppExtensions(
        DepIndex index,
        Dictionary<string, HashSet<string>> visited,
        Dictionary<string, Queue<string>> pending,
        Dictionary<string, string> allExtracted,
        HashSet<(string TypeName, string Name)>? unresolved = null)
    {
        int added;
        do
        {
            added = 0;
            var appsInSlice = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fn in allExtracted.Keys)
            {
                var app = GetAppOfFile(fn);
                if (!string.IsNullOrEmpty(app)) appsInSlice.Add(app);
            }
            foreach (var typeName in AllTypes)
            {
                if (!index.Extensions.TryGetValue(typeName, out var exts)) continue;
                if (!visited.TryGetValue(typeName, out var visitedNames)) continue;
                foreach (var (baseName, extList) in exts)
                {
                    if (!visitedNames.Contains(baseName)) continue;
                    foreach (var (fileName, source) in extList)
                    {
                        if (allExtracted.ContainsKey(fileName)) continue;
                        var extApp = GetAppOfFile(fileName);
                        if (extApp.Length == 0 || !appsInSlice.Contains(extApp)) continue;
                        allExtracted[fileName] = source;
                        Console.Error.WriteLine($"  + {fileName} (cross-app extension)");
                        added++;
                        EnqueueTransitiveDeps(source, visited, pending);
                    }
                }
            }
            if (added > 0)
                ExpandSlice(pending, visited, index, allExtracted, unresolved);
        } while (added > 0);
    }

    /// <summary>
    /// Returns the set of dep apps containing a definition of (typeName, objectName).
    /// Empty when the object is not in the dep index (e.g. consumer-defined). Used to
    /// gate cross-app pull-in of extensions and event subscribers.
    /// </summary>
    private static HashSet<string> GetAppsForObject(DepIndex index, string typeName, string objectName)
    {
        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (index.Definitions.TryGetValue(typeName, out var byName) &&
            byName.TryGetValue(objectName, out var list))
            foreach (var (fn, _) in list) apps.Add(GetAppOfFile(fn));
        return apps;
    }

    /// <summary>
    /// First path segment of <paramref name="fileName"/>, treated as the app folder name.
    /// LoadDepSources stores file names as <c>Path.GetRelativePath(depRoot, file)</c>, so
    /// for layouts like <c>.deps-source/&lt;App&gt;/...</c> this yields the app name.
    /// Returns "" for flat layouts; callers treat empty as "no app boundary, allow".
    /// </summary>
    private static string GetAppOfFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "";
        int slash = fileName.IndexOfAny(new[] { '/', '\\' });
        return slash > 0 ? fileName[..slash] : "";
    }

    /// <summary>
    /// Returns the app directory of the first file in <paramref name="allExtracted"/> whose
    /// source contains <paramref name="objectName"/>. Falls back to the first app found if
    /// no file directly references the name.
    /// </summary>
    private static string FindFirstConsumerApp(Dictionary<string, string> allExtracted, string objectName)
    {
        foreach (var (fileName, source) in allExtracted)
        {
            if (source.Contains(objectName, StringComparison.OrdinalIgnoreCase))
            {
                var app = GetAppOfFile(fileName);
                if (!string.IsNullOrEmpty(app)) return app;
            }
        }
        // Fallback: first app in the slice
        foreach (var fileName in allExtracted.Keys)
        {
            var app = GetAppOfFile(fileName);
            if (!string.IsNullOrEmpty(app)) return app;
        }
        return "";
    }

    /// <summary>
    /// Find the distinct (app, namespace) pairs of every non-stub source file in
    /// <paramref name="allExtracted"/> that references <paramref name="objectName"/>
    /// as a quoted token (e.g. <c>"Sales Order Print Option"</c>). The namespace is
    /// taken from the file's <c>namespace X;</c> declaration; files without one map
    /// to the empty string (global root).
    ///
    /// Used by the auto-stub pass to emit one stub copy per consumer namespace —
    /// see issue #1521 — because BC name resolution for unqualified references
    /// searches the file's declared namespace + `using` namespaces but not the
    /// global root, so a single root-level stub is invisible to namespace-aware
    /// consumers.
    /// </summary>
    private static List<(string consumerApp, string ns)> FindConsumerNamespaces(
        Dictionary<string, string> allExtracted, string objectName)
    {
        var result = new HashSet<(string, string)>();
        var nsRegex = new System.Text.RegularExpressions.Regex(
            @"^\s*namespace\s+([\w.]+)\s*[;{]",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        var quotedToken = "\"" + objectName + "\"";
        foreach (var (fileName, source) in allExtracted)
        {
            // Don't recurse on previously generated stubs.
            if (fileName.Contains("__GeneratedStubs__", StringComparison.OrdinalIgnoreCase))
                continue;
            if (source.IndexOf(quotedToken, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var app = GetAppOfFile(fileName);
            if (string.IsNullOrEmpty(app)) continue;

            var nsMatch = nsRegex.Match(source);
            var ns = nsMatch.Success ? nsMatch.Groups[1].Value : "";
            result.Add((app, ns));
        }
        return result.ToList();
    }

    /// <summary>
    /// Generates a minimal blank-shell AL stub source for the given object type and name.
    /// </summary>
    private static string GenerateStub(string typeName, string objectName, int id) =>
        typeName switch
        {
            "Table"     => $"table {id} \"{objectName}\" {{ fields {{ field(1; \"DummyKey\"; Code[20]) {{}} }} keys {{ key(PK; \"DummyKey\") {{}} }} }}",
            "Page"      => $"page {id} \"{objectName}\" {{ }}",
            "Codeunit"  => $"codeunit {id} \"{objectName}\" {{ }}",
            "Enum"      => $"enum {id} \"{objectName}\" {{ value(0; Default) {{}} }}",
            "Report"    => $"report {id} \"{objectName}\" {{ }}",
            "Query"     => $"query {id} \"{objectName}\" {{ elements {{ dataitem(DummyDI; Integer) {{ column(DummyCol; Number) {{}} }} }} }}",
            "XmlPort"   => $"xmlport {id} \"{objectName}\" {{ schema {{ textelement(root) {{}} }} }}",
            "Interface" => $"interface \"{objectName}\" {{ }}",
            _           => $"codeunit {id} \"{objectName}\" {{ }}",
        };

    /// <summary>
    /// Generates a stub enriched with members (procedures / fields) discovered by
    /// scanning <paramref name="allExtracted"/> for variables typed as the missing
    /// object and member-access patterns on those variables.
    ///
    /// For pages and codeunits, every <c>varName.MethodName(args)</c> call observed
    /// becomes a forgiving <c>procedure MethodName(arg1: Variant; ...) begin end;</c>
    /// declaration so AL0132 stops firing at the call site.
    ///
    /// For tables, every <c>varName."Field Name"</c> reference becomes a
    /// <c>field(idN; "Field Name"; Variant) {}</c> declaration so AL0118 stops firing.
    ///
    /// This is the "auto-stub method/field modeling" half of issue #1521 final stretch.
    /// </summary>
    private static string GenerateStub(string typeName, string objectName, int id,
        Dictionary<string, string> allExtracted)
    {
        if (typeName != "Page" && typeName != "Codeunit" && typeName != "Table")
            return GenerateStub(typeName, objectName, id);

        var members = DiscoverStubMembers(typeName, objectName, allExtracted);

        if (typeName == "Page")
        {
            if (members.Methods.Count == 0) return GenerateStub(typeName, objectName, id);
            var procs = string.Join(" ", members.Methods.Select(m => RenderProcedureStub(m)));
            return $"page {id} \"{objectName}\" {{ {procs} }}";
        }
        if (typeName == "Codeunit")
        {
            if (members.Methods.Count == 0) return GenerateStub(typeName, objectName, id);
            var procs = string.Join(" ", members.Methods.Select(m => RenderProcedureStub(m)));
            return $"codeunit {id} \"{objectName}\" {{ {procs} }}";
        }
        // Table
        var fieldDecls = new List<string> { "field(1; \"DummyKey\"; Code[20]) {}" };
        int nextFieldId = 50000;
        foreach (var f in members.Fields)
        {
            // Skip if it collides with the synthesized PK name.
            if (string.Equals(f, "DummyKey", StringComparison.OrdinalIgnoreCase)) continue;
            // BC's compiler accepts Variant on tables only as Blob+text — keep it broad
            // by using Text[250]; that lets both <Rec>."X" reads/writes and FieldNo("X")
            // resolve without coupling to the real type.
            fieldDecls.Add($"field({nextFieldId++}; \"{f}\"; Text[250]) {{}}");
        }
        var fields = string.Join(" ", fieldDecls);
        return $"table {id} \"{objectName}\" {{ fields {{ {fields} }} keys {{ key(PK; \"DummyKey\") {{}} }} }}";
    }

    /// <summary>
    /// Render a forgiving procedure-stub for a discovered (name, arity) tuple. Any
    /// signature with the right arity satisfies BC's overload resolution because
    /// every parameter is typed <c>Variant</c>.
    /// </summary>
    private static string RenderProcedureStub((string Name, int Arity) m)
    {
        if (m.Arity == 0)
            return $"procedure \"{m.Name}\"() begin end;";
        var args = string.Join("; ", Enumerable.Range(1, m.Arity).Select(i => $"arg{i}: Variant"));
        return $"procedure \"{m.Name}\"({args}) begin end;";
    }

    private record StubMembers(List<(string Name, int Arity)> Methods, List<string> Fields);

    /// <summary>
    /// Scan the slice for variables typed as <c>typeName "objectName"</c> and harvest
    /// every member-access usage on those variables. Returns the distinct method calls
    /// (with arity) and the distinct quoted-field references.
    /// </summary>
    private static StubMembers DiscoverStubMembers(string typeName, string objectName,
        Dictionary<string, string> allExtracted)
    {
        var methods = new HashSet<(string Name, int Arity)>();
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Variable declaration patterns:
        //   var Foo: Page "Power BI Element Addin Host";
        //   Foo: Codeunit "X";
        //   Foo: Record "Y";
        var typeKw = typeName switch
        {
            "Page"     => "Page",
            "Codeunit" => "Codeunit",
            "Table"    => "Record", // tables are referenced as Record "X"
            _          => typeName,
        };

        // Match (with optional whitespace) the declaration: identifier ":" typeKw "ObjectName"
        // case-insensitive on the typeKw.
        var declRx = new System.Text.RegularExpressions.Regex(
            @"\b([A-Za-z_][A-Za-z0-9_]*)\s*:\s*" + typeKw + @"\s+""" +
                System.Text.RegularExpressions.Regex.Escape(objectName) + @"""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var src in allExtracted.Values)
        {
            // Quick reject for files that don't reference the object.
            if (!src.Contains(objectName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (System.Text.RegularExpressions.Match dm in declRx.Matches(src))
            {
                var varName = dm.Groups[1].Value;
                CollectMemberAccess(src, varName, methods, fields);
            }
        }

        return new StubMembers(methods.ToList(), fields.ToList());
    }

    /// <summary>
    /// Find every <c>varName.MemberToken</c> occurrence in <paramref name="src"/>.
    /// If the member token is followed by <c>(</c>, count top-level commas inside
    /// the parenthesis to derive arity and record (name, arity). Otherwise, treat
    /// it as a quoted-field reference (only when the token itself is quoted).
    /// </summary>
    private static void CollectMemberAccess(string src, string varName,
        HashSet<(string Name, int Arity)> methods, HashSet<string> fields)
    {
        // Match: varName.<member> where member is either an unquoted identifier
        // or a quoted "Field Name". Followed optionally by an arg list.
        var rx = new System.Text.RegularExpressions.Regex(
            @"\b" + System.Text.RegularExpressions.Regex.Escape(varName) +
            @"\s*\.\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match m in rx.Matches(src))
        {
            var quoted = m.Groups[1].Success ? m.Groups[1].Value : null;
            var ident  = m.Groups[2].Success ? m.Groups[2].Value : null;
            int after = m.Index + m.Length;
            // Skip whitespace before optional '('
            while (after < src.Length && char.IsWhiteSpace(src[after])) after++;
            bool isCall = after < src.Length && src[after] == '(';

            if (isCall)
            {
                int arity = CountTopLevelArgs(src, after);
                var name = quoted ?? ident!;
                methods.Add((name, arity));
            }
            else
            {
                // Treat quoted member as a field reference. Skip unquoted idents to
                // avoid polluting tables with random accesses (e.g. property reads).
                if (quoted != null) fields.Add(quoted);
            }
        }
    }

    /// <summary>
    /// Given <paramref name="src"/> and the index of an opening <c>(</c>, return the
    /// number of top-level comma-separated args inside the matching <c>)</c>.
    /// Handles nested parens and string literals. Returns 0 for an empty arg list.
    /// </summary>
    private static int CountTopLevelArgs(string src, int openIdx)
    {
        int i = openIdx + 1;
        int depth = 1;
        int commas = 0;
        bool nonWs = false;
        bool inSingle = false;
        while (i < src.Length && depth > 0)
        {
            char c = src[i];
            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                i++; continue;
            }
            switch (c)
            {
                case '\'': inSingle = true; nonWs = true; break;
                case '(':  depth++; nonWs = true; break;
                case ')':  depth--; if (depth == 0) goto done; nonWs = true; break;
                case ',':  if (depth == 1) commas++; nonWs = true; break;
                default:   if (!char.IsWhiteSpace(c)) nonWs = true; break;
            }
            i++;
        }
        done:
        if (!nonWs) return 0;
        return commas + 1;
    }

    /// <summary>Strips all non-alphanumeric characters from a name to make it safe for a filename.</summary>
    private static string MakeSafeName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"[^A-Za-z0-9]", "");

    /// <summary>
    /// Reads SymbolReference.json from every .app file in the given package paths (files or directories)
    /// and returns a set of (TypeName, ObjectName) pairs they declare. Used to exclude package-provided
    /// objects from auto-stub generation so compile-dep does not see AL0197 duplicate-declaration errors.
    /// </summary>
    private static HashSet<(string TypeName, string Name)> CollectPackageDeclaredObjects(List<string> packagePaths)
    {
        var result = new HashSet<(string, string)>(
            EqualityComparer<(string, string)>.Create(
                (a, b) => string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase),
                x => StringComparer.OrdinalIgnoreCase.GetHashCode(x.Item1)
                   ^ StringComparer.OrdinalIgnoreCase.GetHashCode(x.Item2)));

        // Map from SymbolReference.json property name → AL type name
        var sectionToType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tables"]    = "Table",
            ["Pages"]     = "Page",
            ["Codeunits"] = "Codeunit",
            ["EnumTypes"] = "Enum",
            ["Interfaces"] = "Interface",
            ["Reports"]   = "Report",
            ["Queries"]   = "Query",
            ["XmlPorts"]  = "XmlPort",
        };

        var appFiles = new List<string>();
        foreach (var p in packagePaths)
        {
            if (File.Exists(p) && p.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                appFiles.Add(p);
            else if (Directory.Exists(p))
                appFiles.AddRange(Directory.GetFiles(p, "*.app", SearchOption.TopDirectoryOnly));
        }

        foreach (var appPath in appFiles)
        {
            try
            {
                var fileBytes = File.ReadAllBytes(appPath);
                int zipOffset = 0;
                if (fileBytes.Length >= 8
                    && fileBytes[0] == (byte)'N' && fileBytes[1] == (byte)'A'
                    && fileBytes[2] == (byte)'V' && fileBytes[3] == (byte)'X')
                    zipOffset = (int)BitConverter.ToUInt32(fileBytes, 4);

                using var zipStream = new MemoryStream(fileBytes, zipOffset, fileBytes.Length - zipOffset);
                using var zip = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read);

                var symEntry = zip.GetEntry("SymbolReference.json");
                if (symEntry == null) continue;

                using var reader = new StreamReader(symEntry.Open(), System.Text.Encoding.UTF8);
                var json = reader.ReadToEnd();
                if (json.Length > 0 && json[0] == '\uFEFF') json = json[1..]; // strip BOM

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                CollectFromElement(root, sectionToType, result);
            }
            catch { /* ignore unreadable packages */ }
        }

        return result;
    }

    /// <summary>
    /// Walks both the top-level object arrays (Tables/Pages/...) and the nested
    /// Namespaces tree of a SymbolReference.json element, adding every (TypeName, Name)
    /// pair found. Platform packages such as System.app put all objects under
    /// nested Namespaces; application packages typically use flat top-level arrays.
    /// Both shapes are valid in BC SymbolReference.json.
    /// </summary>
    private static void CollectFromElement(
        System.Text.Json.JsonElement element,
        Dictionary<string, string> sectionToType,
        HashSet<(string TypeName, string Name)> result)
    {
        foreach (var (section, typeName) in sectionToType)
        {
            if (!element.TryGetProperty(section, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("Name", out var nameEl))
                {
                    var name = nameEl.GetString();
                    if (!string.IsNullOrEmpty(name))
                        result.Add((typeName, name));
                }
            }
        }

        if (element.TryGetProperty("Namespaces", out var nsArr)
            && nsArr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var ns in nsArr.EnumerateArray())
                CollectFromElement(ns, sectionToType, result);
        }
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
        // AL keywords are case-insensitive, so compare with OrdinalIgnoreCase —
        // BC Base Application uses both "Page" and "page" (lowercase) liberally.
        foreach (var subtyped in root.DescendantNodes().OfType<SubtypedDataTypeSyntax>())
        {
            var typeName = subtyped.TypeName.ToFullString().Trim();
            var name = UnquoteIdentifier(subtyped.Subtype?.ToFullString().Trim());
            if (string.IsNullOrEmpty(name)) continue;

            if (string.Equals(typeName, "Codeunit",  StringComparison.OrdinalIgnoreCase)) result.Codeunits.Add(name);
            else if (string.Equals(typeName, "Page",      StringComparison.OrdinalIgnoreCase)) result.Pages.Add(name);
            else if (string.Equals(typeName, "Report",    StringComparison.OrdinalIgnoreCase)) result.Reports.Add(name);
            else if (string.Equals(typeName, "Query",     StringComparison.OrdinalIgnoreCase)) result.Queries.Add(name);
            else if (string.Equals(typeName, "XmlPort",   StringComparison.OrdinalIgnoreCase)) result.XmlPorts.Add(name);
            else if (string.Equals(typeName, "Interface", StringComparison.OrdinalIgnoreCase)) result.Interfaces.Add(name);
            // "Record" is covered by RecordTypeReferenceSyntax above.
            // "Enum" is covered by EnumDataTypeSyntax below.
        }

        // Report dataitem tables: dataitem(d; Customer) { dataitem(d2; "Cust. Ledger Entry") { ... } }
        foreach (var di in root.DescendantNodes().OfType<ReportDataItemSyntax>())
        {
            var name = UnquoteIdentifier(di.DataItemTable?.ToFullString().Trim());
            if (!string.IsNullOrEmpty(name))
                result.Tables.Add(name);
        }

        // Property-based object references: SourceTable, RunObject, TableRelation, CalcFormula
        foreach (var prop in root.DescendantNodes().OfType<PropertySyntax>())
        {
            var propName = prop.Name?.Identifier.ValueText ?? "";
            switch (propName)
            {
                case "SourceTable":
                    // page/report: SourceTable = Customer
                    if (prop.Value is ObjectReferencePropertyValueSyntax srcTbl)
                    {
                        var name = UnquoteIdentifier(srcTbl.ObjectNameOrId?.ToFullString().Trim());
                        if (!string.IsNullOrEmpty(name)) result.Tables.Add(name);
                    }
                    break;

                case "RunObject":
                    // action: RunObject = Page "Customer Card" / Report "..." / Codeunit "..." etc.
                    // The object-type keyword is case-insensitive in AL ("page", "Page", "PAGE"
                    // are all valid). BC Base Application uses both forms — see e.g.
                    // JobProjectManagerRC.Page.al referencing `RunObject = page "FS Bookable
                    // Resource List";` — so compare with OrdinalIgnoreCase.
                    if (prop.Value is QualifiedObjectReferencePropertyValueSyntax runObj)
                    {
                        var objType = runObj.ObjectType.ToFullString().Trim();
                        var objName = UnquoteIdentifier(runObj.ObjectNameOrId?.ToFullString().Trim());
                        if (!string.IsNullOrEmpty(objName))
                        {
                            if      (string.Equals(objType, "Page",     StringComparison.OrdinalIgnoreCase)) result.Pages.Add(objName);
                            else if (string.Equals(objType, "Report",   StringComparison.OrdinalIgnoreCase)) result.Reports.Add(objName);
                            else if (string.Equals(objType, "Codeunit", StringComparison.OrdinalIgnoreCase)) result.Codeunits.Add(objName);
                            else if (string.Equals(objType, "Query",    StringComparison.OrdinalIgnoreCase)) result.Queries.Add(objName);
                            else if (string.Equals(objType, "XmlPort",  StringComparison.OrdinalIgnoreCase)) result.XmlPorts.Add(objName);
                        }
                    }
                    break;

                case "TableRelation":
                    // field property: TableRelation = Customer  OR  TableRelation = "Sales Header"."No."
                    // Walk all qualified names in the relation — handles simple, compound and conditional forms.
                    if (prop.Value != null)
                    {
                        // Simple unqualified: TableRelation = Customer
                        if (prop.Value is TableRelationPropertyValueSyntax tr)
                        {
                            var raw = tr.RelatedTableField?.ToFullString().Trim();
                            var tableName = UnquoteIdentifier(SplitTableFromTableField(raw));
                            if (!string.IsNullOrEmpty(tableName)) result.Tables.Add(tableName);
                        }
                        // Conditional/complex forms — pick up any qualified Table.Field refs
                        foreach (var qn in prop.Value.DescendantNodes().OfType<QualifiedNameSyntax>())
                        {
                            var tableName = UnquoteIdentifier(qn.Left?.ToFullString().Trim());
                            if (!string.IsNullOrEmpty(tableName)) result.Tables.Add(tableName);
                        }
                    }
                    break;

                case "CalcFormula":
                    // field: CalcFormula = Sum("Cust. Ledger Entry".Amount WHERE (...))
                    // All formula variants (Sum, Count, Avg, Min, Max, Exist, Lookup) contain
                    // qualified Table.Field names — collect the left (table) side of each.
                    if (prop.Value != null)
                        foreach (var qn in prop.Value.DescendantNodes().OfType<QualifiedNameSyntax>())
                        {
                            var tableName = UnquoteIdentifier(qn.Left?.ToFullString().Trim());
                            if (!string.IsNullOrEmpty(tableName)) result.Tables.Add(tableName);
                        }
                    break;
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
            // Namespace-qualified option access like
            //   Database::Microsoft.Manufacturing.StandardCost."Standard Cost Worksheet"
            // parses as OptionAccess(Database, Microsoft) wrapped in a MemberAccess chain
            // (Manufacturing → StandardCost → "Standard Cost Worksheet"). The leftmost
            // identifier ("Microsoft") is a namespace, NOT the table name. Walk up the
            // member-access chain so the trailing identifier becomes the table name —
            // otherwise a stub table named "Microsoft" is generated and clashes with
            // the Microsoft.* namespace at compile-dep time (AL0275).
            SyntaxNode candidate = optAccess;
            while (candidate.Parent is MemberAccessExpressionSyntax memberAccess
                   && memberAccess.Expression == candidate)
                candidate = memberAccess;
            string? name;
            if (candidate is MemberAccessExpressionSyntax topMemberAccess)
                name = UnquoteIdentifier(topMemberAccess.Name?.ToFullString().Trim());
            else
                name = UnquoteIdentifier(optAccess.Name?.ToFullString().Trim());
            if (string.IsNullOrEmpty(name)) continue;

            // AL is case-insensitive on object-type keywords (Xmlport vs XmlPort).
            switch (prefix.ToLowerInvariant())
            {
                case "codeunit": result.Codeunits.Add(name); break;
                case "page":     result.Pages.Add(name);     break;
                case "report":   result.Reports.Add(name);   break;
                case "query":    result.Queries.Add(name);   break;
                case "xmlport":  result.XmlPorts.Add(name);  break;
                // `DATABASE::"<Name>"` is the AL idiom for retrieving a table id
                // at runtime — semantically a table reference.
                case "database": result.Tables.Add(name);    break;
                // `Enum::RegexOptions::IgnoreCase` parses as nested OptionAccess; the inner
                // node has prefix "Enum" + name "RegexOptions" — pick that up so the enum
                // gets pulled into the slice.
                case "enum":     result.Enums.Add(name);     break;
            }
        }

        // Quoted-identifier static-call/option-access references — the AL pattern
        //   "Sales Order Print Option".FromInteger(123)
        //   "Sales Order Print Option"::"Order Confirmation"
        // is invisible to the OptionAccess switch above (which only matches bare
        // keyword prefixes like Codeunit/Page/Enum) and to ordinary record-method
        // dispatch. The LHS is parsed as an IdentifierName whose Identifier.Text
        // begins and ends with a literal quote. Treat such names as enum
        // references — in BC AL, only enums (and codeunits, but those are
        // resolved through other paths) accept this dotted/coloned static syntax,
        // and AL string literals use single quotes, so a quoted identifier here
        // is unambiguously an object name. See DocumentPrint.Codeunit.al for the
        // canonical Base App example.
        foreach (var ma in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (ma.Expression is not IdentifierNameSyntax idn) continue;
            var raw = idn.Identifier.Text;
            if (string.IsNullOrEmpty(raw) || raw.Length < 3 || raw[0] != '"') continue;
            var name = UnquoteIdentifier(raw);
            if (!string.IsNullOrEmpty(name)) result.Enums.Add(name!);
        }
        foreach (var optAccess in root.DescendantNodes().OfType<OptionAccessExpressionSyntax>())
        {
            if (optAccess.Expression is not IdentifierNameSyntax idn2) continue;
            var raw = idn2.Identifier.Text;
            if (string.IsNullOrEmpty(raw) || raw.Length < 3 || raw[0] != '"') continue;
            var name = UnquoteIdentifier(raw);
            if (!string.IsNullOrEmpty(name)) result.Enums.Add(name!);
        }

        // Page-part references inside page/pageextension layouts:
        //   part(Control1; "Target Page") { ... }
        // Without this, AL0185 "Page 'X' is missing" fires whenever the part target is
        // not independently reached by some other path. PagePartSyntax.PartName is an
        // ObjectNameOrIdSyntax — its full text is the quoted target name (or numeric id).
        foreach (var pagePart in root.DescendantNodes().OfType<PagePartSyntax>())
        {
            var name = UnquoteIdentifier(pagePart.PartName?.ToFullString().Trim());
            if (!string.IsNullOrEmpty(name) && !name.All(char.IsDigit))
                result.Pages.Add(name);
        }

        // Extension object base references: `pageextension ... extends "X"` means the slice
        // must contain the base object "X" so the extension can compile against it.
        foreach (var extObj in root.DescendantNodes().OfType<ApplicationObjectExtensionSyntax>())
        {
            var baseName = UnquoteIdentifier(extObj.BaseObject?.ToFullString().Trim());
            if (string.IsNullOrEmpty(baseName)) continue;
            switch (extObj.Kind)
            {
                case SyntaxKind.PageExtensionObject:   result.Pages.Add(baseName);    break;
                case SyntaxKind.TableExtensionObject:  result.Tables.Add(baseName);   break;
                case SyntaxKind.ReportExtensionObject: result.Reports.Add(baseName);  break;
                case SyntaxKind.EnumExtensionType:     result.Enums.Add(baseName);    break;
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
    // Compiler-driven fixup — trial compile to find statically-undetected refs
    // -----------------------------------------------------------------------

    /// <summary>
    /// Removes preprocessor directive lines that are unbalanced after DotNet stripping.
    /// When a DotNet var wrapped in #if/#endif has the #if removed (as part of the var's
    /// leading trivia) but the #endif remains (as leading trivia of the next declaration),
    /// the result is an orphaned #endif that causes AL0624 parse errors.
    /// This pass removes any #else/#endif with no matching #if, and any #if with no matching #endif.
    /// </summary>
    private static string RemoveOrphanedPreprocessorDirectives(string source)
    {
        if (!source.Contains('#')) return source;

        var lines = source.Split('\n');
        var result = new System.Text.StringBuilder(source.Length);
        int depth = 0;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.TrimStart();
            if (trimmed.StartsWith("#if", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("#ifdef", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
                result.Append(rawLine).Append('\n');
            }
            else if (trimmed.StartsWith("#else", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("#elif", StringComparison.OrdinalIgnoreCase))
            {
                if (depth > 0)
                    result.Append(rawLine).Append('\n');
                // else: orphaned #else — skip it
            }
            else if (trimmed.StartsWith("#endif", StringComparison.OrdinalIgnoreCase))
            {
                if (depth > 0)
                {
                    depth--;
                    result.Append(rawLine).Append('\n');
                }
                // else: orphaned #endif — skip it
            }
            else
            {
                result.Append(rawLine).Append('\n');
            }
        }

        // Remove the trailing \n we added to the last line if source didn't end with one.
        var text = result.ToString();
        if (!source.EndsWith('\n') && text.EndsWith('\n'))
            text = text[..^1];
        return text;
    }

    private static readonly System.Text.RegularExpressions.Regex MissingSymbolPattern =
        new(@"(Codeunit|Table|Page|Enum|Interface|Report|Query|XmlPort)\s+'([^']+)'\s+is missing",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Trial-compile the given AL sources and return the set of (typeName, objectName)
    /// pairs reported as missing by the BC compiler. DotNet misses are ignored — those
    /// are an architectural limit, not resolvable from source.
    /// </summary>
    /// <param name="resolvedPkgDirs">
    /// Already-resolved package directories (from <see cref="AlTranspiler.ResolvePackagePaths"/>).
    /// When provided, the BC compiler loads these packages so that objects declared by
    /// platform DLLs or embedded AL source inside the packages are not reported as missing.
    /// </param>
    private static List<(string TypeName, string Name)> TrialCompileMissing(
        List<string> sources,
        List<string>? resolvedPkgDirs = null)
    {
        if (sources.Count == 0) return [];

        // Strip DotNet procedures first so they don't pollute the diagnostics.
        var stripped = sources
            .Select(s => StripDotNetProcedures(s, "").Source ?? s)
            .ToList();

        var pkgArg = resolvedPkgDirs is { Count: > 0 } ? resolvedPkgDirs : null;

        // Capture stderr from the BC compiler.
        var savedErr = Console.Error;
        var capture = new System.IO.StringWriter();
        Console.SetError(capture);
        try { AlTranspiler.TranspileMulti(stripped, pkgArg, null); }
        catch { }
        finally { Console.SetError(savedErr); }

        var output = capture.ToString();
        var result = new List<(string, string)>();
        foreach (System.Text.RegularExpressions.Match m in MissingSymbolPattern.Matches(output))
            result.Add((m.Groups[1].Value, m.Groups[2].Value));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return result.Where(x => seen.Add($"{x.Item1}|{x.Item2}")).ToList();
    }

    // -----------------------------------------------------------------------
    // ControlAddIn stripping
    // -----------------------------------------------------------------------

    /// <summary>True if the file's top-level object is a controladdin (which we always drop).</summary>
    private static bool IsControlAddInObjectFile(string source)
    {
        try
        {
            var tree = SyntaxTree.ParseObjectText(source);
            if (tree.GetRoot() is not CompilationUnitSyntax cu) return false;
            return cu.Objects.Any(o => o.Kind == SyntaxKind.ControlAddInObject);
        }
        catch { return false; }
    }

    /// <summary>
    /// True if the file is a page with <c>SubType = UserControlHost</c>. Such a page must
    /// contain exactly one usercontrol; once we strip those, the page no longer compiles
    /// (AL0874). Drop the whole file — the runner has no browser to host a control anyway.
    /// </summary>
    private static bool IsUserControlHostPage(string source)
    {
        if (!source.Contains("UserControlHost", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var tree = SyntaxTree.ParseObjectText(source);
            if (tree.GetRoot() is not CompilationUnitSyntax cu) return false;
            // The AL keyword is `PageType = UserControlHost;` even though AL0874 calls it
            // 'SubType' — match either to be safe.
            return cu.Objects.OfType<PageSyntax>().Any(p =>
                p.PropertyList?.Properties.OfType<PropertySyntax>().Any(prop =>
                    (string.Equals(prop.Name?.Identifier.ValueText, "PageType", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(prop.Name?.Identifier.ValueText, "SubType", StringComparison.OrdinalIgnoreCase))
                    && prop.Value?.ToFullString().Trim().Equals("UserControlHost", StringComparison.OrdinalIgnoreCase) == true
                ) ?? false);
        }
        catch { return false; }
    }

    /// <summary>
    /// Remove report layout properties (<c>RDLCLayout</c>, <c>WordLayout</c>, <c>ExcelLayout</c>,
    /// <c>RenderingLayout</c>) and the entire <c>rendering { ... }</c> section. Microsoft uses
    /// Windows backslash separators in <c>LayoutFile = '.\Reports\...'</c> which trip AL0363
    /// on Linux. The runner already excludes <c>CompilationGenerationOptions.ReportLayout</c>
    /// from generation, so dropping these is consistent with the runner's no-RDLC architecture.
    /// </summary>
    private static (string Source, int Removed) StripReportLayouts(string source)
    {
        if (!source.Contains("Layout", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("rendering", StringComparison.OrdinalIgnoreCase))
            return (source, 0);
        try
        {
            var tree = SyntaxTree.ParseObjectText(source);
            var root = tree.GetRoot();
            var spans = new List<(int Start, int End)>();

            foreach (var rs in root.DescendantNodes().OfType<ReportRenderingSectionSyntax>())
                spans.Add((rs.Span.Start, rs.Span.End));

            foreach (var prop in root.DescendantNodes().OfType<PropertySyntax>())
            {
                var n = prop.Name?.Identifier.ValueText ?? "";
                if (n.Equals("RDLCLayout", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("WordLayout", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("ExcelLayout", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("RenderingLayout", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("DefaultLayout", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("DefaultRenderingLayout", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("LayoutFile", StringComparison.OrdinalIgnoreCase))
                    spans.Add((prop.Span.Start, prop.Span.End));
            }

            if (spans.Count == 0) return (source, 0);
            // Dedupe: if span A is contained within span B, drop A. Otherwise overlapping
            // removals corrupt the source (e.g. rendering section + its child layout +
            // their child LayoutFile property all firing produces garbage).
            var sorted = spans.OrderBy(x => x.Start).ThenByDescending(x => x.End).ToList();
            var pruned = new List<(int Start, int End)>();
            foreach (var sp in sorted)
                if (pruned.Count == 0 || sp.Start >= pruned[^1].End)
                    pruned.Add(sp);
            var result = source;
            foreach (var (s, e) in pruned.OrderByDescending(x => x.Start))
                result = result[..s] + result[e..];
            return (result, pruned.Count);
        }
        catch { return (source, 0); }
    }

    /// <summary>
    /// Remove the <c>ContextSensitiveHelpPage</c> property from all pages. The runner does
    /// not render help, and Microsoft sets this on most Base/System Application pages —
    /// the consuming app.json's contextSensitiveHelpUrl is the trigger for AL0543, so the
    /// cleanest fix in standalone-slice mode is to drop the property entirely.
    /// </summary>
    private static (string Source, int Removed) StripContextSensitiveHelpPage(string source)
    {
        if (!source.Contains("ContextSensitiveHelpPage", StringComparison.OrdinalIgnoreCase)) return (source, 0);
        try
        {
            var tree = SyntaxTree.ParseObjectText(source);
            var root = tree.GetRoot();
            var props = root.DescendantNodes().OfType<PropertySyntax>()
                .Where(p => string.Equals(p.Name?.Identifier.ValueText, "ContextSensitiveHelpPage", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (props.Count == 0) return (source, 0);
            var result = source;
            foreach (var p in props.OrderByDescending(p => p.Span.Start))
                result = result[..p.Span.Start] + result[p.Span.End..];
            return (result, props.Count);
        }
        catch { return (source, 0); }
    }

    /// <summary>
    /// Remove `usercontrol(localName; "ControlAddInName")` blocks and their dependent
    /// page-level event triggers (`trigger localName::EventName(...)`). Returns the
    /// modified source and the count of usercontrols removed.
    /// </summary>
    private static (string Source, int Removed) StripPageUserControls(string source)
    {
        if (!source.Contains("usercontrol", StringComparison.OrdinalIgnoreCase)) return (source, 0);
        try
        {
            var tree = SyntaxTree.ParseObjectText(source);
            var root = tree.GetRoot();
            var userControls = root.DescendantNodes().OfType<PageUserControlSyntax>().ToList();
            if (userControls.Count == 0) return (source, 0);

            var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var spans = new List<(int Start, int End, string Text)>();
            foreach (var uc in userControls)
            {
                var ln = uc.Name?.Identifier.ValueText;
                if (!string.IsNullOrEmpty(ln)) localNames.Add(ln);
                spans.Add((uc.Span.Start, uc.Span.End, ""));
            }
            // Drop dependent event triggers (`trigger localName::EventName`).
            foreach (var evt in root.DescendantNodes().OfType<EventTriggerDeclarationSyntax>())
            {
                var pub = evt.Publisher?.Identifier.ValueText;
                if (!string.IsNullOrEmpty(pub) && localNames.Contains(pub))
                    spans.Add((evt.Span.Start, evt.Span.End, ""));
            }
            // Replace bodies of procedures that reference `CurrPage.<strippedLocalName>` —
            // those calls would dangle (AL0132 'CurrPage X does not contain Y') because the
            // usercontrol field is gone.
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Cast<SyntaxNode>()
                .Concat(root.DescendantNodes().OfType<TriggerDeclarationSyntax>().Cast<SyntaxNode>()))
            {
                var block = method.ChildNodes().OfType<BlockSyntax>().FirstOrDefault();
                if (block == null) continue;
                // Match any `CurrPage.<strippedLocalName>` member access — both as a value
                // (`Foo(CurrPage.X, ...)`) and as a chain head (`CurrPage.X.Method()`).
                bool refsStripped = block.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                    .Any(m => m.Expression is IdentifierNameSyntax id
                        && id.Identifier.ValueText.Equals("CurrPage", StringComparison.OrdinalIgnoreCase)
                        && m.Name is IdentifierNameSyntax cpName
                        && localNames.Contains(cpName.Identifier.ValueText));
                if (!refsStripped) continue;
                var name = (method as MethodDeclarationSyntax)?.Name?.Identifier.ValueText
                    ?? (method as TriggerDeclarationSyntax)?.Name?.ToFullString().Trim()
                    ?? "Unknown";
                var escaped = name.Replace("'", "''");
                var stub = "\n    begin\n" +
                    $"        Error('AL Runner: ''{escaped}'' uses a stripped ControlAddIn (no browser in standalone mode).');\n" +
                    "    end;";
                spans.Add((block.Span.Start, block.Span.End, stub));
            }

            // Dedupe overlapping spans: an outer usercontrol span swallows any inner
            // body-stub spans we'd otherwise also apply, which would corrupt the source.
            var sorted = spans.OrderBy(x => x.Start).ThenByDescending(x => x.End).ToList();
            var pruned = new List<(int Start, int End, string Text)>();
            foreach (var sp in sorted)
                if (pruned.Count == 0 || sp.Start >= pruned[^1].End)
                    pruned.Add(sp);

            var result = source;
            foreach (var (s, e, t) in pruned.OrderByDescending(r => r.Start))
                result = result[..s] + t + result[e..];
            return (result, userControls.Count);
        }
        catch { return (source, 0); }
    }

    // -----------------------------------------------------------------------
    // DotNet stripping
    // -----------------------------------------------------------------------

    /// <summary>
    /// Replaces bodies of procedures that reference DotNet or ControlAddIn types with an
    /// <c>Error()</c> call. Returns (strippedSource, count) where strippedSource is null
    /// if the entire file is a pure assembly declaration with nothing callable.
    /// ControlAddIn types are treated like DotNet because the runner has no browser to host
    /// the JS/HTML control — calling such a procedure has no meaningful behavior.
    /// </summary>
    private static bool IsUnsupportedTypeName(string typeName) =>
        typeName.Equals("DotNet", StringComparison.OrdinalIgnoreCase)
        || typeName.Equals("ControlAddIn", StringComparison.OrdinalIgnoreCase);

    private static (string? Source, int Stripped) StripDotNetProcedures(string source, string fileName)
    {
        // Fast path: neither keyword anywhere, nothing to do.
        if (!source.Contains("DotNet", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("ControlAddIn", StringComparison.OrdinalIgnoreCase))
            return (source, 0);

        try
        {
            var tree = SyntaxTree.ParseObjectText(source);
            var root = tree.GetRoot();

            // Collect (start, end, replacement) for each DotNet-referencing procedure.
            var replacements = new List<(int Start, int End, string Text)>();

            // Pre-pass: identify DotNet vars at object level so we can fully remove their
            // dependent page-level event triggers (`trigger VarName::EventName`) instead of
            // patching their bodies — patching would leave AL0461 since the publisher is gone.
            var strippedDotNetVarNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var globalVars in root.DescendantNodes().OfType<GlobalVarSectionSyntax>())
            {
                foreach (var decl in globalVars.Variables)
                {
                    var hasDn = decl.DescendantNodes().OfType<SubtypedDataTypeSyntax>()
                        .Any(s => IsUnsupportedTypeName(s.TypeName.ToFullString().Trim()));
                    if (!hasDn) continue;
                    if (decl is VariableDeclarationSyntax vd)
                    {
                        var ident = vd.Name?.Identifier.ValueText;
                        if (!string.IsNullOrEmpty(ident))
                            strippedDotNetVarNames.Add(ident);
                    }
                }
            }

            // Strip DotNet from all callable declarations: procedures, field/codeunit triggers,
            // and event subscriber triggers (EventTriggerDeclarationSyntax).
            // All share the same child structure: ParameterList, ReturnValue?, VarSection?, Block?.
            var candidateNodes =
                root.DescendantNodes().OfType<MethodDeclarationSyntax>().Cast<SyntaxNode>()
                .Concat(root.DescendantNodes().OfType<TriggerDeclarationSyntax>().Cast<SyntaxNode>())
                .Concat(root.DescendantNodes().OfType<EventTriggerDeclarationSyntax>().Cast<SyntaxNode>());

            foreach (var method in candidateNodes)
            {
                // Skip event triggers whose publisher var was stripped — we will remove
                // the entire trigger declaration below.
                if (method is EventTriggerDeclarationSyntax evtSkip
                    && evtSkip.Publisher?.Identifier.ValueText is string pubName
                    && strippedDotNetVarNames.Contains(pubName))
                    continue;

                var hasDotNet = method.DescendantNodes()
                    .OfType<SubtypedDataTypeSyntax>()
                    .Any(s => IsUnsupportedTypeName(s.TypeName.ToFullString().Trim()));

                // Also stub procedures whose *body* references a global var we just removed
                // (DotNet/ControlAddIn-typed). The body would otherwise reference an undefined
                // identifier (AL0118).
                bool bodyTouchesStripped = strippedDotNetVarNames.Count > 0
                    && method.ChildNodes().OfType<BlockSyntax>().FirstOrDefault() is BlockSyntax blk
                    && blk.DescendantNodes().OfType<IdentifierNameSyntax>()
                        .Any(id => strippedDotNetVarNames.Contains(id.Identifier.ValueText));

                if (!hasDotNet && !bodyTouchesStripped) continue;

                var methodName = method switch
                {
                    MethodDeclarationSyntax m => m.Name?.Identifier.ValueText ?? "Unknown",
                    TriggerDeclarationSyntax t => t.Name?.ToFullString().Trim() ?? "Unknown",
                    EventTriggerDeclarationSyntax e => e.ChildNodes().OfType<IdentifierNameSyntax>().Skip(1).FirstOrDefault()?.Identifier.ValueText ?? "Unknown",
                    _ => "Unknown"
                };
                var varSection = method.ChildNodes().OfType<VarSectionSyntax>().FirstOrDefault();
                var block = method.ChildNodes().OfType<BlockSyntax>().FirstOrDefault();

                if (block != null)
                {
                    // [IntegrationEvent] / [BusinessEvent] declarations must have an empty
                    // body — AL0286 fires if we inject Error(). EventSubscriber bodies are
                    // real code and DO get the Error() replacement.
                    bool isEventDecl = method.DescendantNodes()
                        .OfType<MemberAttributeSyntax>()
                        .Any(a => {
                            var n = a.Name?.ToFullString().Trim() ?? "";
                            return n.Equals("IntegrationEvent", StringComparison.OrdinalIgnoreCase)
                                || n.Equals("BusinessEvent", StringComparison.OrdinalIgnoreCase)
                                || n.Equals("InternalEvent", StringComparison.OrdinalIgnoreCase);
                        });

                    var replaceStart = varSection?.Span.Start ?? block.Span.Start;
                    var replaceEnd = block.Span.End;
                    string newBody;
                    if (isEventDecl)
                    {
                        newBody = "\n    begin\n    end;";
                    }
                    else
                    {
                        var escaped = methodName.Replace("'", "''");
                        newBody =
                            "\n    begin\n" +
                            $"        Error('AL Runner: ''{escaped}'' uses DotNet interop — not supported in standalone mode. Add this object to your compiled dependency slice.');\n" +
                            "    end;";
                    }
                    replacements.Add((replaceStart, replaceEnd, newBody));
                }
                // For interface procedures (no block): only clean up signatures below.

                // Also replace DotNet types in parameter and return type positions.
                // These are outside the body/var span but still cause unresolved-type errors.
                // Replace: param: DotNet XmlNode → param: Variant
                // Replace: ): DotNet XmlNode   → ): Text
                var returnValue = method.ChildNodes().OfType<ReturnValueSyntax>().FirstOrDefault();
                if (returnValue != null)
                {
                    var retDotNet = returnValue.DescendantNodes()
                        .OfType<SubtypedDataTypeSyntax>()
                        .FirstOrDefault(s => IsUnsupportedTypeName(s.TypeName.ToFullString().Trim()));
                    if (retDotNet != null)
                        replacements.Add((retDotNet.Span.Start, retDotNet.Span.End, "Text"));
                }

                var paramList = method.ChildNodes().OfType<ParameterListSyntax>().FirstOrDefault();
                if (paramList != null)
                {
                    foreach (var param in paramList.DescendantNodes().OfType<SubtypedDataTypeSyntax>()
                        .Where(s => IsUnsupportedTypeName(s.TypeName.ToFullString().Trim())))
                    {
                        replacements.Add((param.Span.Start, param.Span.End, "Variant"));
                    }
                }
            }

            // Remove DotNet variable declarations from object-level (GlobalVarSection).
            // These are codeunit/page/report-level vars outside any procedure — not handled
            // by the procedure loop above but still cause the DepCompiler's regex to exclude
            // the entire file, making the object "missing" from compilation.
            foreach (var globalVars in root.DescendantNodes().OfType<GlobalVarSectionSyntax>())
            {
                var dotNetDecls = globalVars.Variables
                    .Where(decl => decl.DescendantNodes()
                        .OfType<SubtypedDataTypeSyntax>()
                        .Any(s => IsUnsupportedTypeName(s.TypeName.ToFullString().Trim())))
                    .ToList();

                if (dotNetDecls.Count == 0) continue;

                if (dotNetDecls.Count == globalVars.Variables.Count)
                    replacements.Add((globalVars.FullSpan.Start, globalVars.FullSpan.End, ""));
                else
                    foreach (var decl in dotNetDecls)
                        replacements.Add((decl.FullSpan.Start, decl.FullSpan.End, ""));
            }

            // Page-level `trigger VarName::EventName(...)` — these reference a [WithEvents]
            // DotNet var that we just removed. Leaving the trigger orphans it (AL0461
            // 'X is not a valid event publisher'). Use Span (not FullSpan) to avoid
            // consuming the next member's leading trivia.
            if (strippedDotNetVarNames.Count > 0)
            {
                foreach (var evt in root.DescendantNodes().OfType<EventTriggerDeclarationSyntax>())
                {
                    var publisher = evt.Publisher?.Identifier.ValueText;
                    if (!string.IsNullOrEmpty(publisher) && strippedDotNetVarNames.Contains(publisher))
                        replacements.Add((evt.Span.Start, evt.Span.End, ""));
                }
            }

            if (replacements.Count == 0)
                return (source, 0);

            // Apply replacements from end to start to preserve offsets.
            var result = source;
            foreach (var (start, end, text) in replacements.OrderByDescending(r => r.Start))
                result = result[..start] + text + result[end..];

            // Remove orphaned preprocessor directives left behind when a DotNet var that was
            // wrapped in #if/#endif had its leading #if trivia removed but the closing #endif
            // (as leading trivia of the next var) was not part of the removed span.
            result = RemoveOrphanedPreprocessorDirectives(result);

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
                    try
                    {
                        var src = File.ReadAllText(f);
                        // Strip UTF-8 BOM if present — SyntaxTree.ParseObjectText does not handle it
                        // and produces parse errors for files written with BOM.
                        if (src.Length > 0 && src[0] == '﻿') src = src[1..];
                        result.Add((depSource, Path.GetRelativePath(depSource, f), src));
                    }
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

    // For TableRelation = "Acc. Schedule"."No." or Customer."No." or Customer:
    // returns the table portion (left of the dot that separates Table from Field).
    // Quoted-identifier-aware: dots INSIDE "..." don't split.
    private static string? SplitTableFromTableField(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        if (raw[0] == '"')
        {
            var closing = raw.IndexOf('"', 1);
            if (closing < 0) return raw; // malformed — leave as-is
            return raw[..(closing + 1)];
        }
        var dot = raw.IndexOf('.');
        return dot >= 0 ? raw[..dot] : raw;
    }

    private static string? UnquoteIdentifier(string? raw)
    {
        if (raw == null) return null;
        raw = raw.Trim();
        // Strip namespace prefix: `Microsoft.Sales.Posting."Sales Post Invoice"` → `"Sales Post Invoice"`
        // and `Microsoft.Sales.Posting.SalesPost` → `SalesPost`. The rightmost segment is the
        // simple object name we use for indexing.
        if (raw.EndsWith('"'))
        {
            var openQuote = raw.LastIndexOf('"', raw.Length - 2);
            if (openQuote > 0) raw = raw[openQuote..];
        }
        else
        {
            var lastDot = raw.LastIndexOf('.');
            if (lastDot >= 0 && !raw.Contains('"')) raw = raw[(lastDot + 1)..];
        }
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
    // typeName -> objectName -> list of (fileName, source). A name can resolve to multiple
    // files when modules redefine the same object — e.g. \"Source Code Setup\" exists as the
    // obsolete-and-moved table in Base Application AND as the live table in Business Foundation.
    // The compiler picks the right one via preprocessor symbols (CLEANSCHEMA<N>); we must include
    // all of them in the slice or the live table is dropped and references fail.
    public Dictionary<string, Dictionary<string, List<(string FileName, string Source)>>> Definitions { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    // baseTypeName -> baseObjectName -> list of (fileName, source)
    public Dictionary<string, Dictionary<string, List<(string FileName, string Source)>>> Extensions { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    // targetTypeName -> targetObjectName -> list of (fileName, source)
    public Dictionary<string, Dictionary<string, List<(string FileName, string Source)>>> EventSubscribers { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public int DefinitionCount  => Definitions.Values.Sum(d => d.Values.Sum(l => l.Count));
    public int ExtensionCount   => Extensions.Values.Sum(d => d.Values.Sum(l => l.Count));
    public int SubscriberCount  => EventSubscribers.Values.Sum(d => d.Values.Sum(l => l.Count));

    public void AddDefinition(string? typeName, string? objectName, string fileName, string source)
    {
        if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(objectName)) return;
        if (!Definitions.TryGetValue(typeName, out var byName))
            Definitions[typeName] = byName = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        if (!byName.TryGetValue(objectName, out var list))
            byName[objectName] = list = new List<(string, string)>();
        if (!list.Any(e => e.Item1 == fileName))
            list.Add((fileName, source));
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
    public HashSet<string> ControlAddIns { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty =>
        Tables.Count == 0 && Codeunits.Count == 0 && Enums.Count == 0 &&
        Pages.Count == 0 && Reports.Count == 0 && Queries.Count == 0 &&
        XmlPorts.Count == 0 && Interfaces.Count == 0 && ControlAddIns.Count == 0;
}
