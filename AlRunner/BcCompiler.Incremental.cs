// BcCompiler.Incremental — --watch's fast path (issue #1902).
//
// Every prior --watch cycle called the SAME BcCompiler.Emit a one-shot run uses: parse every
// .al file, bind the WHOLE module, generate C# for every object, every cycle — so a one-line
// edit to one codeunit costs the same as a cold build. Measured on a 7,053-file app: 761–862s
// per save.
//
// BC's own compiler exposes the fix: Compilation.CreateForRad — the same public factory BC's
// own RAD (VS Code F5 "publish") uses. Given a change model (which objects were Added/
// Modified/Removed) and the PREVIOUS cycle's compiled symbol picture (a
// SymbolReference.ModuleDefinition — the same shape SymbolJsonWriter already produces for
// cross-app dependency resolution), it binds and generates C# for ONLY the touched objects,
// resolving everything else from the baseline instead of re-parsing/re-binding it.
//
// This file wires that in, restricted to the single case that can be proven safe without also
// solving the much larger "hot-swap a live .NET module" problem BC's own service-tier RAD
// solves at runtime (out of scope here — see the class doc below):
//
//   A file that already declared exactly one id-bearing object (Codeunit/Table/Page/Report/
//   XmlPort/Query/Enum/TableExtension/PageExtension/ReportExtension/EnumExtension/
//   PermissionSet/PermissionSetExtension) had its CONTENT edited — same (Kind,Id,Name) as
//   before. Everything else (add/remove/rename any file, an id-less object kind such as
//   interface/controladdin/profile/pagecustomization/profileextension/entitlement, an app.json
//   or dependency-set change, more/fewer than one object in a touched file, ANY diagnostic or
//   exception from the delta compile) falls back to the ordinary, already-correct, whole-module
//   Emit() — never a stale or wrong result, just not accelerated for that cycle.
//
// Why this is safe to reuse UNCHANGED cached C# for every object that was not touched
// -----------------------------------------------------------------------------------
// Confirmed by inspecting BC's own generated C# (`al-runner --dump-csharp`): a call from one AL
// object to another does NOT compile to a direct C# type reference. It compiles to
// `new NavCodeunitHandle(this, <object id>).Target.Invoke(<memberId>, args)` — an ID + a
// deterministic hash of the called procedure's name+signature, computed independently at BOTH
// the call site and the callee's own OnInvoke dispatch switch. Neither side's C# encodes the
// other's class name. So:
//   - An unmodified caller's cached C# is unaffected by ANY change to a callee's shape — it
//     dispatches by (object id, member hash) at RUNTIME, against whichever type is CURRENTLY
//     registered for that id in the SAME final assembly this cycle produces (built from the
//     UNION of every object's C# — changed objects freshly generated, everything else served
//     from the cache). It automatically sees the callee's NEW behaviour without being touched.
//   - A genuinely breaking edit (e.g. a renamed procedure an unmodified caller still calls by
//     its old name) is NOT a silent wrong answer: the caller's unchanged member-hash no longer
//     matches any case in the callee's regenerated OnInvoke switch, so the call throws
//     NavNCLMissingMethodException at the call site — loud, not silent, satisfying
//     loud-failures.md. It is also not the common case: it is a signature change to something an
//     UNMODIFIED file calls, since a MODIFIED caller would already have its own fresh call site
//     hash from the classify step below.
//   - The final union of cached + freshly-generated C# still goes through ONE ordinary Roslyn
//     C#-to-IL build and ONE ordinary module load — completely unchanged from today's full-
//     rebuild path. There is no multi-generation-assembly runtime merge anywhere in this design;
//     that is what keeps the correctness surface no larger than the existing full-rebuild path's.
//
// What CreateForRad needs beyond `packagedModuleDefinition`
// -----------------------------------------------------------
// Empirically (`Compilation.CreateForRad` is undocumented outside BC's own source), passing only
// `packagedModuleDefinition` is NOT enough for the changed object to resolve a reference to an
// untouched sibling — it throws deep inside codegen with "Unexpected value 'None' of type
// NavTypeKind" (the SAME crash class BcCompiler.Emit's DotNet-resolver comment documents for an
// unresolved DotNet type, but here caused by an unresolved AL cross-object reference instead).
// The fix is to ALSO register a self-referencing ISymbolReferenceLoader/SymbolReferenceSpecification
// (same AppId/Publisher/Version as the module itself) exposing the SAME baseline objects, with
// the objects actually being changed THIS cycle excluded from it (leaving them in would raise
// "already declared" duplicate-object errors, since packagedModuleDefinition already carries
// their OLD shape). See BuildSelfLoader below.
//
// Why the next baseline is a MERGE, not just "convert the RAD compilation"
// --------------------------------------------------------------------------
// Also confirmed empirically: converting a RAD Compilation via the same SerializableSymbolModelConverter
// SymbolJsonWriter uses returns ONLY the objects that were actually source-compiled THIS cycle —
// not the untouched baseline objects pulled in via packagedModuleDefinition/the self-loader. A
// second incremental cycle chained naively off that (unmerged) module would silently forget every
// object from 2+ cycles back — exactly the kind of stale-cache bug this repo has been burned by
// before. MergeModuleDefinition below is the fix: the next baseline is (old baseline minus the
// objects just changed) UNION (freshly converted definitions for exactly those objects) — built by
// this code, never assumed from a BC API.
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using NavEmit = Microsoft.Dynamics.Nav.CodeAnalysis.Emit;
using NavSymRef = Microsoft.Dynamics.Nav.CodeAnalysis.SymbolReference;

