// TddSupport — issue #1997: turns objects excluded from a --tdd emit (because they
// reference a symbol the implementing app doesn't have yet) into synthetic FAILED
// TestResult entries, one per [Test] procedure the excluded object declares.
//
// Scope of this file matches the issue's reduced-scope acceptance criteria (1, 2, 6,
// 7, 9, 10, 11, 12): it REFUSES to infer a missing member's type and generate it —
// every excluded [Test] procedure reports failed, naming the object and carrying the
// AL diagnostics that identified the break. Type inference / member generation
// (criteria 3, 4, 5, 8's "list of GENERATED members") is tracked as a follow-up; see
// the PR this file shipped in for the issue number.
//
// Why re-parse rather than reuse a compiled type: an EXCLUDED object never reached
// Compilation.Emit successfully, so there is no IL, no MethodInfo, nothing reflection
// can see. The only surviving artifact is its own source file, so [Test] procedures
// are found the same way RecordPatches.AlSourceParser finds table/field declarations —
// by walking BC's own AL syntax tree (NavSyntax.SyntaxTree.ParseObjectText), never by
// copying the file elsewhere (see BcCompiler.Emit's interposition-point comment: a
// temp-directory copy would make the reported path unclickable in the editor and
// would desync --watch's watched tree from the tree actually compiled).
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner;

public static class TddSupport
{
    // Mirrors BcCompiler's own ParseOptions (CLEANSCHEMA1..25 + whatever --define /
    // --preprocessor-symbols supplied) so this re-parse sees exactly the same source
    // the original (failed) emit attempt saw — same rule RecordPatches.AlSourceParser
    // follows for the same reason (see its AlParseOptions doc comment). Recomputed per
    // call rather than cached: BcCompiler.SetExtraPreprocessorSymbols can run after
    // this type is first touched, and a frozen `static readonly` would miss that.
    private static NavCA.ParseOptions ParseOptions => new(
        runtimeVersion: null!,
        preprocessorSymbols: Enumerable.Range(1, 25).Select(n => $"CLEANSCHEMA{n}")
            .Concat(BcCompiler.GetExtraPreprocessorSymbols()),
        documentationMode: NavCA.DocumentationMode.None);

    private static string Unquote(string s)
        => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    private static string IdentText(NavSyntax.IdentifierNameSyntax? id)
        => id == null ? "" : Unquote(id.Identifier.ValueText ?? id.Identifier.Text ?? "");

    /// <summary>
    /// Builds one synthetic <see cref="TestResult"/> per <c>[Test]</c> procedure declared by
    /// each excluded object, all with <see cref="TestOutcome.Fail"/>. Objects with no
    /// <c>[Test]</c> procedure (a table, a non-test codeunit, …) contribute nothing here —
    /// they were never a "test [that] vanished", so there is no test-shaped result to
    /// report for them; the caller's own EMIT-EXCLUDED log line still names the object.
    /// </summary>
    public static IReadOnlyList<TestResult> BuildFailedTests(
        IReadOnlyList<TddExcludedObjectDetail> details)
    {
        var results = new List<TestResult>();
        var parseOpts = ParseOptions;
        foreach (var detail in details)
        {
            string src;
            try { src = File.ReadAllText(detail.FilePath); }
            catch (Exception ex)
            {
                // The file itself is unreadable (deleted between emit and this call, race
                // with an editor save, …) — still report SOMETHING for this object rather
                // than silently dropping it, per loud-failures.md. There is no method name
                // to attach it to, so it becomes a single synthetic "object" result.
                results.Add(new TestResult(
                    detail.ObjectDisplayName, "<tdd-excluded>", TestOutcome.Fail,
                    $"--tdd: could not re-read {detail.FilePath} to find its [Test] procedures: {ex.Message}",
                    string.Join("\n", detail.Diagnostics), TimeSpan.Zero,
                    AlCallStack: null, CodeunitDisplayName: detail.ObjectDisplayName,
                    Exception: null, Expectation: null, InsideTestProc: false));
                continue;
            }

            var tree = NavSyntax.SyntaxTree.ParseObjectText(
                src, path: detail.FilePath, encoding: null!, parseOpts, default);
            if (tree.GetRoot() is not NavSyntax.CompilationUnitSyntax root) continue;

            var diagText = string.Join("\n", detail.Diagnostics);
            var firstDiag = detail.Diagnostics.Count > 0 ? detail.Diagnostics[0] : "(no diagnostic captured)";

            foreach (var obj in root.ChildNodes().OfType<NavSyntax.ObjectSyntax>())
            {
                var objName = IdentText(obj.Name);
                if (objName.Length == 0) objName = detail.ObjectDisplayName;
                foreach (var member in obj.Members)
                {
                    if (member is not NavSyntax.MethodDeclarationSyntax method) continue;
                    var isTest = method.Attributes.Any(a =>
                        string.Equals(IdentText(a.Name), "Test", StringComparison.OrdinalIgnoreCase));
                    if (!isTest) continue;

                    var methodName = IdentText(method.Name);
                    if (methodName.Length == 0) methodName = "<unnamed>";

                    results.Add(new TestResult(
                        objName, methodName, TestOutcome.Fail,
                        $"--tdd: {objName} did not compile — {firstDiag}",
                        diagText, TimeSpan.Zero,
                        AlCallStack: null, CodeunitDisplayName: objName,
                        Exception: null, Expectation: null, InsideTestProc: false));
                }
            }
        }
        return results;
    }
}
