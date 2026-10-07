// The precompiled half of --tdd generation (#5037): see TddPrecompiledStub.cs for the design.
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using NavDiag = Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using NavEmit = Microsoft.Dynamics.Nav.CodeAnalysis.Emit;

namespace AlRunner;

public static partial class TddGeneration
{
    /// <summary>A codeunit the compile reads from a loaded package: an object with a name that no tree of the
    /// compile declares. The caller has already established that no source bundle of the run declares it.</summary>
    private static bool IsPackagedObject(NavCA.ITypeSymbol type)
        => type.NavTypeKind == NavCA.NavTypeKind.Codeunit && !string.IsNullOrEmpty(type.Name);

    private static (List<string> ParamTypes, string ReturnType)? InferProcedureShape(
        NavCA.Compilation compilation, NavSyntax.InvocationExpressionSyntax inv,
        Func<NavCA.ITypeSymbol, bool>? typeAllowed)
    {
        var paramTypes = new List<string>();
        foreach (var arg in inv.ArgumentList.Arguments)
        {
            var t = InferAlTypeText(compilation, arg, typeAllowed);
            if (t == null) return null;
            paramTypes.Add(t);
        }
        var returnType = InferReturnTypeText(compilation, inv, typeAllowed); // null includes the bare-statement case
        return returnType == null ? null : (paramTypes, returnType);
    }

    private static string ParamListText(IEnumerable<string> paramTypes)
        => string.Join("; ", paramTypes.Select((t, i) => $"Arg{i + 1}: {t}"));

