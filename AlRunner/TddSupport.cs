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
    // The options the failed emit parsed this file with: the compile's own builder over the
    // manifest that compile read, carried on the detail (#4071). Per call, never `static
    // readonly`: --define is registered after this type may be touched (#1900).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static NavCA.ParseOptions ParseOptionsFor(TddExcludedObjectDetail detail) =>
        BcCompiler.BuildParseOptions(BcCompiler.ReadManifestCompilerInputs(detail.ManifestAppJsonPath));

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
        => Build(details, TestOutcome.Fail, "--tdd", "<tdd-excluded>", AlErrorKind.Compile);

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
        => Build(details, TestOutcome.Skipped, "emit-excluded", "<emit-excluded>", null);

    private static IReadOnlyList<TestResult> Build(
        IReadOnlyList<TddExcludedObjectDetail> details, TestOutcome outcome,
        string prefix, string unreadableMethodName, AlErrorKind? kind)
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
                    Exception: null, Expectation: null, InsideTestProc: false, KnownErrorKind: kind));
                continue;
            }

            var tree = NavSyntax.SyntaxTree.ParseObjectText(
                src, path: detail.FilePath, encoding: null!, ParseOptionsFor(detail), default);
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
                        Exception: null, Expectation: null, InsideTestProc: false, KnownErrorKind: kind));
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Every member --tdd generated that <paramref name="moduleName"/>'s compile depended on: the
    /// ones its own emit generated, plus (#5037) the ones an earlier pass generated into another
    /// source bundle for it, which this compile resolved and so does not report itself.
    /// </summary>
    public static List<TddGeneratedMember> MembersFor(BcEmitOutput emitOutput, string moduleName)
    {
        var own = emitOutput.TddGeneratedMembers ?? Array.Empty<TddGeneratedMember>();
        return own.Concat(TddCrossBundle.GeneratedFor(moduleName).Where(m => !own.Contains(m))).ToList();
    }
}

/// <summary>
/// The tests that reach a --tdd-generated member, resolved statically from each AL0132's
/// location and the compile's call graph (<see cref="TddGeneratedMember.DependentTests"/>,
/// <see cref="TddCallGraph"/>), and the annotation that
/// names those members on each such result (#5147). The result's own outcome and message are
/// kept: the test reports what its assertions said. Shared by the CLI/--watch run loop and
/// --server (#5034); docs/server-mode.md#tdd.
/// </summary>
public sealed class TddDependents
{
    // "ObjectDisplayName.MethodName" -> the generated members that test's compile needed.
    private readonly Dictionary<string, List<TddGeneratedMember>> _byTest = new();

    public int Count => _byTest.Count;

    public void Add(IEnumerable<TddGeneratedMember> members)
    {
        foreach (var m in members)
            foreach (var testLabel in m.DependentTests)
            {
                if (!_byTest.TryGetValue(testLabel, out var list))
                    _byTest[testLabel] = list = new List<TddGeneratedMember>();
                if (!list.Contains(m)) list.Add(m);
            }
    }

    public TestResult Apply(TestResult t)
    {
        var label = string.IsNullOrEmpty(t.CodeunitDisplayName) ? t.Codeunit : t.CodeunitDisplayName!;
        if (!_byTest.TryGetValue($"{label}.{t.Method}", out var deps) || deps.Count == 0) return t;
        return t with { GeneratedStubs = deps.Select(TddReport.Describe).ToList() };
    }

    public List<TestResult> Apply(IReadOnlyList<TestResult> raw)
        => _byTest.Count == 0 ? raw as List<TestResult> ?? raw.ToList() : raw.Select(Apply).ToList();
}

/// <summary>
/// #5147: the text every surface (CLI, --watch, --server stderr) prints for tests that ran
/// against generated members. A statement of fact only: the test's status is its own.
/// </summary>
public static class TddReport
{
    public const string PerTestPrefix = "reaches generated stub(s): ";

    public static string Describe(TddGeneratedMember m) => $"{m.ObjectDisplayName}: {m.MemberKind} {m.Signature}";

    public static string PerTestLine(TestResult t) => PerTestPrefix + string.Join("; ", t.GeneratedStubs ?? Array.Empty<string>());

    /// <summary>A heading and one line per test carrying <see cref="TestResult.GeneratedStubs"/>; empty when none does.</summary>
    public static List<string> SummaryLines(IEnumerable<TestResult> tests, string scope)
    {
        var flagged = tests.Where(t => t.GeneratedStubs is { Count: > 0 }).ToList();
        var lines = new List<string>();
        if (flagged.Count == 0) return lines;
        lines.Add($"--tdd: {flagged.Count} test(s) reach generated stubs this {scope}:");
        foreach (var t in flagged)
        {
            var name = string.IsNullOrEmpty(t.CodeunitDisplayName) ? t.Codeunit : t.CodeunitDisplayName!;
            lines.Add($"  {name}.{t.Method} ({t.Outcome.ToString().ToLowerInvariant()}): {string.Join("; ", t.GeneratedStubs!)}");
        }
        return lines;
    }
}

/// <summary>
/// One --server <c>runTests</c> request with <c>tdd</c> on (#5034): where the bundle loop streams
/// the results it builds itself, and which bundle's dependents are running now.
/// </summary>
public sealed class TddServerRequest
{
    public TddServerRequest(Action<TestResult> report) => Report = report;

    /// <summary>Streams one result as a <c>test</c> line.</summary>
    public Action<TestResult> Report { get; }

    /// <summary>The dependents of the bundle whose tests are running; null between bundles.</summary>
    public TddDependents? Active { get; set; }

    /// <summary>Every member generated in this request, for the stderr summary.</summary>
    public List<TddGeneratedMember> Generated { get; } = new();

    /// <summary>Every result of this request that reaches a generated member, for the stderr summary.</summary>
    public List<TestResult> RanAgainstStubs { get; } = new();

    /// <summary>The annotation for a test of the bundle running now; records it for the summary.</summary>
    public TestResult Apply(TestResult t)
    {
        var applied = Active?.Apply(t) ?? t;
        if (applied.GeneratedStubs is { Count: > 0 }
            && !RanAgainstStubs.Any(r => r.Codeunit == applied.Codeunit && r.Method == applied.Method))
            RanAgainstStubs.Add(applied);
        return applied;
    }
}
