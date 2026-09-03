// Per-member facts from BC's AL syntax tree (#2056): loops (AlLoopSite) for
// iterationTracking and statement write sets (AlStatementWrites) for captureValues.
// Parsed once per execute request with the bundle's preprocessor symbols, independent
// of the compile cache. Lookup is by (file, member name); same-named triggers are told
// apart by statement position.
using Microsoft.Dynamics.Nav.CodeAnalysis;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Infrastructure;

/// <summary>One trigger/procedure: its loops and assigning statements. Both empty for a member
/// without any. <c>QualifiedName</c> is BC's scope name for a trigger owned by a field, action,
/// control or data item ("Number - OnValidate"); for a procedure it equals <c>Name</c>.</summary>
public sealed record AlMemberSyntax(
    string FilePath,
    string Name,
    string QualifiedName,
    AlTextRange BodyRange,
    IReadOnlyList<AlLoopSite> Sites,
    IReadOnlyList<AlStatementWrites> Writes);

public sealed class AlMemberSyntaxIndex
{
    // Built-ins whose first argument is by reference.
    private static readonly HashSet<string> ByRefFirstArgumentBuiltins =
        new(StringComparer.OrdinalIgnoreCase) { "Clear", "Evaluate" };

    private readonly Dictionary<string, List<AlMemberSyntax>> _byFile = new(StringComparer.OrdinalIgnoreCase);

    private AlMemberSyntaxIndex() { }

