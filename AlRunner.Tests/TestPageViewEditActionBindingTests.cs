// TestPageViewEditActionBindingTests — the runner-internal half of issue #3185.
//
// AL's `SomePage.View()` / `SomePage.Edit()` raised
//     InvalidOperationException: The UISessionManager was expected to be initialized.
//     at TestPageClientSession.GetTestLogicalDispatcher()
// because NavTestPageBase.ALView()/ALEdit() wrap their result in
// TestClientProxy<ITestAction>.Proxy(...) and NclCecilRewrite's step 4 stripped that call from
// a HARD-CODED LIST OF SIX method names, while NavTestPageBase has EIGHT Proxy call sites. The
// two the list did not name were exactly ALView and ALEdit.
//
// WHAT IS PINNED WHERE. What the built-in View/Edit actions DO on real BC — the card opens,
// once, on the list's current row, read-only for View and editable for Edit — is a plain
// BC-behaviour claim and is measured upstream on a service tier: corpus codeunit 60461
// "TPVE Tests" (StefanMaron/BusinessCentral.AL.Language.Tests#203). Nothing here duplicates it.
//
// These are RUNNER-INTERNAL claims only:
//   * that our Cecil pass leaves NO TestClientProxy.Proxy call anywhere on NavTestPageBase,
//     rather than on six named methods — the defect was the list, so the test is over the type;
//   * that it removed the PROXY and not the call underneath it, so ALView still asks the
//     runner's ITestPage for its View action;
//   * that RunnerPendingPageOpenMode, the runner's stand-in for the FormState(ViewMode) BC's
//     client hands its form builder, is consumed exactly once and only by the page it was
//     armed for.
using System.Linq;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

// Reads the Ncl image this process actually loaded, which BcEngineBootstrap has already
// Cecil-rewritten in place — so it must share the serial bc-engine collection.
[Collection(BcEngineCollection.Name)]
public class TestPageViewEditActionBindingTests
{
    private readonly BcEngineFixture _engine;

    public TestPageViewEditActionBindingTests(BcEngineFixture engine) => _engine = engine;

    private static TypeDefinition NavTestPageBase()
    {
        var nclPath = typeof(ITreeObject).Assembly.Location;
        var asm = AssemblyDefinition.ReadAssembly(nclPath);
        var type = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavTestPageBase");
        Assert.NotNull(type);
        return type!;
    }

    private static bool CallsProxy(MethodDefinition m)
        => m.HasBody && m.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Call
            && i.Operand is MethodReference mr
            && mr.Name == "Proxy"
            && mr.DeclaringType.Name.StartsWith("TestClientProxy", System.StringComparison.Ordinal));

    private static bool CallsInterfaceMember(MethodDefinition m, string memberName)
        => m.HasBody && m.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && i.Operand is MethodReference mr
            && mr.Name == memberName
            && mr.DeclaringType.Name == "ITestPage");

    private static MethodDefinition Method(TypeDefinition type, string name)
    {
        var m = type.Methods.FirstOrDefault(x => x.Name == name && x.HasBody);
        Assert.True(m != null, $"NavTestPageBase.{name}() not found — Ncl shape changed.");
        return m!;
    }

    // THE REGRESSION. Named per method so a failure says which one came back, and stated over
    // the WHOLE type so a ninth call site in a future Ncl fails here rather than in the corpus.
    [SkippableFact]
    public void NoNavTestPageBaseMethodStillCallsTestClientProxy()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var offenders = NavTestPageBase().Methods.Where(CallsProxy).Select(m => m.Name).ToList();
        Assert.True(offenders.Count == 0,
            "Every TestClientProxy.Proxy call site on NavTestPageBase must be stripped — Proxy "
            + "needs the client's test dispatcher, which does not exist in this process, and "
            + "reaching one raises 'The UISessionManager was expected to be initialized.' Still "
            + $"calling it: {string.Join(", ", offenders)}");
    }

    // The two the old six-name list missed, called out on their own so the failure message
    // names issue #3185's actual surface rather than a set difference.
    [SkippableTheory]
    [InlineData("ALView")]
    [InlineData("ALEdit")]
    public void PageModeAffordanceDoesNotReachTheClientDispatcher(string methodName)
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        Assert.False(CallsProxy(Method(NavTestPageBase(), methodName)),
            $"NavTestPageBase.{methodName}() is AL's TestPage.{methodName.Substring(2)}(); with "
            + "the Proxy call left in place it raises 'The UISessionManager was expected to be "
            + "initialized.' before any page can open (#3185).");
    }

    // The other direction, and the one a blunt "delete the instruction" fix would break: the
    // rewrite removes the PROXY WRAPPER, not the call it wraps. ALView must still ask the
    // runner's own ITestPage for its View action, otherwise it would hand BC a null
    // NavTestAction and AL's .Invoke() would surface as a bare NullReferenceException.
    [SkippableTheory]
    [InlineData("ALView", "View")]
    [InlineData("ALEdit", "Edit")]
    public void PageModeAffordanceStillAsksTheRunnersTestPage(string methodName, string member)
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        Assert.True(CallsInterfaceMember(Method(NavTestPageBase(), methodName), member),
            $"NavTestPageBase.{methodName}() must still call ITestPage.{member}() — that call is "
            + "what reaches LiveNavTestPage's built-in page-mode action.");
    }

    // The six methods the old list DID name, so the sweep that replaced it cannot be a
    // regression for them either.
    [SkippableTheory]
    [InlineData("GetField")]
    [InlineData("GetAction")]
    [InlineData("GetDataItem")]
    [InlineData("GetPart")]
    [InlineData("GetBuiltInAction")]
    [InlineData("FindBuiltInAction")]
    public void PreviouslyStrippedMethodsStayStripped(string methodName)
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        Assert.False(CallsProxy(Method(NavTestPageBase(), methodName)));
    }
}

