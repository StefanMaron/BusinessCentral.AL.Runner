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
// Scope, and why nothing here needs a separate "revert a bad guess" step: this only ever
// generates into a tree BcCompiler.Emit ALREADY has in memory as one of its own `trees[]` —
// i.e. a SOURCE-COMPILED object belonging to the app being compiled. A symbol whose declaring
// object has no such tree (a precompiled dependency's type, or a symbol BC couldn't resolve at
// all) has no reachable Location.SourceTree, so it is refused for a structural reason, not a
// policy one — this is what keeps precompiled .app dependencies out of scope (#1997/#2001 say
// so explicitly) without any special-casing. And because generation runs strictly BEFORE the
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
public sealed record TddGeneratedMember(string ObjectDisplayName, string MemberKind, string Signature);

public static class TddGeneration
{
    private const string MemberNotFoundDiagnosticId = "AL0132";

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
        NavEmit.EmitResult emitResult)
    {
        var generated = new List<TddGeneratedMember>();
        // Snapshot BEFORE any mutation: once a tree in `trees` is replaced (a second missing
        // member found on an object already patched earlier in this same pass), the ORIGINAL
        // SyntaxTree instance a symbol's Location still points at is no longer present in
        // `trees` — so tree-identity lookups always go through this frozen snapshot, and only
        // the mutation itself touches `trees[idx]`.
        var originalTrees = (NavSyntax.SyntaxTree[])trees.Clone();
        // De-dup: the SAME missing member can be named by more than one AL0132 diagnostic
        // (two sibling [Test] procedures referencing the same not-yet-declared field, the
        // enum-value case's own two-diagnostic shape covered by ProcessedTargets below) —
        // generate it once.
        var processed = new HashSet<(int TreeIndex, string Kind, string Name)>();

        foreach (var diag in emitResult.Diagnostics)
        {
            if (diag.Severity != NavDiag.DiagnosticSeverity.Error) continue;
            if (diag.Id != MemberNotFoundDiagnosticId) continue;
            if (!diag.Location.IsInSource || diag.Location.SourceTree == null) continue;

            try
            {
                TryGenerateOne(compilation, trees, originalTrees, parseOptions, diag, processed, generated);
            }
            catch
            {
                // Best-effort: any failure inferring/generating THIS diagnostic's member just
                // leaves it for the pre-existing refuse path — never a reason to fail the run.
            }
        }
        return generated;
    }

    private static void TryGenerateOne(
        NavCA.Compilation compilation,
        NavSyntax.SyntaxTree[] trees,
        NavSyntax.SyntaxTree[] originalTrees,
        NavCA.ParseOptions parseOptions,
        NavDiag.Diagnostic diag,
        HashSet<(int, string, string)> processed,
        List<TddGeneratedMember> generated)
    {
        var tree = diag.Location.SourceTree!;
        var root = tree.GetRoot();
        var token = root.FindToken(diag.Location.SourceSpan.Start);
        if (token.Parent is not NavSyntax.IdentifierNameSyntax idNode) return;
        var memberName = Unquote(idNode.Identifier.ValueText ?? idNode.Identifier.Text ?? "");
        if (memberName.Length == 0) return;

        NavSyntax.CodeExpressionSyntax qualifierExpr;
        bool isEnumValueAccess;
        NavSyntax.MemberAccessExpressionSyntax? mae = null;
        if (idNode.Parent is NavSyntax.OptionAccessExpressionSyntax oae && SpanEq(oae.Name, idNode))
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
            return; // unrecognized shape — refuse
        }

        var qualModel = compilation.GetSemanticModel(qualifierExpr.SyntaxTree);
        var qualType = ResolveExpressionType(qualModel, qualifierExpr);
        if (qualType == null) return; // qualifier itself didn't resolve to anything with a type — refuse

        var isInvocation = mae != null
            && mae.Parent is NavSyntax.InvocationExpressionSyntax invCheck
            && SpanEq(invCheck.Expression, mae);

        string kind;
        if (isEnumValueAccess)
        {
            if (qualType.NavTypeKind != NavCA.NavTypeKind.Enum) return;
            kind = "enum-value";
        }
        else if (isInvocation)
        {
            if (qualType.NavTypeKind != NavCA.NavTypeKind.Codeunit) return;
            kind = "procedure";
        }
        else
        {
            if (qualType.NavTypeKind != NavCA.NavTypeKind.Record) return;
            kind = "field";
        }

        // Precompiled-dependency / genuinely-unresolvable guard: only a symbol declared in ONE
        // OF THIS COMPILE'S OWN TREES can be generated into — see this file's header comment.
        var declLoc = qualType.Location;
        if (declLoc?.SourceTree == null) return;
        var targetTreeIdx = Array.IndexOf(originalTrees, declLoc.SourceTree);
        if (targetTreeIdx < 0) return;

        var key = (targetTreeIdx, kind, memberName);
        if (!processed.Add(key)) return; // already generated (or already attempted) this exact member

        var currentRoot = (NavSyntax.CompilationUnitSyntax)trees[targetTreeIdx].GetRoot();
        var objects = currentRoot.Objects;
        var objIdx = objects.IndexOf(o => Unquote(IdentTextOf(o.Name)) == qualType.Name);
        if (objIdx < 0) return;
        var targetObj = objects[objIdx];

        (NavSyntax.ObjectSyntax NewObj, TddGeneratedMember Member)? result = kind switch
        {
            "field" => TryGenerateField(compilation, parseOptions, targetObj, memberName, mae!),
            "procedure" => TryGenerateProcedure(compilation, parseOptions, targetObj, memberName, mae!),
            "enum-value" => TryGenerateEnumValue(parseOptions, targetObj, memberName),
            _ => null,
        };
        if (result == null) return;

        var newObjects = objects.Replace(targetObj, result.Value.NewObj);
        var newRoot = currentRoot.WithObjects(newObjects);
        trees[targetTreeIdx] = trees[targetTreeIdx].WithRootAndOptions(newRoot, trees[targetTreeIdx].Options);
        generated.Add(result.Value.Member);
    }

    private static (NavSyntax.ObjectSyntax, TddGeneratedMember)? TryGenerateField(
        NavCA.Compilation compilation, NavCA.ParseOptions parseOptions,
        NavSyntax.ObjectSyntax targetObj, string fieldName, NavSyntax.MemberAccessExpressionSyntax mae)
    {
        if (targetObj is not NavSyntax.TableSyntax tableSyntax) return null;
        // Only infer from "Rec.\"Field\" := <expr>;" — the assignment's RHS is the one place a
        // field's type is unambiguously anchored by this diagnostic's own call site (see this
        // file's header: everything else falls through to the refuse path on purpose).
        if (mae.Parent is not NavSyntax.AssignmentStatementSyntax asn || !SpanEq(asn.Target, mae)) return null;
        var typeText = InferAlTypeText(compilation, asn.Source);
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
        NavSyntax.ObjectSyntax targetObj, string procName, NavSyntax.MemberAccessExpressionSyntax mae)
    {
        if (targetObj is not NavSyntax.CodeunitSyntax codeunitSyntax) return null;
        if (mae.Parent is not NavSyntax.InvocationExpressionSyntax inv) return null;

        var paramTypes = new List<string>();
        foreach (var arg in inv.ArgumentList.Arguments)
        {
            var t = InferAlTypeText(compilation, arg);
            if (t == null) return null; // any un-inferable argument refuses the WHOLE procedure
            paramTypes.Add(t);
        }

        var returnType = InferReturnTypeText(compilation, inv);
        if (returnType == null) return null; // includes the "bare statement" refuse case

        var quotedName = Quote(procName);
        var paramList = string.Join("; ", paramTypes.Select((t, i) => $"Arg{i + 1}: {t}"));
        var errMsg = $"--tdd: {procName} is a generated stub -- the implementing app has not defined it yet.";
        var snippet =
            $"codeunit 1 \"__TddGen__\" {{ procedure {quotedName}({paramList}): {returnType} " +
            $"begin Error('{errMsg.Replace("'", "''")}'); end; }}";
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
    private static string? InferAlTypeText(NavCA.Compilation compilation, NavSyntax.CodeExpressionSyntax expr)
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
            NavCA.ITypeSymbol t => t,
            _ => null,
        };
    }

    private static string? TypeSymbolToAlText(NavCA.ITypeSymbol? type)
    {
        if (type == null) return null;
        if (SimpleBuiltinTypes.Contains(type.NavTypeKind)) return type.NavTypeKind.ToString();
        if (type.NavTypeKind == NavCA.NavTypeKind.Enum) return $"Enum {Quote(type.Name)}";
        return null; // Text/Code (length), Record/Codeunit/Page/... (too broad) — refuse
    }

    /// <summary>
    /// Return type for a generated procedure, from the invocation's OWN call-site context —
    /// never a default. A bare-statement invocation (<c>Cu.DoThing();</c>) is the issue's own
    /// explicit refuse example: there is no way to tell a void procedure from a discarded
    /// return value from that shape alone, so it refuses rather than guessing "void".
    /// </summary>
    private static string? InferReturnTypeText(NavCA.Compilation compilation, NavSyntax.InvocationExpressionSyntax inv)
    {
        var parent = inv.Parent;
        if (parent is NavSyntax.ExpressionStatementSyntax) return null;

        if (parent is NavSyntax.AssignmentStatementSyntax asn && SpanEq(asn.Source, inv))
            return InferAlTypeText(compilation, asn.Target);

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
            return TypeSymbolToAlText(outerSymbol.Parameters[ordinal].ParameterType);
        }

        return null;
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
}
