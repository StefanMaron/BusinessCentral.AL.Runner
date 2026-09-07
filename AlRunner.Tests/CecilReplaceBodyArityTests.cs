// CecilReplaceBodyArityTests — issue #3328.
//
// The defect
// ----------
// NclCecilRewrite.Runtime.cs no-ops BC's three NavDialog.ALUpdateAsync overloads by
// replacing each body with a `ReturnValueTaskN` shim. The shim was chosen from the
// method's DECLARED parameter count, but ReplaceBodyWithHelper forwards declared
// params PLUS `this`, and `ReturnValueTaskN` takes exactly N arguments. So the
// zero-declared-parameter overload emitted ONE `ldarg` and then called a TWO-parameter
// shim — invalid IL. There was no `ReturnValueTask1` for it to have picked.
//
// The JIT rejects that body the first time it is reached, as a bare
//     InvalidProgramException: Common Language Runtime detected an invalid program.
//        at Microsoft.Dynamics.Nav.Runtime.NavDialog.ALUpdateAsync()
// naming no method of ours. Any AL that calls `Dialog.Update()` with no arguments hits
// it; BC's Codeunit550 (VAT Rate Change Tool) does, which is why 202 Tests-VAT tests
// failed on this one line.
//
// Why these are RUNNER-INTERNAL claims, not BC-behaviour claims
// ------------------------------------------------------------
// Every assertion here is about IL that OUR Cecil pass emits into BC's runtime engine
// (Ncl.dll) and about OUR rewrite helper refusing a mismatch. What BC's Dialog.Update()
// does is not in question and is not asserted. See the PR body for why a corpus test
// cannot reach this path.
using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

// Reads the Ncl image this process actually loaded, which BcEngineBootstrap has already
// Cecil-rewritten in place — so it must share the serial bc-engine collection.
[Collection(BcEngineCollection.Name)]
public class CecilReplaceBodyArityTests
{
    private readonly BcEngineFixture _engine;

    public CecilReplaceBodyArityTests(BcEngineFixture engine) => _engine = engine;

    private static TypeDefinition NavDialog()
    {
        var nclPath = typeof(ITreeObject).Assembly.Location;
        var asm = AssemblyDefinition.ReadAssembly(nclPath);
        var t = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavDialog");
        Assert.NotNull(t);
        return t!;
    }

    /// <summary>
    /// The number of values a body pushes before its `call`, and the callee's parameter
    /// count. For a rewritten no-op body these must be equal, or the IL is invalid.
    /// </summary>
    private static (int pushes, int calleeParams, string calleeName) ShimShape(MethodDefinition m)
    {
        int pushes = 0;
        foreach (var i in m.Body.Instructions)
        {
            if (i.OpCode == OpCodes.Ldarg || i.OpCode == OpCodes.Ldarg_S
                || i.OpCode == OpCodes.Ldarg_0 || i.OpCode == OpCodes.Ldarg_1
                || i.OpCode == OpCodes.Ldarg_2 || i.OpCode == OpCodes.Ldarg_3)
            {
                pushes++;
                continue;
            }
            if (i.OpCode == OpCodes.Box) continue;      // consumes and re-pushes one slot
            if (i.OpCode == OpCodes.Call && i.Operand is MethodReference mr)
                return (pushes, mr.Parameters.Count, mr.Name);
        }
        return (pushes, -1, "<no call>");
    }

    // RED before the fix: ALUpdateAsync() pushed 1 and called ReturnValueTask2.
    // This asserts the concrete arities, so a wrong-but-different body (say, a
    // 3-parameter shim, or a shim reached with two pushes) still fails it.
    [SkippableTheory]
    [InlineData(0, 1)]   // `this` only              → 1-arg shim
    [InlineData(1, 2)]   // `this` + controlId       → 2-arg shim
    [InlineData(2, 3)]   // `this` + controlId+value → 3-arg shim
    public void ALUpdateAsync_ForwardsExactlyAsManyArgsAsTheShimTakes(int declaredParams, int expectedSlots)
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var m = NavDialog().Methods.FirstOrDefault(
            x => x.Name == "ALUpdateAsync" && x.HasBody && x.HasThis && x.Parameters.Count == declaredParams);
        Assert.NotNull(m);