namespace AlRunner;

/// <summary>
/// (Kind, Id) identity of an AL application object — the unit CreateForRad's
/// ObjectChangeModelDefinition classifies changes by. Id-less kinds (interface, controladdin,
/// profile, pagecustomization, profileextension, entitlement) are never represented here — see
/// <see cref="BcCompiler.IdlessSymbolKinds"/>.
/// </summary>
internal readonly record struct RadObjectIdentity(NavCA.SymbolKind Kind, int Id, string Name);

/// <summary>Per-module (--watch bundle) incremental compile state, kept warm on the BcCompiler instance.</summary>
internal sealed class RadBaseline
{
    public required Guid AppId;
    public required string Publisher;
    public required Version Version;
    public required string ManifestFingerprint;
    public required string SharedRefsFingerprint;
    public required NavSymRef.ModuleDefinition ModuleDef;
    public required Dictionary<string, string> FileHashByPath;
    public required Dictionary<string, RadObjectIdentity> ObjectByPath;
    public required Dictionary<string, EmittedSource> SourceByKey;
    public required BcEmitOutput LastOutput;
}

public sealed partial class BcCompiler
{
    private readonly Dictionary<string, RadBaseline> _radBaselines = new();

    /// <summary>
    /// Object kinds with no numeric Id — a (Kind,Id) pair can never identify them, so any file
    /// declaring one always takes the full-rebuild path. Matches issue #1902's own enumeration.
    /// </summary>
    internal static readonly IReadOnlySet<NavCA.SymbolKind> IdlessSymbolKinds = new HashSet<NavCA.SymbolKind>
    {
        NavCA.SymbolKind.Interface, NavCA.SymbolKind.ControlAddIn, NavCA.SymbolKind.Profile,
        NavCA.SymbolKind.PageCustomization, NavCA.SymbolKind.ProfileExtension, NavCA.SymbolKind.Entitlement,
    };

