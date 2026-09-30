// The runner-side half of #5000: which runner call sites run the service tier's view round
// trip (RunnerPageInstance.ReapplyViewSorting), and in which order. The AL-observable claims
// (the order a handler, Trap() or post-action page shows) are measured upstream by corpus
// codeunit 67962 "SPR Tests"; this pins the wiring those claims depend on.
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class ViewSortingRoundTripRouteTests
{
    private static TypeDefinition LoadType(System.Type type)
    {
        var asm = AssemblyDefinition.ReadAssembly(type.Assembly.Location);
        var cecilType = asm.MainModule.GetType(type.FullName);
        Assert.NotNull(cecilType);
        return cecilType!;
    }

    private static MethodDefinition Method(TypeDefinition type, string name)
    {
        var m = type.Methods.SingleOrDefault(x => x.Name == name && x.HasBody);
        Assert.NotNull(m);
        return m!;
    }

    /// <summary>Offsets of every call to <paramref name="memberName"/> in the body, in order.</summary>
    private static int[] CallOffsets(MethodDefinition m, string memberName)
        => m.Body.Instructions
            .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                        && i.Operand is MethodReference mr && mr.Name == memberName)
            .Select(i => i.Offset)
            .ToArray();

    // A handler or trapped page is built here after BC's own OpenForm ran OnOpenPage, and never
    // passes through RaiseOnOpenPage. The round trip must run BEFORE the page is positioned, so
    // the row it opens on is the first row of the reset order — as corpus 67962
    // OpenView_CurrentRow_OnOpenPageSetAscending measures for a page the TestPage opens itself.
    [Fact]
    public void GetPage_ReappliesTheViewSorting_BeforePositioningTheHandlerPage()
    {
        var m = Method(LoadType(typeof(AlRunner.Patches.RunnerTestClientSession)), "GetPage");

        var reapply = CallOffsets(m, "ReapplyViewSorting");
        var moveFirst = CallOffsets(m, "MoveFirstDuringOpen");

        Assert.Single(reapply);
        Assert.Single(moveFirst);
        Assert.True(reapply[0] < moveFirst[0],
            "GetPage must reset the per-field sorting before MoveFirstDuringOpen positions the page; "
            + "positioning first would open a handler page on the per-field order's first row.");
    }

    // An action invoke is a service call: after OnAction the service stores GetTableView() and
    // puts it back before the next call, so a per-field SetAscending made in OnAction is gone for
    // the next row read and the next action (corpus 67962 Action_SetAscending_*).
    [Fact]
    public void ActionInvoke_ReappliesTheViewSorting_AfterOnAction()
    {
        var m = Method(LoadType(typeof(AlRunner.LiveNavTestAction)), "Invoke");

        var raise = CallOffsets(m, "RaiseOnAction");
        var reapply = CallOffsets(m, "ReapplyViewSortingAsTheServiceTierDoes");

        Assert.Single(raise);
        Assert.Single(reapply);
        Assert.True(raise[0] < reapply[0],
            "the round trip belongs AFTER OnAction: the runner's row reads and later calls do not "
            + "run it themselves, so a reset before OnAction leaves the trigger's per-field "
            + "direction in place for them.");
    }

    // BC's own NavRecord.SetTableView order: the key first, then the view's default direction.
    // The direction step is what drops a per-field SetAscending; re-setting the key alone does not.
    [Fact]
    public void ReapplyViewSorting_ReadsTheViewThenSetsKeyThenDirection()
    {
        var m = Method(LoadType(typeof(AlRunner.Patches.RunnerPageInstance)), "ReapplyViewSorting");

        var view = CallOffsets(m, "GetTableView");
        var key = CallOffsets(m, "ALSetCurrentKey");
        var direction = CallOffsets(m, "set_ALAscending");

        Assert.Single(view);
        Assert.Single(key);
        Assert.Single(direction);
        Assert.True(view[0] < key[0] && key[0] < direction[0],
            "ReapplyViewSorting must read GetTableView, then ALSetCurrentKey, then set ALAscending.");
    }
}
