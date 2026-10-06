// TestPageErrorTeardownContractTests — pins the C# CONTRACT issue #2656's fix depends on,
// not "what BC does" (that's the job of the companion corpus PR,
// StefanMaron/BusinessCentral.AL.Language.Tests#142, merged, codeunit 60795 "TestPage
// ErrTeardown Tests", 5/5 -- and the pre-existing codeunit 60793 "Test Page BgTask Tests"
// EnqueueBackgroundTask_UnhandledErrorPropagates this fix also flips GREEN).
//
// Measured against a real BC service tier: an unhandled error raised inside a page's
// OnAfterGetRecord trigger, fired by a TestPage navigation call (GoToRecord, MoveNext, ...)
// on an already-open TestPage, tears the TestPage's underlying client session down. Every
// subsequent call on that same TestPage variable then raises BC's own
// "The TestPage is not open.", discarding the trigger's own error text. An unhandled error
// from OnValidate or OnAction does NOT do this -- both propagate their own text and leave the
// page open.
//
// There is no reflection surface that exercises MockTestPage's dispatch without a loaded BC
// runtime/session, so -- following the same proven pattern as
// TestPageImplicitPositioningBindingTests (issue #2392) -- this reads the COMPILED IL of the
// loaded al-runner.dll via Mono.Cecil, not raw source text. A raw-source-text version of this
// file deterministically failed to find its own search strings when run under this repo's
// Linux CI (reproduced across 5 retries with delay, ruling out a transient read race); the
// IL-based approach mirrors a mechanism already proven reliable in that same environment.
using System.Linq;
using AlRunner.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageErrorTeardownContractTests
{
    private static TypeDefinition LiveNavTestPageType()
    {
        var path = typeof(AlRunner.LiveNavTestPage).Assembly.Location;
        var asm = AssemblyDefinition.ReadAssembly(path);
        var type = asm.MainModule.GetType(typeof(AlRunner.LiveNavTestPage).FullName);
        Assert.NotNull(type);
        return type!;
    }

    private static MethodDefinition Method(TypeDefinition type, string name)
    {
        var m = type.Methods.FirstOrDefault(x => x.Name == name && x.HasBody);
        Assert.True(m != null, $"could not locate '{name}' on {type.Name}");
        return m!;
    }

    private static bool HasField(TypeDefinition type, string fieldName)
        => type.Fields.Any(f => f.Name == fieldName);

    private static bool ReadsField(MethodDefinition m, string fieldName)
        => m.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Ldfld || i.OpCode == OpCodes.Ldsfld)
            && i.Operand is FieldReference fr && fr.Name == fieldName);

    private static bool WritesField(MethodDefinition m, string fieldName)
        => m.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Stsfld)
            && i.Operand is FieldReference fr && fr.Name == fieldName);

    private static bool Calls(MethodDefinition m, string memberName)
        => m.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && i.Operand is MethodReference mr && mr.Name == memberName);

    [Fact]
    public void TornDown_And_SuppressTeardownOnLoad_FieldsExist()
    {
        var type = LiveNavTestPageType();
        Assert.True(HasField(type, "_tornDown"),
            "LiveNavTestPage must carry a _tornDown flag distinct from _opened (see " +
            "TornDown_IsDistinctFromOpened below for why they must not be the same flag).");
        Assert.True(HasField(type, "_suppressTeardownOnLoad"),
            "LiveNavTestPage must carry a flag suppressing teardown during the page-" +
            "construction-time initial position (MarkOpened / GetPage's own MoveFirst call) -- " +
            "see MoveFirstDuringOpen below.");
    }

    // Loaded(bool) -- the single choke point every navigation primitive (MoveFirst/Next/
    // Previous/Last, GoToBookmark, FindRowFromTableFieldValues via its internal scan) runs
    // through before firing OnAfterGetRecord/OnAfterGetCurrRecord -- must catch NavBaseException
    // specifically (real BC's own teardown, NstDataAccess.Abort(NavBaseException exception),
    // only wraps a genuine AL-catchable error) and, on catch, write _tornDown and call
    // MakeTestPageNotOpenException -- discarding the trigger's own exception rather than
    // propagating it, UNLESS _suppressTeardownOnLoad is set (the initial-open-time exemption).
    [Fact]
    public void Loaded_CatchesOnlyNavBaseExceptionAndTearsDown()
    {
        var type = LiveNavTestPageType();
        var m = Method(type, "Loaded");

        var handler = m.Body.ExceptionHandlers.FirstOrDefault(
            h => h.HandlerType == ExceptionHandlerType.Catch
                 && h.CatchType is { } ct && ct.Name == "NavBaseException");
        Assert.True(handler != null,
            "Loaded(bool) must catch NavBaseException specifically -- a bare `catch` would also " +
            "catch RunnerOutOfScopeException (plain System.Exception, never NavBaseException -- " +
            "see NavDotNetPatches.cs) and a genuine runner NRE, relabelling either as " +
            "\"The TestPage is not open.\" instead of letting it propagate (.claude/rules/" +
            "loud-failures.md).");

        Assert.True(ReadsField(m, "_suppressTeardownOnLoad"),
            "Loaded(bool)'s catch must check _suppressTeardownOnLoad and rethrow unmodified " +
            "during the page-construction-time initial position -- otherwise a swallowed " +
            "first-row failure (MarkOpened's own blanket catch{}) would leave the page silently, " +
            "permanently unusable from the AL test's own point of view.");
        Assert.True(Calls(m, "TearDown"),
            "Loaded(bool)'s catch must call TearDown() (the _tornDown flag paired with BC's detach) " +
            "so later calls on the same TestPage variable refuse instead of silently proceeding.");
        Assert.True(Calls(m, "MakeTestPageNotOpenException"),
            "Loaded(bool)'s catch must construct BC's own not-open exception instead of letting " +
            "the trigger's own error text propagate.");
    }

    // Every other entry point a torn-down TestPage could still reach must refuse the same way --
    // RequireRecord is the single choke point for Move*/GoToBookmark/
    // FindRowFromTableFieldValues/SetFilter/GetFilter/field reads; Close/GetField/GetAction/
    // GetPart/GetBuiltInAction do not route through RequireRecord and need their own guard,
    // because BC's own CheckPageOpened gate (which would normally do this uniformly) is
    // Cecil-neutralised to a no-op for an unrelated reason (NclCecilRewrite.Forms.cs, fix #3).
    [Theory]
    [InlineData("RequireRecord")]
    [InlineData("Close")]
    [InlineData("GetField")]
    [InlineData("GetAction")]
    [InlineData("GetPart")]
    [InlineData("GetBuiltInAction")]
    public void EntryPoint_RefusesWhenTornDown(string methodName)
    {
        var type = LiveNavTestPageType();
        var m = Method(type, methodName);
        Assert.True(ReadsField(m, "_tornDown") && Calls(m, "MakeTestPageNotOpenException"),
            $"'{methodName}' does not guard on _tornDown / MakeTestPageNotOpenException() -- a " +
            "torn-down TestPage would still answer this call instead of refusing it, unlike " +
            "real BC (#2656).");
    }

    // MoveFirstDuringOpen -- the page-construction-time initial positioning wrapper -- must set
    // _suppressTeardownOnLoad around its own call to MoveFirst().
    [Fact]
    public void MoveFirstDuringOpen_SuppressesTeardownAroundMoveFirst()
    {
        var type = LiveNavTestPageType();
        var m = Method(type, "MoveFirstDuringOpen");
        Assert.True(WritesField(m, "_suppressTeardownOnLoad"),
            "MoveFirstDuringOpen() must write _suppressTeardownOnLoad around its MoveFirst() " +
            "call.");
        Assert.True(Calls(m, "MoveFirst"),
            "MoveFirstDuringOpen() must call MoveFirst() -- it is a suppression wrapper, not a " +
            "replacement.");
    }

    // TearDown() pairs the _tornDown flag with MarkDetached(), which is what BC's own guards read: a
    // detached page answers IsOpened() false, so NavTestPageBase.Open() lets the variable open again
    // (#5388), and CheckPageOpened() raises "The TestPage is not open." for every call, including a
    // control read before the teardown (#5390) and Close() -- BC's ALClose is CheckPageOpened() then
    // Close(). Clearing _opened alone makes Close() a silent no-op (measured), so the pairing is the
    // contract; the AL-level proof is TestPageTeardownReopenTests.
    [Fact]
    public void TearDown_PairsTheTornDownFlagWithBcsDetach()
    {
        var type = LiveNavTestPageType();
        var m = Method(type, "TearDown");
        Assert.True(WritesField(m, "_tornDown"), "TearDown() must set _tornDown.");
        Assert.True(Calls(m, "MarkDetached"),
            "TearDown() must detach the page (MarkDetached): without it Open() refuses a torn-down " +
            "variable, and a control read before the teardown keeps answering.");
    }
}
