// CachedReflectionHandleScanner — the instrument behind CachedReflectionHandleNullConditionalRatchetTests (#3702).
//
// It answers one question with the compiler rather than with text: which `?.` dereferences sit
// on a reflection handle (PropertyInfo / MethodInfo / FieldInfo / ConstructorInfo / MemberInfo /
// MethodBase / Type) whose RESOLUTION can have produced null without refusing?
//
// WHY A SEMANTIC MODEL, NOT A TEXT SCAN
//   SilentReflectionLookupRatchetTests reads one expression at a time and abstains on every
//   lookup stored into a variable. This residue is a property of a SYMBOL: the lookup is in one
//   place and the `?.` in another, often in another file of the same partial class. Roslyn
//   already knows which symbol an identifier binds to and whether an assigned value can be null.
//
// WHAT "SILENT RESOLUTION" MEANS
//   A handle is silently resolved when at least one assignment to it stores a value the
//   nullable flow analysis cannot prove non-null: a raw `t.GetProperty(...)`, a `?? otherLookup`
//   chain, a runner helper declared to return `PropertyInfo?`. It is loud when every assignment
//   is non-null by construction: `BcShape.Property(...)` / `RequiredField(...)` (declared
//   non-nullable), `?? throw ...`, or a null-forgiving `!`.
//
//   The `!` case is deliberately classed as NOT silent. BcInternalsNullForgivingGuardTests owns
//   that lookup (#3051), so counting the handle's later `?.` uses would count one hazard under
//   two ratchets. A literal `null` / `default` assignment is a reset, not a resolution.
//
// THE COMPILATION IS APPROXIMATE ON PURPOSE
//   BC's assemblies are not referenced, so every BC type is an error type and most diagnostics
//   are noise. What must still bind is the BCL half — `Type.GetProperty` and friends — because
//   that is where "this can return null" comes from. AlRunner builds with ImplicitUsings, so the
//   same global usings are supplied here; without them `Type` does not bind, overload resolution
//   fails, and every raw lookup reads as never-null (measured while writing this). Unbound counts
//   lookup assignments that failed to bind, so the ratchet can refuse a scan in that state
//   instead of reporting a smaller population.
//
// AN UNRESOLVED SYMBOL IS A THIRD STATE, NOT "LOUD" (#3702 review)
//   `typeof(NavFormExtension).GetProperty("ParentObject", flags)` still BINDS with an error type
//   inside it, and Roslyn then reports the result as not-null — so the usual way to write a BC
//   lookup read as loud and escaped the ratchet. Any assignment whose right-hand side contains an
//   unresolved symbol is therefore UNMEASURABLE and counts as possibly null (Unresolved), unless a
//   `!` or `?? throw` above it makes it loud by syntax alone. The other remedy, referencing the BC
//   assemblies, was not taken: the scan would then depend on which BC build the test host carries,
//   and a type only some builds have would move the population between legs. BcUnresolvedNames
//   is what separates "BC types are unresolved here, by design" from "the compilation lost a BCL
//   reference": it lists unresolved names that DO exist in the BCL.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AlRunner.Tests;

internal enum HandleKind { Field, Property, Local }

/// <summary>One `?.` dereference of a silently-resolved reflection handle.</summary>
internal sealed record HandleUse(
    string File, int Line, HandleKind Kind, string Owner, string Handle, string Accessed);

/// <summary>What a scan measured beyond the uses themselves, so a caller can refuse a scan that
/// looked at nothing (guards-need-a-third-state.md).</summary>
internal sealed record HandleScanStats(
    int FilesParsed, int NullConditionalAccesses, int HandleReceivers, int SilentHandles,
    int LoudHandles, int LookupAssignments, IReadOnlyList<string> UnboundSites,
    int Assignments, int UnresolvedAssignments, IReadOnlyList<string> BcUnresolvedNames)
{
    public int Unbound => UnboundSites.Count;
}

