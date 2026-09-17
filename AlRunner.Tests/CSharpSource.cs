// CSharpSource — #3527.
//
// One place that answers "is this spelling in real code, or in a comment / a string literal /
// a region the compiler does not build?", for the structural guards in this assembly that scan
// C# source.
//
// It exists because four separate hand-rolled C# tokenizers had grown here, two of them written
// days apart without either author knowing about the other, and the holes in them were shipping
// defects: #4251 (a guard that listed itself, because the text it scans for is the argument to
// its own Contains call) and #4249. Every such scanner has to re-derive verbatim-vs-regular
// escaping, raw-string fences, doc comments and interpolation nesting, and each one gets a
// different subset right.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AlRunner.Tests;

/// <summary>Raised when the source cannot be read as C#. Deliberately not a return value: a
/// guard that could not parse its input has measured nothing, and must not report its success
/// state (guards-need-a-third-state.md).</summary>
internal sealed class CSharpSourceRefusedException : Exception
{
    internal CSharpSourceRefusedException(string message) : base(message) { }
}

internal static class CSharpSource
{
    /// <summary>
    /// <paramref name="sourceText"/> with comments, regions the compiler does not build, and the
    /// CONTENT of string and char literals all blanked to spaces. Newlines are kept, so line N of
    /// the result is line N of the input.
    /// </summary>
    internal static string CodeOnly(string sourceText) => Strip(sourceText, blankLiteralContents: true);

    /// <summary>
    /// The same pass with literals LEFT ALONE, for the scans whose subject is a value spelled as
    /// a literal (an artifact path, a manifest fragment) and for which blanking literals would
    /// remove the thing being looked for.
    /// </summary>
    internal static string CommentsBlanked(string sourceText) => Strip(sourceText, blankLiteralContents: false);

    /// <summary>
    /// Roslyn draws the line, not a character scanner. <c>DescendantTrivia</c> yields comments in
    /// all four spellings plus the regions the compiler does not build; literal CONTENT is a token
    /// KIND, so verbatim escaping, raw-string fences of any length and the boundary between an
    /// interpolated string's text and its <c>{…}</c> holes all come from the compiler rather than
    /// from rules each guard re-derives differently.
    ///
    /// <para>Blanking is by SPAN into a copy of the original text, so everything not explicitly
    /// blanked — code, and the literals <see cref="CommentsBlanked"/> keeps — survives byte for
    /// byte, and newlines are never overwritten.</para>
    /// </summary>
    private static string Strip(string text, bool blankLiteralContents)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Take(3)
            .ToList();
        if (errors.Count > 0)
        {
            throw new CSharpSourceRefusedException(
                "cannot parse this source as C#, so nothing has been measured about it: "
                + string.Join("; ", errors.Select(d => $"{d.Id} at {d.Location.GetLineSpan().StartLinePosition}: {d.GetMessage()}")));
        }

        var root = tree.GetRoot();
        var kept = new System.Text.StringBuilder(text);

        void Blank(Microsoft.CodeAnalysis.Text.TextSpan span)
        {
            for (var i = span.Start; i < span.End && i < kept.Length; i++)
                if (kept[i] != '\n' && kept[i] != '\r') kept[i] = ' ';
        }

        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
            if (IsCommentOrDisabledCode(trivia.Kind()))
                Blank(trivia.Span);

        if (blankLiteralContents)
            foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
                if (IsLiteralContent(token.Kind()))
                    Blank(token.Span);

        return kept.ToString();
    }

    /// <summary>
    /// Trivia that is not code. Disabled <c>#if</c> regions are included because the compiler does
    /// not build them either, so a call spelled there is not a call this assembly makes.
    /// </summary>
    private static bool IsCommentOrDisabledCode(SyntaxKind kind) => kind
        is SyntaxKind.SingleLineCommentTrivia
        or SyntaxKind.MultiLineCommentTrivia
        or SyntaxKind.SingleLineDocumentationCommentTrivia
        or SyntaxKind.MultiLineDocumentationCommentTrivia
        or SyntaxKind.DocumentationCommentExteriorTrivia
        or SyntaxKind.DisabledTextTrivia;

    /// <summary>
    /// Every token kind whose text is literal CONTENT rather than code. The interpolated-string
    /// TEXT token is content; the expressions inside <c>{…}</c> holes are separate tokens and are
    /// deliberately left alone, because a hole really can call something — the blind spot #3527
    /// names, and the one no character-level scanner reaches without re-implementing
    /// interpolation nesting.
    /// </summary>
    private static bool IsLiteralContent(SyntaxKind kind) => kind
        is SyntaxKind.StringLiteralToken
        or SyntaxKind.Utf8StringLiteralToken
        or SyntaxKind.SingleLineRawStringLiteralToken
        or SyntaxKind.MultiLineRawStringLiteralToken
        or SyntaxKind.Utf8SingleLineRawStringLiteralToken
        or SyntaxKind.Utf8MultiLineRawStringLiteralToken
        or SyntaxKind.InterpolatedStringTextToken
        or SyntaxKind.CharacterLiteralToken;

    /// <summary>
    /// True when any string-literal token satisfies <paramref name="match"/>, checking both
    /// <c>Text</c> (the escaped form a manifest embedded in a C# literal uses) and
    /// <c>ValueText</c> (the unescaped value), so either spelling counts.
    /// </summary>
    internal static bool AnyStringLiteral(string sourceText, Func<string, bool> match)
    {
        foreach (var token in CSharpSyntaxTree.ParseText(sourceText).GetRoot().DescendantTokens())
        {
            if (!IsLiteralContent(token.Kind())) continue;
            if (match(token.Text) || match(token.ValueText)) return true;
        }

        return false;
    }

    /// <summary>Every .cs file under <paramref name="dir"/>, refusing rather than returning an
    /// empty list: a scan with nothing to scan reporting zero violations is the worst property a
    /// guard can have.</summary>
    internal static IReadOnlyList<string> CsFilesUnder(string dir)
    {
        if (!Directory.Exists(dir)) throw new CSharpSourceRefusedException($"no such directory: '{dir}'");
        var files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).ToList();
        if (files.Count == 0) throw new CSharpSourceRefusedException($"no .cs files under '{dir}'");
        return files;
    }
}
