// HandlerRethrowStackPreservationTests — issue #3500.
//
// The defect
// ----------
// BC's NavTestExecution.InvokeHandler is the one frame every [HandlerFunctions] callback is
// invoked through, and its catch arm is
//
//     catch (TargetInvocationException ex) { throw ex.GetBaseException(); }
//
// `throw <existing exception object>` RESETS that object's stack trace. So the frames the AL
// handler was actually raised in are discarded, and the reported origin becomes InvokeHandler
// — a frame at which nothing was raised. Measured on Microsoft BaseApp surface run
// 34169134540: 126 failures with that top frame.
//
// Why these are RUNNER-INTERNAL claims, not BC-behaviour claims
// ------------------------------------------------------------
// Every assertion here is about (a) IL that OUR Cecil pass emits into BC's runtime engine
// (Ncl.dll), (b) OUR locator refusing to rewrite a shape it cannot identify, and (c) what .NET
// itself does to a stack trace under `throw` versus ExceptionDispatchInfo. None of it asserts
// anything about what Business Central does, and none of it is expressible as AL: a stack
// trace's contents is a runner diagnostic, not an AL-observable value — BC's own AL surface
// exposes no `GETLASTERRORCALLSTACK` reading of a .NET frame list. See the PR body.
//
// The control that makes the pair meaningful is BaselineThrowResetsTheStack: it measures
// BC's own construct and shows the frame really is lost, so the preservation test is a
// difference rather than an assertion about one number.

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

// Reads the Ncl image this process actually loaded, which BcEngineBootstrap has already
// Cecil-rewritten in place — so it must share the serial bc-engine collection.
[Collection(BcEngineCollection.Name)]
public class HandlerRethrowStackPreservationTests
{
    private readonly BcEngineFixture _engine;

    public HandlerRethrowStackPreservationTests(BcEngineFixture engine) => _engine = engine;

    private static MethodDefinition InvokeHandlerFromLoadedNcl()
    {
        var nclPath = typeof(ITreeObject).Assembly.Location;
        var asm = AssemblyDefinition.ReadAssembly(nclPath);
        var t = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavTestExecution");
        Assert.NotNull(t);
        var m = t!.Methods.FirstOrDefault(
            x => x.Name == "InvokeHandler" && x.HasBody && x.HasThis && x.Parameters.Count == 2);
        Assert.NotNull(m);
        return m!;
    }

    private static string[] CalleesInCatchArms(MethodDefinition m)
    {
        var names = new System.Collections.Generic.List<string>();
        foreach (var h in m.Body.ExceptionHandlers)
        {
            if (h.HandlerType != ExceptionHandlerType.Catch) continue;
            if (h.CatchType?.FullName != "System.Reflection.TargetInvocationException") continue;
            for (var i = h.HandlerStart; i != null && i != h.HandlerEnd; i = i.Next)
                if ((i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                    && i.Operand is MethodReference mr)
                    names.Add(mr.DeclaringType.FullName + "::" + mr.Name);
        }
        return names.ToArray();
    }

    // ---------------------------------------------------------------------------------
    // 1. The IL half: the rewrite landed on the arm, and BC's resetting call is gone.
    // ---------------------------------------------------------------------------------

    // RED before the fix: the arm called System.Exception::GetBaseException.
    [SkippableFact]
    public void InvokeHandlerCatchArm_CallsThePreservingHelper()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var callees = CalleesInCatchArms(InvokeHandlerFromLoadedNcl());

        Assert.Contains(
            "AlRunner.Patches.HandlerStackPreservation::RethrowPreservingStack",
            callees);
    }

    // The negative half. A rewrite that ADDED the helper call but left BC's GetBaseException
    // in place would satisfy the positive above and still reset the stack, because whichever
    // result the following `throw` consumed would be the one that propagated.
    [SkippableFact]
    public void InvokeHandlerCatchArm_NoLongerCallsGetBaseException()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var callees = CalleesInCatchArms(InvokeHandlerFromLoadedNcl());