    /// <summary>Indexes every *.al file under the roots, with each root's app.json preprocessor symbols.</summary>
    public static AlMemberSyntaxIndex Build(IEnumerable<string> roots)
    {
        var members = new List<AlMemberSyntax>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            var appJson = Path.Combine(root, "app.json");
            var symbols = PreprocessorSymbols(File.Exists(appJson) ? appJson : null);
            foreach (var file in SafeDirectoryScan.Files(root, "*.al"))
            {
                string source;
                try { source = File.ReadAllText(file); }
                catch (IOException) { continue; }
                members.AddRange(Parse(source, file, symbols));
            }
        }
        return FromMembers(members);
    }

    public static AlMemberSyntaxIndex FromMembers(IEnumerable<AlMemberSyntax> members)
    {
        var index = new AlMemberSyntaxIndex();
        foreach (var m in members)
        {
            if (!index._byFile.TryGetValue(m.FilePath, out var list))
                index._byFile[m.FilePath] = list = new List<AlMemberSyntax>();
            list.Add(m);
        }
        return index;
    }

    /// <summary>
    /// The member BC calls <paramref name="scopeName"/> in the file: by qualified name
    /// ("Number - OnValidate"), else by plain name; among several candidates, the one whose
    /// body contains the position. Null rather than a guess.
    /// </summary>
    public AlMemberSyntax? FindMember(string filePath, string scopeName, AlTextPosition? statementStart)
    {
        if (!_byFile.TryGetValue(NormalizePath(filePath), out var members)) return null;
        var candidates = members.Where(m => string.Equals(m.QualifiedName, scopeName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
            candidates = members.Where(m => string.Equals(m.Name, scopeName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) return null;
        if (statementStart is { } p)
            foreach (var m in candidates)
                if (m.BodyRange.ContainsStart(p)) return m;
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>The loops of the member FindMember resolves, or null.</summary>
    public IReadOnlyList<AlLoopSite>? FindSites(string filePath, string memberName, AlTextPosition? statementStart) =>
        FindMember(filePath, memberName, statementStart)?.Sites;

    /// <summary>Parses one file. Symbols default to the compiler's baseline set.</summary>
    public static IReadOnlyList<AlMemberSyntax> Parse(string source, string filePath, IEnumerable<string>? preprocessorSymbols = null)
    {
        var parseOpts = new NavCA.ParseOptions(
            runtimeVersion: null!,
            preprocessorSymbols: preprocessorSymbols ?? PreprocessorSymbols(appJsonPath: null),
            documentationMode: NavCA.DocumentationMode.None);
        var tree = NavSyntax.SyntaxTree.ParseObjectText(source, path: filePath, encoding: null!, parseOpts, default);
        var root = tree.GetCompilationUnitRoot();
        var normalized = NormalizePath(filePath);

        var result = new List<AlMemberSyntax>();
        foreach (var obj in root.Objects)
        {
            // Which parameters of this object's own procedures are `var`, by procedure name,
            // so a call statement `Helper(x)` can claim x as a write.
            var varParams = new Dictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var proc in obj.DescendantNodes().OfType<NavSyntax.MethodDeclarationSyntax>())
            {
                var name = proc.Name?.Identifier.ValueText;
                if (name == null) continue;
                var ps = proc.ParameterList?.Parameters;
                varParams[name] = ps == null
                    ? Array.Empty<bool>()
                    : ps.Value.Select(p => p.VarKeyword.Kind == NavCA.SyntaxKind.VarKeyword).ToArray();
            }
            foreach (var member in obj.DescendantNodes().OfType<NavSyntax.MethodOrTriggerDeclarationSyntax>())
            {
                if (member.Body == null) continue; // a declaration without a body has no statements
                var sites = new List<AlLoopSite>();
                VisitNonLoop(member.Body, parent: null, sites);
                var name = MemberName(member);
                result.Add(new AlMemberSyntax(normalized, name, QualifiedName(member, name), Range(member.Body), sites,
                    CollectWrites(member.Body, varParams)));
            }
        }
        return result;
    }

    // Same union BcCompiler.Emit uses.
    private static IReadOnlyList<string> PreprocessorSymbols(string? appJsonPath)
    {
        var manifest = BcCompiler.ReadManifestCompilerInputs(appJsonPath);
        return Enumerable.Range(1, 25).Select(n => $"CLEANSCHEMA{n}")
            .Concat(BcCompiler.GetExtraPreprocessorSymbols())
            .Concat(manifest.PreprocessorSymbols)
            .ToList();
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string MemberName(NavSyntax.MethodOrTriggerDeclarationSyntax member) =>
        member.Name?.Identifier.ValueText ?? "?";

    // BC names a trigger's scope after its owner: a field, action, control or data item
    // declaration ("Number - OnValidate"). Owners are found by walking up to the first
    // ancestor that has an identifier Name, which every such declaration has and the object
    // itself does not contribute (an object-level trigger is just "OnRun").
    private static string QualifiedName(NavSyntax.MethodOrTriggerDeclarationSyntax member, string name)
    {
        if (member is not NavSyntax.TriggerDeclarationSyntax) return name;
        for (var p = member.Parent; p != null && p is not NavSyntax.ObjectSyntax; p = p.Parent)
        {
            var owner = OwnerName(p);
            if (owner != null) return $"{owner} - {name}";
        }
        return name;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> _nameProps = new();

    private static string? OwnerName(SyntaxNode node)
    {
        var prop = _nameProps.GetOrAdd(node.GetType(), t =>
        {
            var pi = t.GetProperty("Name");
            return pi != null && typeof(NavSyntax.IdentifierNameSyntax).IsAssignableFrom(pi.PropertyType) ? pi : null;
        });
        return prop?.GetValue(node) is NavSyntax.IdentifierNameSyntax id ? id.Identifier.ValueText : null;
    }

    // ---------------------------------------------------------------- write sets

    /// <summary>Every assigning statement in the body, all nesting levels; see AlWriteSetModel.cs for what counts.</summary>
    internal static IReadOnlyList<AlStatementWrites> CollectWrites(
        NavSyntax.BlockSyntax body, IReadOnlyDictionary<string, bool[]>? varParams = null)
    {
        var result = new List<AlStatementWrites>();
        foreach (var stmt in body.DescendantNodes().OfType<NavSyntax.StatementSyntax>())
        {
            if (stmt is NavSyntax.BlockSyntax) continue;
            var targets = WriteTargetsOf(stmt, varParams);
            if (targets.Count > 0) result.Add(new AlStatementWrites(Start(stmt), targets));
        }
        return result;
    }

    private static IReadOnlyList<string> WriteTargetsOf(NavSyntax.StatementSyntax stmt, IReadOnlyDictionary<string, bool[]>? varParams)
    {
        string? single = stmt switch
        {
            NavSyntax.AssignmentStatementSyntax a => RootLocal(a.Target),
            NavSyntax.CompoundAssignmentStatementSyntax c => RootLocal(c.Target),
            // for/foreach loop variables are handled by AlLoopScopeTable.LoopVariablesAssignedBefore.
            _ => null,
        };
        if (single != null) return new[] { single };
        if (stmt is NavSyntax.ExpressionStatementSyntax { Expression: NavSyntax.InvocationExpressionSyntax inv })
            return InvocationWriteTargets(inv, varParams);
        return Array.Empty<string>();
    }

    // A method-call statement writes its receiver (assumed to mutate it); Clear/Evaluate
    // write their first argument; a same-object procedure writes its `var` arguments.
    private static IReadOnlyList<string> InvocationWriteTargets(NavSyntax.InvocationExpressionSyntax inv, IReadOnlyDictionary<string, bool[]>? varParams)
    {
        var args = inv.ArgumentList?.Arguments;
        switch (inv.Expression)
        {
            case NavSyntax.MemberAccessExpressionSyntax ma:
                return RootLocal(ma.Expression) is { } receiver ? new[] { receiver } : Array.Empty<string>();
            case NavSyntax.IdentifierNameSyntax id when ByRefFirstArgumentBuiltins.Contains(id.Identifier.ValueText):
                return args is { Count: > 0 } && RootLocal(args.Value[0]) is { } first ? new[] { first } : Array.Empty<string>();
            case NavSyntax.IdentifierNameSyntax id when varParams != null && varParams.TryGetValue(id.Identifier.ValueText, out var flags):
                var targets = new List<string>();
                if (args != null)
                    for (int i = 0; i < args.Value.Count && i < flags.Length; i++)
                        if (flags[i] && RootLocal(args.Value[i]) is { } t) targets.Add(t);
                return targets;
            default:
                return Array.Empty<string>();
        }
    }

    // The local an expression names: `Rec.Amount` and `arr[1]` root at Rec and arr.
    private static string? RootLocal(SyntaxNode? expr) => expr switch
    {
        null => null,
        NavSyntax.IdentifierNameSyntax id => id.Identifier.ValueText,
        NavSyntax.MemberAccessExpressionSyntax ma => RootLocal(ma.Expression),
        NavSyntax.ElementAccessExpressionSyntax ea => RootLocal(ea.Expression),
        NavSyntax.ParenthesizedExpressionSyntax p => RootLocal(p.Expression),
        _ => null,
    };

    // ---------------------------------------------------------------- loops

    private static bool IsLoop(SyntaxNode node) =>
        node is NavSyntax.ForStatementSyntax or NavSyntax.ForEachStatementSyntax
            or NavSyntax.WhileStatementSyntax or NavSyntax.RepeatStatementSyntax;

    private static void VisitNonLoop(SyntaxNode node, int? parent, List<AlLoopSite> sites)
    {
        foreach (var child in node.ChildNodes())
        {
            if (IsLoop(child)) VisitLoop((NavSyntax.StatementSyntax)child, parent, sites);
            else VisitNonLoop(child, parent, sites);
        }
    }

    private static int VisitLoop(NavSyntax.StatementSyntax loop, int? parent, List<AlLoopSite> sites)
    {
        int index = sites.Count;
        sites.Add(null!); // reserve the slot: nested sites get higher indices than their parent

        AlLoopKind kind;
        string? loopVariable = null;
        AlTextRange header;
        IEnumerable<NavSyntax.StatementSyntax> bodyStatements;
        switch (loop)
        {
            case NavSyntax.ForStatementSyntax f:
                kind = AlLoopKind.For;
                loopVariable = RootLocal(f.LoopVariable) ?? f.LoopVariable?.ToString().Trim();
                header = new AlTextRange(Start(f), End(f.DoKeywordToken));
                bodyStatements = Flatten(f.Statement);
                break;
            case NavSyntax.ForEachStatementSyntax fe:
                kind = AlLoopKind.ForEach;
                loopVariable = RootLocal(fe.IterationVariable);
                header = new AlTextRange(Start(fe), End(fe.DoKeywordToken));
                bodyStatements = Flatten(fe.Statement);
                break;
            case NavSyntax.WhileStatementSyntax w:
                kind = AlLoopKind.While;
                header = new AlTextRange(Start(w), End(w.DoKeywordToken));
                bodyStatements = Flatten(w.Statement);
                break;
            case NavSyntax.RepeatStatementSyntax r:
                kind = AlLoopKind.Repeat;
                header = new AlTextRange(Start(r.UntilKeywordToken), r.Condition != null ? End(r.Condition) : End(r));
                bodyStatements = r.Statements.SelectMany(Flatten);
                break;
            default:
                throw new InvalidOperationException($"[iterations] not a loop statement: {loop.Kind}");
        }

        var body = new List<AlLoopBodyStatement>();
        foreach (var stmt in bodyStatements)
        {
            if (IsLoop(stmt))
            {
                int nested = VisitLoop(stmt, index, sites);
                body.Add(new AlLoopBodyStatement(Range(stmt), nested));
            }
            else
            {
                body.Add(new AlLoopBodyStatement(Range(stmt), null));
                VisitNonLoop(stmt, index, sites); // loops inside an if/case branch are children too
            }
        }

        sites[index] = new AlLoopSite(index, kind, loopVariable, Range(loop), new[] { header }, body, parent,
            ContainsBreak(loop));
        return index;
    }

    // A `break` targets the innermost loop around it.
    private static bool ContainsBreak(NavSyntax.StatementSyntax loop)
    {
        foreach (var b in loop.DescendantNodes().OfType<NavSyntax.BreakStatementSyntax>())
        {
            SyntaxNode? p = b.Parent;
            while (p != null && !IsLoop(p)) p = p.Parent;
            if (ReferenceEquals(p, loop)) return true;
        }
        return false;
    }

    // A begin..end block is a statement in AL; the body is its statements.
    private static IEnumerable<NavSyntax.StatementSyntax> Flatten(NavSyntax.StatementSyntax? stmt)
    {
        if (stmt == null) yield break;
        if (stmt is NavSyntax.BlockSyntax block)
        {
            foreach (var s in block.Statements)
                foreach (var inner in Flatten(s))
                    yield return inner;
        }
        else
        {
            yield return stmt;
        }
    }

    // ---------------------------------------------------------------- positions

    private static AlTextPosition Start(SyntaxNode node)
    {
        var p = node.GetLocation().GetLineSpan().StartLinePosition;
        return new AlTextPosition(p.Line, p.Character);
    }

    private static AlTextPosition End(SyntaxNode node)
    {
        var p = node.GetLocation().GetLineSpan().EndLinePosition;
        return new AlTextPosition(p.Line, p.Character);
    }

    private static AlTextPosition Start(NavSyntax.SyntaxToken token)
    {
        var p = token.GetLocation().GetLineSpan().StartLinePosition;
        return new AlTextPosition(p.Line, p.Character);
    }

    private static AlTextPosition End(NavSyntax.SyntaxToken token)
    {
        var p = token.GetLocation().GetLineSpan().EndLinePosition;
        return new AlTextPosition(p.Line, p.Character);
    }

    private static AlTextRange Range(SyntaxNode node) => new(Start(node), End(node));
}