    private static string RadObjKey(NavCA.SymbolKind kind, int id) => $"{kind}:{id}";

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Includes the app.json file's own bytes (id/version/publisher/dependencies/idRanges/…),
    /// not just the compiler-input-relevant subset ManifestCompilerInputs.CacheKeyFragment
    /// reads — a caller that never wired _currentAppId/_currentPublisher/_currentVersion from
    /// the manifest would otherwise let a real app.json edit (a version bump, a new dependency)
    /// through undetected. Cheap: one file, hashed once per cycle only when the fast path is
    /// even attempted.
    /// </summary>
    private static string RadManifestFingerprint(
        Guid appId, string publisher, Version version, ManifestCompilerInputs manifestInputs, string? manifestAppJsonPath)
    {
        var appJsonHash = manifestAppJsonPath != null && File.Exists(manifestAppJsonPath)
            ? HashFile(manifestAppJsonPath) : "<none>";
        return $"{appId}|{publisher}|{version}|{manifestInputs.CacheKeyFragment}|{appJsonHash}";
    }

    /// <summary>
    /// Attempts a --watch-only incremental (BC RAD) recompile against the baseline
    /// <see cref="Emit"/> recorded on this instance for <paramref name="moduleName"/> (see its
    /// <c>trackIncrementalBaseline</c> parameter). Returns null — the caller MUST fall back to
    /// an ordinary <c>Emit(..., trackIncrementalBaseline: true)</c> — for any condition this path
    /// cannot prove safe; <paramref name="fallbackReason"/> names it. On success, this instance's
    /// baseline for <paramref name="moduleName"/> is already updated for the NEXT cycle.
    /// </summary>
    public BcEmitOutput? TryEmitIncremental(
        IEnumerable<string> alFolders, string moduleName, string? appRootDir, out string fallbackReason)
    {
        fallbackReason = "";
        if (!_radBaselines.TryGetValue(moduleName, out var baseline))
        {
            fallbackReason = "no incremental baseline yet for this bundle (first --watch cycle, or the previous cycle fell back)";
            return null;
        }

        var dirs = alFolders.Where(Directory.Exists).Distinct().ToList();
        var alFiles = dirs.SelectMany(d => Directory.EnumerateFiles(d, "*.al", SearchOption.AllDirectories)).Distinct().ToList();

        var manifestAppJsonPath = (appRootDir != null && File.Exists(Path.Combine(appRootDir, "app.json")))
            ? Path.Combine(appRootDir, "app.json")
            : dirs.Select(d => Path.Combine(d, "app.json")).FirstOrDefault(File.Exists);
        var manifestInputs = ReadManifestCompilerInputs(manifestAppJsonPath);
        var appId = _currentAppId ?? DeterministicGuid(moduleName);
        var publisher = _currentPublisher ?? "AlRunner";
        var version = _currentVersion ?? new Version(1, 0, 0, 0);
        var manifestFingerprint = RadManifestFingerprint(appId, publisher, version, manifestInputs, manifestAppJsonPath);
        if (manifestFingerprint != baseline.ManifestFingerprint)
        {
            fallbackReason = "app.json (identity/version/preprocessor symbols/features/help url) changed since the last cycle";
            return null;
        }

        var bundleAlpackages = dirs.SelectMany(d => Directory.EnumerateDirectories(d, ".alpackages", SearchOption.AllDirectories)).Distinct();
        // Same BCCOMPILER_TIMING=1 diagnostic convention Emit() uses (see its own
        // GetSharedReferences _mark call) — WatchTests' warm-vs-cold regression guard scrapes
        // this exact "[emit-timing] GetSharedReferences (...): <n>ms" shape on stderr, and this
        // path calls GetSharedReferences too, so it must keep emitting it or that guard goes
        // blind the moment a cycle takes the fast path instead of Emit().
        bool timing = Environment.GetEnvironmentVariable("BCCOMPILER_TIMING") == "1";
        var refsSw = timing ? System.Diagnostics.Stopwatch.StartNew() : null;
        var (refLoader, specs) = GetSharedReferences(bundleAlpackages);
        if (timing) Console.Error.WriteLine($"[emit-timing] GetSharedReferences ({specs.Length} specs): {refsSw!.ElapsedMilliseconds}ms");
        var sharedRefsFingerprint = string.Join(",", specs.Select(s => $"{s.AppId}:{s.Version}").OrderBy(s => s, StringComparer.Ordinal));
        if (sharedRefsFingerprint != baseline.SharedRefsFingerprint)
        {
            fallbackReason = "resolved dependency set changed since the last cycle";
            return null;
        }

        var currentHashes = new Dictionary<string, string>();
        foreach (var f in alFiles) currentHashes[f] = HashFile(f);

        var addedOrRemoved = new List<string>();
        var modifiedPaths = new List<string>();
        foreach (var kv in currentHashes)
        {
            if (!baseline.FileHashByPath.TryGetValue(kv.Key, out var oldHash)) { addedOrRemoved.Add(kv.Key); continue; }
            if (!string.Equals(oldHash, kv.Value, StringComparison.Ordinal)) modifiedPaths.Add(kv.Key);
        }
        foreach (var oldPath in baseline.FileHashByPath.Keys)
            if (!currentHashes.ContainsKey(oldPath)) addedOrRemoved.Add(oldPath);

        if (addedOrRemoved.Count > 0)
        {
            fallbackReason = $"{addedOrRemoved.Count} file(s) added/removed/renamed since the last cycle " +
                $"(fast path only handles a content edit to an already-tracked object): {string.Join(", ", addedOrRemoved.Take(5))}";
            return null;
        }

        if (modifiedPaths.Count == 0)
        {
            // Every file hashes identical to the last cycle — including a touch-with-identical-
            // bytes. Genuinely zero work: replay the last cycle's result verbatim.
            return baseline.LastOutput;
        }

        var parseOpts = RadParseOptions(manifestInputs);
        var compOpts = RadCompilationOptions(manifestInputs);

        var changeElements = new List<NavCA.ObjectChangeElement>();
        var changedIdentities = new HashSet<RadObjectIdentity>();
        var changedTrees = new List<NavSyntax.SyntaxTree>();
        foreach (var path in modifiedPaths)
        {
            if (!baseline.ObjectByPath.TryGetValue(path, out var oldIdentity))
            {
                fallbackReason = $"'{path}' was not tracked as a single-object file by the previous baseline";
                return null;
            }
            if (IdlessSymbolKinds.Contains(oldIdentity.Kind))
            {
                fallbackReason = $"'{path}' declares a {oldIdentity.Kind} — id-less object kinds always take the full-rebuild path";
                return null;
            }

            var src = File.ReadAllText(path);
            var tree = NavSyntax.SyntaxTree.ParseObjectText(src, path: path, encoding: null!, parseOpts, default);
            var classify = NavCA.Compilation.Create(moduleName: "__rad_classify", syntaxTrees: new[] { tree }, options: compOpts);
            var declared = classify.GetDeclaredApplicationObjectSymbols();
            if (declared.Length != 1)
            {
                fallbackReason = $"'{path}' now declares {declared.Length} object(s) (fast path requires exactly 1)";
                return null;
            }
            var sym = declared[0];
            var newId = (sym as NavCA.ISymbolWithId)?.Id;
            if (newId == null || IdlessSymbolKinds.Contains(sym.Kind))
            {
                fallbackReason = $"'{path}' declares a {sym.Kind} — id-less object kinds always take the full-rebuild path";
                return null;
            }
            if (sym.Kind != oldIdentity.Kind || newId.Value != oldIdentity.Id || !string.Equals(sym.Name, oldIdentity.Name, StringComparison.Ordinal))
            {
                fallbackReason = $"'{path}' object identity changed (was {oldIdentity.Kind} {oldIdentity.Id} \"{oldIdentity.Name}\", " +
                    $"now {sym.Kind} {newId} \"{sym.Name}\") — that is an add+remove, not an edit";
                return null;
            }

            changeElements.Add(new NavCA.ObjectChangeElement { Id = newId.Value, Kind = sym.Kind, Name = sym.Name });
            changedIdentities.Add(new RadObjectIdentity(sym.Kind, newId.Value, sym.Name));
            changedTrees.Add(tree);
        }

        var changeModel = new NavCA.ObjectChangeModelDefinition
        {
            Added = Array.Empty<NavCA.ObjectChangeElement>(),
            Modified = changeElements.ToArray(),
            Removed = Array.Empty<NavCA.ObjectChangeElement>(),
        };

        var selfSpec = new NavCA.SymbolReferenceSpecification(
            publisher: publisher, name: moduleName, version: version,
            exact: false, appId: appId, isPropagated: false, alternateIds: ImmutableArray<Guid>.Empty);
        var selfModule = ExcludeObjects(baseline.ModuleDef, changedIdentities);
        var selfLoader = new RadSelfBaselineLoader(appId, selfModule);
        var combinedLoader = refLoader != null
            ? new CompositeSymbolReferenceLoader(new NavCA.ISymbolReferenceLoader[] { selfLoader, refLoader })
            : (NavCA.ISymbolReferenceLoader)selfLoader;
        var combinedSpecs = specs.Append(selfSpec).ToArray();

        NavCA.Compilation radComp;
        try
        {
            radComp = NavCA.Compilation.CreateForRad(
                moduleName: moduleName,
                objectChangeModelDefinition: changeModel,
                packagedModuleDefinition: baseline.ModuleDef,
                symbolReferenceLoader: combinedLoader,
                symbolReferences: combinedSpecs,
                publisher: publisher, version: version, appId: appId,
                syntaxTrees: changedTrees, options: compOpts);
        }
        catch (Exception ex)
        {
            fallbackReason = $"CreateForRad threw: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
        if (appRootDir != null && Directory.Exists(appRootDir))
            radComp = radComp.WithFileSystem(new NavCA.RelativeFileSystem(appRootDir));
        radComp = radComp.WithDotNetResolverFactory(GetOrCreateDotNetFactory());

        var radOut = new CaptureOutputter();
        NavEmit.EmitResult? radResult;
        try { radResult = radComp.Emit(NavCA.EmitOptions.Default, radOut); }
        catch (Exception ex)
        {
            fallbackReason = $"RAD Emit threw: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
        if (!radResult.Success)
        {
            fallbackReason = "RAD Emit failed: " + string.Join(
                " | ", radResult.Diagnostics.Where(d => d.Severity == NavCA.Diagnostics.DiagnosticSeverity.Error).Select(d => d.GetMessage()));
            return null;
        }

        var newByKey = new Dictionary<string, EmittedSource>();
        foreach (var src in radOut.Captured)
        {
            var match = changeElements.FirstOrDefault(e => string.Equals(e.Name, src.Name, StringComparison.Ordinal));
            if (match == null)
            {
                fallbackReason = $"RAD Emit produced an unexpected object '{src.Name}'";
                return null;
            }
            newByKey[RadObjKey(match.Kind, match.Id!.Value)] = src;
        }
        if (newByKey.Count != changeElements.Count)
        {
            fallbackReason = $"RAD Emit produced {newByKey.Count} object(s), expected {changeElements.Count}";
            return null;
        }

        var unionedSources = new List<EmittedSource>(baseline.SourceByKey.Count);
        foreach (var kv in baseline.SourceByKey)
            unionedSources.Add(newByKey.TryGetValue(kv.Key, out var fresh) ? fresh : kv.Value);
        foreach (var kv in newByKey)
            if (!baseline.SourceByKey.ContainsKey(kv.Key)) unionedSources.Add(kv.Value);

        var deltaModuleDef = SymbolJsonWriter.GetModuleDefinition(radComp);
        var mergedModuleDef = MergeModuleDefinition(baseline.ModuleDef, changedIdentities, deltaModuleDef);

        var newFileHashByPath = new Dictionary<string, string>(baseline.FileHashByPath, StringComparer.Ordinal);
        foreach (var path in modifiedPaths) newFileHashByPath[path] = currentHashes[path];
        // ObjectByPath and the set of (Kind,Id) keys are unchanged — the fast path only allows
        // a CONTENT edit to an object whose (Kind,Id,Name,Path) are all unchanged.

        var newSourceByKey = new Dictionary<string, EmittedSource>(baseline.SourceByKey.Count, StringComparer.Ordinal);
        foreach (var kv in baseline.SourceByKey)
            newSourceByKey[kv.Key] = newByKey.TryGetValue(kv.Key, out var fresh) ? fresh : kv.Value;

        var output = new BcEmitOutput(unionedSources, Array.Empty<string>(), Array.Empty<string>());

        _radBaselines[moduleName] = new RadBaseline
        {
            AppId = appId, Publisher = publisher, Version = version,
            ManifestFingerprint = manifestFingerprint, SharedRefsFingerprint = sharedRefsFingerprint,
            ModuleDef = mergedModuleDef,
            FileHashByPath = newFileHashByPath,
            ObjectByPath = baseline.ObjectByPath,
            SourceByKey = newSourceByKey,
            LastOutput = output,
        };

        return output;
    }

    /// <summary>
    /// Called by <see cref="Emit"/> after a clean success, when its caller passed
    /// <c>trackIncrementalBaseline: true</c>. Builds the (Kind,Id)-keyed state
    /// <see cref="TryEmitIncremental"/> needs for the NEXT cycle.
    /// </summary>
    private void RecordIncrementalBaseline(
        string moduleName, NavCA.Compilation compilation, IReadOnlyList<string> alFiles,
        IReadOnlyList<EmittedSource> captured, NavCA.SymbolReferenceSpecification[] specs,
        ManifestCompilerInputs manifestInputs, string? manifestAppJsonPath, Guid appId, string publisher, Version version,
        BcEmitOutput fullOutput)
    {
        var declared = compilation.GetDeclaredApplicationObjectSymbols();
        var byName = new Dictionary<string, List<(NavCA.SymbolKind Kind, int? Id, string? Path)>>(StringComparer.Ordinal);
        foreach (var sym in declared)
        {
            var id = (sym as NavCA.ISymbolWithId)?.Id;
            var path = sym.Location?.SourceTree?.FilePath;
            if (!byName.TryGetValue(sym.Name, out var list)) byName[sym.Name] = list = new();
            list.Add((sym.Kind, id, path));
        }

        // ObjectByPath is built from EVERY declared object (including id-less kinds like
        // interface/controladdin/…), not just the ones with captured C#: an id-less kind never
        // reaches AddApplicationObject (no runtime type to emit), so if this only walked
        // `captured` those files would never be tracked at all, and a later touch would report
        // the vaguer "not tracked" fallback instead of the specific "id-less" one.
        var objectByPath = new Dictionary<string, RadObjectIdentity>(StringComparer.Ordinal);
        var claimedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sym in declared)
        {
            var path = sym.Location?.SourceTree?.FilePath;
            if (path == null) continue;
            var id = (sym as NavCA.ISymbolWithId)?.Id ?? 0; // sentinel for id-less kinds — never dispatched on, see IdlessSymbolKinds gate below.
            if (!claimedPaths.Add(path))
            {
                // A second object claimed a path already seen this pass — that file declares
                // more than one object; the fast path requires exactly one, so untrack the path
                // entirely rather than record a misleading single identity for it.
                objectByPath.Remove(path);
                continue;
            }
            objectByPath[path] = new RadObjectIdentity(sym.Kind, id, sym.Name);
        }

        var sourceByKey = new Dictionary<string, EmittedSource>(StringComparer.Ordinal);
        foreach (var src in captured)
        {
            if (!byName.TryGetValue(src.Name, out var candidates) || candidates.Count != 1)
                continue; // ambiguous (name shared across kinds, or unresolved) — leave untracked, not fatal.
            var (kind, id, path) = candidates[0];
            if (id == null || IdlessSymbolKinds.Contains(kind))
                continue; // id-less kind, or unresolved — no (Kind,Id) key to cache under.
            sourceByKey[RadObjKey(kind, id.Value)] = src;
        }

        var fileHashByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in alFiles) fileHashByPath[f] = HashFile(f);

        var moduleDef = SymbolJsonWriter.GetModuleDefinition(compilation);
        var manifestFingerprint = RadManifestFingerprint(appId, publisher, version, manifestInputs, manifestAppJsonPath);
        var sharedRefsFingerprint = string.Join(",", specs.Select(s => $"{s.AppId}:{s.Version}").OrderBy(s => s, StringComparer.Ordinal));

        _radBaselines[moduleName] = new RadBaseline
        {
            AppId = appId, Publisher = publisher, Version = version,
            ManifestFingerprint = manifestFingerprint, SharedRefsFingerprint = sharedRefsFingerprint,
            ModuleDef = moduleDef,
            FileHashByPath = fileHashByPath,
            ObjectByPath = objectByPath,
            SourceByKey = sourceByKey,
            LastOutput = fullOutput,
        };
    }