    /// <summary>
    /// A procedure the tests call on a precompiled codeunit and it does not declare: generated in a stub
    /// codeunit of THIS compile (one per precompiled object), every call naming it pointed at the stub.
    /// A member is generated only when every call site of it can be pointed there, so no site is left
    /// failing; members fill <paramref name="generatedByKey"/> and <paramref name="diagsByKey"/> the way the
    /// in-compile ones do, so the tests reaching them are annotated by the same path.
    /// </summary>
    private static void GenerateBesidePrecompiled(
        NavCA.Compilation compilation, NavSyntax.SyntaxTree[] originalTrees, NavSyntax.SyntaxTree[] trees,
        NavCA.ParseOptions parseOptions, NavEmit.EmitResult emitResult, string? manifestAppJsonPath,
        Dictionary<string, TddGeneratedMember> generatedByKey, Dictionary<string, List<NavDiag.Diagnostic>> diagsByKey)
    {
        var byKey = new Dictionary<string, List<(NavDiag.Diagnostic Diag, Target Target)>>(StringComparer.Ordinal);
        // Test seam: AL_RUNNER_TDD_DIAG_ORDER=reverse feeds the diagnostics in the opposite order, so a test can
        // show that nothing below depends on the order the emit happens to report them in (it varies between runs).
        IEnumerable<NavDiag.Diagnostic> diagnostics = emitResult.Diagnostics;
        if (Environment.GetEnvironmentVariable("AL_RUNNER_TDD_DIAG_ORDER") == "reverse")
            diagnostics = diagnostics.Reverse();
        foreach (var diag in diagnostics)
        {
            if (diag.Severity != NavDiag.DiagnosticSeverity.Error || diag.Id != MemberNotFoundDiagnosticId) continue;
            if (!diag.Location.IsInSource || diag.Location.SourceTree == null) continue;
            try
            {
                var target = ResolveTarget(compilation, originalTrees, diag);
                if (target is not { Precompiled: true } t) continue;
                var key = $"beside|{t.TargetObjectName.ToLowerInvariant()}|{t.MemberName.ToLowerInvariant()}";
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new();
                list.Add((diag, t));
            }
            catch (Exception ex)
            {
                // Refused, and said why: the diagnostic stays for the refuse path, which names only the missing symbol.
                Console.Error.WriteLine($"--tdd: not generated beside a precompiled object ({diag.Location.GetLineSpan()}): {ex.GetType().Name}: {FirstLine(ex.Message)}");
            }
        }
        if (byKey.Count == 0) return;

        var idRanges = TddPrecompiledStub.ReadIdRanges(manifestAppJsonPath);
        var reserved = new HashSet<int>();
        var groups = new Dictionary<string, TddPrecompiledStub.Group>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(string Key, TddGeneratedMember Member, List<NavDiag.Diagnostic> Diags)>();
        // Members in key order, and each member's sites in file then position order: the emit reports
        // diagnostics in an order that changes between runs, and neither the shape a member is inferred
        // from, nor the stub ids, nor the order of the generated list may follow it.
        foreach (var (key, unordered) in byKey.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var entries = unordered
                .OrderBy(e => Array.IndexOf(originalTrees, e.Diag.Location.SourceTree))
                .ThenBy(e => e.Diag.Location.SourceSpan.Start)
                .ToList();
            var obj = entries[0].Target.TargetObjectName;
            var member = entries[0].Target.MemberName;
            void Refuse(string reason) =>
                Console.Error.WriteLine($"--tdd: not generated beside precompiled {obj}: \"{member}\" - {reason}");
            try
            {
                var first = entries[0].Target;
                var sites = new List<TddPrecompiledStub.Site>();
                foreach (var (diag, target) in entries)
                    if (TddPrecompiledStub.SiteOf(originalTrees, diag.Location.SourceTree, target.Mae) is { } site) sites.Add(site);
                if (sites.Count != entries.Count)
                {
                    Refuse("a call to it is not through a plain variable inside a codeunit, so it cannot be pointed at a stub");
                    continue; // a call that cannot be pointed at a stub would still fail
                }
                // The first call, in that order, whose shape the call site fixes.
                (List<string> ParamTypes, string ReturnType)? shape = null;
                foreach (var (_, target) in entries)
                    if (target.Mae!.Parent is NavSyntax.InvocationExpressionSyntax inv
                        && InferProcedureShape(compilation, inv, null) is { } inferred)
                    {
                        shape = inferred;
                        break;
                    }
                if (shape == null)
                {
                    Refuse("no call to it fixes its parameter and return types (a bare statement, a Text argument, ...)");
                    continue;
                }

                var quotedName = Quote(first.MemberName);
                var sig = $"{quotedName}({ParamListText(shape.Value.ParamTypes)}): {shape.Value.ReturnType}";
                // Ids first, so a member that cannot get all of them changes nothing.
                var perTree = sites.GroupBy(x => x.TreeIdx).ToList();
                var taken = new HashSet<int>(reserved);
                var fresh = new List<TddPrecompiledStub.Group>();
                var idsFound = true;
                foreach (var treeSites in perTree)
                {
                    if (groups.ContainsKey($"{first.TargetObjectName}|{treeSites.Key}")) continue;
                    var id = TddPrecompiledStub.FreeCodeunitId(trees, idRanges, taken);
                    if (id == null) { idsFound = false; break; }
                    taken.Add(id.Value);
                    fresh.Add(new TddPrecompiledStub.Group { StubId = id.Value, TreeIdx = treeSites.Key });
                }
                if (!idsFound)
                {
                    Refuse("no free codeunit id in the app's idRanges for the generated stub");
                    continue;
                }
                reserved.UnionWith(taken);
                foreach (var g in fresh) groups[$"{first.TargetObjectName}|{g.TreeIdx}"] = g;
                foreach (var treeSites in perTree)
                {
                    var group = groups[$"{first.TargetObjectName}|{treeSites.Key}"];
                    group.Procedures.Add($"procedure {sig}\n    begin\n    end;");
                    group.Sites.AddRange(treeSites);
                }
                pending.Add((key, new TddGeneratedMember(first.TargetObjectName, "procedure", sig),
                    entries.Select(e => e.Diag).ToList()));
            }
            catch (Exception ex)
            {
                Refuse($"{ex.GetType().Name}: {FirstLine(ex.Message)}");
            }
        }
        if (groups.Count == 0) return;
        TddPrecompiledStub.Apply(trees, parseOptions, groups.Values.ToList());
        foreach (var (key, member, diags) in pending)
        {
            generatedByKey[key] = member;
            diagsByKey[key] = diags;
        }
    }

    private static string FirstLine(string message) => message.Split('\n', 2)[0];
}
