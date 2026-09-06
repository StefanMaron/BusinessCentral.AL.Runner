// Issue #3071. Object (2000000001) on the runner: what it holds, and what stays true about it
// now that it holds nothing.
//
// WHAT A REAL TIER SAID, AND WHY THAT IS NOT WHAT THIS SUITE ASSERTS
//   This suite used to pin a row set: the runner projected its own object inventory — the one
//   AllObj (2000000038) is answered from — into this table's column shape, and five tests here
//   asserted that projection and the blanks it left behind (#2774).
//
//   A service tier has since measured the table. Corpus codeunit 61202,
//   tests/al-language-onprem/record/TestObjectSystemTable.al
//   (StefanMaron/BusinessCentral.AL.Language.Tests#197), reads 2000000001 from a Target = OnPrem
//   app and finds it present, readable and EMPTY — on every BC OnPrem leg that executed it,
//   seven of the corpus's eight. Its centerpiece carries a control arm reading the populated
//   sibling "Object Metadata" in the same session, so "empty" cannot be an unreadable table
//   misreported as an empty one.
//
//   THAT CLAIM IS NOT REPEATED HERE. "The legacy Object registry holds no rows" is plain BC
//   behaviour and belongs upstream, where a tier adjudicates it every time the corpus runs
//   (.claude/rules/bc-behavior-tests-go-upstream.md). Copying it down into a runner suite would
//   turn a measured fact into the runner agreeing with itself — which is how the two stale
//   assertions #3066 found survived.
//
// WHAT IS RUNNER-SPECIFIC, AND IS THEREFORE WHAT THESE THREE TESTS PIN
//   1. THE POLICY, NOT THE ROW COUNT. The runner has no application database, so an empty
//      2000000001 could equally be "correct" or "we never got round to it". The distinction is
//      visible in ONE session: AllObj is a table the runner DOES project, from the very
//      inventory this table used to be projected from, and it still lists these objects. Object
//      being empty WHILE AllObj is full is a statement about which tables the runner
//      synthesises for — the same shape as 61202's control arm, and the arm that fails if the
//      projection is ever reinstated.
//   2. AN EMPTY TABLE IS STILL A READABLE ONE. All four DataAccess request paths must ANSWER,
//      not refuse: keyed Get, find, count, and IsEmpty — which RecordImplementation.IsEmptyAsync
//      serves from its own ExistsAsync rather than from CountAsync, so a change that handles
//      three of them and forgets the fourth is green until somebody writes the line. #2519 is
//      the trap: refusing at row-build time takes out FindSet / Count / IsEmpty / Get together.
//   3. THE OemText COLUMNS STAY READABLE. Microsoft's own AL compiler treats four of this
//      table's Text[n] columns as OemText (CodeGenerator.IsOemTextFieldOnObjectTable), and the
//      runner mirrors that when it builds the metatable. Get the metadata wrong and the read
//      throws NavObjectDefinitionChangedException — it does NOT return a wrong value. With the
//      row projection gone that predicate has no rows left to protect, so nothing else would
//      notice if it were deleted as dead; this is the test that would.
//
// Target is OnPrem, not Cloud: Microsoft declares this table Scope = OnPrem, so a Cloud-target
// app fails AL0296 on `Record "Object"` before any of this runs.
codeunit 65551 "OST Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "OST Assert";

    [Test]
    procedure Object_IsEmpty_WhileAllObj_StillListsTheSameObjects()
    var
        Obj: Record "Object";
        AllObj: Record AllObj;
    begin
        // ── The control arm comes FIRST, for the same reason 61202's does. ────────────────
        // AllObj is projected from EnumerateKnownAlObjects — the inventory Object's rows used
        // to be projected from too. If this arm fails, the run has no object inventory at all
        // and the emptiness below would prove nothing.
        Assert.IsTrue(
            AllObj.Get(AllObj."Object Type"::Codeunit, 65551),
            'CONTROL ARM: AllObj must list codeunit 65551, the test codeunit running this line.');
        Assert.AreEqual(
            'OST Tests', AllObj."Object Name",
            'CONTROL ARM: AllObj must carry this codeunit''s real name, not a placeholder.');

        // ── The claim: the same object, named the same way, is absent from Object. ────────
        // Concrete ids of three different kinds, so a partial reinstatement of the projection
        // (one kind, or one ordinal for everything) fails here rather than passing.
        Assert.IsFalse(
            Obj.Get(Obj.Type::Codeunit, '', 65551),
            'Object must not list codeunit 65551; the runner synthesises no rows for 2000000001.');
        Assert.IsFalse(
            Obj.Get(Obj.Type::Table, '', 2000000001),
            'Object must not list table 2000000001, its own table id.');
        Assert.IsFalse(
            Obj.Get(Obj.Type::Table, '', 18),
            'Object must not list table 18 (Customer); the emptiness is not only about this bundle.');
    end;

    [Test]
    procedure EmptyObject_StillAnswersOnAllFourRequestPaths()
    var
        Obj: Record "Object";
        AllObj: Record AllObj;
    begin
        // Every line below would ERROR rather than FAIL if the table had been made empty by
        // refusing at row-build time (#2519) instead of by having nothing to build. That is the
        // whole point of naming all four paths: they are four different DataAccess entry
        // points, and #2519 took out all four at once.

        // ── count (CountAsync) ────────────────────────────────────────────────────────────
        Assert.AreEqual(0, Obj.Count(), 'Object.Count must answer 0 on the unfiltered table.');

        // ── IsEmpty (ExistsAsync — a fourth path, not a spelling of Count) ────────────────
        Assert.IsTrue(Obj.IsEmpty(), 'Object.IsEmpty must answer true, not raise.');

        // ── find (InnerFindAsync), unfiltered and filtered ────────────────────────────────
        Assert.IsFalse(Obj.FindSet(), 'Object.FindSet must answer false on the unfiltered table.');
        Obj.SetRange(ID, 65551);
        Assert.IsFalse(Obj.FindFirst(), 'Object.FindFirst must answer false under a filter too.');
        Assert.AreEqual(0, Obj.Count(), 'Object.Count must answer 0 under a filter.');

        // ── keyed Get (InternalTryGetByPrimaryKeyAsync) ───────────────────────────────────
        Obj.Reset();
        Assert.IsFalse(Obj.Get(Obj.Type::Table, '', 18), 'Object.Get must answer false, not raise.');

        // Control: the SAME four paths on AllObj, in the same session, do find rows. Without
        // this, every assertion above would also pass against a runner whose record layer had
        // stopped answering anything at all.
        Assert.IsFalse(AllObj.IsEmpty(), 'CONTROL ARM: AllObj.IsEmpty must be false.');
        Assert.IsTrue(AllObj.FindSet(), 'CONTROL ARM: AllObj.FindSet must find rows.');
        Assert.IsTrue(AllObj.Count() > 0, 'CONTROL ARM: AllObj.Count must be positive.');
        Assert.IsTrue(
            AllObj.Get(AllObj."Object Type"::Codeunit, 65551),
            'CONTROL ARM: AllObj.Get must still find codeunit 65551.');
    end;

    [Test]
    procedure OemTextColumns_ReadAsEmptyText_RatherThanRaising()
    var
        Obj: Record "Object";
    begin
        // Field numbers 2, 4, 12 and 50 — "Company Name", "Name", "Version List", "Locked By".
        // The table declares them Text[n]; Microsoft's AL compiler emits
        // ValidateExpectedType(fieldNo, NavType.OemText) for reads of them, because
        // CodeGenerator.IsOemTextFieldOnObjectTable substitutes NavTypeKind.OemText for this one
        // table id. If the runner's metatable took SymbolReference.json's Text[30] at face
        // value, each of these lines would raise NavObjectDefinitionChangedException — "old
        // type: OemText, new type: Text" — and NOT return a wrong value.
        //
        // The read happens on an un-found record, which is the only kind there is here. That
        // still exercises the compiled read path: ValidateExpectedType runs on the field access,
        // not on the row lookup.
        Assert.IsFalse(Obj.Get(Obj.Type::Codeunit, '', 65551), 'precondition: no row for 65551.');

        Assert.AreEqual('', Obj.Name, 'field 4 "Name" must read as empty text, not raise.');
        Assert.AreEqual('', Obj."Company Name", 'field 2 "Company Name" must read as empty text, not raise.');
        Assert.AreEqual('', Obj."Version List", 'field 12 "Version List" must read as empty text, not raise.');
        Assert.AreEqual('', Obj."Locked By", 'field 50 "Locked By" must read as empty text, not raise.');

        // A non-OemText column of the same table, so this test cannot pass merely because every
        // read of anything returns the empty string.
        Assert.AreEqual(0, Obj.ID, 'field 3 "ID" is an Integer and must read 0.');
        Assert.IsFalse(Obj.Compiled, 'field 6 "Compiled" is a Boolean and must read false.');
    end;
}
