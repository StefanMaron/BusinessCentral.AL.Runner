// TestPageImplicitPositioningBindingTests — issue #2392.
//
// Root cause: a TestPage a [PageHandler]/[ModalPageHandler] receives (built by
// RunnerTestClientSession.GetPage) and a TestPage a test opens itself (positioned via
// RunnerTestPageState.MarkOpened) were both left sitting on NO current record when their
// view matched zero rows — even on a page whose new-row line a real client would already be
// showing. AL that writes a field with no New()/First() of its own (Base App's
// ApprovalCommentsHandler on codeunit 1535's "Approval Comments" page is the shape that
// surfaced it) wrote into a record nothing had ever positioned, and BC's own OnInsert/OnModify
// machinery then reported "does not exist" against Entry No. 0.
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it proves our own
// wiring — that both page-construction sites call LiveNavTestPage.MoveFirst() when the page
// has a record, and that MoveFirst() itself still falls onto the implicit new-row line on an
// empty result (mirroring MoveNext() past the last data row) rather than just refusing.
//
// The BEHAVIORAL claim ("a field write with no prior New()/First() on an empty editable,
// insert-allowed TestPage still creates a row") is proven upstream against a live BC service
// tier on all 8 supported versions — see StefanMaron/BusinessCentral.AL.Language.Tests PR #96,
// "EmptyEditableList_SetValueWithoutNewOrFirst_InsertsARow" in
// tests/al-language/handlers/TestPageNewRowLine_Tests.al (codeunit 60743), per
// docs/rules/bc-behavior-tests-go-upstream.md.
using System.Linq;
using AlRunner.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageImplicitPositioningBindingTests
{
    private static MethodDefinition Load(System.Type declaringType, string methodName)
    {
        var path = declaringType.Assembly.Location;
        var asm = AssemblyDefinition.ReadAssembly(path);
        var type = asm.MainModule.GetType(declaringType.FullName);
        Assert.NotNull(type);
        var m = type!.Methods.FirstOrDefault(x => x.Name == methodName && x.HasBody);
        Assert.NotNull(m);
        return m!;
    }

    private static bool Calls(MethodDefinition m, string memberName)
        => m.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && i.Operand is MethodReference mr && mr.Name == memberName);

    // RunnerTestClientSession.GetPage is what BC hands a [PageHandler]/[ModalPageHandler] its
    // TestPage through — the construction site the ApprovalCommentsHandler failure actually
    // went through, since it never calls TestPage.Trap() itself.
    [Fact]
    public void GetPage_PositionsTheNewlyBuiltLiveNavTestPage()
    {
        var m = Load(typeof(RunnerTestClientSession), "GetPage");
        Assert.True(Calls(m, "MoveFirst"),
            "RunnerTestClientSession.GetPage must call LiveNavTestPage.MoveFirst() so a page " +
            "handed to a [PageHandler]/[ModalPageHandler] is positioned the moment it is built, " +
            "the same way a real client positions a page the instant it opens.");
    }

    // RunnerTestPageState.MarkOpened is the twin construction site: a TestPage the AL TEST
    // opens itself (OpenEdit/OpenView/Trap()+Run()), not one BC hands to a handler.
    [Fact]
    public void MarkOpened_PositionsAPageOpenedInEditOrViewMode()
    {
        var m = Load(typeof(RunnerTestPageState), "MarkOpened");
        Assert.True(Calls(m, "MoveFirst"),
            "RunnerTestPageState.MarkOpened must call LiveNavTestPage.MoveFirst() for a page " +
            "opened in Edit/View mode, mirroring the Create-mode branch's InsertEmptyRow call " +
            "just above it — otherwise a page a TEST opens itself sits on no row until the AL " +
            "explicitly navigates, same defect as the [PageHandler] construction site.");
    }

    // The fallback itself: an empty MoveFirst() must not just refuse — it must land on the
    // implicit new-row line as a side effect, exactly like MoveNext() past the last data row,
    // or the two call sites above would be positioning onto nothing.
    [Fact]
    public void MoveFirst_FallsOntoTheNewRowLineOnAnEmptyResult()
    {
        var type = typeof(AlRunner.LiveNavTestPage);
        var path = type.Assembly.Location;
        var asm = AssemblyDefinition.ReadAssembly(path);
        var cecilType = asm.MainModule.GetType(type.FullName);
        Assert.NotNull(cecilType);
        var m = cecilType!.Methods.First(x => x.Name == "MoveFirst" && x.HasBody
            && x.Parameters.Count == 0);
        Assert.True(Calls(m, "EnterNewRowLine"),
            "LiveNavTestPage.MoveFirst() must call EnterNewRowLine on an empty find, the same " +
            "fallback MoveNext() already uses past the last data row — without it, positioning " +
            "an empty page still leaves nothing for a field write to land on.");
    }
}
