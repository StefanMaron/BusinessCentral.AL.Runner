// BcAssemblerCallSiteArgWrapTests — RED/GREEN proof for issue #2590 at the level that
// actually matters: BcAssembler.CompileCore's own retry loop, against the REAL
// Microsoft.Dynamics.Nav.Runtime.ByRef<T> from the BC service tier. CallSiteArgWrapTests
// proves the rewrite mechanics in isolation against a stand-in shape; this class proves
// the loop that decides WHEN CallSiteArgWrap.TryRewrite runs, and that the decision is
// correct — not merely fast.
//
// Needs the real service-tier DLLs on disk (Microsoft.Dynamics.Nav.Ncl.dll, which is
// where ByRef<T> lives) so BcAssembler can reference them, but NOT the in-process BC
// engine bootstrap: BcAssembler.Compile is a pure Roslyn metadata-reference compile, it
// never loads those DLLs into the CLR. TestArtifacts.SkipIf is the right gate; unlike the
// BcEngineCollection tests, this class does not need [Collection(BcEngineCollection.Name)].
//
// What each test proves (tdd.md: must prove, not just pass):
//   - Genuine gap: a call site BC's emitter under-wraps compiles successfully via the
//     on-demand rewrite retry, and the resulting assembly is loaded and RUN, not merely
//     reported Success — a rewrite that produced syntactically valid but semantically
//     wrong code (e.g. wrapping the wrong argument) would still "compile".
//   - Genuine error preserved: a real CS1503 that is NOT a ByRef-shape conversion must
//     come back as ITS OWN diagnostic text, unmodified by ever having passed through the
//     rewrite attempt. This is the exact correctness risk #2590 calls out: "Any
//     reordering has to keep a real CS1503 that is not a ByRef gap reporting the
//     original diagnostic."
//   - No-gap fast path: an ordinary module with no ByRef gap still compiles successfully
//     — the common case the whole change exists to speed up must still work at all.
using System.Reflection;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class BcAssemblerCallSiteArgWrapTests
{
    private const string ByRefGapSource = """
        public class Holder { public int Field; }
        public static class Caller
        {
            public static int Take(Microsoft.Dynamics.Nav.Runtime.ByRef<int> x)
            {
                x.Value = x.Value + 1;
                return x.Value;
            }
            public static int Go(Holder h)
            {
                // The under-wrapped call site: h.Field is 'int', Take wants ByRef<int>.
                return Take(h.Field);
            }
        }
        """;

    private const string GenuineErrorSource = """
        public static class Caller
        {
            public static void Take(int x) { }
            // Ordinary CS1503 — a string where an int is expected. Shares the diagnostic
            // ID with the ByRef-gap case but not its message shape.
            public static void Go() { Take("not an int"); }
        }
        """;

    private const string NoGapSource = """
        public static class Plain
        {
            public static int Add(int a, int b) => a + b;
        }
        """;

    [SkippableFact]
    public void Compile_RewritesAGenuineByRefGap_AndTheAssemblyRunsCorrectly()
    {
        TestArtifacts.SkipIfMissing();

        var result = new BcAssembler().Compile(
            "ByRefGapAssembly", new[] { new EmittedSource("Gap", ByRefGapSource) });

        Assert.True(result.Success, string.Join("\n", result.Errors));

        // Not just "compiled" — load it and run it. A rewrite that wrapped the wrong
        // expression, or built a getter/setter pair that didn't round-trip, would still
        // produce a loadable assembly; only running it proves the semantics are right.
        var asm = Assembly.Load(result.AssemblyBytes!);
        var caller = asm.GetType("Caller")!;
        var holderType = asm.GetType("Holder")!;
        var holder = Activator.CreateInstance(holderType)!;
        holderType.GetField("Field")!.SetValue(holder, 41);

        var returned = (int)caller.GetMethod("Go")!.Invoke(null, new[] { holder })!;

        Assert.Equal(42, returned);
        // The setter half of the ByRef wrap must have written back through to the field —
        // proves the wrap is the real get/set pair, not a one-way snapshot.
        Assert.Equal(42, (int)holderType.GetField("Field")!.GetValue(holder)!);
    }

    [SkippableFact]
    public void Compile_ReportsAGenuineCompileError_UnmodifiedByTheRewriteAttempt()
    {
        TestArtifacts.SkipIfMissing();

        var result = new BcAssembler().Compile(
            "GenuineErrorAssembly", new[] { new EmittedSource("Bad", GenuineErrorSource) });

        Assert.False(result.Success);
        // The ORIGINAL diagnostic — not swallowed, not replaced by a rewrite-loop failure,
        // not turned into "no output" with no explanation.
        Assert.Contains(result.Errors, e =>
            e.Contains("CS1503", StringComparison.Ordinal) &&
            e.Contains("string", StringComparison.Ordinal) &&
            e.Contains("int", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void Compile_WithNoByRefGap_StillCompilesSuccessfully()
    {
        TestArtifacts.SkipIfMissing();

        var result = new BcAssembler().Compile(
            "NoGapAssembly", new[] { new EmittedSource("Plain", NoGapSource) });

        Assert.True(result.Success, string.Join("\n", result.Errors));
        var asm = Assembly.Load(result.AssemblyBytes!);
        var plain = asm.GetType("Plain")!;
        Assert.Equal(7, (int)plain.GetMethod("Add")!.Invoke(null, new object[] { 3, 4 })!);
    }
}