        Assert.DoesNotContain("System.Exception::GetBaseException", callees);
    }

    // The rewrite must not have disturbed BC's own body. A replacement that cleared the body
    // and re-emitted it would pass both assertions above while having thrown away the
    // awaitable branch, the direct handler.Invoke branch, or the handler region that routes
    // either one into the arm. Exact counts, so a body that merely still "has some" fails.
    [SkippableFact]
    public void InvokeHandlerBody_KeepsBothInvocationPathsAndItsSingleCatchRegion()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var m = InvokeHandlerFromLoadedNcl();

        var handlers = m.Body.ExceptionHandlers
            .Where(h => h.HandlerType == ExceptionHandlerType.Catch
                && h.CatchType?.FullName == "System.Reflection.TargetInvocationException")
            .ToList();
        Assert.Single(handlers);

        var calls = m.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => ((MethodReference)i.Operand).Name)
            .ToList();

        // The awaitable branch: BC's private InvokeHandlerAsync, awaited synchronously.
        Assert.Contains("InvokeHandlerAsync", calls);
        // The direct branch: MethodBase.Invoke on the handler.
        Assert.Contains("Invoke", calls);
        // The branch selector, which decides between them.
        Assert.Contains("IsAwaitable", calls);

        // Exactly one `throw`, the arm's — so the rewrite added no second raise point.
        Assert.Equal(1, m.Body.Instructions.Count(i => i.OpCode == OpCodes.Throw));
    }

    // ---------------------------------------------------------------------------------
    // 2. The behavioural half: measure what each construct does to a stack trace.
    //    These need no BC artifacts — the subject is .NET's rethrow semantics and our
    //    helper, which is what the rewrite substitutes.
    // ---------------------------------------------------------------------------------

    // A stand-in for an AL handler body that fails. The frame name is the thing a preserved
    // trace must still contain and a reset trace must not.
    private static void TheHandlerFrameThatActuallyFailed()
        => throw new InvalidOperationException("raised inside the handler");

    private static TargetInvocationException CaughtFromReflection()
    {
        var mi = typeof(HandlerRethrowStackPreservationTests).GetMethod(
            nameof(TheHandlerFrameThatActuallyFailed),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            mi.Invoke(null, null);
            throw new InvalidOperationException("the handler stand-in did not throw");
        }
        catch (TargetInvocationException tie)
        {
            return tie;
        }
    }

    // THE CONTROL. Without it the test below asserts a property of a trace with nothing to
    // compare against, and a reader cannot tell whether the frame was ever at risk. This
    // reproduces BC's exact construct and measures the loss.
    [Fact]
    public void BaselineThrowResetsTheStack_WhichIsTheDefect()
    {
        var tie = CaughtFromReflection();

        // BC's body, verbatim. A named local rather than a lambda: a lambda whose only
        // statement is `throw` infers Func<Task> and binds to the obsolete overload.
        void BcsOwnRethrow() => throw tie.GetBaseException();

        var baseline = Record.Exception(BcsOwnRethrow)!;

        // Same object, so the type and message survive — that is why the defect is invisible
        // except in the trace.
        Assert.IsType<InvalidOperationException>(baseline);
        Assert.Equal("raised inside the handler", baseline.Message);

        // ...and the frame that raised it is gone. This is the 126 failures' whole content.
        Assert.DoesNotContain(
            nameof(TheHandlerFrameThatActuallyFailed),
            baseline.StackTrace ?? "");
    }

    [Fact]
    public void RethrowPreservingStack_KeepsTheFrameThatActuallyRaisedIt()
    {
        var tie = CaughtFromReflection();

        // Statement lambda, deliberately: an expression lambda returning Exception binds to
        // Record.Exception(Func<Task>) rather than Record.Exception(Action).
        var preserved = Record.Exception(
            () => { HandlerStackPreservation.RethrowPreservingStack(tie); })!;

        // The frame BC's `throw` discards is present. Paired with the control above — which
        // measures the SAME exception through BC's construct and finds it absent — this is a
        // difference, not an assertion that a trace happens to be non-empty.
        Assert.Contains(
            nameof(TheHandlerFrameThatActuallyFailed),
            preserved.StackTrace ?? "");
    }

    [Fact]
    public void RethrowPreservingStack_PropagatesTheSameObjectTypeAndMessage()
    {
        var tie = CaughtFromReflection();
        var expected = tie.GetBaseException();

        var actual = Record.Exception(
            () => { HandlerStackPreservation.RethrowPreservingStack(tie); })!;

        // Reference identity, not just equality: AL code above this frame may be matching on
        // the exception instance, and a helper that wrapped or re-created it would change
        // what BC's own callers and AL's asserterror observe. This is the "does not change
        // what AL observes" constraint, measured.
        Assert.Same(expected, actual);
        Assert.IsType<InvalidOperationException>(actual);
        Assert.Equal("raised inside the handler", actual.Message);
    }

    [Fact]
    public void RethrowPreservingStack_UnwrapsExactlyAsGetBaseExceptionDoes()
    {
        // Nested wrapping: GetBaseException walks to the innermost, and so must we, or the
        // object reaching AL would change from what BC's body delivered.
        var innermost = new FormatException("innermost");
        var nested = new TargetInvocationException(
            new TargetInvocationException(innermost));

        var actual = Record.Exception(
            () => { HandlerStackPreservation.RethrowPreservingStack(nested); })!;

        Assert.Same(innermost, actual);
        Assert.Same(nested.GetBaseException(), actual);
    }

    [Fact]
    public void RethrowPreservingStack_OnNull_SaysWhichRewriteIsWrong()
    {
        // The third state: BC's body cannot deliver null here, so if it ever arrives the
        // Cecil rewrite is wired to the wrong instruction. A bare NullReferenceException out
        // of a diagnostics fix would be the worst available failure mode.
        var ex = Assert.Throws<ArgumentNullException>(
            () => { HandlerStackPreservation.RethrowPreservingStack(null!); });

        Assert.Contains("InvokeHandler", ex.Message);
        Assert.Contains("NclCecilRewrite", ex.Message);
        Assert.Contains("3500", ex.Message);
    }

    // ---------------------------------------------------------------------------------
    // 3. The locator: provable against constructed bodies, no BC artifacts needed.
    // ---------------------------------------------------------------------------------

    private static (ModuleDefinition module, MethodDefinition method) NewMethod()
    {
        var asm = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Probe", new Version(1, 0)), "Probe", ModuleKind.Dll);
        var module = asm.MainModule;
        var t = new TypeDefinition("N", "T", Mono.Cecil.TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(t);
        var m = new MethodDefinition("M", Mono.Cecil.MethodAttributes.Public, module.TypeSystem.Void);
        t.Methods.Add(m);
        return (module, m);
    }

    /// <summary>
    /// Build `try { nop } catch (<paramref name="catchType"/>) { [call getBase]; throw }`.
    /// </summary>
    private static MethodDefinition BodyWithCatch(
        Type catchType, bool includeGetBaseExceptionCall, int callCount = 1)
    {
        var (module, m) = NewMethod();
        var il = m.Body.GetILProcessor();

        var tryStart = il.Create(OpCodes.Nop);
        var leave = il.Create(OpCodes.Leave_S, il.Create(OpCodes.Ret));
        var handlerStart = il.Create(OpCodes.Nop);
        var end = il.Create(OpCodes.Ret);

        il.Append(tryStart);
        il.Append(leave);
        il.Append(handlerStart);

        var getBase = module.ImportReference(
            typeof(Exception).GetMethod(nameof(Exception.GetBaseException))!);
        if (includeGetBaseExceptionCall)
            for (int i = 0; i < callCount; i++)
                il.Append(il.Create(OpCodes.Callvirt, getBase));

        il.Append(il.Create(OpCodes.Throw));
        il.Append(end);
        leave.Operand = end;

        m.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(catchType),
            TryStart = tryStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = end,
        });
        return m;
    }

    [Fact]
    public void Locator_FindsTheCallInATargetInvocationExceptionCatchArm()
    {
        var sites = NclCecilRewrite.FindCatchRethrowSites(
            BodyWithCatch(typeof(TargetInvocationException), includeGetBaseExceptionCall: true));

        Assert.Single(sites);
        Assert.Equal("GetBaseException", ((MethodReference)sites[0].Operand).Name);
    }

    [Fact]
    public void Locator_IgnoresACatchArmForADifferentExceptionType()
    {
        // Scoping is the point: a GetBaseException in some other arm is not the rethrow this
        // fix targets, and substituting a rethrow for it would change control flow. Without
        // this arm a locator that simply matched the whole body would pass the test above.
        var sites = NclCecilRewrite.FindCatchRethrowSites(
            BodyWithCatch(typeof(InvalidOperationException), includeGetBaseExceptionCall: true));

        Assert.Empty(sites);
    }

    [Fact]
    public void Locator_FindsNothingWhenTheArmDoesNotCallIt()
    {
        var sites = NclCecilRewrite.FindCatchRethrowSites(
            BodyWithCatch(typeof(TargetInvocationException), includeGetBaseExceptionCall: false));

        Assert.Empty(sites);
    }

    [Fact]
    public void Locator_ReportsEveryCallSoTheCallerCanRefuseAnAmbiguousShape()
    {
        // Two is what makes the rewrite's `!= 1` refusal fire rather than pick one. A locator
        // that stopped at the first match would report 1 here and the rewrite would silently
        // rewrite half a shape it does not understand.
        var sites = NclCecilRewrite.FindCatchRethrowSites(
            BodyWithCatch(typeof(TargetInvocationException), includeGetBaseExceptionCall: true, callCount: 2));

        Assert.Equal(2, sites.Count);
    }
}
