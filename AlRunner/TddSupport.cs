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
    // The symbol union the failed emit parsed this file with: CLEANSCHEMA1..25, --define, and
    // the owning app.json's preprocessorSymbols (#4071). Per file, never `static readonly`:
    // --define is registered after this type may be touched (#1900).
    private static NavCA.ParseOptions ParseOptionsFor(string filePath) => new(
        runtimeVersion: null!,
        preprocessorSymbols: Infrastructure.AlMemberSyntaxIndex.PreprocessorSymbols(
            Infrastructure.AlMemberSyntaxIndex.NearestAppJson(filePath)),
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
        => Build(details, TestOutcome.Fail, "--tdd", "<tdd-excluded>");

    /// <summary>
    /// Issue #3476: the same enumeration, reported as <see cref="TestOutcome.Skipped"/>. Used
    /// by the non-tdd EMIT-EXCLUDED path when every dropped object is safe to drop and the
    /// module runs anyway — the dropped object's tests DID NOT RUN, and a run that reports a
    /// number while quietly discarding them is worse than the refusal it replaced. Skipped,
    /// not Fail: nothing is known about whether these tests would pass, and Fail would make
    /// the runner assert something it did not measure.
    /// </summary>
    public static IReadOnlyList<TestResult> BuildSkippedTests(
        IReadOnlyList<TddExcludedObjectDetail> details)
        => Build(details, TestOutcome.Skipped, "emit-excluded", "<emit-excluded>");

    private static IReadOnlyList<TestResult> Build(
        IReadOnlyList<TddExcludedObjectDetail> details, TestOutcome outcome,
        string prefix, string unreadableMethodName)
    {
        var results = new List<TestResult>();
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
                    detail.ObjectDisplayName, unreadableMethodName, outcome,
                    $"{prefix}: could not re-read {detail.FilePath} to find its [Test] procedures: {ex.Message}",
                    string.Join("\n", detail.Diagnostics), TimeSpan.Zero,
                    AlCallStack: null, CodeunitDisplayName: detail.ObjectDisplayName,
                    Exception: null, Expectation: null, InsideTestProc: false));
                continue;
            }

            var tree = NavSyntax.SyntaxTree.ParseObjectText(
                src, path: detail.FilePath, encoding: null!, ParseOptionsFor(detail.FilePath), default);
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
                        objName, methodName, outcome,
                        $"{prefix}: {objName} did not compile — {firstDiag}",
                        diagText, TimeSpan.Zero,
                        AlCallStack: null, CodeunitDisplayName: objName,
                        Exception: null, Expectation: null, InsideTestProc: false));
                }
            }
        }
        return results;
    }
}