    /// <summary>Drops the current baseline for a bundle — used when a caller knows the next cycle must be a full rebuild regardless (e.g. a watched suite set changed).</summary>
    public void ClearIncrementalBaseline(string moduleName) => _radBaselines.Remove(moduleName);

    private static NavCA.ParseOptions RadParseOptions(ManifestCompilerInputs manifestInputs) => new(
        runtimeVersion: null!,
        preprocessorSymbols: Enumerable.Range(1, 25).Select(n => $"CLEANSCHEMA{n}"),
        documentationMode: NavCA.DocumentationMode.None);

    private static NavCA.CompilationOptions RadCompilationOptions(ManifestCompilerInputs manifestInputs) => new(
        continueBuildOnError: true,
        target: NavCA.CompilationTarget.OnPrem,
        generateOptions: NavCA.CompilationGenerationOptions.Code | NavCA.CompilationGenerationOptions.Navigation,
        compilerFeatures: manifestInputs.CompilerFeatures,
        contextSensitiveHelpUrl: manifestInputs.ContextSensitiveHelpUrl);

    /// <summary>Shallow-clones a ModuleDefinition with the given (Kind,Id) objects removed from every id-bearing array property.</summary>
    private static NavSymRef.ModuleDefinition ExcludeObjects(NavSymRef.ModuleDefinition module, IReadOnlySet<RadObjectIdentity> exclude)
    {
        var clone = CloneModuleDefinition(module);
        foreach (var (propName, kind) in RadMergeablePropertiesByKind)
        {
            var idsToExclude = new HashSet<int>(exclude.Where(e => e.Kind == kind).Select(e => e.Id));
            if (idsToExclude.Count == 0) continue;
            var prop = typeof(NavSymRef.ModuleDefinition).GetProperty(propName)!;
            if (prop.GetValue(module) is not Array arr) continue;
            var elemType = prop.PropertyType.GetElementType()!;
            var idProp = elemType.GetProperty("Id")!;
            var kept = arr.Cast<object>().Where(item => !idsToExclude.Contains((int)idProp.GetValue(item)!)).ToList();
            var result = Array.CreateInstance(elemType, kept.Count);
            for (int i = 0; i < kept.Count; i++) result.SetValue(kept[i], i);
            prop.SetValue(clone, result);
        }
        return clone;
    }