        var (pushes, calleeParams, calleeName) = ShimShape(m!);
        Assert.Equal(expectedSlots, pushes);
        Assert.Equal(expectedSlots, calleeParams);
        Assert.Equal($"ReturnValueTask{expectedSlots}", calleeName);
    }

    // The AL-observable half: the JIT must accept the body. Before the fix this threw
    // InvalidProgramException on the zero-parameter overload — the exact failure that
    // took out every Dialog.Update() call. Invoking it here reaches the JIT, which is
    // the thing that rejected it; an IL-shape assertion alone would not.
    [SkippableFact]
    public void ALUpdateAsync_NoArgOverload_IsJitAcceptedAndCompletesAsANoOp()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var t = typeof(NavDialog);
        var mi = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(x => x.Name == "ALUpdateAsync" && x.GetParameters().Length == 0);
        Assert.NotNull(mi);

        // GetUninitializedObject: the no-op body reads no field, so an unconstructed
        // receiver is enough to reach the JIT — and it deliberately avoids BC's real
        // ctor, which needs the service-tier state a headless run does not have.
        var inst = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);

        var ex = Record.Exception(() => mi!.Invoke(inst, null));
        Assert.Null(ex?.InnerException ?? ex);

        // Positive, not just "did not throw": the shim's job is to hand back a completed
        // ValueTask, which is what makes `Dialog.Update()` a no-op instead of a hang.
        var vt = (System.Threading.Tasks.ValueTask)mi!.Invoke(inst, null)!;
        Assert.True(vt.IsCompletedSuccessfully);
    }

    // The generalising half: the guard that would have made the defect impossible.
    // ReplaceBodyWithHelper had no arity check at all, while its sibling
    // PrependStaticCall throws on precisely this mistake — so a wrong shim choice
    // produced a malformed body that survived the rewrite, survived being cached, and
    // only failed when the JIT reached it at runtime.

    [Fact]
    public void ArityGuard_Throws_WhenTheHelperTakesFewerArgsThanTheTargetForwards()
    {
        // The #3328 shape exactly: 1 slot forwarded (`this` on a 0-parameter instance
        // method), a 2-parameter shim.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NclCecilRewrite.AssertHelperArityMatches(
                "Microsoft.Dynamics.Nav.Runtime.NavDialog::ALUpdateAsync()",
                "BcRuntime.ReturnValueTask2", helperParamCount: 2, argCount: 1));

        // Name both numbers and both methods — a guard whose message says only "arity
        // mismatch" leaves the next reader doing this arithmetic by hand.
        Assert.Contains("ALUpdateAsync", ex.Message);
        Assert.Contains("ReturnValueTask2", ex.Message);
        Assert.Contains("2", ex.Message);
        Assert.Contains("1", ex.Message);
    }

    [Fact]
    public void ArityGuard_Throws_WhenTheHelperTakesMoreArgsThanTheTargetForwards()
    {
        // The mirror direction, which is equally invalid IL and equally silent today.
        Assert.Throws<InvalidOperationException>(() =>
            NclCecilRewrite.AssertHelperArityMatches(
                "T::M(int,int)", "BcRuntime.ReturnValueTask2", helperParamCount: 2, argCount: 3));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 5)]
    public void ArityGuard_DoesNotThrow_WhenTheArityMatches(int helperParamCount, int argCount)
    {
        // Negative control. Without this pair, a guard that threw unconditionally —
        // or one stubbed to throw nothing — would satisfy the tests above.
        NclCecilRewrite.AssertHelperArityMatches(
            "T::M", "BcRuntime.Shim", helperParamCount, argCount);
    }
}