// The ambient open mode, as a pure unit — no BC runtime, so every row of its contract is
// reachable. BC carries the requested mode as `new FormState(ViewMode)` into its form builder;
// NavForm.RunAsync hands back no form to configure, so the runner parks the mode here for
// RunnerModalDispatch to stamp onto the form it is about to open.
public class RunnerPendingPageOpenModeTests
{
    public RunnerPendingPageOpenModeTests() => RunnerPendingPageOpenMode.Disarm();

    [Fact]
    public void NothingArmed_ConsumesNothing()
    {
        Assert.False(RunnerPendingPageOpenMode.TryConsume(60458, out var readOnly));
        Assert.False(readOnly);
    }

    [Fact]
    public void ArmedReadOnly_IsConsumedByTheArmedPage()
    {
        RunnerPendingPageOpenMode.Arm(60458, readOnly: true);
        Assert.True(RunnerPendingPageOpenMode.TryConsume(60458, out var readOnly));
        Assert.True(readOnly);
    }

    // Edit mode arms too, and must answer FALSE rather than "nothing armed" — the two are
    // different: RunnerModalDispatch leaves NavForm.Editable exactly as InitializeFromMetadata
    // set it in both cases, so a caller cannot tell them apart from the return value alone, and
    // a future caller that could would read a wrong answer here.
    [Fact]
    public void ArmedEditable_IsConsumedAndReportsNotReadOnly()
    {
        RunnerPendingPageOpenMode.Arm(60458, readOnly: false);
        Assert.True(RunnerPendingPageOpenMode.TryConsume(60458, out var readOnly));
        Assert.False(readOnly);
    }

    // The load-bearing one: a page opened from inside the target's OWN OnOpenPage is a
    // different open and must not inherit this mode.
    [Fact]
    public void ConsumedOnce_TheSecondReadFindsNothing()
    {
        RunnerPendingPageOpenMode.Arm(60458, readOnly: true);
        Assert.True(RunnerPendingPageOpenMode.TryConsume(60458, out _));
        Assert.False(RunnerPendingPageOpenMode.TryConsume(60458, out var readOnly));
        Assert.False(readOnly);
    }

    // Armed for one page, asked about another — the mode stays armed for its own page rather
    // than leaking onto whatever opens first.
    [Fact]
    public void ADifferentPageDoesNotConsumeIt()
    {
        RunnerPendingPageOpenMode.Arm(60458, readOnly: true);
        Assert.False(RunnerPendingPageOpenMode.TryConsume(60459, out var readOnly));
        Assert.False(readOnly);
        Assert.True(RunnerPendingPageOpenMode.TryConsume(60458, out readOnly));
        Assert.True(readOnly);
    }

    // Page id 0 is "the form's page number could not be read", never a wildcard.
    [Fact]
    public void PageIdZeroNeverConsumes()
    {
        RunnerPendingPageOpenMode.Arm(60458, readOnly: true);
        Assert.False(RunnerPendingPageOpenMode.TryConsume(0, out _));
        Assert.True(RunnerPendingPageOpenMode.TryConsume(60458, out _));
    }

    // Disarm is what RunnerPendingPageOpenMode's caller runs in a finally: an open that never
    // reached the dispatch (the page refused to open) must not leave the mode armed for a
    // later, unrelated open of the same page on this thread.
    [Fact]
    public void Disarm_DropsAnUnconsumedMode()
    {
        RunnerPendingPageOpenMode.Arm(60458, readOnly: true);
        RunnerPendingPageOpenMode.Disarm();
        Assert.False(RunnerPendingPageOpenMode.TryConsume(60458, out _));
    }
}
