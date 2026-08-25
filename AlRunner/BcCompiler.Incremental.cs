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
// This file classifies every touched file into one of: content edit, add, remove, or rename
// (of the file, of the object's own AL name, or both at once) — for every object kind,
// including the six with no numeric Id (interface/controladdin/profile/pagecustomization/
// profileextension/entitlement). It falls back to the ordinary, already-correct, whole-module
// Emit() only for what genuinely cannot be proven safe: the first cycle for a bundle, an
// app.json/dependency-set change, more than one object declared in a touched file, a duplicate
// declaration only the compiler can adjudicate, or any diagnostic/exception the delta compile
// itself raises — never a stale or wrong result, just not accelerated for that cycle.
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
//   - A REMOVED object's cached C# is dropped from the union entirely, so its runtime type is
//     simply absent from the freshly built assembly this cycle — every registry that discovers
//     AL objects by reflecting the loaded assembly (AllObj, TestExecutor discovery, subscriber
//     registries, …) naturally forgets it too, with no extra bookkeeping required here.
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
// their OLD shape). See RadSelfBaselineLoader below.
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
//
// Renames are not a distinct BC-facing case
// -------------------------------------------
// BC's ObjectChangeElement carries no file path — its own equality (decompiled and confirmed:
// ObjectChangeElement.NamespaceAgnosticEqualityComparer) is (Kind,Id) when Id is set, else
// (Kind,Name). So from CreateForRad's point of view a file rename that preserves the object's
// (Kind,Id-or-Name) IS a content edit (Modified) — only OUR OWN ObjectByPath bookkeeping needs
// to move the path. What is genuinely new is: (a) a "vacated" set — identities no longer found
// at their old path (a removed file, or a modified file whose declared identity itself changed,
// e.g. the AL object was renamed in place), and (b) an "appeared" set — new identities found
// this cycle (an added file, or a modified file whose identity changed). Any identity present in
// BOTH sets is a rename/move (Modified, tree taken from wherever it now lives); what is left in
// "appeared" is genuinely Added; what is left in "vacated" is genuinely Removed.
//
// The six id-less object kinds
// -------------------------------
// `Compilation.GetDeclaredApplicationObjectSymbols()` is typed to
// `ImmutableArray<IApplicationObjectTypeSymbol>`, and `IApplicationObjectTypeSymbol : ISymbolWithId`
// (decompiled and confirmed) — an id-less kind that does not satisfy this can never be found by
// asking "does this file declare an interface/controladdin/…" through it. Confirmed EMPIRICALLY
// (not just from the interface type hierarchy) that this API's actual behaviour splits the six
// unevenly: interface and controladdin genuinely never come back from it; profile,
// pagecustomization and profileextension DO (their symbol types implement ISymbolWithId after
// all) — but the Id that comes back is BC's own internal SymbolMap bookkeeping value (same
// pattern as InterfaceTypeSymbol.Id below), NOT stable across independently-constructed
// Compilations of the identically-named object, so it is NEVER trusted here: every one of the
// six is forced to a Name-keyed RadObjectIdentity (Id = null) regardless of which API found it or
// what numeric value that API happened to report (see the IdlessSymbolKinds.Contains guards in
// both ClassifyDeclaredObject and RecordIncrementalBaseline). Two different fallbacks for
// actually FINDING them:
//   - interface, controladdin, profile, pagecustomization, profileextension: BC's
//     SymbolReference.ModuleDefinition DOES represent all five (Interfaces/ControlAddIns/
//     Profiles/PageCustomizations/ProfileExtensions arrays — SerializableSymbolModelConverter
//     walks ALL declared objects, not just IApplicationObjectTypeSymbol ones), so this is the
//     fallback ClassifyDeclaredObject uses for interface/controladdin specifically (profile/
//     pagecustomization/profileextension are already caught by the id-bearing branch above,
//     Name-forced). Tracked across cycles via each element's own ReferenceSourceFileName — NOT
//     always the same string as tree.FilePath (confirmed empirically): with a RelativeFileSystem
//     attached (appRootDir != null, the normal --watch case — ControlAddIn/etc. resource paths
//     need one, see ControlAddInFileSystemTests) it comes back APP-ROOT-RELATIVE and must be
//     resolved against appRootDir; with none attached it is already absolute. LanguageElement.Id
//     is `int?` on EVERY *Definition type, including id-bearing ones (they just always populate
//     it) — so ExcludeObjects/MergeModuleDefinition below key by a generic "id:<n>" or "name:<x>"
//     string derived from the KIND (never trusting the reflected value for these five — see
//     ElementKey), and all five are merged into the baseline ModuleDefinition exactly like an
//     id-bearing kind, keyed by name.
//   - entitlement: the ONE kind with NO SymbolReference.ModuleDefinition representation AT ALL
//     (ModuleDefinition has no Entitlements array — confirmed by decompiling its property list).
//     It can never round-trip through packagedModuleDefinition, touched or not, so the ONLY way
//     it is ever present in ANY cycle's RAD compilation is by being included in `syntaxTrees`
//     THAT cycle. Every tracked entitlement file is therefore re-included in `syntaxTrees` on
//     EVERY incremental cycle regardless of whether it changed — proportional to the (typically
//     0-2) entitlement files in an app, not to the whole module — classified syntactically
//     (there is no semantic/ModuleDefinition path to find it) via the same "exactly one
//     ObjectSyntax child" technique Program.cs's IsFullAlObjectDeclaration already uses. It
//     produces no runtime C# (EntitlementSymbol has no Emit path), so this never inflates the
//     cached-C#-union.
//
// A `dotnet` package declaration is deliberately NOT in the recognised-kind set below (the
// issue's own list of forced-fallback conditions names it) — a file that declares one falls
// through ClassifyDeclaredObject's every branch and returns null, landing on the ordinary
// "declares 0 classifiable object(s)" fallback with no special-casing needed.
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using NavEmit = Microsoft.Dynamics.Nav.CodeAnalysis.Emit;
using NavSymRef = Microsoft.Dynamics.Nav.CodeAnalysis.SymbolReference;

