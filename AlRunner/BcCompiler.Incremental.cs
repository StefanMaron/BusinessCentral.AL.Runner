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
//
// SymbolReference.ModuleDefinition nests namespace-declared objects — never trust the
// top-level kind arrays alone (issue #2507)
// -----------------------------------------------------------------------------------------
// Confirmed by decompiling SerializableSymbolModelConverter: `ConvertModuleToSerializableSymbolModel`
// populates a ModuleDefinition's OWN Codeunits/Tables/Pages/etc. arrays from
// `moduleSymbol.GlobalNamespace.SymbolMap` — the GLOBAL namespace's DIRECT children only.
// An object declared inside a `namespace Foo.Bar;` block is NOT a direct child of the global
// namespace; it lives under a chain of nested `NamespaceDefinition` nodes instead
// (`ModuleDefinition.Namespaces[i].Namespaces[j]....Codeunits`, built recursively by
// `ConvertNamespaces`/`SetContainerObjects`), and BC's converter is faithful about this — it is
// not a bug in BC. `Compilation.GetDeclaredApplicationObjectSymbols()` does NOT have this split
// (it walks `ModuleSymbol.SymbolMap`, which recurses through every namespace level itself), which
// is why that API and `SymbolJsonWriter.GetModuleDefinition` on the SAME compilation can report
// wildly different counts (140 declared objects vs. 0 in the top-level arrays) for an app that
// declares `namespace` in (nearly) every file — the modern `al new` default, and true of the real
// corpus this was diagnosed against. `RecordIncrementalBaseline`/`ExcludeObjects`/
// `MergeModuleDefinition` below therefore never touch a ModuleDefinition's or NamespaceDefinition's
// kind-array properties without ALSO recursing into `.Namespaces` — see `RadMergeablePropertiesByKind`
// (reflects on `IObjectContainerDefinition`, the interface BOTH ModuleDefinition and
// NamespaceDefinition implement, precisely so the same by-kind filtering logic applies at every
// namespace depth without duplicating it) and the namespace-matching-by-`Name` step in
// `MergeContainerRecursive`. A `NamespaceDefinition` itself is never a RAD object with its own
// (Kind,Id-or-Name) identity — it is purely structural, so it is matched across old/delta trees by
// its own `Name` (BC never sets `NamespaceDefinition.Id` — confirmed in `ConvertNamespaces`), never
// excluded/merged as if it were a changed application object.
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
    internal BcEmitOutput? TryEmitIncremental(
        IEnumerable<string> alFolders, string moduleName, string? appRootDir,
        out string fallbackReason, out IReadOnlyList<AffectedObjectId>? changedObjects)
    {
        fallbackReason = "";
        changedObjects = null;
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
        var alFiles = dirs.SelectMany(d => AlRunner.Infrastructure.SafeDirectoryScan.Files(d, "*.al")).Distinct().ToList();

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

        var bundleAlpackages = dirs.SelectMany(d => AlRunner.Infrastructure.SafeDirectoryScan.Directories(d, ".alpackages")).Distinct();
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
            changedObjects = Array.Empty<AffectedObjectId>();
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
        changedObjects = allChangedIdentities
            .OrderBy(i => i.Kind.ToString(), StringComparer.Ordinal)
            .ThenBy(i => i.Id ?? int.MaxValue)
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .Select(ToAffectedObjectId)
            .ToArray();

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
        // #2151: same file-relative LayoutFile override Emit() applies — scanned against the
        // FULL alFiles list (not just this cycle's changedTrees) so an incremental cycle
        // resolves identically to a from-scratch Emit() of the same bundle.
        var radFileSystem = ReportLayoutFileSystem.Build(alFiles, appRootDir);
        if (radFileSystem != null)
            radComp = radComp.WithFileSystem(radFileSystem);
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

        // #2548: the ONE hole in this file's "an unmodified caller is always safe" argument, and
        // it is silent. See OverloadAddedUnderAnExistingName for the mechanism. Checked here
        // because this is the first point where BOTH serialized surfaces exist, and the last
        // point at which returning null is still free — nothing below has been committed yet.
        if (OverloadAddedUnderAnExistingName(baseline.ModuleDef, deltaModuleDef, allChangedIdentities) is { } overloaded)
        {
            fallbackReason =
                $"{overloaded} gained a procedure under a name it already declared (an added overload). "
                + "That moves which member id an UNMODIFIED caller binds to without moving any existing "
                + "member's own id, so reusing the caller's cached C# would leave it dispatching the "
                + "previous overload — silently, since that member still exists. Falling back to a full "
                + "compile for this cycle";
            return null;
        }

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

    internal BcEmitOutput? TryEmitIncremental(
        IEnumerable<string> alFolders, string moduleName, string? appRootDir, out string fallbackReason)
        => TryEmitIncremental(alFolders, moduleName, appRootDir, out fallbackReason, out _);

    internal IReadOnlyDictionary<string, AffectedObjectId>? TryGetTrackedObjectsByPath(string moduleName)
        => _radBaselines.TryGetValue(moduleName, out var baseline)
            ? baseline.ObjectByPath.ToDictionary(kv => kv.Key, kv => ToAffectedObjectId(kv.Value), StringComparer.Ordinal)
            : null;

    /// <summary>
    /// #2492: a side-effect-free "peek" at which AL objects changed in <paramref name="moduleName"/>'s
    /// OWN files since its last recorded RAD baseline — no <c>CreateForRad</c>, no <c>Emit</c>, no
    /// baseline mutation, and (unlike <see cref="TryEmitIncremental"/>) no requirement that the
    /// resolved dependency set is unchanged, since a dependency-symbol change is not this method's
    /// concern.
    ///
    /// Exists for a multi-<c>sourcePaths</c> server request's affectedOnly selection: a test in
    /// bundle B can cover an AL object declared in bundle A (a real cross-app call, not a
    /// hypothetical — see issue #2492's Pageworks/Pageworks.Test repro), so B's own per-file diff
    /// alone can never tell whether A changed. The caller peeks EVERY bundle in the request BEFORE
    /// deciding any bundle's coverage-based narrowing, and unions the results — see
    /// <c>RunAllBundlesForServer</c> in Program.cs.
    ///
    /// Returns null when this bundle's own changed-object set cannot be determined with
    /// confidence — no baseline yet, a touched file could not be parsed/classified, or a
    /// removed file was not tracked. The caller MUST treat null as "unknown, do not narrow" (the
    /// same posture <see cref="TryEmitIncremental"/>'s own null-changedObjects fallback already
    /// takes), never as "nothing changed here". Deliberately over-inclusive rather than precise: a
    /// rename is reported as BOTH the old and the new identity changing (no vacated/appeared
    /// pairing), and a removed/added file's declared identity is always included — extra entries
    /// only cause a test to run that didn't strictly need to, never the silent loss this method
    /// exists to close.
    ///
    /// Deliberately does NOT check the manifest fingerprint <see cref="TryEmitIncremental"/> does
    /// (identity/version/preprocessor-symbols/features) — that check exists there to decide
    /// whether the REAL RAD compile is safe to attempt at all, using <c>_currentAppId</c>/
    /// <c>_currentPublisher</c>/<c>_currentVersion</c>, static state that is only valid while the
    /// real per-bundle compile flow has scoped it via <c>SetCurrentAppIdentity</c>. A caller
    /// peeking every bundle in a multi-<c>sourcePaths</c> request UP FRONT, before any bundle's own
    /// compile has scoped that state, cannot rely on it — and does not need to: an app.json change
    /// this method fails to notice only means a slightly stale (but still safe, still additive)
    /// contribution to the caller's union, never a false "nothing changed".
    /// </summary>
    internal IReadOnlyList<AffectedObjectId>? PeekChangedObjects(
        IEnumerable<string> alFolders, string moduleName, string? appRootDir)
    {
        if (!_radBaselines.TryGetValue(moduleName, out var baseline))
            return null;

        var dirs = alFolders.Where(Directory.Exists).Distinct().ToList();
        var alFiles = dirs.SelectMany(d => AlRunner.Infrastructure.SafeDirectoryScan.Files(d, "*.al")).Distinct().ToList();

        var manifestAppJsonPath = (appRootDir != null && File.Exists(Path.Combine(appRootDir, "app.json")))
            ? Path.Combine(appRootDir, "app.json")
            : dirs.Select(d => Path.Combine(d, "app.json")).FirstOrDefault(File.Exists);
        var manifestInputs = ReadManifestCompilerInputs(manifestAppJsonPath);

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
            return Array.Empty<AffectedObjectId>();

        var parseOpts = RadParseOptions(manifestInputs);
        var compOpts = RadCompilationOptions(manifestInputs);
        var changed = new HashSet<RadObjectIdentity>();

        foreach (var path in removedPaths)
        {
            if (!baseline.ObjectByPath.TryGetValue(path, out var oldIdentity))
                return null; // untracked removal — only the real compile path can adjudicate this
            changed.Add(oldIdentity);
        }

        foreach (var path in addedPaths.Concat(modifiedPaths))
        {
            NavSyntax.SyntaxTree tree;
            try
            {
                var src = File.ReadAllText(path);
                tree = NavSyntax.SyntaxTree.ParseObjectText(src, path: path, encoding: null!, parseOpts, default);
            }
            catch { return null; }

            var (identity, error) = ClassifyDeclaredObject(tree, compOpts);
            if (identity == null) return null;
            changed.Add(identity.Value);

            // An in-place rename (path already tracked under a DIFFERENT identity) vacates the
            // old identity too — report both rather than pairing them, per this method's own
            // over-inclusive-by-design contract above.
            if (baseline.ObjectByPath.TryGetValue(path, out var oldIdentityAtSamePath)
                && IdentityKey(oldIdentityAtSamePath) != IdentityKey(identity.Value))
                changed.Add(oldIdentityAtSamePath);
        }

        return changed
            .OrderBy(i => i.Kind.ToString(), StringComparer.Ordinal)
            .ThenBy(i => i.Id ?? int.MaxValue)
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .Select(ToAffectedObjectId)
            .ToArray();
    }

    /// <summary>Projects to the NavCA-free shape Program.cs is allowed to name.
    /// See <see cref="AffectedObjectId"/> for why that boundary exists.</summary>
    private static AffectedObjectId ToAffectedObjectId(RadObjectIdentity id)
        => new(id.Kind.ToString(), id.Id, id.Name);

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
        // #2507: a namespace-declared id-less object lives under `.Namespaces[...]`, not the
        // module's own top-level arrays — walk every container in the tree, not just the root
        // (see this file's header comment on ModuleDefinition namespace nesting).
        foreach (var container in EnumerateContainers(moduleDef))
        {
            if (container.Interfaces != null) foreach (var e in container.Interfaces) TrackIdless(e.ReferenceSourceFileName, e.Name, NavCA.SymbolKind.Interface);
            if (container.ControlAddIns != null) foreach (var e in container.ControlAddIns) TrackIdless(e.ReferenceSourceFileName, e.Name, NavCA.SymbolKind.ControlAddIn);
            if (container.Profiles != null) foreach (var e in container.Profiles) TrackIdless(e.ReferenceSourceFileName, e.Name, NavCA.SymbolKind.Profile);
            if (container.PageCustomizations != null) foreach (var e in container.PageCustomizations) TrackIdless(e.ReferenceSourceFileName, e.Name, NavCA.SymbolKind.PageCustomization);
            if (container.ProfileExtensions != null) foreach (var e in container.ProfileExtensions) TrackIdless(e.ReferenceSourceFileName, e.Name, NavCA.SymbolKind.ProfileExtension);
        }

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

    /// <summary>
    /// Clones a ModuleDefinition with the given objects removed from every mergeable array
    /// property, keyed by <see cref="ElementKey"/> (id when the kind has one, else name) —
    /// recursing into <c>.Namespaces</c> at every depth (issue #2507: a namespace-declared
    /// object never appears in the top-level arrays at all — see this file's header comment).
    /// Never mutates <paramref name="module"/> or any of its descendants.
    /// </summary>
    private static NavSymRef.ModuleDefinition ExcludeObjects(NavSymRef.ModuleDefinition module, IReadOnlySet<RadObjectIdentity> exclude)
        => (NavSymRef.ModuleDefinition)ExcludeObjectsRecursive(module, exclude);

    private static NavSymRef.IObjectContainerDefinition ExcludeObjectsRecursive(NavSymRef.IObjectContainerDefinition container, IReadOnlySet<RadObjectIdentity> exclude)
    {
        var clone = CloneContainerShallow(container);
        foreach (var (propName, kind) in RadMergeablePropertiesByKind)
        {
            var prop = RadContainerProperty(propName);
            if (prop.GetValue(container) is not Array arr) continue;
            var keysToExclude = new HashSet<string>(exclude.Where(e => e.Kind == kind).Select(IdentityElementKeyOf));
            if (keysToExclude.Count == 0)
            {
                // #2479: nothing of THIS kind is being excluded from THIS container — but the
                // property must still be copied onto the clone. `CloneContainerShallow`'s
                // NamespaceDefinition branch (unlike ModuleDefinition's own BC-provided Clone())
                // only ever sets Id/Name/Namespaces, so any kind this loop does not otherwise
                // touch is left at its CLR default (null) on a namespace clone. A namespaced
                // bundle where the touched/excluded object is a Codeunit and an untouched
                // sibling is any OTHER kind (Table, Report, Page, …) previously lost that
                // sibling's entire array here — the self-loader then handed BC's binder a
                // namespace with a null Tables/Reports/… array, and CreateForRad's codegen
                // crashed deep inside EmitFieldInitializer with "Unexpected value 'None' of type
                // NavTypeKind" (a symbol that resolves far enough for name lookup but was never
                // given a real TypeKind). A same-kind sibling (Codeunit referencing Codeunit) was
                // never affected, because excluding the touched Codeunit already forced this
                // loop's Codeunits branch to run and (correctly) reassign the property. See
                // BcCompilerIncrementalCrossKindSiblingTests for the RED/GREEN proof.
                prop.SetValue(clone, arr);
                continue;
            }
            var elemType = prop.PropertyType.GetElementType()!;
            var kept = arr.Cast<object>().Where(item => !keysToExclude.Contains(ElementKey(item, kind))).ToList();
            var result = Array.CreateInstance(elemType, kept.Count);
            for (int i = 0; i < kept.Count; i++) result.SetValue(kept[i], i);
            prop.SetValue(clone, result);
        }
        if (container.Namespaces != null)
            clone.Namespaces = container.Namespaces
                .Select(ns => (NavSymRef.NamespaceDefinition)ExcludeObjectsRecursive(ns, exclude))
                .ToArray();
        return clone;
    }

    /// <summary>
    /// (old module, minus the just-changed objects) UNION (delta module's definitions for
    /// exactly those objects) — see this file's header comment for why this must be a manual
    /// merge rather than trusting the delta compilation's own conversion to be complete.
    /// Recurses into <c>.Namespaces</c> at every depth, matching an old namespace against its
    /// delta counterpart BY NAME (a `namespace` block has no (Kind,Id-or-Name) identity of its
    /// own — see this file's header comment) — a namespace delta declares ONLY THIS cycle
    /// touches is brought in wholesale (everything under it is, by construction, part of
    /// <paramref name="changed"/>, since <c>SymbolJsonWriter.GetModuleDefinition</c> on a RAD
    /// compilation reflects only source-declared objects — see its own doc comment).
    /// </summary>
    private static NavSymRef.ModuleDefinition MergeModuleDefinition(
        NavSymRef.ModuleDefinition oldModule, IReadOnlySet<RadObjectIdentity> changed, NavSymRef.ModuleDefinition delta)
        => (NavSymRef.ModuleDefinition)MergeContainerRecursive(oldModule, changed, delta);

    private static NavSymRef.IObjectContainerDefinition MergeContainerRecursive(
        NavSymRef.IObjectContainerDefinition oldContainer, IReadOnlySet<RadObjectIdentity> changed, NavSymRef.IObjectContainerDefinition? deltaContainer)
    {
        var merged = CloneContainerShallow(oldContainer);
        foreach (var (propName, kind) in RadMergeablePropertiesByKind)
        {
            var changedKeys = new HashSet<string>(changed.Where(c => c.Kind == kind).Select(IdentityElementKeyOf));
            var prop = RadContainerProperty(propName);
            var elemType = prop.PropertyType.GetElementType()!;

            var kept = new List<object>();
            if (prop.GetValue(oldContainer) is Array oldArr)
                foreach (var item in oldArr)
                    if (!changedKeys.Contains(ElementKey(item, kind))) kept.Add(item);
            if (changedKeys.Count > 0 && deltaContainer != null && prop.GetValue(deltaContainer) is Array deltaArr)
                foreach (var item in deltaArr)
                    if (changedKeys.Contains(ElementKey(item, kind))) kept.Add(item);

            var result = Array.CreateInstance(elemType, kept.Count);
            for (int i = 0; i < kept.Count; i++) result.SetValue(kept[i], i);
            prop.SetValue(merged, result);
        }

        var oldNamespaces = oldContainer.Namespaces ?? Array.Empty<NavSymRef.NamespaceDefinition>();
        var deltaNamespaces = deltaContainer?.Namespaces ?? Array.Empty<NavSymRef.NamespaceDefinition>();
        if (oldNamespaces.Length > 0 || deltaNamespaces.Length > 0)
        {
            var deltaByName = deltaNamespaces.ToDictionary(n => n.Name ?? "", StringComparer.Ordinal);
            var mergedNamespaces = new List<NavSymRef.NamespaceDefinition>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var oldNs in oldNamespaces)
            {
                seenNames.Add(oldNs.Name ?? "");
                deltaByName.TryGetValue(oldNs.Name ?? "", out var deltaNs);
                mergedNamespaces.Add((NavSymRef.NamespaceDefinition)MergeContainerRecursive(oldNs, changed, deltaNs));
            }
            foreach (var (name, deltaNs) in deltaByName)
                if (!seenNames.Contains(name))
                    mergedNamespaces.Add(deltaNs); // wholly new namespace this cycle — everything under it is already "changed" by construction
            merged.Namespaces = mergedNamespaces.Count > 0 ? mergedNamespaces.ToArray() : null;
        }
        return merged;
    }

    /// <summary>Same format as <see cref="ElementKey"/> ("id:&lt;n&gt;"/"name:&lt;x&gt;"), derived from a <see cref="RadObjectIdentity"/> instead of a reflected element.</summary>
    private static string IdentityElementKeyOf(RadObjectIdentity id) => id.Id.HasValue ? "id:" + id.Id.Value : "name:" + id.Name;

    /// <summary>
    /// Names the first changed object that gained a procedure under a name it ALREADY declared,
    /// or null when none did. Issue #2548 — the one edit shape this file's header argument does
    /// not cover, and the only one whose damage is silent.
    ///
    /// <para><b>The mechanism.</b> BC's <c>MethodSymbol.CalculateMethodIdForNewVersions</c> is
    /// method-local: adding <c>Which(Integer)</c> beside <c>Which(Decimal)</c> moves neither the
    /// Decimal overload's id nor its <c>case</c> label in the re-emitted callee's <c>OnInvoke</c>
    /// switch. What moves is the id the CALLER bakes — an Integer argument used to widen to
    /// <c>Which(Decimal)</c> and now binds to <c>Which(Integer)</c>. An un-rebound caller
    /// therefore dispatches a member that still exists and gets the PREVIOUS overload's answer:
    /// no <c>NavNCLMissingMethodException</c>, no diagnostic, no log line. Every other breaking
    /// edit retires or moves an existing id and is loud at the call site, exactly as this file's
    /// header describes.</para>
    ///
    /// <para><b>Why a full-compile fallback rather than a rebind.</b> Rebinding needs to know who
    /// the callers ARE, which needs an object-reference graph this fast path does not maintain.
    /// Falling back is correct, is already this path's answer to everything it cannot prove safe,
    /// and costs one cycle on an edit shape that is rare next to ordinary body edits.</para>
    ///
    /// <para><b>Why the comparison is serialized-against-serialized.</b> Both sides come out of
    /// <c>SymbolJsonWriter.GetModuleDefinition</c>-shaped module definitions, so both describe the
    /// same public surface under the same rules — in particular BC serializes no <c>local</c>
    /// method on either side, so adding a local overload (which no other object can call, and
    /// which therefore cannot move anyone's baked id) does not trip this. Comparing the new
    /// SYNTAX against the old serialized surface would, and would fall back for nothing.</para>
    ///
    /// <para><b>Deliberately narrow.</b> Only "a name that already had at least one member now has
    /// more" triggers. A procedure added under a NEW name changes no existing call site's overload
    /// resolution and moves no id, so it stays on the fast path; so does a body edit, a rename, and
    /// a signature change (loud at runtime). An object absent from, or ambiguous in, either
    /// definition is skipped rather than treated as a trigger: an ADDED object has no baseline copy
    /// and no pre-existing callers, and a kind with no <c>Methods</c> array has no member ids for
    /// anyone to bake.</para>
    ///
    /// <para>Credit: the hazard and the compiler contract under it were found and pinned by Mikkel
    /// Mansa Vilhelmsen (vhn) in his AL Runner fork.</para>
    /// </summary>
    private static string? OverloadAddedUnderAnExistingName(
        NavSymRef.ModuleDefinition before, NavSymRef.ModuleDefinition after, IReadOnlySet<RadObjectIdentity> changed)
    {
        if (changed.Count == 0) return null;
        var previousByObject = RadMethodNameCounts(before, changed);
        if (previousByObject.Count == 0) return null;
        var currentByObject = RadMethodNameCounts(after, changed);

        foreach (var (id, previous) in previousByObject)
        {
            if (previous == null || previous.Count == 0) continue;
            if (!currentByObject.TryGetValue(id, out var current) || current == null) continue;
            foreach (var (name, count) in current)
                if (previous.TryGetValue(name, out var had) && count > had)
                    return $"{id.Kind} '{id.Name}'";
        }
        return null;
    }

    /// <summary>
    /// How many serialized methods each object in <paramref name="wanted"/> declares under each
    /// name, in one pass over <paramref name="module"/> — every namespace depth included (#2507).
    ///
    /// <para>One pass, not one per identity: a bulk change (a branch switch, a bulk rename) can put
    /// hundreds of objects in the change set, and asking per identity would re-walk the whole
    /// module — reflection <c>GetValue</c> over every element of every kind array — once per one of
    /// them.</para>
    ///
    /// <para>An object missing from the result was not found. A null VALUE is "found, but cannot be
    /// counted" — present more than once (ambiguous), no readable <c>Methods</c> array, or a member
    /// with no usable name. Both are non-answers to
    /// <see cref="OverloadAddedUnderAnExistingName"/> and neither is "no methods", which is an
    /// empty dictionary. Names are counted case-insensitively because AL identifiers are.</para>
    /// </summary>
    private static Dictionary<RadObjectIdentity, Dictionary<string, int>?> RadMethodNameCounts(
        NavSymRef.ModuleDefinition module, IReadOnlySet<RadObjectIdentity> wanted)
    {
        var byKindAndKey = new Dictionary<(NavCA.SymbolKind Kind, string Key), RadObjectIdentity>();
        foreach (var id in wanted)
            if (RadMergeablePropertiesByKind.Any(p => p.Kind == id.Kind))
                byKindAndKey[(id.Kind, IdentityElementKeyOf(id))] = id;

        var result = new Dictionary<RadObjectIdentity, Dictionary<string, int>?>();
        if (byKindAndKey.Count == 0) return result;

        var kindsWanted = byKindAndKey.Keys.Select(k => k.Kind).ToHashSet();
        foreach (var container in EnumerateContainers(module))
        {
            foreach (var (propName, kind) in RadMergeablePropertiesByKind)
            {
                if (!kindsWanted.Contains(kind)) continue;
                if (RadContainerProperty(propName).GetValue(container) is not Array arr) continue;
                foreach (var item in arr)
                {
                    if (item == null) continue;
                    if (!byKindAndKey.TryGetValue((kind, ElementKey(item, kind)), out var id)) continue;
                    // A second copy of a key is ambiguous: which one answers would be decided by
                    // array order rather than by the edit. Refuse to answer for it.
                    result[id] = result.ContainsKey(id) ? null : RadMemberNameCounts(item);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// One serialized object's method-name multiset, or null when a member cannot be named — see
    /// <see cref="RadMethodNameCounts"/> for how null is read.
    /// </summary>
    private static Dictionary<string, int>? RadMemberNameCounts(object element)
    {
        if (element.GetType().GetProperty("Methods")?.GetValue(element) is not Array methods) return null;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var method in methods)
        {
            if (method == null) continue;
            if (method.GetType().GetProperty("Name")?.GetValue(method) is not string name || name.Length == 0)
                return null;
            counts[name] = counts.TryGetValue(name, out var n) ? n + 1 : 1;
        }
        return counts;
    }

    private static NavSymRef.ModuleDefinition CloneModuleDefinition(NavSymRef.ModuleDefinition module)
        => (NavSymRef.ModuleDefinition)typeof(NavSymRef.ModuleDefinition)
            .GetMethod("Clone", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(module, null)!;

    /// <summary>
    /// Shallow-clones a container (ModuleDefinition OR NamespaceDefinition — both implement
    /// <see cref="NavSymRef.IObjectContainerDefinition"/>) WITHOUT sharing any mutable array
    /// reference with the original: <see cref="CloneModuleDefinition"/>'s <c>MemberwiseClone</c>
    /// copies every property (including every mergeable array) verbatim, but
    /// <c>NamespaceDefinition</c> has no <c>Clone()</c> of its own (confirmed by decompile), so
    /// this hand-rolled branch sets ONLY <c>Id</c>/<c>Name</c>/<c>Namespaces</c> — every
    /// mergeable-array property starts out unset (CLR default, i.e. null) on a namespace clone.
    ///
    /// #2479: that means EVERY caller here MUST explicitly set every mergeable property on the
    /// result, for every kind — not just the kinds it is actually excluding/merging — or a
    /// namespace-nested kind the caller had no reason to touch this cycle silently loses its
    /// entire array. <see cref="MergeContainerRecursive"/> always does
    /// (`prop.SetValue(merged, result)` runs unconditionally for every kind in
    /// <see cref="RadMergeablePropertiesByKind"/>). <see cref="ExcludeObjectsRecursive"/> did NOT
    /// before this fix — it skipped `prop.SetValue` entirely whenever nothing of that kind was
    /// being excluded, so a namespaced bundle where the touched object was a Codeunit left every
    /// OTHER kind's array null on the clone. BC's binder resolved a reference into a null Tables/
    /// Reports/… array far enough to bind a symbol, but never gave it a real
    /// <c>NavTypeKind</c> — <c>Compilation.Emit</c> crashed deep inside
    /// <c>CodeGenerator.EmitFieldInitializer</c> with "Unexpected value 'None' of type
    /// NavTypeKind" the first time an untouched, namespace-nested, non-Codeunit sibling (Table,
    /// Report, Page, …) was referenced from the edited object's own syntax tree. See
    /// BcCompilerIncrementalCrossKindSiblingTests for the RED/GREEN proof — a same-kind sibling
    /// (Codeunit referencing Codeunit) never hit this, because excluding the touched Codeunit
    /// already forced the Codeunits property to be set.
    /// </summary>
    private static NavSymRef.IObjectContainerDefinition CloneContainerShallow(NavSymRef.IObjectContainerDefinition container)
        => container switch
        {
            NavSymRef.ModuleDefinition module => CloneModuleDefinition(module),
            NavSymRef.NamespaceDefinition ns => new NavSymRef.NamespaceDefinition { Id = ns.Id, Name = ns.Name, Namespaces = ns.Namespaces },
            _ => throw new InvalidOperationException($"Unexpected {nameof(NavSymRef.IObjectContainerDefinition)} implementation: {container.GetType().Name}"),
        };

    /// <summary>
    /// <see cref="RadMergeablePropertiesByKind"/> reflects on <see cref="NavSymRef.IObjectContainerDefinition"/>
    /// itself (not <c>ModuleDefinition</c>) so the SAME <see cref="PropertyInfo"/> works whether
    /// <paramref name="propName"/> is being read/written on a <c>ModuleDefinition</c> (the
    /// module root) or a <c>NamespaceDefinition</c> (any nested namespace level) — both
    /// implement the interface with the identical property set (confirmed by decompile), and
    /// .NET reflection through an interface-typed <see cref="PropertyInfo"/> dispatches
    /// correctly to whichever concrete type the instance actually is.
    /// </summary>
    private static PropertyInfo RadContainerProperty(string propName)
        => typeof(NavSymRef.IObjectContainerDefinition).GetProperty(propName)!;

    /// <summary>
    /// Depth-first walk of <paramref name="root"/> and every <c>NamespaceDefinition</c> nested
    /// under it (issue #2507) — the container itself first, then each child's own subtree.
    /// </summary>
    private static IEnumerable<NavSymRef.IObjectContainerDefinition> EnumerateContainers(NavSymRef.IObjectContainerDefinition root)
    {
        yield return root;
        if (root.Namespaces == null) yield break;
        foreach (var ns in root.Namespaces)
            foreach (var descendant in EnumerateContainers(ns))
                yield return descendant;
    }

    /// <summary>
    /// ModuleDefinition/NamespaceDefinition array properties this fast path merges/excludes
    /// objects from — every id-bearing kind, plus the 5 id-less kinds ModuleDefinition DOES
    /// represent (see this file's header comment). Entitlement is deliberately absent: neither
    /// type has an Entitlements array at all.
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
    ///
    /// Placed FIRST in the <see cref="CompositeSymbolReferenceLoader"/> chain built by
    /// <see cref="TryEmitIncremental"/> (self loader, then <c>refLoader</c> — the real
    /// package/JSON-symbols loader for every OTHER dependency, e.g. System Application, Base
    /// Application). "Not mine" (a spec for any AppId other than this module's own) MUST
    /// throw <see cref="FileNotFoundException"/> — the ONE "not mine" convention every
    /// <see cref="NavCA.ISymbolReferenceLoader"/> composed via
    /// <see cref="CompositeSymbolReferenceLoader"/> in this file uses (see
    /// <see cref="JsonSymbolReferenceLoader"/>'s <c>LoadModule</c>/<c>LoadModuleInfo</c>/
    /// <c>GetDependencies</c> in SymbolJson.cs, which already throw for exactly this reason,
    /// on all three methods).
    ///
    /// Issue #2009: this loader used to signal "not mine" by returning <c>null</c> /
    /// <c>Enumerable.Empty&lt;&gt;()</c> instead — a DIFFERENT convention from the rest of the
    /// chain. Sitting first, that null/empty answer WAS the composite's final result for
    /// every non-self spec on two of the three methods:
    ///   - <c>CompositeSymbolReferenceLoader.LoadModuleInfo</c> has no null-check (`return
    ///     child.LoadModuleInfo(...)` inside a `catch (FileNotFoundException)` only) — a
    ///     `null` answer from THIS (first) child was returned as the composite's own final
    ///     answer, without ever asking `refLoader`. Confirmed the live cause by instrumenting
    ///     this method and reproducing #2009's exact "could not be loaded" diagnostics: BC's
    ///     `CreateForRad` calls `LoadModuleInfo` (never bare `LoadModule`) to resolve each
    ///     dependency spec, got `null` for every MS-app package, and reported it unresolved.
    ///   - <c>CompositeSymbolReferenceLoader.GetDependencies</c> only falls through on
    ///     `null`, and `Enumerable.Empty&lt;&gt;()` is not null — the same failure
    ///     <see cref="JsonSymbolReferenceLoader.GetDependencies"/>'s own comment warns about
    ///     ("would WIN the composite race and erase the real dependency list").
    /// `LoadModule` happened to keep working only because
    /// <c>CompositeSymbolReferenceLoader.LoadModule</c> is the one method with an explicit
    /// `if (module != null) return module;` check — a second, independent "not mine" signal
    /// that the other two methods do not share. Converging THIS loader onto the throwing
    /// convention (rather than adding matching null-checks to the other two composite
    /// methods) removes the split itself, so the next loader added to this chain cannot
    /// reintroduce the same bug by picking the "wrong" one of two coexisting conventions.
    ///
    /// Throwing here is safe even when this loader is used bare (no `refLoader` — a bundle
    /// with zero resolved dependencies, so `TryEmitIncremental` skips the
    /// <see cref="CompositeSymbolReferenceLoader"/> wrapper entirely and hands
    /// <c>Compilation.CreateForRad</c> this loader directly): <see cref="JsonSymbolReferenceLoader"/>
    /// already throws unconditionally on every miss and is *also* sometimes handed to BC bare
    /// (<c>BcCompiler.GetSharedReferences</c>' `chain.Count == 1` case) — proven safe by every
    /// green corpus run that exercises that path, since BC's own reference resolution treats
    /// the exception exactly like the null/empty answer it tolerates from a `Compilation`
    /// built without any dependencies at all: a graceful "not found" diagnostic, not a crash.
    /// </summary>
    private sealed class RadSelfBaselineLoader : NavCA.ISymbolReferenceLoader
    {
        private readonly Guid _appId;
        private readonly NavSymRef.ModuleDefinition _module;
        public RadSelfBaselineLoader(Guid appId, NavSymRef.ModuleDefinition module) { _appId = appId; _module = module; }

        public NavSymRef.ModuleDefinition? LoadModule(NavCA.SymbolReferenceSpecification reference, IList<NavCA.Diagnostics.Diagnostic> diagnostics)
        {
            if (reference.AppId != _appId)
                throw new FileNotFoundException(
                    $"Symbol reference not found: {reference.Publisher}/{reference.Name} {reference.Version}");
            return _module;
        }

        public NavCA.ModuleInfo LoadModuleInfo(NavCA.SymbolReferenceSpecification reference, IList<NavCA.Diagnostics.Diagnostic> diagnostics, NavCA.LoadModuleInfoFlags flags)
        {
            if (reference.AppId != _appId)
                throw new FileNotFoundException(
                    $"Symbol reference not found: {reference.Publisher}/{reference.Name} {reference.Version}");
            return new NavCA.ModuleInfo(_module, documentationProvider: null);
        }

        public IEnumerable<NavCA.SymbolReferenceSpecification> GetDependencies(NavCA.SymbolReferenceSpecification reference, IList<NavCA.Diagnostics.Diagnostic> diagnostics)
        {
            if (reference.AppId != _appId)
                throw new FileNotFoundException(
                    $"Symbol reference dependencies not found: {reference.Publisher}/{reference.Name} {reference.Version}");
            // Self has no further transitive deps THIS loader needs to report — the
            // module's own dependency closure is already the (separately supplied)
            // `combinedSpecs` list, not something discovered on demand here.
            return Enumerable.Empty<NavCA.SymbolReferenceSpecification>();
        }
    }
}