    /// <summary>
    /// (old module, minus the just-changed objects) UNION (delta module's definitions for
    /// exactly those objects) — see this file's header comment for why this must be a manual
    /// merge rather than trusting the delta compilation's own conversion to be complete.
    /// </summary>
    private static NavSymRef.ModuleDefinition MergeModuleDefinition(
        NavSymRef.ModuleDefinition oldModule, IReadOnlySet<RadObjectIdentity> changed, NavSymRef.ModuleDefinition delta)
    {
        var merged = CloneModuleDefinition(oldModule);
        foreach (var (propName, kind) in RadMergeablePropertiesByKind)
        {
            var changedIds = new HashSet<int>(changed.Where(c => c.Kind == kind).Select(c => c.Id));
            var prop = typeof(NavSymRef.ModuleDefinition).GetProperty(propName)!;
            var elemType = prop.PropertyType.GetElementType()!;
            var idProp = elemType.GetProperty("Id")!;

            var kept = new List<object>();
            if (prop.GetValue(oldModule) is Array oldArr)
                foreach (var item in oldArr)
                    if (!changedIds.Contains((int)idProp.GetValue(item)!)) kept.Add(item);
            if (changedIds.Count > 0 && prop.GetValue(delta) is Array deltaArr)
                foreach (var item in deltaArr)
                    if (changedIds.Contains((int)idProp.GetValue(item)!)) kept.Add(item);

            var result = Array.CreateInstance(elemType, kept.Count);
            for (int i = 0; i < kept.Count; i++) result.SetValue(kept[i], i);
            prop.SetValue(merged, result);
        }
        return merged;
    }

