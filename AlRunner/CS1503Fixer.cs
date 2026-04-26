using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

// No namespace — must be accessible from Program.cs top-level statements

/// <summary>
/// Second-pass fixup for CS1503 "cannot convert from 'int' to 'string'" errors that arise
/// when BC 26+ emits implicit Integer→Text coercion in AL as a plain 'int' argument to a
/// 'string' or 'NavText' parameter in C#.
///
/// The fixer wraps each offending argument with <c>AlRunner.Runtime.AlCompat.Format(value)</c>,
/// which replicates BC's AL implicit Integer→Text conversion using invariant formatting.
///
/// Issue #1426.
/// </summary>
public static class CS1503Fixer
{
    /// <summary>
    /// Detects CS1503 errors where an <c>int</c> (or <c>long</c>) argument is passed to a
    /// <c>string</c> or <c>NavText</c> parameter, wraps each with
    /// <c>AlRunner.Runtime.AlCompat.Format(value)</c>, and returns the patched trees.
    ///
    /// Returns <c>null</c> when no such errors are found (caller skips the retry).
    /// </summary>
    public static List<SyntaxTree>? Fix(
        CSharpCompilation compilation,
        ImmutableArray<Diagnostic> diagnostics)
    {
        // Filter to CS1503 errors where actual type is int/long and expected is string/NavText.
        var intToStringErrors = diagnostics
            .Where(d =>
                d.Severity == DiagnosticSeverity.Error &&
                d.Id == "CS1503" &&
                d.Location.IsInSource &&
                IsIntToTextMessage(d.GetMessage()))
            .ToList();

        if (intToStringErrors.Count == 0)
            return null;

        // Group errors by syntax tree to batch rewrites per file.
        var errorsByTree = intToStringErrors
            .GroupBy(d => d.Location.SourceTree)
            .Where(g => g.Key != null)
            .ToList();

        bool anyFixed = false;
        var updatedTrees = compilation.SyntaxTrees.ToList();

        foreach (var group in errorsByTree)
        {
            var tree = group.Key!;
            var root = (CSharpSyntaxNode)tree.GetRoot();
            var treeIndex = updatedTrees.IndexOf(tree);
            if (treeIndex < 0) continue;

            // Build a map: original-expression-node → wrapped-expression-node.
            // ReplaceNodes rewrites all matches in one immutable-tree pass, which is
            // correct and avoids span-invalidation issues.
            var nodeMap = new Dictionary<SyntaxNode, SyntaxNode>();

            foreach (var diag in group)
            {
                var span = diag.Location.SourceSpan;
                var node = root.FindNode(span);
                if (node == null) continue;

                // Walk up to find the enclosing ArgumentSyntax
                ArgumentSyntax? argNode = null;
                var current = node as SyntaxNode;
                while (current != null)
                {
                    if (current is ArgumentSyntax a) { argNode = a; break; }
                    current = current.Parent;
                }
                if (argNode == null) continue;

                var expr = argNode.Expression;
                if (nodeMap.ContainsKey(expr)) continue;

                // Wrap: AlRunner.Runtime.AlCompat.Format(<expr>)
                // Build via text round-trip to avoid trivia-extension-method conflicts
                // between the two CodeAnalysis assemblies (Roslyn vs BC).
                var exprText = expr.ToFullString().Trim();
                var wrapped = SyntaxFactory.ParseExpression(
                    $"AlRunner.Runtime.AlCompat.Format({exprText})")
                    .WithTriviaFrom(expr);

                nodeMap[expr] = wrapped;
                anyFixed = true;
            }

            if (nodeMap.Count > 0)
            {
                var newRoot = root.ReplaceNodes(
                    nodeMap.Keys,
                    (original, _) =>
                        nodeMap.TryGetValue(original, out var replacement) ? replacement : original);
                updatedTrees[treeIndex] = CSharpSyntaxTree.Create(
                    newRoot, (CSharpParseOptions?)tree.Options, tree.FilePath);
            }
        }

        return anyFixed ? updatedTrees : null;
    }

    /// <summary>
    /// Returns true when the CS1503 message describes an int or long argument being passed
    /// to a string or NavText parameter — the pattern produced by BC 26+ implicit
    /// Integer→Text coercion.
    /// </summary>
    public static bool IsIntToTextMessage(string message) =>
        message.Contains("cannot convert from 'int' to 'string'") ||
        message.Contains("cannot convert from 'long' to 'string'") ||
        message.Contains("cannot convert from 'int' to 'NavText'") ||
        message.Contains("cannot convert from 'long' to 'NavText'");
}
