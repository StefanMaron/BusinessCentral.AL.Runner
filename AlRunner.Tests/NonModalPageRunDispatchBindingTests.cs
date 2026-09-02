// NonModalPageRunDispatchBindingTests — proves the #2349 fix at the two places it lives.
//
// Root cause: NavTestExecution.TestHandleForm (the NON-modal twin of TestHandleModalForm)
// ended in the client round trip
//     TestClientProxy<IClientCallbackHandler>.Proxy(ServiceConnection.CallbackHandler)
//         .FormRun(formRunRequest);
// and `ServiceConnection` is `ClientSession != null ? testServiceConnection : null`. The
// runner field-pokes NavTestExecution.testClientSession with its own RunnerTestClientSession
// (MetadataPatches) but never sets testServiceConnection — BC only assigns that inside
// CreateTestClientSession(), which the poke exists to bypass. So ServiceConnection returned
// null and `callvirt IService::get_CallbackHandler()` NRE'd inside TestHandleForm's own
// frame, with no inner frame to name the cause. TestHandleModalForm had had that receiver
// chain redirected long ago; its twin had not.
//
// These are deliberately RUNNER-INTERNAL claims: that OUR Cecil pass removed the null read
// from both dispatch methods, that OUR replacement refuses loudly rather than no-opping, and
// that OUR compile-pipeline polyfill no longer refuses non-modal Page.Run as out-of-scope.
// Whether real BC hands a non-modal Page.Run to a [PageHandler] or to TestPage.Trap() is a
// plain BC-behaviour claim and lives in the upstream corpus (tests/al-language), where a real
// service tier adjudicates it.
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
public class NonModalPageRunDispatchBindingTests
{
    private readonly BcEngineFixture _engine;

    public NonModalPageRunDispatchBindingTests(BcEngineFixture engine) => _engine = engine;

    private static MethodDefinition Load(string methodName)
    {
        var nclPath = typeof(ITreeObject).Assembly.Location;
        var asm = AssemblyDefinition.ReadAssembly(nclPath);
        var type = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavTestExecution");
        Assert.NotNull(type);
        var m = type!.Methods.FirstOrDefault(x => x.Name == methodName && x.HasBody);
        Assert.NotNull(m);
        return m!;
    }

    private static bool Calls(MethodDefinition m, string declaringTypeSuffix, string memberName)
        => m.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && i.Operand is MethodReference mr
            && mr.Name == memberName
            && mr.DeclaringType.FullName.EndsWith(declaringTypeSuffix, StringComparison.Ordinal));

    private static bool CallsAnyNamed(MethodDefinition m, string memberName)
        => m.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && i.Operand is MethodReference mr && mr.Name == memberName);

    // Positive: the non-modal dispatch now goes to the runner's own FormRun.
    [SkippableFact]
    public void TestHandleForm_DispatchesToRunnerModalDispatchFormRun()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var m = Load("TestHandleForm");
        Assert.True(Calls(m, "AlRunner.Patches.RunnerModalDispatch", "FormRun"),
            "TestHandleForm must call RunnerModalDispatch.FormRun — without the redirect the "
            + "client callback runs and NREs on a null ServiceConnection.");
    }

    // Negative: the exact dereference that produced the bare NRE must be gone. A redirect that
    // added the new call but left the receiver chain in place would still evaluate
    // ServiceConnection.CallbackHandler and still throw, and the positive above would not
    // notice.
    [SkippableFact]
    public void TestHandleForm_NoLongerReadsTheNullServiceConnection()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var m = Load("TestHandleForm");
        Assert.False(CallsAnyNamed(m, "get_ServiceConnection"),
            "TestHandleForm still reads NavTestExecution.ServiceConnection, which is null here.");
        Assert.False(CallsAnyNamed(m, "get_CallbackHandler"),
            "TestHandleForm still reads IService.CallbackHandler — that callvirt on a null "
            + "ServiceConnection is the NRE in issue #2349.");
        // Not "no call named FormRun" — the replacement is also called FormRun. The claim is
        // that no call named FormRun is left on a CLIENT interface.
        Assert.False(
            m.Body.Instructions.Any(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                && i.Operand is MethodReference mr
                && mr.Name == "FormRun"
                && !mr.DeclaringType.FullName.StartsWith("AlRunner.", StringComparison.Ordinal)),
            "TestHandleForm still calls the client's IClientCallbackHandler.FormRun.");
    }

    // The sibling that was already fixed must keep the same invariant: both dispatch methods
    // go through one redirect helper, so neither can drift back to the client chain alone.
    [SkippableFact]
    public void TestHandleModalForm_KeepsTheSameInvariant()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var m = Load("TestHandleModalForm");
        Assert.True(Calls(m, "AlRunner.Patches.RunnerModalDispatch", "FormRunModal"),
            "TestHandleModalForm must call RunnerModalDispatch.FormRunModal.");
        Assert.False(CallsAnyNamed(m, "get_ServiceConnection"),
            "TestHandleModalForm still reads NavTestExecution.ServiceConnection.");
    }

    // The replacement must refuse loudly rather than return a default: a FormRun that quietly
    // did nothing when handed no context would leave the [PageHandler] unrun and the test green.
    [Fact]
    public void FormRun_WithNoContext_ThrowsRunnerOutOfScope_NotSilentNoOp()
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => AlRunner.Patches.RunnerModalDispatch.FormRun(null!, null!));
        Assert.Contains("TestPage page dispatch", ex.Message);
        Assert.Contains("testpage-page", ex.Message);
    }

    // The compile-pipeline half. AL-compiled Page.Run is redirected at source level to
    // NavRuntimeHelpersShim.NavForm_Run; that shim used to throw out-of-scope non-modal-ui
    // unconditionally, so the SAME AL behaved differently depending only on whether it was
    // compiled here or arrived in a precompiled Base App DLL (which never went through the
    // redirect). It must now forward to BC's own RunAsync and let TestHandleForm decide.
    [Fact]
    public void PolyfillNavFormRunShim_ForwardsToBc_AndNoLongerRefusesNonModalUi()
    {
        var field = typeof(AlRunner.BcAssembler).GetField("PolyfillSource",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("BcAssembler.PolyfillSource not found.");
        var source = (string)field.GetRawConstantValue()!;

        Assert.DoesNotContain("non-modal-ui", source);

        // All five NavForm_Run overloads must forward. Named RunAsync, never Run(, so the
        // _polyfillRedirects rewrite of the literal "NavForm.Run(" cannot send the shim
        // bodies back to themselves.
        var forwards = System.Text.RegularExpressions.Regex.Matches(
            source, @"NavForm\.RunAsync\(").Count;
        Assert.Equal(5, forwards);
        Assert.DoesNotContain("Runtime.NavForm.Run(", source);
    }
}
