// TestPageNewRowLinePromotionTests — issue #2923.
//
// Root cause: the implicit new-row line of a TestPage was turned into a pending insert AFTER
// the write that triggered it had already validated. LiveNavTestPage.EnterNewRowLine blanks
// the record buffer and clears every primary-key field, and the only promotion step
// (MarkEdited) ran from LiveNavTestField's setter after ALValidateAsync — so the typed field's
// own OnValidate saw a row with no key. On a subpage part carrying a SubPageLink that is
// fatal: Sales Line's first OnValidate reaches TestStatusOpen -> GetSalesHeader ->
// TestField("Document No.") and raises "Document No. must have a value". 35 tests of
// Microsoft's Tests-SMB bucket fail on it, 27 on Sales Line and 8 on Purchase Line.
//
// The second half of the same defect was LiveNavTestPart.ReloadLinkedRow, which left a part
// with no matching row on NO row at all, so a write with no New()/First() of its own had
// nothing to land on.
//
// These are RUNNER-MECHANISM tests, not claims about what real BC does: they pin our own
// wiring — that the promotion happens BEFORE the validate rather than after, that it goes
// through the same InsertEmptyRow entry point New() uses (which is where a part stamps its
// SubPageLink values), and that a linked part with no matching row parks on its draft line.
//
// The BEHAVIORAL claims are measured upstream against a live BC service tier — see
// StefanMaron/BusinessCentral.AL.Language.Tests PR #176, codeunit 60996 "TPDL Tests" in
// tests/al-language/handlers/TestPagePartDraftLineLink.al, per
// .claude/rules/bc-behavior-tests-go-upstream.md. Those five tests went 2 pass / 3 fail
// before this change and 5 pass after, run against the corpus branch.
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageNewRowLinePromotionTests
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

    // Any reference to the named method, whether it is invoked (call/callvirt) or captured as
    // a delegate (ldftn/ldvirtftn). A method group passed as an Action compiles to ldftn, which
    // Calls() below does not see.
    private static bool References(MethodDefinition m, string memberName)
        => m.Body.Instructions.Any(i =>
            i.Operand is MethodReference mr && mr.Name == memberName);

    private static bool Calls(MethodDefinition m, string memberName)
        => m.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && i.Operand is MethodReference mr && mr.Name == memberName);

    // Index of the first instruction loading the named instance field, or -1.
    private static int IndexOfFieldLoad(MethodDefinition m, string fieldName)
    {
        var instructions = m.Body.Instructions;
        for (var i = 0; i < instructions.Count; i++)
            if (instructions[i].OpCode == OpCodes.Ldfld
                && instructions[i].Operand is FieldReference fr && fr.Name == fieldName)
                return i;
        return -1;
    }

    // Index of the first call to the named method, or -1.
    private static int IndexOfCall(MethodDefinition m, string memberName)
    {
        var instructions = m.Body.Instructions;
        for (var i = 0; i < instructions.Count; i++)
            if ((instructions[i].OpCode == OpCodes.Call || instructions[i].OpCode == OpCodes.Callvirt)
                && instructions[i].Operand is MethodReference mr && mr.Name == memberName)
                return i;
        return -1;
    }

    // THE ORDERING CLAIM, and the whole point of the fix. Both callbacks are Action?.Invoke()
    // so they are indistinguishable by call name in IL — the field loads that feed them are
    // what tells them apart, and their positions relative to ALValidateAsync are the defect.
    [Fact]
    public void FieldSetter_PromotesTheNewRowLineBeforeValidatingAndFlagsTheEditAfter()
    {
        var type = LoadType(typeof(AlRunner.LiveNavTestPage));
        var live = type.Module.GetType("AlRunner.LiveNavTestField");
        Assert.NotNull(live);

        var setter = Method(live!, "set_Value");

        var beforeEdit = IndexOfFieldLoad(setter, "_onBeforeEdit");
        var validate = IndexOfCall(setter, "ALValidateAsync");
        var afterEdit = IndexOfFieldLoad(setter, "_onEdited");

        Assert.True(beforeEdit >= 0,
            "LiveNavTestField's Value setter must read _onBeforeEdit — the callback that turns "
            + "the page's implicit new-row line into a started row before this write is validated.");
        Assert.True(validate >= 0,
            "LiveNavTestField's Value setter must call ALValidateAsync — setting a field on a "
            + "page is a validate, not an assignment.");
        Assert.True(afterEdit >= 0,
            "LiveNavTestField's Value setter must read _onEdited — the callback that marks the "
            + "row for persistence once the write has happened.");

        Assert.True(beforeEdit < validate,
            "the _onBeforeEdit callback must run BEFORE ALValidateAsync. Its whole job is to let "
            + "the page start the row (keys stamped from the page's filters, OnNewRecord raised) "
            + "while the typed field's own OnValidate can still see it — issue #2923. Invoking it "
            + $"after the validate would be the defect itself (before={beforeEdit}, validate={validate}).");

        Assert.True(validate < afterEdit,
            "the _onEdited callback must run AFTER ALValidateAsync, as it always has: it records "
            + "that a write happened, which is only true once the write has. It is NOT a "
            + "substitute for _onBeforeEdit — it cannot give the row its keys in time "
            + $"(validate={validate}, after={afterEdit}).");
    }

    // The promotion must reuse the New() entry point, because that is where the SubPageLink
    // stamping lives (LiveNavTestPart.InsertEmptyRow overrides it). A hand-rolled promotion
    // that only flipped the pending-insert flag would pass every other assertion here and
    // still leave a linked part's row without its key.
    [Fact]
    public void PromoteNewRowLineForWrite_GoesThroughTheSameInsertEmptyRowNewUses()
    {
        var type = LoadType(typeof(AlRunner.LiveNavTestPage));
        var m = Method(type, "PromoteNewRowLineForWrite");

        Assert.True(Calls(m, "InsertEmptyRow"),
            "LiveNavTestPage.PromoteNewRowLineForWrite must call InsertEmptyRow — the same entry "
            + "point New() uses, and the one LiveNavTestPart overrides to stamp its SubPageLink "
            + "values onto the primary-key fields. Setting _pendingNewRow directly would start "
            + "the row without its link values, which is the bug.");
    }

    // A part with no matching row must park on its own draft line, the way a top-level page
    // already does from MoveFirst. Without this a write with no New()/First() of its own on a
    // linked part had no row to land on at all.
    [Fact]
    public void ReloadLinkedRow_ParksAnEmptyPartOnItsNewRowLine()
    {
        var type = LoadType(typeof(AlRunner.LiveNavTestPage));
        var part = type.Module.GetType("AlRunner.LiveNavTestPart");
        Assert.NotNull(part);

        var m = Method(part!, "ReloadLinkedRow");

        Assert.True(Calls(m, "EnterNewRowLine"),
            "LiveNavTestPart.ReloadLinkedRow must call EnterNewRowLine when the link matches no "
            + "row, mirroring LiveNavTestPage.MoveFirst's own empty-result fallback — otherwise "
            + "a linked part that opens empty sits on no row and a field write targets nothing.");

        Assert.True(Calls(m, "AbandonNewRowLine"),
            "LiveNavTestPart.ReloadLinkedRow must call AbandonNewRowLine first. It is re-entered "
            + "on every parent move and Loaded() does not clear the flag, so a part that walked "
            + "onto its draft line and then had its parent move would sit on a real row while "
            + "still claiming to be on the blank line — and the next write would insert instead "
            + "of modify.");
    }

    // The draft line is not blank in every column: it carries the page's own single-valued
    // filters on the PRIMARY-KEY fields, which is BC's RecordImplementation.InitRecordFromFilters
    // rule. EnterNewRowLine cleared the key and stopped, so the runner answered blank in a
    // linked part's key column where real BC answers the link's value.
    //
    // Measured on all 8 BC legs, corpus run 33995429394: the original corpus assertion said
    // "the draft line must read blank in the column the SubPageLink constrains" and every leg
    // returned Expected:<> Actual:<H1>. Corrected upstream to
    // LinkedPart_DraftLine_ReadsTheLinkValueInTheLinkedKeyColumn.
    //
    // Reading it off the FILTER rather than off the SubPageLink is what keeps the two
    // neighbouring corpus suites green: CU60743's standalone page has no filters and stays
    // blank, and CU60648's link on a non-key field is never visited because the loop only walks
    // the primary key.
    [Fact]
    public void EnterNewRowLine_PutsThePagesSingleValuedKeyFiltersBackOnTheBlankedBuffer()
    {
        var type = LoadType(typeof(AlRunner.LiveNavTestPage));
        var m = Method(type, "EnterNewRowLine");

        Assert.True(Calls(m, "ClearFieldValue"),
            "EnterNewRowLine must still clear the primary-key fields ALInit preserves — without "
            + "that the draft line reports the key of the row the cursor just walked off.");

        Assert.True(Calls(m, "TryGetSingleFilterValue"),
            "EnterNewRowLine must read each primary-key field's single-valued filter — BC's "
            + "InitRecordFromFilters rule. Clearing the key and stopping made the runner answer "
            + "blank in a linked part's key column where real BC answers the link's value.");

        Assert.True(Calls(m, "SetFieldValue"),
            "EnterNewRowLine must write the filter's value back onto the blanked buffer, not "
            + "merely read it.");

        Assert.False(Calls(m, "ALValidateAsync"),
            "EnterNewRowLine must NOT validate what it copies. That validate belongs to "
            + "NavForm.NewRecordAsync — starting a row — and merely standing on the blank line "
            + "starts nothing (corpus CU60743 NewRowLine_LeftUntouched_InsertsNothing). Running "
            + "OnValidate here would give walking a page side effects.");
    }

    // The wiring between the two: the page has to hand its promotion callback to the fields it
    // builds, or the ordering proven above never runs for any real control.
    [Fact]
    public void GetField_HandsRecBoundControlsThePromotionCallback()
    {
        var type = LoadType(typeof(AlRunner.LiveNavTestPage));
        var m = Method(type, "GetField");

        Assert.True(References(m, "PromoteNewRowLineForWrite"),
            "LiveNavTestPage.GetField must pass PromoteNewRowLineForWrite to every Rec-bound "
            + "LiveNavTestField it constructs (as a method group, which the compiler emits as an "
            + "ldftn) — a field built without it silently keeps the old after-the-validate "
            + "behavior for that control.");
    }
}