    private static NavSymRef.ModuleDefinition CloneModuleDefinition(NavSymRef.ModuleDefinition module)
        => (NavSymRef.ModuleDefinition)typeof(NavSymRef.ModuleDefinition)
            .GetMethod("Clone", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(module, null)!;

    /// <summary>
    /// ModuleDefinition array properties that carry an id-bearing object kind, and the
    /// SymbolKind each corresponds to. Every kind NOT listed here is either id-less (see
    /// <see cref="IdlessSymbolKinds"/>, always full-rebuild) or not something this fast path's
    /// (Kind,Id) classification step can ever produce.
    /// </summary>
    private static readonly (string PropertyName, NavCA.SymbolKind Kind)[] RadMergeablePropertiesByKind =
    {
        ("Tables", NavCA.SymbolKind.Table),
        ("Codeunits", NavCA.SymbolKind.Codeunit),
        ("Pages", NavCA.SymbolKind.Page),
        ("PageExtensions", NavCA.SymbolKind.PageExtension),
        ("TableExtensions", NavCA.SymbolKind.TableExtension),
        ("Reports", NavCA.SymbolKind.Report),
        ("ReportExtensions", NavCA.SymbolKind.ReportExtension),
        ("XmlPorts", NavCA.SymbolKind.XmlPort),
        ("Queries", NavCA.SymbolKind.Query),
        ("EnumTypes", NavCA.SymbolKind.Enum),
        ("EnumExtensionTypes", NavCA.SymbolKind.EnumExtension),
        ("PermissionSets", NavCA.SymbolKind.PermissionSet),
        ("PermissionSetExtensions", NavCA.SymbolKind.PermissionSetExtension),
    };

    /// <summary>
    /// Resolves CreateForRad's mandatory <c>symbolReferenceLoader</c>/<c>symbolReferences</c> for
    /// THIS module's own baseline objects — see this file's header comment for why
    /// packagedModuleDefinition alone does not resolve them.
    /// </summary>
    private sealed class RadSelfBaselineLoader : NavCA.ISymbolReferenceLoader
    {
        private readonly Guid _appId;
        private readonly NavSymRef.ModuleDefinition _module;
        public RadSelfBaselineLoader(Guid appId, NavSymRef.ModuleDefinition module) { _appId = appId; _module = module; }

        public NavSymRef.ModuleDefinition? LoadModule(NavCA.SymbolReferenceSpecification reference, IList<NavCA.Diagnostics.Diagnostic> diagnostics)
            => reference.AppId == _appId ? _module : null;

        public NavCA.ModuleInfo LoadModuleInfo(NavCA.SymbolReferenceSpecification reference, IList<NavCA.Diagnostics.Diagnostic> diagnostics, NavCA.LoadModuleInfoFlags flags)
            => reference.AppId == _appId ? new NavCA.ModuleInfo(_module, documentationProvider: null) : null!;

        public IEnumerable<NavCA.SymbolReferenceSpecification> GetDependencies(NavCA.SymbolReferenceSpecification reference, IList<NavCA.Diagnostics.Diagnostic> diagnostics)
            => Enumerable.Empty<NavCA.SymbolReferenceSpecification>();
    }
}
