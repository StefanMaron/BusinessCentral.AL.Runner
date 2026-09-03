// CallSiteArgWrap — fixes the residual call-site ByRef gap in BC's emitter.
//
// BC's `Compilation.Emit` wraps parameter declarations in `ByRef<T>` natively
// (codeanalysis.cs:342854 EmitParameterType). It also wraps most argument
// expressions at call sites (codeanalysis.cs:264213 EmitFieldRefByRefArgument).
// But it misses some — e.g. `dict.ALGet(K, fieldOfHandleT)` where the field's
// static type is `T` (a Handle subclass) and the callee expects `ByRef<T>`.
//
// Strategy:
//   1. Take the CS1503 diagnostics of a compile that ALREADY RAN (see below).
//   2. Filter to "cannot convert from 'T' to 'ByRef<T>'" shape.
//   3. Rewrite the offending argument expression `expr` to
//      `new ByRef<T>(() => expr, v => expr = v)`.
//   4. Return the rewritten trees so the caller can retry the emit.
//
// Why the diagnostics come from a compile that already ran (#2590)
// ------------------------------------------------------------------
// Earlier, this pass built its OWN throwaway CSharpCompilation and asked it for
// diagnostics purely to find a ByRef gap, before every real compile — a full Roslyn
// bind, thrown away, whether or not the module had a gap at all. Measured on the
// al-language corpus (781 sources, cold cache): 8039ms in this speculative pass vs
// 7958ms in the real bind + IL gen that followed it — the pre-pass cost slightly MORE
// than the compile it preceded, 47% of the whole Roslyn half. On the two small
// dependency compiles in that corpus it was 3x and 2.4x the real compile, because a
// nearly-empty module still pays a full bind against all ~195 references.
//
// TryRewrite instead takes the diagnostics of the REAL compile's own (failed) emit.
// The caller (BcAssembler.CompileCore) emits first; only when that emit fails does it
// hand this method the diagnostics it already has, and only then does rewriting cost
// anything. A module with no ByRef gap — the overwhelming majority — now pays for
// exactly one bind, not two.
//
// This adds NO type renames or identifier rewrites — only argument expressions
// are wrapped, so the resulting IL stays binary-compatible with Microsoft's
// pre-compiled R2R DLLs (the §A "no rewriting" rule).
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AlRunner.Rewriters;

public static class CallSiteArgWrap
{
    private static readonly Regex _byRefMessage = new(
        @"cannot convert from '(?<from>[^']+)' to '(?<to>(?:[\w\.]+\.)?ByRef<[^']+>)'",
        RegexOptions.Compiled);

    /// <summary>
    /// Rewrites the ByRef argument gaps named by <paramref name="diagnostics"/> — the
    /// diagnostics of a compile that already ran over <paramref name="trees"/>. Returns the
    /// rewritten tree list, or <c>null</c> when there was nothing to rewrite: either no
    /// CS1503 in the ByRef-gap shape was present at all, or every one that was present
    /// resolved to a span the rewriter could not locate an argument at. <c>null</c> tells the
    /// caller "these are genuine compile errors, not a ByRef gap — report them as themselves,"
    /// which is the property #2590 calls out explicitly: a real CS1503 that happens to share
    /// the diagnostic ID but not the message shape must never be swallowed as "try again".
    /// </summary>
    public static IReadOnlyList<SyntaxTree>? TryRewrite(
        IReadOnlyList<SyntaxTree> trees,
        IEnumerable<Diagnostic> diagnostics)
    {
        var targets = new List<(SyntaxTree Tree, TextSpan Span, string ByRefType)>();
        foreach (var d in diagnostics)
        {
            if (d.Severity != DiagnosticSeverity.Error) continue;
            if (d.Id != "CS1503") continue;
            var m = _byRefMessage.Match(d.GetMessage());
            if (!m.Success) continue;
            if (d.Location.SourceTree == null) continue;
            targets.Add((d.Location.SourceTree, d.Location.SourceSpan, m.Groups["to"].Value));
        }
        if (targets.Count == 0) return null;

        var byTree = targets
            .GroupBy(t => t.Tree)
            .ToDictionary(g => g.Key, g => g.ToList());

        var current = trees.ToList();
        var changed = false;
        for (int i = 0; i < current.Count; i++)
        {
            if (!byTree.TryGetValue(current[i], out var spans)) continue;
            var oldRoot = current[i].GetRoot();
            var rewriter = new ArgRewriter(spans);
            var newRoot = rewriter.Visit(oldRoot);
            if (rewriter.Rewrote == 0) continue;
            current[i] = current[i].WithRootAndOptions(newRoot, current[i].Options);
            changed = true;
        }
        return changed ? current : null;
    }

    private sealed class ArgRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<TextSpan, string> _spanToType;
        public int Rewrote;

        public ArgRewriter(IEnumerable<(SyntaxTree Tree, TextSpan Span, string ByRefType)> targets)
        {
            _spanToType = targets.ToDictionary(t => t.Span, t => t.ByRefType);
        }

        public override SyntaxNode? VisitArgument(ArgumentSyntax node)
        {
            // The CS1503 location is on the argument's expression, not the ArgumentSyntax
            // node itself. Match by the expression's span overlapping the diagnostic span.
            var expr = node.Expression;
            string? byRefType = null;
            foreach (var kv in _spanToType)
            {
                if (kv.Key.OverlapsWith(expr.Span) || expr.Span.OverlapsWith(kv.Key))
                {
                    byRefType = kv.Value;
                    break;
                }
            }
            if (byRefType == null) return base.VisitArgument(node);

            // Build:  new <ByRefType>(() => <expr>, v => <expr> = v)
            // <ByRefType> is "Microsoft.Dynamics.Nav.Runtime.ByRef<T>" or similar.
            var exprText = expr.ToString();
            var wrapped = SyntaxFactory.ParseExpression(
                $"new {byRefType}(() => {exprText}, v => {exprText} = v)")
                .WithTriviaFrom(expr);
            Rewrote++;
            return node.WithExpression(wrapped);
        }
    }
}
