// TestPageOpenNewPartCatchUpTests — issue #4576.
//
// A part built while its host was opened with OpenNew() saw a parent with no row, so
// LiveNavTestPart.ReloadLinkedRow entered no draft line (#3029), and nothing positioned the
// part again once the header was inserted or its key typed. The first write then reached an
// unlinked Init() buffer: Purchase Line's Type OnValidate raised "Document No. must have a value".
//
// RUNNER-MECHANISM tests: they pin our wiring — that a write catches the part up with its parent
// row BEFORE the draft-line promotion reads its state, for a Rec-bound control and for a
// page-variable control alike. The BC behaviour is measured upstream, corpus codeunit 60868
// "ONPL Tests" (StefanMaron/BusinessCentral.AL.Language.Tests#406).
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageOpenNewPartCatchUpTests
{
    private static TypeDefinition LoadType(string fullName)
    {
        var asm = AssemblyDefinition.ReadAssembly(typeof(AlRunner.LiveNavTestPage).Assembly.Location);
        var cecilType = asm.MainModule.GetType(fullName);
        Assert.NotNull(cecilType);
        return cecilType!;
    }

    private static MethodDefinition Method(TypeDefinition type, string name)
    {
        var m = type.Methods.FirstOrDefault(x => x.Name == name && x.HasBody);
        Assert.NotNull(m);
        return m!;
    }

    private static int IndexOfCall(MethodDefinition m, string memberName)
    {
        var instructions = m.Body.Instructions;
        for (var i = 0; i < instructions.Count; i++)
            if ((instructions[i].OpCode == OpCodes.Call || instructions[i].OpCode == OpCodes.Callvirt)
                && instructions[i].Operand is MethodReference mr && mr.Name == memberName)
                return i;
        return -1;
    }

    private static int IndexOfField(MethodDefinition m, OpCode op, string fieldName)
    {
        var instructions = m.Body.Instructions;
        for (var i = 0; i < instructions.Count; i++)
            if (instructions[i].OpCode == op
                && instructions[i].Operand is FieldReference fr && fr.Name == fieldName)
                return i;
        return -1;
    }

    private static bool References(MethodDefinition m, string memberName)
        => m.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == memberName);

    [Fact]
    public void RecBoundWrite_CatchesThePartUpBeforeReadingTheDraftLineState()
    {
        var promote = Method(LoadType("AlRunner.LiveNavTestPage"), "PromoteNewRowLineForWrite");
        var catchUp = IndexOfCall(promote, "CatchUpPartWithParentRowForWrite");
        var draft = IndexOfField(promote, OpCodes.Ldfld, "_onNewRowLine");

        Assert.True(catchUp >= 0,
            "PromoteNewRowLineForWrite must catch a part up with its parent row — a part opened under an "
            + "OpenNew header has no draft line to promote otherwise (#4576).");
        Assert.True(draft >= 0, "PromoteNewRowLineForWrite must still gate on _onNewRowLine.");
        Assert.True(catchUp < draft,
            "the catch-up must run BEFORE _onNewRowLine is read: it is what puts the part on its draft line.");
    }

    [Fact]
    public void PageVariableWrite_RunsBeforeWriteAheadOfTheValueAndValidate()
    {
        var field = LoadType("AlRunner.PageVariableTestField");
        var setter = Method(field, "set_Value");
        var before = IndexOfCall(setter, "Invoke");
        var core = IndexOfCall(setter, "SetValueCore");

        Assert.True(before >= 0,
            "PageVariableTestField's setter must invoke BeforeWrite — the Purchase Invoice subform's "
            + "FilteredTypeField is page-variable-bound and its OnValidate writes Rec (#4576).");
        Assert.True(core >= 0, "PageVariableTestField's setter must perform the write through SetValueCore.");
        Assert.True(before < core, "BeforeWrite must run before the value is written and validated.");
    }

    [Fact]
    public void LivePage_WiresPageVariableFieldsToThePartCatchUp()
    {
        var getField = Method(LoadType("AlRunner.LiveNavTestPage"), "GetField");
        Assert.True(References(getField, "CatchUpPartWithParentRowForWrite"),
            "LiveNavTestPage.GetField must hand CatchUpPartWithParentRowForWrite to a page-variable control.");
        Assert.True(References(getField, "set_BeforeWrite"),
            "LiveNavTestPage.GetField must set the page-variable control's BeforeWrite.");
    }

    [Fact]
    public void Part_RemembersAParentWithNoRow_AndReloadsOnceItHasOne()
    {
        var part = LoadType("AlRunner.LiveNavTestPart");

        Assert.True(IndexOfField(Method(part, "ReloadLinkedRow"), OpCodes.Stfld, "_awaitingParentRow") >= 0,
            "ReloadLinkedRow must record whether it found the parent with no row.");

        var catchUp = Method(part, "CatchUpWithParentRow");
        Assert.True(IndexOfField(catchUp, OpCodes.Ldfld, "_awaitingParentRow") >= 0,
            "CatchUpWithParentRow must only act on a part left waiting for its parent row.");
        Assert.True(IndexOfCall(catchUp, "HasCurrentRow") >= 0,
            "CatchUpWithParentRow must only reload once the parent has a row.");
        Assert.True(IndexOfCall(catchUp, "ReloadLinkedRow") >= 0,
            "CatchUpWithParentRow must reposition the part through ReloadLinkedRow, which enters its draft line.");

        // Explicit navigation takes over: a First() on the part must not be undone by a later catch-up.
        foreach (var move in new[] { "MoveFirst", "MoveLast", "MoveNext", "MovePrevious" })
            Assert.True(IndexOfField(Method(part, move), OpCodes.Stfld, "_awaitingParentRow") >= 0,
                $"LiveNavTestPart.{move} must clear _awaitingParentRow.");
    }
}