internal static class CachedReflectionHandleScanner
{
    private static readonly string[] HandleTypes =
    {
        "System.Reflection.PropertyInfo", "System.Reflection.MethodInfo", "System.Reflection.FieldInfo",
        "System.Reflection.ConstructorInfo", "System.Reflection.MemberInfo", "System.Reflection.EventInfo",
        "System.Reflection.MethodBase", "System.Type",
    };

    /// <summary>The lookups SilentReflectionLookupRatchetTests already counts when they hang off a
    /// `?.` receiver — `typeHandle?.GetProperty(...)` is its population, not this one.</summary>
    private static readonly string[] OwnedByTheLookupRatchet =
        { "GetProperty", "GetMethod", "GetField", "GetConstructor", "GetNestedType" };

    private static readonly string[] LookupNames =
        { "GetProperty", "GetMethod", "GetField", "GetConstructor", "GetNestedType", "GetType", "GetEvent", "GetMember" };

    /// <summary>What `<ImplicitUsings>enable</ImplicitUsings>` adds for Microsoft.NET.Sdk. A COPY of
    /// the SDK's list, pinned against AlRunner.csproj / Directory.Build.* by the ratchet's tests.</summary>
    internal static readonly string[] ImplicitUsings =
    {
        "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http",
        "System.Threading", "System.Threading.Tasks",
    };

