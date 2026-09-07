// TestPageMoveLastNewRowLineTests — issue #2964.
//
// LiveNavTestPage has three cursor moves that LAND ON A ROW by searching for one, rather than
// stepping relative to where the cursor already is: MoveFirst, MoveLast, and the part's
// ReloadLinkedRow. On an editable, insert-allowed page a client that finds no matching row
// still renders exactly one row — the implicit new-row line — so all three have to leave the
// cursor parked there when they find nothing, or a subsequent write has nowhere to land.
//
// MoveFirst got that fallback in #2392 and ReloadLinkedRow in #2923. MoveLast was left out,
// so `TP.OpenEdit(); TP.Last(); TP.SomeField.SetValue('X');` wrote into a record nothing had
// positioned and inserted no row at all.
//
// These are RUNNER-MECHANISM tests, not claims about what real BC does. They pin the
// SYMMETRY of our own wiring — that the three searching moves agree with each other, and that
// the two stepping moves (MoveNext, MovePrevious) deliberately do not join them, because
// stepping off the end of a rowset is a different question from failing to find a row in one.
//
// The BEHAVIORAL claims are measured upstream against a live BC service tier: corpus codeunit
// 60757 "Test Page Last New Row Line" in tests/al-language/handlers/TestPageLastNewRowLine_Tests.al,
// StefanMaron/BusinessCentral.AL.Language.Tests PR #231, per
// .claude/rules/bc-behavior-tests-go-upstream.md. Run against that corpus branch, its twelve
// arms went 10 pass / 2 fail before this change and 12 pass after — the two failures being
// EmptyEditableList_SetValueAfterLast_InsertsARow and its linked-part twin
// ModalHostPart_EmptyPart_SetValueAfterLast_InsertsARow, one per code path.
//
// Cecil rather than a live page because the claim is about which IL each method contains: a
// behavioural test of the same thing is the corpus's job, and duplicating it here would be
// asserting BC behaviour in the wrong repository.
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageMoveLastNewRowLineTests
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
        var m = type.Methods.FirstOrDefault(x => x.Name == name && x.HasBody);
        Assert.NotNull(m);
        return m!;
    }

    private static bool Calls(MethodDefinition m, string memberName)
        => m.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && i.Operand is MethodReference mr && mr.Name == memberName);

    private static int IndexOfCall(MethodDefinition m, string memberName)
    {
        var instructions = m.Body.Instructions;
        for (var i = 0; i < instructions.Count; i++)
            if ((instructions[i].OpCode == OpCodes.Call || instructions[i].OpCode == OpCodes.Callvirt)
                && instructions[i].Operand is MethodReference mr && mr.Name == memberName)
                return i;
        return -1;
    }

    // THE FIX. MoveLast must park on the draft line when its find comes back empty, exactly as
    // MoveFirst does. Without EnterNewRowLine in the body there is no cursor for a following
    // SetValue to write into.
    [Fact]
    public void MoveLast_FallsOntoTheNewRowLineWhenItFindsNothing()
    {
        var type = LoadType(typeof(AlRunner.LiveNavTestPage));
        var moveLast = Method(type, "MoveLast");

        Assert.True(Calls(moveLast, "ALFindLastAsync"),
            "LiveNavTestPage.MoveLast must still locate the last row with ALFindLastAsync — "
            + "the fallback is for the empty case only, and must not replace the search.");

        Assert.True(Calls(moveLast, "EnterNewRowLine"),
            "LiveNavTestPage.MoveLast must call EnterNewRowLine so that a Last() finding no row "
            + "on an editable, insert-allowed page leaves the cursor on the implicit new-row "
            + "line — otherwise a following SetValue writes into a record nothing positioned "
            + "and inserts nothing (issue #2964). MoveFirst has done this since #2392.");
    }

    // THE SYMMETRY, which is the actual defect: MoveLast differed from MoveFirst for no
    // reason anyone had measured. Asserting the pair together is what stops the two drifting
    // apart again — a future edit that drops the fallback from either one fails here.
    [Fact]
    public void TheThreeSearchingCursorMoves_AllParkOnTheNewRowLineWhenTheyFindNothing()
    {
        var page = LoadType(typeof(AlRunner.LiveNavTestPage));
        var part = page.Module.GetType("AlRunner.LiveNavTestPart");
        Assert.NotNull(part);

        foreach (var (declaring, name, why) in new[]
        {
            (page, "MoveFirst", "First() on an empty editable list — issue #2392"),
            (page, "MoveLast", "Last() on an empty editable list — issue #2964"),
            (part!, "ReloadLinkedRow", "a linked part whose SubPageLink matches no row — issue #2923"),
        })
        {
            var m = Method(declaring, name);
            Assert.True(Calls(m, "EnterNewRowLine"),
                $"{declaring.Name}.{name} must call EnterNewRowLine: it locates a row by "
                + "searching, so finding nothing on an editable, insert-allowed page has to "
                + $"leave the cursor on the implicit new-row line ({why}). All three searching "
                + "cursor moves must agree — the asymmetry between them WAS issue #2964.");
        }
    }

    // The fallback must not change what Last() ANSWERS, only where the cursor sits. The
    // return value is computed from the find's own result, so EnterNewRowLine's return value
    // must be discarded (pop) rather than returned.
    //
    // This is load-bearing for FindRowFromTableFieldValues, whose backward scan starts at
    // MoveLast() and enters `while (hasRow)` only on true: a MoveLast that answered true for
    // the draft line would let a search for an empty value "find" a row that is not in the
    // table.
    [Fact]
    public void MoveLast_StillAnswersFromTheFind_NotFromTheFallback()
    {
        var type = LoadType(typeof(AlRunner.LiveNavTestPage));
        var moveLast = Method(type, "MoveLast");

        var enter = IndexOfCall(moveLast, "EnterNewRowLine");
        var loaded = IndexOfCall(moveLast, "Loaded");

        Assert.True(enter >= 0, "MoveLast must call EnterNewRowLine (see the arm above).");
        Assert.True(loaded >= 0,
            "MoveLast must return through Loaded(bool), which is what raises the row's "
            + "OnAfterGetRecord for the found case.");
        Assert.True(enter < loaded,
            "EnterNewRowLine must run BEFORE the Loaded() that produces the return value, so "
            + "the answer comes from the find and not from the fallback "
            + $"(enter={enter}, loaded={loaded}).");

        Assert.True(
            moveLast.Body.Instructions.Any(i => i.OpCode == OpCodes.Pop),
            "EnterNewRowLine returns bool, and MoveLast must DISCARD it — Last() answers false "
            + "on an empty editable page whether or not the draft line exists. Returning the "
            + "fallback's own result would make Last() answer true there, and would let "
            + "FindRowFromTableFieldValues's backward scan match the blank line.");
    }

    // THE NEGATIVE. MoveNext and MovePrevious step relative to the current row rather than
    // searching for one, and must NOT gain the fallback: MoveNext reaching the end of the
    // rowset is how the draft line is ENTERED (a different, already-measured path), and
    // MovePrevious stepping back off the first row must leave the cursor where it is
    // (corpus codeunit 60756 TestPage_Previous_OnTheFirstRow_ReturnsFalseAndKeepsTheCursorThere).
    //
    // Without this arm, "call EnterNewRowLine everywhere" would satisfy every assertion above.
    [Fact]
    public void MovePrevious_DoesNotParkOnTheNewRowLine()
    {
        var type = LoadType(typeof(AlRunner.LiveNavTestPage));
        var movePrevious = Method(type, "MovePrevious");

        Assert.False(Calls(movePrevious, "EnterNewRowLine"),
            "LiveNavTestPage.MovePrevious must NOT call EnterNewRowLine. Previous() steps "
            + "backwards from the current row; it never searches, so it has no not-found case "
            + "to fall back from. Stepping back off the first row leaves the cursor on that "
            + "row (corpus codeunit 60756), and parking it on a blank line instead would "
            + "silently move the page somewhere the test never navigated to.");

        Assert.True(Calls(movePrevious, "LeaveNewRowLine"),
            "MovePrevious must still LEAVE the draft line when it is parked on one — stepping "
            + "back off the blank line lands on the last data row.");
    }
}
