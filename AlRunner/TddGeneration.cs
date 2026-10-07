// TddGeneration — issue #2001 (the deferred half of #1997): infers the missing member an
// AL0132 ("'<type>' does not contain a definition for '<member>'") diagnostic names, and
// generates it directly into the SOURCE-COMPILED implementing app's own SyntaxTree, so the
// object compiles and the [Test] procedure that references it actually RUNS instead of being
// excluded and reported from a compile diagnostic (TddSupport's refuse path — #2000).
//
// Interposition point: called from BcCompiler.Emit, BEFORE the exclude-and-retry loop gets a
// chance to drop anything, on the trees BcCompiler.Emit already parsed from the real files on
// disk — never a temp-directory copy (BcCompiler.Emit's own interposition-point comment and
// precompiled-dll-respect.md both apply: a copy would desync --watch's watched tree from the
// tree actually compiled, and would make a --tdd diagnostic's path unclickable in the editor).
//
// Scope, and why nothing here needs a separate "revert a bad guess" step: this generates into a
// tree BcCompiler.Emit ALREADY has in memory as one of its own `trees[]`, or (#5037) into the
// source of another bundle of the same run that is compiled from source — as overlay text
// (TddCrossBundle.cs), recompiled by re-running the cycle. A procedure of a codeunit declared by neither
// (a package's: .app symbols, a DLL or embedded source) is stubbed BESIDE the object in a new codeunit of
// this compile, never in it (#5037, TddPrecompiledStub.cs, docs/tdd-precompiled.md); any other symbol
// declared by neither, or one BC couldn't resolve at all, is refused for a structural reason. And because
// generation runs strictly BEFORE the
// pre-existing exclude-and-retry loop, a wrong guess is caught for free: if a generated member
// still doesn't make its referencing object compile (a bad inferred type, a shape this file
// doesn't recognize, anything), that object is excluded and its [Test] procedures reported
// failed exactly the way TddSupport already handles every other unresolved symbol — nothing in
// this file needs to notice or undo its own mistake.
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using NavDiag = Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using NavEmit = Microsoft.Dynamics.Nav.CodeAnalysis.Emit;

namespace AlRunner;

/// <summary>
/// One member --tdd generated. <see cref="Signature"/> is the human-readable declaration text
/// (e.g. <c>CalcTotal(Arg1: Integer): Integer</c>, <c>"Loyalty Points": Integer</c>,
/// <c>Archived = 1</c>) printed in the run's criterion-8 summary and usable directly as the
/// API the implementing app still has to hand-write to replace the generated stub.
/// </summary>
/// <remarks>
/// <see cref="DependentTests"/> — every "<c>ObjectDisplayName.MethodName</c>" [Test] that
/// reaches THIS member: the procedure holding an AL0132 naming it, or one that calls that
/// procedure through this compile's call graph (<see cref="TddCallGraph"/>), resolved
/// statically rather than from anything observed at runtime. Each of
/// those tests' results names this member (<see cref="TddDependents"/>, #5147) whatever its
/// outcome, so a pass against a generated member is never silent.
/// </remarks>
public sealed record TddGeneratedMember(string ObjectDisplayName, string MemberKind, string Signature)
{
    public IReadOnlyList<string> DependentTests { get; init; } = Array.Empty<string>();

    /// <summary>Set when the member was generated into ANOTHER source bundle of the run (#5037):
    /// the file it went into, as in-memory overlay text. The current compile cannot see it until
    /// that bundle is recompiled.</summary>
    public string? GeneratedIntoFile { get; init; }
}

public static partial class TddGeneration
{
    private const string MemberNotFoundDiagnosticId = "AL0132";
    // #5228: "No overload for method 'X' takes N arguments" — the procedure exists, the call has an
    // argument count none of its overloads takes.
    private const string NoOverloadForArgumentCountDiagnosticId = "AL0126";

    // NavTypeKind values this file will use as a FIELD type or a PROCEDURE parameter/return
    // type without needing anything beyond the bare type name — every one of these is fixed-
    // width, so there is no length to guess. Deliberately excludes Text/Code (length-bearing —
    // guessing a length is inventing, not inferring, so those cases refuse instead; see
    // TypeSymbolToAlText) and excludes Record/Codeunit/Page/... (too broad an object reference
    // to synthesize faithfully from a single call site).
    private static readonly HashSet<NavCA.NavTypeKind> SimpleBuiltinTypes = new()
    {
        NavCA.NavTypeKind.Integer, NavCA.NavTypeKind.BigInteger, NavCA.NavTypeKind.Decimal,
        NavCA.NavTypeKind.Boolean, NavCA.NavTypeKind.Date, NavCA.NavTypeKind.DateTime,
        NavCA.NavTypeKind.Time, NavCA.NavTypeKind.Duration, NavCA.NavTypeKind.Guid,
    };