    private static readonly Lazy<IReadOnlyList<MetadataReference>> Refs = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => File.Exists(p) && Path.GetFileName(p) is var n
                        && (n.StartsWith("System.", StringComparison.Ordinal) || n is "netstandard.dll" or "mscorlib.dll"))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList());

    internal static IReadOnlyList<HandleUse> Scan(
        IEnumerable<(string Path, string Text)> files, out HandleScanStats stats, string[]? globalUsings = null)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Text, parse, path: f.Path)).ToList();
        var implicitUsings = CSharpSyntaxTree.ParseText(
            string.Concat((globalUsings ?? ImplicitUsings).Select(u => $"global using {u}; ")), parse, path: "<implicit-usings>");
        var compilation = CSharpCompilation.Create("handle-scan", trees.Append(implicitUsings), Refs.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));

        // Pass 1: every `?.` whose receiver binds to a source-declared field / property / local of
        // a handle type. `MemberInfo.DeclaringType` is the BCL's property, not a handle resolved here.
        var uses = new List<(ConditionalAccessExpressionSyntax Node, ISymbol Sym, SyntaxTree Tree)>();
        var cae = 0;
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<ConditionalAccessExpressionSyntax>())
            {
                cae++;
                var sym = model.GetSymbolInfo(node.Expression).Symbol;
                if (sym is not (IFieldSymbol or IPropertySymbol or ILocalSymbol)) continue;
                if (!sym.Locations.Any(l => l.IsInSource)) continue;
                if (!IsHandleType(TypeOf(sym))) continue;
                uses.Add((node, sym, tree));
            }
        }

        // Pass 2: the assignments to just those symbols.
        var wanted = uses.Select(u => u.Sym).Distinct(SymbolEqualityComparer.Default).ToHashSet(SymbolEqualityComparer.Default);
        var names = wanted.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var silentAssignments = new Dictionary<ISymbol, bool>(SymbolEqualityComparer.Default);
        var lookupAssignments = 0;
        var assignments = 0;
        var unresolved = 0;
        var bcUnresolved = new SortedSet<string>(StringComparer.Ordinal);
        var unbound = new List<string>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                ISymbol? target;
                ExpressionSyntax rhs;
                switch (node)
                {
                    case VariableDeclaratorSyntax { Initializer: { } init } d when names.Contains(d.Identifier.ValueText):
                        target = model.GetDeclaredSymbol(d); rhs = init.Value; break;
                    case PropertyDeclarationSyntax { Initializer: { } pinit } pd when names.Contains(pd.Identifier.ValueText):
                        target = model.GetDeclaredSymbol(pd); rhs = pinit.Value; break;
                    case AssignmentExpressionSyntax a
                            when a.IsKind(SyntaxKind.SimpleAssignmentExpression) || a.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                        if (!names.Contains(NameOf(a.Left))) continue;
                        target = model.GetSymbolInfo(a.Left).Symbol; rhs = a.Right; break;
                    default: continue;
                }
                if (target is null || !wanted.Contains(target) || IsReset(rhs)) continue;

                if (IsLookupCall(rhs, out var call))
                {
                    lookupAssignments++;
                    if (model.GetSymbolInfo(call).Symbol is null)
                        unbound.Add($"{tree.FilePath}:{call.GetLocation().GetLineSpan().StartLinePosition.Line + 1}  {call.Expression}");
                }

                assignments++;
                var silent = PossiblyNull(model, rhs, out var viaUnresolved);
                if (viaUnresolved)
                {
                    unresolved++;
                    foreach (var name in BclNamesThatDidNotResolve(compilation, model, rhs)) bcUnresolved.Add(name);
                }
                silentAssignments[target] = silentAssignments.GetValueOrDefault(target) | silent;
            }
        }

        // A handle with NO source assignment cannot be proven loud, so a field or property is silent
        // by default. A LOCAL with none is a pattern / out / foreach variable the compiler populated,
        // not a lookup this code stored: it abstains rather than inflating the population.
        bool Silent(ISymbol s) => silentAssignments.TryGetValue(s, out var v) ? v : s is not ILocalSymbol;
        var silentHandles = wanted.Where(Silent).ToHashSet(SymbolEqualityComparer.Default);

        var result = new List<HandleUse>();
        foreach (var (node, sym, tree) in uses)
        {
            if (!silentHandles.Contains(sym)) continue;
            var accessed = AccessedMember(node);
            if (OwnedByTheLookupRatchet.Contains(accessed, StringComparer.Ordinal)) continue;
            var span = node.GetLocation().GetLineSpan();
            result.Add(new HandleUse(
                tree.FilePath.Replace('\\', '/'),
                span.StartLinePosition.Line + 1,
                sym switch { IFieldSymbol => HandleKind.Field, IPropertySymbol => HandleKind.Property, _ => HandleKind.Local },
                sym is ILocalSymbol ? EnclosingMember(node) : sym.ContainingType.Name,
                sym.Name,
                accessed));
        }

        stats = new HandleScanStats(trees.Count, cae, uses.Count, silentHandles.Count,
            wanted.Count - silentHandles.Count, lookupAssignments, unbound, assignments, unresolved,
            bcUnresolved.ToList());
        return result;
    }

    /// <summary>
    /// Can <paramref name="e"/> evaluate to null? A `!` and a `?? throw` are loud by syntax alone.
    /// Otherwise an expression with an UNRESOLVED symbol anywhere in it (BC types are error types
    /// here) is unmeasurable and counts as possibly null: the compiler still binds the BCL call
    /// around an error-typed receiver or argument, but reports its result without the `?`.
    /// <paramref name="viaUnresolved"/> says the verdict came from that rule, not from flow state.
    /// </summary>
    private static bool PossiblyNull(SemanticModel model, ExpressionSyntax e, out bool viaUnresolved)
    {
        viaUnresolved = false;
        while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
        switch (e)
        {
            case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression }:
                return false;
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.CoalesceExpression):
                return PossiblyNull(model, b.Right, out viaUnresolved);
            case ThrowExpressionSyntax:
                return false;
        }
        if (HasUnresolvedSymbol(model, e) || (e is InvocationExpressionSyntax inv && IsLookupCall(inv, out _)
                                                && model.GetSymbolInfo(inv).Symbol is null))
        {
            viaUnresolved = true;
            return true;
        }
        return model.GetTypeInfo(e).Nullability.FlowState != NullableFlowState.NotNull;
    }

    /// <summary>An error diagnostic inside the expression (`typeof(Nope)`), or a sub-expression whose
    /// type is an error type without a diagnostic of its own (a call on an error-typed receiver).</summary>
    private static bool HasUnresolvedSymbol(SemanticModel model, ExpressionSyntax e) =>
        model.GetDiagnostics(e.Span).Any(d => d.Severity == DiagnosticSeverity.Error)
        || e.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(n => model.GetTypeInfo(n).Type is IErrorTypeSymbol);

    /// <summary>Names the right-hand side failed to resolve that DO exist in the BCL namespaces the
    /// project imports implicitly — the signature of a compilation that lost a reference, as
    /// opposed to a BC type, which is unresolved here on purpose.</summary>
    private static IEnumerable<string> BclNamesThatDidNotResolve(CSharpCompilation compilation, SemanticModel model, ExpressionSyntax e)
    {
        foreach (var d in model.GetDiagnostics(e.Span).Where(d => d.Id == "CS0246"))
        {
            var name = d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan).Trim();
            var simple = name.Split('<')[0].Split('.').Last();
            var arity = name.Contains('<') ? name.Count(c => c == ',') + 1 : 0;
            var meta = arity == 0 ? simple : $"{simple}`{arity}";
            if (ImplicitUsings.Any(ns => compilation.GetTypeByMetadataName($"{ns}.{meta}") is not null))
                yield return name;
        }
    }

    private static ITypeSymbol TypeOf(ISymbol s) => s switch
    {
        IFieldSymbol f => f.Type,
        IPropertySymbol p => p.Type,
        _ => ((ILocalSymbol)s).Type,
    };

    private static bool IsHandleType(ITypeSymbol t) =>
        HandleTypes.Contains(
            t.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat).TrimEnd('?'),
            StringComparer.Ordinal);

    /// <summary>True when <paramref name="e"/>, once parentheses, a trailing `!` and the left side of
    /// a `??` are peeled, is a call named like a reflection lookup — so a lookup that failed to BIND
    /// is counted rather than read as never-null.</summary>
    private static bool IsLookupCall(ExpressionSyntax e, out InvocationExpressionSyntax call)
    {
        while (true)
        {
            if (e is ParenthesizedExpressionSyntax p) { e = p.Expression; continue; }
            if (e is PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } s) { e = s.Operand; continue; }
            if (e is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.CoalesceExpression)) { e = b.Left; continue; }
            break;
        }
        call = (e as InvocationExpressionSyntax)!;
        if (call is null) return false;
        var name = call.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,
            IdentifierNameSyntax i => i.Identifier.ValueText,
            _ => "",
        };
        return LookupNames.Contains(name, StringComparer.Ordinal);
    }

    private static bool IsReset(ExpressionSyntax rhs)
    {
        while (rhs is ParenthesizedExpressionSyntax p) rhs = p.Expression;
        if (rhs is PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } s) rhs = s.Operand;
        return rhs.IsKind(SyntaxKind.NullLiteralExpression) || rhs.IsKind(SyntaxKind.DefaultLiteralExpression)
            || rhs is DefaultExpressionSyntax;
    }

    private static string NameOf(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax i => i.Identifier.ValueText,
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        _ => "",
    };

    private static string AccessedMember(ConditionalAccessExpressionSyntax node)
    {
        var first = node.WhenNotNull.DescendantNodesAndSelf().OfType<MemberBindingExpressionSyntax>().FirstOrDefault();
        if (first is not null) return first.Name.Identifier.ValueText;
        return node.WhenNotNull.DescendantNodesAndSelf().OfType<ElementBindingExpressionSyntax>().Any() ? "[]" : "?";
    }

    private static string EnclosingMember(SyntaxNode n)
    {
        for (var c = n.Parent; c is not null; c = c.Parent)
            switch (c)
            {
                case MethodDeclarationSyntax m: return m.Identifier.ValueText;
                case ConstructorDeclarationSyntax k: return k.Identifier.ValueText;
                case LocalFunctionStatementSyntax l: return l.Identifier.ValueText;
                case PropertyDeclarationSyntax pr: return pr.Identifier.ValueText;
            }
        return "<unknown>";
    }
}