namespace AlRunner;

/// <summary>
/// (Kind, Id-or-Name) identity of an AL application object — the unit CreateForRad's
/// ObjectChangeModelDefinition classifies changes by. <see cref="Id"/> is null for the six
/// id-less kinds (interface, controladdin, profile, pagecustomization, profileextension,
/// entitlement — see <see cref="BcCompiler.IdlessSymbolKinds"/>), in which case <see cref="Name"/>
/// is the identity.
/// </summary>
internal readonly record struct RadObjectIdentity(NavCA.SymbolKind Kind, int? Id, string Name);

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
    /// Object kinds with no numeric Id. Matches issue #1902's own enumeration. Five of the six
    /// (everything but entitlement) ARE represented in SymbolReference.ModuleDefinition and are
    /// merged/excluded via <see cref="RadMergeablePropertiesByKind"/> like any id-bearing kind,
    /// keyed by name; entitlement has no ModuleDefinition representation at all — see this
    /// file's header comment.
    /// </summary>
    internal static readonly IReadOnlySet<NavCA.SymbolKind> IdlessSymbolKinds = new HashSet<NavCA.SymbolKind>
    {
        NavCA.SymbolKind.Interface, NavCA.SymbolKind.ControlAddIn, NavCA.SymbolKind.Profile,
        NavCA.SymbolKind.PageCustomization, NavCA.SymbolKind.ProfileExtension, NavCA.SymbolKind.Entitlement,
    };

    private static string RadObjKey(NavCA.SymbolKind kind, int id) => $"{kind}:{id}";

    /// <summary>Stable (Kind,Id-or-Name) key used to match a "vacated" identity against an "appeared" one across paths.</summary>
    private static string IdentityKey(RadObjectIdentity id) => $"{id.Kind}|{(id.Id.HasValue ? "id:" + id.Id.Value : "name:" + id.Name)}";

    private static NavCA.ObjectChangeElement ToChangeElement(RadObjectIdentity id) => new() { Id = id.Id, Kind = id.Kind, Name = id.Name };

    /// <summary>
    /// Same "id:&lt;n&gt;"/"name:&lt;x&gt;" key as <see cref="IdentityKey"/>, but for a reflected
    /// ModuleDefinition array element. Deliberately does NOT trust the element's own reflected
    /// <c>Id</c> property to decide which branch to take: BC assigns EVERY *Definition type
    /// (including the 5 ModuleDefinition-backed id-less kinds) a non-null internal <c>int Id</c>
    /// for its own SymbolMap bookkeeping (decompiled: e.g. InterfaceTypeSymbol.Id is a hash that
    /// folds in the declaring compilation's OWN AppId) — NOT the AL-author-visible identity, and
    /// NOT stable across two independently-constructed Compilations of the identically-named
    /// object (a single-file classify Compilation has a different AppId/shape than the real
    /// module). Keying by that value would silently fail to match (and therefore silently fail
    /// to exclude) for every id-less kind. <paramref name="kind"/> is the caller's own already-
    /// known SymbolKind (it is iterating one ModuleDefinition property at a time) — id-bearing
    /// kinds key by that real Id, id-less kinds key by Name always, regardless of what the
    /// reflected Id happens to hold.
    /// </summary>
    private static string ElementKey(object item, NavCA.SymbolKind kind)
    {
        var t = item.GetType();
        if (!IdlessSymbolKinds.Contains(kind))
        {
            var idVal = t.GetProperty("Id")?.GetValue(item) as int?;
            if (idVal.HasValue) return "id:" + idVal.Value;
        }
        var name = t.GetProperty("Name")?.GetValue(item) as string ?? "";
        return "name:" + name;
    }

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
    /// Classifies exactly what object (if any) a single already-parsed file declares. Tries the
    /// semantic, id-bearing path first (proven, reuses the classification GetDeclaredApplicationObjectSymbols
    /// gives for free), then the 5 ModuleDefinition-backed id-less kinds, then a syntax-only
    /// check for entitlement — see this file's header comment for why each tier exists. Never
    /// throws for AL content a human might plausibly have typed; a genuine internal fault still
    /// surfaces as a (null, error) fallback rather than an unhandled exception.
    /// </summary>
    private static (RadObjectIdentity? Identity, string? Error) ClassifyDeclaredObject(NavSyntax.SyntaxTree tree, NavCA.CompilationOptions compOpts)
    {
        var classify = NavCA.Compilation.Create(moduleName: "__rad_classify", syntaxTrees: new[] { tree }, options: compOpts);

        ImmutableArray<NavCA.IApplicationObjectTypeSymbol> declaredIdBearing;
        try { declaredIdBearing = classify.GetDeclaredApplicationObjectSymbols(); }
        catch (Exception ex) { return (null, $"classification threw: {ex.GetType().Name}: {ex.Message}"); }

        if (declaredIdBearing.Length > 1)
            return (null, $"declares {declaredIdBearing.Length} object(s) (fast path requires exactly 1 per file)");
        if (declaredIdBearing.Length == 1)
        {
            var sym = declaredIdBearing[0];
            // Some of the six id-less-per-issue kinds DO implement ISymbolWithId after all
            // (confirmed empirically: profile/pagecustomization/profileextension do; interface/
            // controladdin do not) — but that Id is BC's own internal SymbolMap bookkeeping
            // value, not stable across independently-constructed Compilations of the identically
            // named object (same instability as InterfaceTypeSymbol.Id — see this file's header
            // comment). Force Name-keyed identity for ALL SIX regardless of what ISymbolWithId
            // happens to report, so this stays consistent with the module-def-level merge/
            // exclusion machinery below, which ALSO always keys these six by name.
            if (IdlessSymbolKinds.Contains(sym.Kind))
                return (new RadObjectIdentity(sym.Kind, null, sym.Name), null);
            var id = (sym as NavCA.ISymbolWithId)?.Id;
            return id == null
                ? (null, $"'{sym.Name}' ({sym.Kind}) has no resolvable Id")
                : (new RadObjectIdentity(sym.Kind, id.Value, sym.Name), null);
        }

        NavSymRef.ModuleDefinition module;
        try { module = SymbolJsonWriter.GetModuleDefinition(classify); }
        catch (Exception ex) { return (null, $"module-definition classification threw: {ex.GetType().Name}: {ex.Message}"); }

        var idless = new List<(NavCA.SymbolKind Kind, string Name)>();
        if (module.Interfaces != null) idless.AddRange(module.Interfaces.Select(e => (NavCA.SymbolKind.Interface, e.Name ?? "")));
        if (module.ControlAddIns != null) idless.AddRange(module.ControlAddIns.Select(e => (NavCA.SymbolKind.ControlAddIn, e.Name ?? "")));
        if (module.Profiles != null) idless.AddRange(module.Profiles.Select(e => (NavCA.SymbolKind.Profile, e.Name ?? "")));
        if (module.PageCustomizations != null) idless.AddRange(module.PageCustomizations.Select(e => (NavCA.SymbolKind.PageCustomization, e.Name ?? "")));
        if (module.ProfileExtensions != null) idless.AddRange(module.ProfileExtensions.Select(e => (NavCA.SymbolKind.ProfileExtension, e.Name ?? "")));

        if (idless.Count > 1)
            return (null, $"declares {idless.Count} object(s) (fast path requires exactly 1 per file)");
        if (idless.Count == 1)
            return (new RadObjectIdentity(idless[0].Kind, null, idless[0].Name), null);

        if (tree.GetRoot() is NavSyntax.CompilationUnitSyntax root)
        {
            var objectNodes = root.ChildNodes().OfType<NavSyntax.ObjectSyntax>().ToList();
            if (objectNodes.Count > 1)
                return (null, $"declares {objectNodes.Count} object(s) (fast path requires exactly 1 per file)");
            if (objectNodes.Count == 1 && objectNodes[0] is NavSyntax.EntitlementSyntax ent)
                return (new RadObjectIdentity(NavCA.SymbolKind.Entitlement, null, ent.Name.Identifier.ValueText ?? ent.Name.ToString()), null);
        }

        return (null, "declares 0 objects the fast path can classify (empty file, a `dotnet` package declaration, or an object kind the fast path does not recognise)");
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
            // #2002: under --tdd, RecordIncrementalBaseline (called from Emit, below) is
            // deliberately skipped whenever the cycle excluded an object for referencing a
            // missing symbol — a baseline built while an object is missing would let a LATER
            // incremental cycle silently treat it as "still there". That means the cycle
            // where a --tdd exclusion happens, AND every cycle after it up to and including
            // the one that finally implements the missing symbol, all land here. Name that
            // explicitly instead of the generic reason, so the console explains WHY (see
            // #1994's precedent for surfacing full-rebuild causes at default verbosity).
            fallbackReason = _tddMode
                ? "no incremental baseline yet for this bundle (first --watch cycle, or --tdd reported " +
                  "a synthetic FAILED test for a missing symbol on a previous cycle — a baseline is only " +
                  "recorded on a clean compile with nothing excluded, so cycles stay a full rebuild until " +
                  "the missing symbol is implemented and the module compiles clean again)"
                : "no incremental baseline yet for this bundle (first --watch cycle, or the previous cycle fell back)";
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

        var currentHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in alFiles) currentHashes[f] = HashFile(f);

        var addedPaths = new List<string>();
        var removedPaths = new List<string>();
        var modifiedPaths = new List<string>();
        foreach (var kv in currentHashes)
        {
            if (!baseline.FileHashByPath.TryGetValue(kv.Key, out var oldHash)) addedPaths.Add(kv.Key);
            else if (!string.Equals(oldHash, kv.Value, StringComparison.Ordinal)) modifiedPaths.Add(kv.Key);
        }
        foreach (var oldPath in baseline.FileHashByPath.Keys)
            if (!currentHashes.ContainsKey(oldPath)) removedPaths.Add(oldPath);

        if (addedPaths.Count == 0 && removedPaths.Count == 0 && modifiedPaths.Count == 0)
        {
            // Every file hashes identical to the last cycle — including a touch-with-identical-
            // bytes. Genuinely zero work: replay the last cycle's result verbatim.
            return baseline.LastOutput;
        }

        var parseOpts = RadParseOptions(manifestInputs);
        var compOpts = RadCompilationOptions(manifestInputs);

        // --- classify every touched path ----------------------------------------------------
        // See this file's header ("Renames are not a distinct BC-facing case") for the
        // vacated/appeared design this is built on.
        var vacated = new Dictionary<string, (string Path, RadObjectIdentity Identity)>(StringComparer.Ordinal);
        var appeared = new Dictionary<string, (string Path, RadObjectIdentity Identity, NavSyntax.SyntaxTree Tree)>(StringComparer.Ordinal);
        var contentEdits = new List<(string Path, RadObjectIdentity Identity, NavSyntax.SyntaxTree Tree)>();

        foreach (var path in removedPaths)
        {
            if (!baseline.ObjectByPath.TryGetValue(path, out var oldIdentity))
            {
                fallbackReason = $"'{path}' was removed but was not tracked as a single-object file by the previous baseline";
                return null;
            }
            vacated[IdentityKey(oldIdentity)] = (path, oldIdentity);
        }

        foreach (var path in addedPaths.Concat(modifiedPaths))
        {
            NavSyntax.SyntaxTree tree;
            try
            {
                var src = File.ReadAllText(path);
                tree = NavSyntax.SyntaxTree.ParseObjectText(src, path: path, encoding: null!, parseOpts, default);
            }
            catch (Exception ex)
            {
                fallbackReason = $"'{path}' could not be read/parsed: {ex.GetType().Name}: {ex.Message}";
                return null;
            }

            var (identity, error) = ClassifyDeclaredObject(tree, compOpts);
            if (identity == null)
            {
                fallbackReason = $"'{path}': {error}";
                return null;
            }

            var wasTracked = baseline.ObjectByPath.TryGetValue(path, out var oldIdentityAtSamePath);
            if (wasTracked && IdentityKey(oldIdentityAtSamePath) == IdentityKey(identity.Value))
            {
                // Same file, same identity: an ordinary content edit.
                contentEdits.Add((path, identity.Value, tree));
                continue;
            }
            if (wasTracked)
            {
                // This path's declared identity itself changed (an in-place rename) — the OLD
                // identity is vacated here, exactly like a removed file's identity would be.
                var oldKey = IdentityKey(oldIdentityAtSamePath);
                if (vacated.ContainsKey(oldKey))
                {
                    fallbackReason = $"'{path}': its previous identity was already vacated by another file this cycle";
                    return null;
                }
                vacated[oldKey] = (path, oldIdentityAtSamePath);
            }

            var newKey = IdentityKey(identity.Value);
            if (!appeared.TryAdd(newKey, (path, identity.Value, tree)))
            {
                fallbackReason = $"two files both now declare '{newKey}' — duplicate declaration, only the compiler can adjudicate that";
                return null;
            }
        }

        // Pair vacated <-> appeared identities (renames/moves): from BC's point of view these
        // are Modified, not Removed+Added — see this file's header comment.
        var renamePairs = new List<(RadObjectIdentity Identity, string OldPath, string NewPath, NavSyntax.SyntaxTree Tree)>();
        foreach (var key in vacated.Keys.Where(appeared.ContainsKey).ToList())
        {
            var oldEntry = vacated[key];
            var newEntry = appeared[key];
            renamePairs.Add((newEntry.Identity, oldEntry.Path, newEntry.Path, newEntry.Tree));
            vacated.Remove(key);
            appeared.Remove(key);
        }

        // What is left in `appeared` is genuinely new. A genuinely new identity colliding with
        // an EXISTING, untouched baseline object is a duplicate declaration only the compiler
        // can adjudicate (the issue's own words) — not something to fast-path.
        foreach (var (key, entry) in appeared)
        {
            if (baseline.ObjectByPath.Values.Any(v => IdentityKey(v) == key))
            {
                fallbackReason = $"'{entry.Path}' declares '{key}', which already exists elsewhere in the baseline — " +
                    "duplicate declaration, only the compiler can adjudicate that";
                return null;
            }
        }

        // Entitlement: no ModuleDefinition representation at all, so every TRACKED entitlement
        // file not already handled above is re-included this cycle regardless of whether it
        // changed — see this file's header comment.
        var touchedPaths = new HashSet<string>(addedPaths, StringComparer.Ordinal);
        touchedPaths.UnionWith(removedPaths);
        touchedPaths.UnionWith(modifiedPaths);
        var alwaysIncluded = new List<(RadObjectIdentity Identity, NavSyntax.SyntaxTree Tree)>();
        foreach (var (path, identity) in baseline.ObjectByPath)
        {
            if (identity.Kind != NavCA.SymbolKind.Entitlement || touchedPaths.Contains(path)) continue;
            if (!File.Exists(path)) continue; // defensive — would already be in removedPaths otherwise
            var src = File.ReadAllText(path);
            var tree = NavSyntax.SyntaxTree.ParseObjectText(src, path: path, encoding: null!, parseOpts, default);
            alwaysIncluded.Add((identity, tree));
        }

        var addedElements = appeared.Values.Select(a => ToChangeElement(a.Identity)).ToArray();
        var modifiedElements = contentEdits.Select(c => ToChangeElement(c.Identity))
            .Concat(renamePairs.Select(r => ToChangeElement(r.Identity)))
            .Concat(alwaysIncluded.Select(a => ToChangeElement(a.Identity)))
            .ToArray();
        var removedElements = vacated.Values.Select(v => ToChangeElement(v.Identity)).ToArray();

        var changeModel = new NavCA.ObjectChangeModelDefinition
        {
            Added = addedElements,
            Modified = modifiedElements,
            Removed = removedElements,
        };

        var changedTrees = contentEdits.Select(c => c.Tree)
            .Concat(renamePairs.Select(r => r.Tree))
            .Concat(appeared.Values.Select(a => a.Tree))
            .Concat(alwaysIncluded.Select(a => a.Tree))
            .ToList();

        var allChangedIdentities = new HashSet<RadObjectIdentity>();
        foreach (var a in appeared.Values) allChangedIdentities.Add(a.Identity);
        foreach (var c in contentEdits) allChangedIdentities.Add(c.Identity);
        foreach (var r in renamePairs) allChangedIdentities.Add(r.Identity);
        foreach (var v in vacated.Values) allChangedIdentities.Add(v.Identity);

        var selfSpec = new NavCA.SymbolReferenceSpecification(
            publisher: publisher, name: moduleName, version: version,
            exact: false, appId: appId, isPropagated: false, alternateIds: ImmutableArray<Guid>.Empty);
        var selfModule = ExcludeObjects(baseline.ModuleDef, allChangedIdentities);
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
                packagedModuleDefinition: selfModule,
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

        // Only id-bearing objects ever produce runtime C# (id-less kinds — interface/
        // controladdin/profile/pagecustomization/profileextension/entitlement — are pure
        // metadata, no OnRun/OnInvoke to emit; see this file's header comment).
        var idBearingChanged = addedElements.Concat(modifiedElements).Where(e => e.Id.HasValue).ToList();
        var newByKey = new Dictionary<string, EmittedSource>();
        foreach (var src in radOut.Captured)
        {
            var match = idBearingChanged.FirstOrDefault(e => string.Equals(e.Name, src.Name, StringComparison.Ordinal));
            if (match == null)
            {
                fallbackReason = $"RAD Emit produced an unexpected object '{src.Name}'";
                return null;
            }
            newByKey[RadObjKey(match.Kind, match.Id!.Value)] = src;
        }
        if (newByKey.Count != idBearingChanged.Count)
        {
            fallbackReason = $"RAD Emit produced {newByKey.Count} object(s), expected {idBearingChanged.Count}";
            return null;
        }

        // Removed id-bearing objects' cached C# is dropped from the union entirely — their
        // runtime metadata goes with them (see this file's header comment).
        var removedKeys = new HashSet<string>(
            vacated.Values.Where(v => v.Identity.Id.HasValue).Select(v => RadObjKey(v.Identity.Kind, v.Identity.Id!.Value)));

        var unionedSources = new List<EmittedSource>(baseline.SourceByKey.Count);
        foreach (var kv in baseline.SourceByKey)
        {
            if (removedKeys.Contains(kv.Key)) continue;
            unionedSources.Add(newByKey.TryGetValue(kv.Key, out var fresh) ? fresh : kv.Value);
        }
        foreach (var kv in newByKey)
            if (!baseline.SourceByKey.ContainsKey(kv.Key)) unionedSources.Add(kv.Value);

        var deltaModuleDef = SymbolJsonWriter.GetModuleDefinition(radComp);
        var mergedModuleDef = MergeModuleDefinition(baseline.ModuleDef, allChangedIdentities, deltaModuleDef);

        var newFileHashByPath = new Dictionary<string, string>(baseline.FileHashByPath, StringComparer.Ordinal);
        foreach (var path in addedPaths.Concat(modifiedPaths)) newFileHashByPath[path] = currentHashes[path];
        foreach (var path in removedPaths) newFileHashByPath.Remove(path);

        // Rebuilt purely from the final classified buckets (vacated/renamePairs/appeared) —
        // these already cover every path-level change: `vacated` holds both originally-removed
        // files AND in-place-modified files whose old identity never found a rename partner,
        // each with the correct OLD path to drop. `contentEdits` needs no action: path and
        // identity are both unchanged from the baseline copy.
        var newObjectByPath = new Dictionary<string, RadObjectIdentity>(baseline.ObjectByPath, StringComparer.Ordinal);
        foreach (var v in vacated.Values) newObjectByPath.Remove(v.Path);
        foreach (var r in renamePairs) { newObjectByPath.Remove(r.OldPath); newObjectByPath[r.NewPath] = r.Identity; }
        foreach (var a in appeared.Values) newObjectByPath[a.Path] = a.Identity;

        var newSourceByKey = new Dictionary<string, EmittedSource>(baseline.SourceByKey.Count, StringComparer.Ordinal);
        foreach (var kv in baseline.SourceByKey)
        {
            if (removedKeys.Contains(kv.Key)) continue;
            newSourceByKey[kv.Key] = newByKey.TryGetValue(kv.Key, out var fresh) ? fresh : kv.Value;
        }
        foreach (var kv in newByKey)
            if (!newSourceByKey.ContainsKey(kv.Key)) newSourceByKey[kv.Key] = kv.Value;

        var output = new BcEmitOutput(unionedSources, Array.Empty<string>(), Array.Empty<string>());

        _radBaselines[moduleName] = new RadBaseline
        {
            AppId = appId, Publisher = publisher, Version = version,
            ManifestFingerprint = manifestFingerprint, SharedRefsFingerprint = sharedRefsFingerprint,
            ModuleDef = mergedModuleDef,
            FileHashByPath = newFileHashByPath,
            ObjectByPath = newObjectByPath,
            SourceByKey = newSourceByKey,
            LastOutput = output,
        };

        return output;
    }

    /// <summary>
    /// Called by <see cref="Emit"/> after a clean success, when its caller passed
    /// <c>trackIncrementalBaseline: true</c>. Builds the (Kind,Id-or-Name)-keyed state
    /// <see cref="TryEmitIncremental"/> needs for the NEXT cycle, for every object kind (see
    /// this file's header comment for how each of the six id-less kinds is recovered).
    /// </summary>
    private void RecordIncrementalBaseline(
        string moduleName, NavCA.Compilation compilation, IReadOnlyList<string> alFiles,
        IReadOnlyList<EmittedSource> captured, NavCA.SymbolReferenceSpecification[] specs,
        ManifestCompilerInputs manifestInputs, string? manifestAppJsonPath, Guid appId, string publisher, Version version,
        string? appRootDir, BcEmitOutput fullOutput)
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

        var objectByPath = new Dictionary<string, RadObjectIdentity>(StringComparer.Ordinal);
        var claimedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sym in declared)
        {
            var path = sym.Location?.SourceTree?.FilePath;
            if (path == null) continue;
            // Force Name-keyed identity for the six id-less-per-issue kinds even when
            // ISymbolWithId reports a value — see ClassifyDeclaredObject's identical guard for
            // why that Id is not trustworthy across compilations.
            var id = IdlessSymbolKinds.Contains(sym.Kind) ? null : (sym as NavCA.ISymbolWithId)?.Id;
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

        var moduleDef = SymbolJsonWriter.GetModuleDefinition(compilation);

        // Of the six id-less kinds, only interface/controladdin genuinely never appear in
        // `declared` above (confirmed empirically: profile/pagecustomization/profileextension DO
        // implement ISymbolWithId and come back from GetDeclaredApplicationObjectSymbols() —
        // the earlier "IApplicationObjectTypeSymbol : ISymbolWithId" decompile only proves an
        // id-less kind CAN be excluded that way, not that every one of these six IS; see this
        // file's header comment). This ModuleDefinition-array recovery is therefore a fallback
        // for whichever of the five module-def-backed kinds `declared` did NOT already surface —
        // `claimedPaths.Contains` (not `.Add`) is the check: a path `declared` already claimed
        // must be LEFT ALONE (its identity from the richer, already-correct API), never
        // overwritten OR treated as a same-file duplicate declaration.
        //
        // Confirmed empirically NOT the same string as tree.FilePath in every case: when a
        // RelativeFileSystem is attached (appRootDir != null — the normal --watch case, since
        // ControlAddIn/PageCustomization/etc. resource paths need one, see
        // ControlAddInFileSystemTests), ReferenceSourceFileName comes back APP-ROOT-RELATIVE
        // ("Addin.al"), not absolute — resolve it against appRootDir the same way
        // alFiles/hash-diffing paths are absolute. With no FileSystem attached it is already
        // absolute (matches tree.FilePath verbatim) — used as-is.
        void TrackIdless(string? path, string? name, NavCA.SymbolKind kind)
        {
            if (string.IsNullOrEmpty(path) || name == null) return;
            var resolved = appRootDir != null && !Path.IsPathFullyQualified(path)
                ? Path.GetFullPath(Path.Combine(appRootDir, path))
                : path;
            if (objectByPath.ContainsKey(resolved)) return; // already tracked via `declared` — a DIFFERENT API surfacing the SAME object, not a real duplicate
            if (!claimedPaths.Add(resolved)) { objectByPath.Remove(resolved); return; } // a genuine second id-less object in one file
            objectByPath[resolved] = new RadObjectIdentity(kind, null, name);
        }
        if (moduleDef.Interfaces != null) foreach (var e in moduleDef.Interfaces) TrackIdless(e.ReferenceSourceFileName, e.Name, NavCA.SymbolKind.Interface);
        if (moduleDef.ControlAddIns != null) foreach (var e in moduleDef.ControlAddIns) TrackIdless(e.ReferenceSourceFileName, e.Name, NavCA.SymbolKind.ControlAddIn);
        if (moduleDef.Profiles != null) foreach (var e in moduleDef.Profiles) TrackIdless(e.ReferenceSourceFileName, e.Name, NavCA.SymbolKind.Profile);
        if (moduleDef.PageCustomizations != null) foreach (var e in moduleDef.PageCustomizations) TrackIdless(e.ReferenceSourceFileName, e.Name, NavCA.SymbolKind.PageCustomization);
        if (moduleDef.ProfileExtensions != null) foreach (var e in moduleDef.ProfileExtensions) TrackIdless(e.ReferenceSourceFileName, e.Name, NavCA.SymbolKind.ProfileExtension);

        // Entitlement: no ModuleDefinition representation at all — recovered from the ALREADY-
        // PARSED syntax trees this compilation holds (no extra parse).
        foreach (var tree in compilation.SyntaxTrees)
        {
            var path = tree.FilePath;
            if (string.IsNullOrEmpty(path)) continue;
            if (tree.GetRoot() is not NavSyntax.CompilationUnitSyntax root) continue;
            var objectNodes = root.ChildNodes().OfType<NavSyntax.ObjectSyntax>().ToList();
            if (objectNodes.Count != 1 || objectNodes[0] is not NavSyntax.EntitlementSyntax ent) continue;
            var name = ent.Name.Identifier.ValueText ?? ent.Name.ToString();
            TrackIdless(path, name, NavCA.SymbolKind.Entitlement);
        }

        var sourceByKey = new Dictionary<string, EmittedSource>(StringComparer.Ordinal);
        foreach (var src in captured)
        {
            if (!byName.TryGetValue(src.Name, out var candidates) || candidates.Count != 1)
                continue; // ambiguous (name shared across kinds, or unresolved) — leave untracked, not fatal.
            var (kind, id, path) = candidates[0];
            if (id == null)
                continue; // id-less kind — never emits runtime C# anyway (see header comment).
            sourceByKey[RadObjKey(kind, id.Value)] = src;
        }

        var fileHashByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in alFiles) fileHashByPath[f] = HashFile(f);

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

    /// <summary>Shallow-clones a ModuleDefinition with the given objects removed from every mergeable array property, keyed by <see cref="ElementKey"/> (id when the kind has one, else name).</summary>
    private static NavSymRef.ModuleDefinition ExcludeObjects(NavSymRef.ModuleDefinition module, IReadOnlySet<RadObjectIdentity> exclude)
    {
        var clone = CloneModuleDefinition(module);
        foreach (var (propName, kind) in RadMergeablePropertiesByKind)
        {
            var keysToExclude = new HashSet<string>(exclude.Where(e => e.Kind == kind).Select(IdentityElementKeyOf));
            if (keysToExclude.Count == 0) continue;
            var prop = typeof(NavSymRef.ModuleDefinition).GetProperty(propName)!;
            if (prop.GetValue(module) is not Array arr) continue;
            var elemType = prop.PropertyType.GetElementType()!;
            var kept = arr.Cast<object>().Where(item => !keysToExclude.Contains(ElementKey(item, kind))).ToList();
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
            var changedKeys = new HashSet<string>(changed.Where(c => c.Kind == kind).Select(IdentityElementKeyOf));
            var prop = typeof(NavSymRef.ModuleDefinition).GetProperty(propName)!;
            var elemType = prop.PropertyType.GetElementType()!;

            var kept = new List<object>();
            if (prop.GetValue(oldModule) is Array oldArr)
                foreach (var item in oldArr)
                    if (!changedKeys.Contains(ElementKey(item, kind))) kept.Add(item);
            if (changedKeys.Count > 0 && prop.GetValue(delta) is Array deltaArr)
                foreach (var item in deltaArr)
                    if (changedKeys.Contains(ElementKey(item, kind))) kept.Add(item);

            var result = Array.CreateInstance(elemType, kept.Count);
            for (int i = 0; i < kept.Count; i++) result.SetValue(kept[i], i);
            prop.SetValue(merged, result);
        }
        return merged;
    }

    /// <summary>Same format as <see cref="ElementKey"/> ("id:&lt;n&gt;"/"name:&lt;x&gt;"), derived from a <see cref="RadObjectIdentity"/> instead of a reflected element.</summary>
    private static string IdentityElementKeyOf(RadObjectIdentity id) => id.Id.HasValue ? "id:" + id.Id.Value : "name:" + id.Name;

    private static NavSymRef.ModuleDefinition CloneModuleDefinition(NavSymRef.ModuleDefinition module)
        => (NavSymRef.ModuleDefinition)typeof(NavSymRef.ModuleDefinition)
            .GetMethod("Clone", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(module, null)!;

    /// <summary>
    /// ModuleDefinition array properties this fast path merges/excludes objects from — every
    /// id-bearing kind, plus the 5 id-less kinds ModuleDefinition DOES represent (see this
    /// file's header comment). Entitlement is deliberately absent: ModuleDefinition has no
    /// Entitlements array at all.
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
        ("Interfaces", NavCA.SymbolKind.Interface),
        ("ControlAddIns", NavCA.SymbolKind.ControlAddIn),
        ("Profiles", NavCA.SymbolKind.Profile),
        ("PageCustomizations", NavCA.SymbolKind.PageCustomization),
        ("ProfileExtensions", NavCA.SymbolKind.ProfileExtension),
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