    /// <summary>
    /// Scans <paramref name="emitResult"/> for AL0132 diagnostics, infers what each one is
    /// missing, and — where the inference is unambiguous — mutates the matching entry of
    /// <paramref name="trees"/> in place (same array, same indices BcCompiler.Emit's `alFiles`
    /// uses) to add the generated member. Diagnostics this cannot confidently resolve are left
    /// completely untouched: they flow on to BcCompiler.Emit's existing exclude-and-retry loop
    /// exactly as they did before this file existed.
    /// </summary>
    public static IReadOnlyList<TddGeneratedMember> Generate(
        NavCA.Compilation compilation,
        NavSyntax.SyntaxTree[] trees,
        NavCA.ParseOptions parseOptions,
        NavEmit.EmitResult emitResult,
        string? moduleName = null,
        bool hasDependents = false,
        string? manifestAppJsonPath = null)
    {
        // Snapshot BEFORE any mutation: once a tree in `trees` is replaced (a second missing
        // member found on an object already patched earlier in this same pass), the ORIGINAL
        // SyntaxTree instance a symbol's Location still points at is no longer present in
        // `trees` — so tree-identity lookups always go through this frozen snapshot, and only
        // the mutation itself touches `trees[idx]`.
        var originalTrees = (NavSyntax.SyntaxTree[])trees.Clone();

        // key = (target tree index, kind, member name) — the SAME missing member can be named
        // by more than one AL0132 diagnostic (two sibling [Test] procedures referencing the
        // same not-yet-declared field). Generated once, from the first call site that anchors it
        // (#5244): a site that cannot (a bare statement) refuses only itself, so a later site of the
        // same key is tried, and a key is refused only when no site of it could be generated.
        var generatedByKey = new Dictionary<string, TddGeneratedMember>(StringComparer.Ordinal);
        // key -> every diagnostic naming it, generated or not: after the member exists, a test that
        // reaches ANY of those sites compiles against it, the refused one included.
        var diagsByKey = new Dictionary<string, List<NavDiag.Diagnostic>>(StringComparer.Ordinal);
        // The cross-bundle state (TddCrossBundle.TryBeginAttempt) is asked once per key and records the
        // attempt, which is what stops a refused key being retried in this cycle; a key an earlier
        // compile of the cycle already attempted is blocked here.
        var crossSeen = new HashSet<string>(StringComparer.Ordinal);
        var blockedKeys = new HashSet<string>(StringComparer.Ordinal);

        // #5037: a procedure missing from a precompiled codeunit is stubbed beside it, in these trees,
        // before the loop below changes any of them (the edits are offsets into the trees as parsed).
        GenerateBesidePrecompiled(compilation, originalTrees, trees, parseOptions, emitResult, manifestAppJsonPath,
            generatedByKey, diagsByKey);

        foreach (var diag in emitResult.Diagnostics)
        {
            if (diag.Severity != NavDiag.DiagnosticSeverity.Error) continue;
            if (diag.Id != MemberNotFoundDiagnosticId && diag.Id != NoOverloadForArgumentCountDiagnosticId) continue;
            if (!diag.Location.IsInSource || diag.Location.SourceTree == null) continue;

            try
            {
                var target = ResolveTarget(compilation, originalTrees, diag);
                if (target == null) continue; // unrecognized shape / unresolvable qualifier — refuse
                if (target.Value.Precompiled) continue; // GenerateBesidePrecompiled above

                var key = target.Value.CrossFile != null
                    ? CrossKey(target.Value.CrossFile, target.Value.Kind, target.Value.MemberKey)
                    : $"{target.Value.TargetTreeIdx}|{target.Value.Kind}|{target.Value.MemberKey}";
                if (!diagsByKey.TryGetValue(key, out var siteList))
                    diagsByKey[key] = siteList = new List<NavDiag.Diagnostic>();
                siteList.Add(diag);
                if (generatedByKey.ContainsKey(key) || blockedKeys.Contains(key)) continue;

                // Begun once per key, here, so a refusal at one site does not close the key to the next.
                if (target.Value.CrossFile != null && crossSeen.Add(key) && !TddCrossBundle.TryBeginAttempt(key))
                {
                    blockedKeys.Add(key);
                    continue;
                }
                var member = target.Value.CrossFile != null
                    ? TryGenerateCrossBundle(compilation, target.Value)
                    : TryGenerate(compilation, trees, parseOptions, target.Value);
                if (member != null) generatedByKey[key] = member;
            }
            catch
            {
                // Best-effort: any failure inferring/generating THIS diagnostic's member just
                // leaves it for the pre-existing refuse path — never a reason to fail the run.
            }
        }

        // key -> every "ObjectDisplayName.MethodName" this run's compile identified as
        // depending on it — EVERY diagnostic naming the same missing member, not just whichever
        // one happened to trigger the actual generation. Resolved statically from each
        // diagnostic's own Location, never from what actually executed — see
        // TddGeneratedMember.DependentTests' doc comment for why that matters.
        var generated = new List<TddGeneratedMember>();
        TddCallGraph? callGraph = null;
        foreach (var (key, member) in generatedByKey)
        {
            var deps = new List<string>();
            try
            {
                // #5147: every [Test] that reaches a referencing method, directly or through
                // procedures it calls in this compile (helpers, library codeunits).
                callGraph ??= TddCallGraph.Build(compilation, originalTrees);
                foreach (var diag in diagsByKey[key])
                    foreach (var label in callGraph.TestsReaching(diag))
                        if (!deps.Contains(label)) deps.Add(label);
                // #5161: generated into another bundle, so a bundle compiled after this one reaches
                // the member through whichever procedures of this compile lead to a call site.
                // The same for a member generated into THIS compile when another bundle depends on it
                // (#5271): its tests are not in this compile, they call into it.
                if (member.GeneratedIntoFile != null || hasDependents)
                    TddCrossBundle.RecordReaching(diagsByKey[key].SelectMany(callGraph.ProcedureKeysReaching), member);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"--tdd: could not find the tests that reach {TddReport.Describe(member)} " +
                    $"({ex.GetType().Name}: {ex.Message.Split('\n', 2)[0]}); they carry no generatedStubs for it.");
            }
            var withDeps = member with { DependentTests = deps };
            if (withDeps.GeneratedIntoFile != null)
                TddCrossBundle.RecordGenerated(moduleName ?? "", withDeps);
            else if (hasDependents)
                TddCrossBundle.RecordGeneratedHere(withDeps);
            generated.Add(withDeps);
        }
        return generated;
    }

    /// <summary>
    /// Pure resolution step (no mutation): given an AL0132 diagnostic (or, #5228, an AL0126 one:
    /// the procedure exists, the call needs an overload), determines WHAT is
    /// missing (field / procedure / enum value), on WHICH object (by name — matched back to a
    /// live <see cref="NavSyntax.ObjectSyntax"/> at generation time, since a PRIOR call in the
    /// same <see cref="Generate"/> pass may already have mutated that object's tree), and the
    /// call-site node (<paramref name="diag"/>'s own tree — never mutated by generation, so
    /// it's safe to re-derive per diagnostic including repeat diagnostics for an
    /// already-generated member). Returns null for every refuse case from this file's header:
    /// unrecognized syntax shape, unresolvable qualifier, or a qualifier declared outside this
    /// compile's own trees that is neither a source bundle's nor a package's codeunit (a Target with Precompiled set).
    /// </summary>
    private static Target? ResolveTarget(
        NavCA.Compilation compilation, NavSyntax.SyntaxTree[] originalTrees, NavDiag.Diagnostic diag)
    {
        var tree = diag.Location.SourceTree!;
        var root = tree.GetRoot();
        var token = root.FindToken(diag.Location.SourceSpan.Start);
        var overload = diag.Id == NoOverloadForArgumentCountDiagnosticId;
        // AL0126 names the call, not the identifier: the invocation holding the diagnostic is the
        // call site, and its member name is the existing procedure.
        var overloadCall = overload
            ? token.Parent?.AncestorsAndSelf().OfType<NavSyntax.InvocationExpressionSyntax>()
                .FirstOrDefault(i => i.Expression is NavSyntax.MemberAccessExpressionSyntax)
            : null;
        if (overload && overloadCall == null) return null;
        var idNode = overload
            ? ((NavSyntax.MemberAccessExpressionSyntax)overloadCall!.Expression).Name as NavSyntax.IdentifierNameSyntax
            : token.Parent as NavSyntax.IdentifierNameSyntax;
        if (idNode == null) return null;
        var memberName = Unquote(idNode.Identifier.ValueText ?? idNode.Identifier.Text ?? "");
        if (memberName.Length == 0) return null;

        NavSyntax.CodeExpressionSyntax qualifierExpr;
        bool isEnumValueAccess;
        NavSyntax.MemberAccessExpressionSyntax? mae = null;
        if (!overload && idNode.Parent is NavSyntax.OptionAccessExpressionSyntax oae && SpanEq(oae.Name, idNode))
        {
            qualifierExpr = oae.Expression;
            isEnumValueAccess = true;
        }
        else if (idNode.Parent is NavSyntax.MemberAccessExpressionSyntax maeNode && SpanEq(maeNode.Name, idNode))
        {
            qualifierExpr = maeNode.Expression;
            mae = maeNode;
            isEnumValueAccess = false;
        }
        else
        {
            return null; // unrecognized shape — refuse
        }

        var qualModel = compilation.GetSemanticModel(qualifierExpr.SyntaxTree);
        var qualType = ResolveExpressionType(qualModel, qualifierExpr);
        if (qualType == null) return null; // qualifier itself didn't resolve to anything with a type — refuse

        var isInvocation = mae != null
            && mae.Parent is NavSyntax.InvocationExpressionSyntax invCheck
            && SpanEq(invCheck.Expression, mae);

        string kind;
        if (isEnumValueAccess)
        {
            if (qualType.NavTypeKind != NavCA.NavTypeKind.Enum) return null;
            kind = "enum-value";
        }
        else if (isInvocation)
        {
            if (qualType.NavTypeKind != NavCA.NavTypeKind.Codeunit) return null;
            kind = "procedure";
        }
        else
        {
            if (qualType.NavTypeKind != NavCA.NavTypeKind.Record) return null;
            kind = "field";
        }

        // An overload is told apart by what the call passes: two call sites with the same argument
        // count but different argument types are two overloads (#5228).
        string? overloadKey = null;
        if (overload)
        {
            overloadKey = string.Join(",", overloadCall!.ArgumentList.Arguments
                .Select(a => InferAlTypeText(compilation, a) ?? "?"));
        }

        // Precompiled-dependency / genuinely-unresolvable guard: only a symbol declared in ONE
        // OF THIS COMPILE'S OWN TREES can be generated into — see this file's header comment.
        var declLoc = qualType.Location;
        if (declLoc?.SourceTree == null)
        {
            // Declared outside this compile: generatable only when another SOURCE bundle of the
            // run declares it (#5037). A precompiled dependency matches no registered source.
            var cross = TddCrossBundle.FindObject(SyntaxTypeFor(kind), qualType.Name);
            if (cross == null)
            {
                // A codeunit of a loaded package (#5037): nothing to add the member to, so GenerateBesidePrecompiled,
                // which takes only AL0132 (a missing procedure), never AL0126 (a missing overload).
                return kind == "procedure" && IsPackagedObject(qualType) && mae != null
                    ? new Target(kind, -1, qualType.Name, memberName, mae, null, null, Precompiled: true)
                    : null;
            }
            return new Target(kind, -1, qualType.Name, memberName, mae, cross.Value.FilePath, overloadKey);
        }
        var targetTreeIdx = Array.IndexOf(originalTrees, declLoc.SourceTree);
        if (targetTreeIdx < 0) return null;

        return new Target(kind, targetTreeIdx, qualType.Name, memberName, mae, null, overloadKey);
    }

    /// <summary>What an AL0132 / AL0126 asks for. <see cref="OverloadKey"/> is set for an AL0126:
    /// the procedure exists and the call needs another overload of it.</summary>
    private readonly record struct Target(
        string Kind, int TargetTreeIdx, string TargetObjectName, string MemberName,
        NavSyntax.MemberAccessExpressionSyntax? Mae, string? CrossFile, string? OverloadKey = null,
        bool Precompiled = false)
    {
        public bool IsOverload => OverloadKey != null;
        public string MemberKey => OverloadKey == null ? MemberName : $"{MemberName}({OverloadKey})";
    }

    private static Type SyntaxTypeFor(string kind) => kind switch
    {
        "procedure" => typeof(NavSyntax.CodeunitSyntax),
        "field" => typeof(NavSyntax.TableSyntax),
        _ => typeof(NavSyntax.EnumTypeSyntax),
    };

    private static string CrossKey(string file, string kind, string member)
        => $"{Path.GetFullPath(file)}|{kind}|{member.ToLowerInvariant()}";

    /// <summary>
    /// #5037: generates into another source bundle's file, as overlay text. The current compile
    /// is not recompiled here — its references come from that bundle's symbols, so the caller
    /// re-runs the cycle to recompile it first. An enum type is accepted only when that same
    /// bundle declares it, because the generated-into bundle cannot reference the dependent's
    /// objects; anything else refuses.
    /// </summary>
    private static TddGeneratedMember? TryGenerateCrossBundle(
        NavCA.Compilation compilation,
        Target target)
    {
        var found = TddCrossBundle.FindObject(SyntaxTypeFor(target.Kind), target.TargetObjectName);
        if (found == null || !string.Equals(found.Value.FilePath, target.CrossFile, StringComparison.Ordinal))
            return null;
        var implDir = TddCrossBundle.SourceImpls()
            .Select(i => i.Dir)
            .FirstOrDefault(d => found.Value.FilePath.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (implDir == null) return null;
        bool TypeAllowed(NavCA.ITypeSymbol t)
        {
            if (t.NavTypeKind != NavCA.NavTypeKind.Enum) return true;
            var enumDecl = TddCrossBundle.FindObject(typeof(NavSyntax.EnumTypeSyntax), t.Name);
            return t.Location?.SourceTree == null && enumDecl != null
                && enumDecl.Value.FilePath.StartsWith(implDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        var parseOptions = BcCompiler.BuildParseOptions(BcCompiler.ReadManifestCompilerInputs(found.Value.AppJson));
        var root = (NavSyntax.CompilationUnitSyntax)found.Value.Tree.GetRoot();
        var objects = root.Objects;
        var objIdx = objects.IndexOf(o => ObjectNameOf(o).Equals(target.TargetObjectName, StringComparison.OrdinalIgnoreCase));
        if (objIdx < 0) return null;
        var targetObj = objects[objIdx];

        (NavSyntax.ObjectSyntax NewObj, TddGeneratedMember Member)? result = target.Kind switch
        {
            "field" => TryGenerateField(compilation, parseOptions, targetObj, target.MemberName, target.Mae!, TypeAllowed),
            "procedure" => TryGenerateProcedure(compilation, parseOptions, targetObj, target.MemberName, target.Mae!, target.IsOverload, TypeAllowed),
            "enum-value" => TryGenerateEnumValue(parseOptions, targetObj, target.MemberName),
            _ => null,
        };
        if (result == null) return null;

        var newRoot = root.WithObjects(objects.Replace(targetObj, result.Value.NewObj));
        TddSourceOverlay.Set(found.Value.FilePath, newRoot.ToFullString());
        return result.Value.Member with { GeneratedIntoFile = found.Value.FilePath };
    }

    private static TddGeneratedMember? TryGenerate(
        NavCA.Compilation compilation, NavSyntax.SyntaxTree[] trees, NavCA.ParseOptions parseOptions,
        Target target)
    {
        var currentRoot = (NavSyntax.CompilationUnitSyntax)trees[target.TargetTreeIdx].GetRoot();
        var objects = currentRoot.Objects;
        var objIdx = objects.IndexOf(o => Unquote(IdentTextOf(o.Name)) == target.TargetObjectName);
        if (objIdx < 0) return null;
        var targetObj = objects[objIdx];

        (NavSyntax.ObjectSyntax NewObj, TddGeneratedMember Member)? result = target.Kind switch
        {
            "field" => TryGenerateField(compilation, parseOptions, targetObj, target.MemberName, target.Mae!),
            "procedure" => TryGenerateProcedure(compilation, parseOptions, targetObj, target.MemberName, target.Mae!, target.IsOverload),
            "enum-value" => TryGenerateEnumValue(parseOptions, targetObj, target.MemberName),
            _ => null,
        };
        if (result == null) return null;

        var newObjects = objects.Replace(targetObj, result.Value.NewObj);
        var newRoot = currentRoot.WithObjects(newObjects);
        trees[target.TargetTreeIdx] = trees[target.TargetTreeIdx].WithRootAndOptions(newRoot, trees[target.TargetTreeIdx].Options);
        return result.Value.Member;
    }

    private static (NavSyntax.ObjectSyntax, TddGeneratedMember)? TryGenerateField(
        NavCA.Compilation compilation, NavCA.ParseOptions parseOptions,
        NavSyntax.ObjectSyntax targetObj, string fieldName, NavSyntax.MemberAccessExpressionSyntax mae,
        Func<NavCA.ITypeSymbol, bool>? typeAllowed = null)
    {
        if (targetObj is not NavSyntax.TableSyntax tableSyntax) return null;
        // Only infer from "Rec.\"Field\" := <expr>;" — the assignment's RHS is the one place a
        // field's type is unambiguously anchored by this diagnostic's own call site (see this
        // file's header: everything else falls through to the refuse path on purpose).
        if (mae.Parent is not NavSyntax.AssignmentStatementSyntax asn || !SpanEq(asn.Target, mae)) return null;
        var typeText = InferAlTypeText(compilation, asn.Source, typeAllowed);
        if (typeText == null) return null;

        var nextId = 1;
        foreach (var f in tableSyntax.Fields?.Fields ?? default)
            if (int.TryParse(f.No.Text, out var n) && n >= nextId) nextId = n + 1;

        var quotedField = Quote(fieldName);
        var snippet = $"table 1 \"__TddGen__\" {{ fields {{ field({nextId}; {quotedField}; {typeText}) {{ }} }} }}";
        var synthTree = NavSyntax.SyntaxTree.ParseObjectText(snippet, path: "<tdd-generated>", encoding: null!, parseOptions, default);
        var synthTable = (NavSyntax.TableSyntax)synthTree.GetRoot().ChildNodes().OfType<NavSyntax.ObjectSyntax>().First();
        var newField = synthTable.Fields.Fields.First();

        var newTable = tableSyntax.AddFieldsFields(newField);

        // The runtime record engine's table metadata comes from a SEPARATE parse of the
        // on-disk file (RecordPatches.AddSourceDirs, run at the bundle level BEFORE
        // BcCompiler.Emit even starts) — this SyntaxTree mutation alone is invisible to it.
        // Without this call the generated field compiles fine but throws
        // NavNCLFieldNotFoundException the first time a test touches it, which would name a
        // field NUMBER, not the field NAME the issue's own acceptance criteria require. See
        // RecordPatches.TddReparse.cs's header for the full why.
        if (int.TryParse(tableSyntax.ObjectId.Value.Text, out var tableId))
            AlRunner.Patches.RecordPatches.TddReparseAndRefreshTable(tableId, newTable.ToFullString());

        return (newTable, new TddGeneratedMember(qualTypeNameOf(targetObj), "field", $"{quotedField}: {typeText}"));
    }

    private static (NavSyntax.ObjectSyntax, TddGeneratedMember)? TryGenerateProcedure(
        NavCA.Compilation compilation, NavCA.ParseOptions parseOptions,
        NavSyntax.ObjectSyntax targetObj, string procName, NavSyntax.MemberAccessExpressionSyntax mae,
        bool overload, Func<NavCA.ITypeSymbol, bool>? typeAllowed = null)
    {
        if (targetObj is not NavSyntax.CodeunitSyntax codeunitSyntax) return null;
        if (mae.Parent is not NavSyntax.InvocationExpressionSyntax inv) return null;
        // #5228: an overload is generated only next to a procedure of that name this codeunit
        // declares. AL0126 also names a call to a built-in method (Codeunit.Run) with the wrong
        // argument count, and nothing here may add a member with a built-in's name.
        if (overload && !codeunitSyntax.Members.OfType<NavSyntax.MethodDeclarationSyntax>()
                .Any(m => Unquote(IdentTextOf(m.Name)).Equals(procName, StringComparison.OrdinalIgnoreCase)))
            return null;

        var shape = InferProcedureShape(compilation, inv, typeAllowed);
        if (shape == null) return null; // an un-inferable argument or return type refuses the WHOLE procedure
        var (paramTypes, returnType) = shape.Value;

        var quotedName = Quote(procName);
        var paramList = ParamListText(paramTypes);
        // #5147: an empty body, so the call returns the type's default and the test's own
        // assertion decides its result — the red of the red-green loop.
        var snippet =
            $"codeunit 1 \"__TddGen__\" {{ procedure {quotedName}({paramList}): {returnType} " +
            "begin end; }";
        var synthTree = NavSyntax.SyntaxTree.ParseObjectText(snippet, path: "<tdd-generated>", encoding: null!, parseOptions, default);
        var synthCodeunit = (NavSyntax.CodeunitSyntax)synthTree.GetRoot().ChildNodes().OfType<NavSyntax.ObjectSyntax>().First();
        var newMethod = synthCodeunit.Members.OfType<NavSyntax.MethodDeclarationSyntax>().First();

        var newCodeunit = codeunitSyntax.AddMembers(newMethod);
        var sig = $"{quotedName}({paramList}): {returnType}";
        return (newCodeunit, new TddGeneratedMember(qualTypeNameOf(targetObj), "procedure", sig));
    }

    private static (NavSyntax.ObjectSyntax, TddGeneratedMember)? TryGenerateEnumValue(
        NavCA.ParseOptions parseOptions, NavSyntax.ObjectSyntax targetObj, string valueName)
    {
        if (targetObj is not NavSyntax.EnumTypeSyntax enumSyntax) return null;

        var nextOrdinal = 0;
        foreach (var v in enumSyntax.Values)
            if (int.TryParse(v.Id.Text, out var n) && n >= nextOrdinal) nextOrdinal = n + 1;

        var quotedName = Quote(valueName);
        var snippet = $"enum 1 \"__TddGen__\" {{ value({nextOrdinal}; {quotedName}) {{ }} }}";
        var synthTree = NavSyntax.SyntaxTree.ParseObjectText(snippet, path: "<tdd-generated>", encoding: null!, parseOptions, default);
        var synthEnum = (NavSyntax.EnumTypeSyntax)synthTree.GetRoot().ChildNodes().OfType<NavSyntax.ObjectSyntax>().First();
        var newValue = synthEnum.Values.First();

        var newEnum = enumSyntax.AddValues(newValue);
        return (newEnum, new TddGeneratedMember(qualTypeNameOf(targetObj), "enum value", $"{quotedName} = {nextOrdinal}"));
    }

    /// <summary>
    /// The type of an expression this file needs to embed directly into generated AL text —
    /// either a literal's own type, or (for anything else) the resolved symbol's declared type.
    /// Returns null — REFUSE, never guess — for anything length-bearing (Text/Code, whose
    /// length this call site cannot possibly anchor) or otherwise not in
    /// <see cref="SimpleBuiltinTypes"/>/Enum.
    /// </summary>
    private static string? InferAlTypeText(NavCA.Compilation compilation, NavSyntax.CodeExpressionSyntax expr,
        Func<NavCA.ITypeSymbol, bool>? typeAllowed = null)
    {
        if (expr is NavSyntax.LiteralExpressionSyntax lit)
        {
            return lit.Literal.Kind switch
            {
                NavCA.SyntaxKind.Int32SignedLiteralValue or NavCA.SyntaxKind.Int64SignedLiteralValue => "Integer",
                NavCA.SyntaxKind.DecimalSignedLiteralValue => "Decimal",
                NavCA.SyntaxKind.BooleanLiteralValue => "Boolean",
                NavCA.SyntaxKind.DateLiteralValue => "Date",
                NavCA.SyntaxKind.TimeLiteralValue => "Time",
                NavCA.SyntaxKind.DateTimeLiteralValue => "DateTime",
                _ => null, // includes StringLiteralValue — Text/Code need a length we can't guess
            };
        }

        var model = compilation.GetSemanticModel(expr.SyntaxTree);
        var type = ResolveExpressionType(model, expr);
        if (type != null && typeAllowed != null && !typeAllowed(type)) return null;
        return TypeSymbolToAlText(type);
    }

    private static NavCA.ITypeSymbol? ResolveExpressionType(NavCA.SemanticModel model, NavCA.SyntaxNode expr)
    {
        var symbol = model.GetSymbolInfo(expr).Symbol;
        return symbol switch
        {
            NavCA.IFieldSymbol f => f.Type,
            NavCA.IVariableSymbol v => v.Type,
            NavCA.IParameterSymbol p => p.ParameterType,
            // `"Loyalty Tier"::Gold` (#5038). An Option member (IOptionSymbol) is deliberately
            // not here: an Option parameter needs a member list one call site does not fix.
            // A value an enumextension adds is contained by the extension, whose Target is the
            // base enum — the type a parameter is declared with (#5044).
            NavCA.IEnumValueSymbol ev => ev.ContainingType is NavCA.IApplicationObjectExtensionTypeSymbol ext
                ? ext.Target as NavCA.ITypeSymbol
                : ev.ContainingType,
            NavCA.ITypeSymbol t => t,
            _ => null,
        };
    }

    private static string? TypeSymbolToAlText(NavCA.ITypeSymbol? type)
    {
        if (type == null) return null;
        if (SimpleBuiltinTypes.Contains(type.NavTypeKind)) return type.NavTypeKind.ToString();
        if (type.NavTypeKind == NavCA.NavTypeKind.Enum)
        {
            // Qualified, because the generated member lands in an object that need not import
            // the enum's namespace.
            var ns = type.ContainingNamespace;
            return ns == null || ns.IsGlobalNamespace || string.IsNullOrEmpty(ns.QualifiedName)
                ? $"Enum {Quote(type.Name)}"
                : $"Enum {ns.QualifiedName}.{Quote(type.Name)}";
        }
        return null; // Text/Code (length), Record/Codeunit/Page/... (too broad) — refuse
    }

    /// <summary>
    /// Return type for a generated procedure, from the invocation's OWN call-site context —
    /// never a default. A bare-statement invocation (<c>Cu.DoThing();</c>) is the issue's own
    /// explicit refuse example: there is no way to tell a void procedure from a discarded
    /// return value from that shape alone, so it refuses rather than guessing "void".
    /// </summary>
    private static string? InferReturnTypeText(NavCA.Compilation compilation, NavSyntax.InvocationExpressionSyntax inv,
        Func<NavCA.ITypeSymbol, bool>? typeAllowed = null)
    {
        var parent = inv.Parent;
        if (parent is NavSyntax.ExpressionStatementSyntax) return null;

        if (parent is NavSyntax.AssignmentStatementSyntax asn && SpanEq(asn.Source, inv))
            return InferAlTypeText(compilation, asn.Target, typeAllowed);

        if (parent is NavSyntax.IfStatementSyntax ifs && SpanEq(ifs.Condition, inv))
            return "Boolean";

        if (parent is NavSyntax.ArgumentListSyntax argList && argList.Parent is NavSyntax.InvocationExpressionSyntax outerInv)
        {
            var ordinal = argList.Arguments.IndexOf(a => SpanEq(a, inv));
            if (ordinal < 0) return null;

            var model = compilation.GetSemanticModel(outerInv.SyntaxTree);
            var symInfo1 = model.GetSymbolInfo(outerInv);
            var symInfo2 = model.GetSymbolInfo(outerInv.Expression);
            // The outer call's OWN overload resolution can fail to commit to a definite Symbol
            // precisely BECAUSE one of its other arguments is this same missing member — BC
            // cannot fully bind `Assert.AreEqual(100, Cu.CalcTotal())` while `CalcTotal` is
            // still unresolved, so it reports the (correct, only) match as a CANDIDATE instead.
            // Falling back to a SINGLE candidate is safe: more than one candidate means real
            // overload ambiguity, which correctly still refuses below.
            var outerSymbol =
                symInfo1.Symbol as NavCA.IMethodSymbol
                ?? symInfo2.Symbol as NavCA.IMethodSymbol
                ?? SingleCandidate(symInfo1) as NavCA.IMethodSymbol
                ?? SingleCandidate(symInfo2) as NavCA.IMethodSymbol;
            if (outerSymbol == null || ordinal >= outerSymbol.Parameters.Length) return null;
            var paramType = outerSymbol.Parameters[ordinal].ParameterType;
            // #5146: a Variant parameter (Library Assert's AreEqual) fixes no type, so the other
            // Variant arguments of the same call do.
            if (paramType?.NavTypeKind == NavCA.NavTypeKind.Variant)
                return InferFromVariantSiblings(compilation, argList, outerSymbol, ordinal, typeAllowed);
            if (paramType != null && typeAllowed != null && !typeAllowed(paramType)) return null;
            return TypeSymbolToAlText(paramType);
        }

        return null;
    }

    /// <summary>
    /// The type of a missing call passed where the outer procedure takes a Variant: the type of
    /// the OTHER arguments bound to Variant parameters, <c>Assert.AreEqual(25, Cu.CalcPoints(250),
    /// '...')</c> giving Integer. Refuses (null) when any of them fixes no type, or when they
    /// do not agree — a Text literal needs a length, a sibling that is itself missing has no type.
    /// </summary>
    private static string? InferFromVariantSiblings(NavCA.Compilation compilation,
        NavSyntax.ArgumentListSyntax argList, NavCA.IMethodSymbol outer, int ordinal,
        Func<NavCA.ITypeSymbol, bool>? typeAllowed)
    {
        string? inferred = null;
        for (var i = 0; i < argList.Arguments.Count && i < outer.Parameters.Length; i++)
        {
            if (i == ordinal || outer.Parameters[i].ParameterType?.NavTypeKind != NavCA.NavTypeKind.Variant) continue;
            var t = InferAlTypeText(compilation, argList.Arguments[i], typeAllowed);
            if (t == null || (inferred != null && inferred != t)) return null;
            inferred = t;
        }
        return inferred;
    }

    private static NavCA.ISymbol? SingleCandidate(NavCA.SymbolInfo info)
        => info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null;

    private static bool SpanEq(NavCA.SyntaxNode? a, NavCA.SyntaxNode? b)
        => a != null && b != null && a.Span.Equals(b.Span);

    private static string Unquote(string s)
        => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    private static string Quote(string s) => $"\"{s.Replace("\"", "\"\"")}\"";

    private static string IdentTextOf(NavSyntax.IdentifierNameSyntax? id)
        => id == null ? "" : (id.Identifier.ValueText ?? id.Identifier.Text ?? "");

    private static string qualTypeNameOf(NavSyntax.ObjectSyntax obj) => Unquote(IdentTextOf(obj.Name));

    internal static string ObjectNameOf(NavSyntax.ObjectSyntax obj) => qualTypeNameOf(obj);
}
