// CallSiteArgWrapTests — proves CallSiteArgWrap.TryRewrite (issue #2590) against a
// self-contained ByRef<T>-shaped stand-in type, so this suite needs no BC service-tier
// artifacts. BcAssemblerCallSiteArgWrapTests repeats the same shape against the REAL
// Microsoft.Dynamics.Nav.Runtime.ByRef<T>, and additionally proves the retry loop inside
// BcAssembler.CompileCore that decides WHEN TryRewrite runs.
//
// Before #2590, BcAssembler ran a throwaway full Roslyn compile before EVERY real one —
// speculatively, whether or not the module had a ByRef gap — purely to collect these
// diagnostics. TryRewrite takes the diagnostics of a compile that already happened
// (the real one), so the pass only costs anything on the rare module that actually needs
// it. What TryRewrite computes did not change; only when it is invoked did.
//
// What each test proves (tdd.md: must prove, not just pass):
//   - Positive: a genuine 'cannot convert T to ByRef<T>' diagnostic is rewritten to the
//     exact wrap expression, and the REWRITTEN trees actually recompile clean — not just
//     "text changed", the fix is semantically correct.
//   - Negative #1: diagnostics with no CS1503 at all return null — nothing to rewrite,
//     caller must report its own (non-existent) errors, not loop forever.
//   - Negative #2: a genuine CS1503 that is NOT a ByRef-shape conversion error (an
//     ordinary type mismatch) also returns null. This is the correctness risk the issue
//     names explicitly: a real compile error that happens to share the diagnostic ID must
//     surface as itself, never get silently swallowed as "nothing to rewrite, try again".
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using AlRunner.Rewriters;
using Xunit;

namespace AlRunner.Tests;

public sealed class CallSiteArgWrapTests
{
    // Shaped like Microsoft.Dynamics.Nav.Runtime.ByRef<T> exactly where it matters to
    // CallSiteArgWrap: a generic class named ByRef<T>, constructed from a getter/setter
    // delegate pair — see BcAssemblerCallSiteArgWrapTests for the proof this shape matches
    // the real type's constructor.
    private const string ByRefShim = """
        namespace Some.Namespace
        {
            public sealed class ByRef<T>
            {
                public ByRef(System.Func<T> getter, System.Action<T> setter) { }
            }
        }
        """;

    private static readonly System.Lazy<System.Collections.Generic.List<MetadataReference>> _refs = new(() =>
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mscorlib.dll", "System.Runtime.dll", "System.Private.CoreLib.dll", "netstandard.dll",
        };
        return tpa.Where(p => wanted.Contains(Path.GetFileName(p)))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
    });

    private static CSharpCompilation Compile(System.Collections.Generic.List<SyntaxTree> trees, string name) =>
        CSharpCompilation.Create(name, trees, _refs.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

    private const string GapSrc = """
        public class Holder { public int Field; }
        public static class Caller
        {
            public static void Take(Some.Namespace.ByRef<int> x) { }
            public static void Go(Holder h) { Take(h.Field); }
        }
        """;

    [Fact]
    public void TryRewrite_WrapsTheByRefGapArgument_AndTheResultRecompilesClean()
    {
        var shimTree = CSharpSyntaxTree.ParseText(ByRefShim, path: "_shim.cs");
        var gapTree = CSharpSyntaxTree.ParseText(GapSrc, path: "Gap.cs");
        var trees = new System.Collections.Generic.List<SyntaxTree> { shimTree, gapTree };

        var failing = Compile(trees, "gap_before");
        var diagnostics = failing.GetDiagnostics();
        Assert.Contains(diagnostics, d => d.Id == "CS1503" && d.Severity == DiagnosticSeverity.Error);

        var rewritten = CallSiteArgWrap.TryRewrite(trees, diagnostics);

        Assert.NotNull(rewritten);
        var gapText = rewritten!.Single(t => t.FilePath == "Gap.cs").GetText().ToString();
        Assert.Contains(
            "new Some.Namespace.ByRef<int>(() => h.Field, v => h.Field = v)",
            gapText, StringComparison.Ordinal);
        // The gap expression itself must be GONE — a rewriter that appended the wrap
        // instead of replacing the argument would leave both and still "contain" the text.
        Assert.DoesNotContain("Take(h.Field)", gapText, StringComparison.Ordinal);

        // Correct, not just textually different: the rewritten trees must actually compile.
        var after = Compile(rewritten!.ToList(), "gap_after");
        Assert.Empty(after.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void TryRewrite_ReturnsNull_WhenNoDiagnosticsAreErrors()
    {
        var cleanTree = CSharpSyntaxTree.ParseText(
            "public class Clean { public int F() => 1; }", path: "Clean.cs");
        var trees = new System.Collections.Generic.List<SyntaxTree> { cleanTree };
        var compilation = Compile(trees, "clean");

        var rewritten = CallSiteArgWrap.TryRewrite(trees, compilation.GetDiagnostics());

        Assert.Null(rewritten);
    }

    /// <summary>
    /// The correctness risk the issue names explicitly: a real CS1503 that is NOT a
    /// ByRef-shape conversion (here: string literal passed where int is expected) must be
    /// left alone — TryRewrite must not mistake it for a rewritable gap, and the caller's
    /// retry loop must go on to report ITS diagnostic text unmodified.
    /// </summary>
    [Fact]
    public void TryRewrite_ReturnsNull_WhenTheOnlyCS1503IsNotAByRefShapeConversion()
    {
        const string ordinaryMismatch = """
            public static class Caller
            {
                public static void Take(int x) { }
                public static void Go() { Take("not an int"); }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(ordinaryMismatch, path: "Ordinary.cs");
        var trees = new System.Collections.Generic.List<SyntaxTree> { tree };
        var compilation = Compile(trees, "ordinary");
        var diagnostics = compilation.GetDiagnostics();
        Assert.Contains(diagnostics, d => d.Id == "CS1503" && d.Severity == DiagnosticSeverity.Error);

        var rewritten = CallSiteArgWrap.TryRewrite(trees, diagnostics);

        Assert.Null(rewritten);
    }
}
