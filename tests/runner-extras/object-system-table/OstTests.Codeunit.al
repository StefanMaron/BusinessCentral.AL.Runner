// Issue #2774. Object (2000000001) on the runner: where the rows come from when there is no
// application database, and what the columns the runner cannot answer read.
//
// WHY THIS IS A RUNNER TEST AND NOT A CORPUS TEST — AND WHAT IS THEREFORE UNVERIFIED
//   What Object CONTAINS on a real tier is plain BC behaviour and BELONGS upstream. It cannot
//   go there, for exactly the reason its sibling Object Metadata (2000000071) could not:
//
//     * The corpus app targets Cloud (tests/al-language/tests/al-language/app.json,
//       "target": "Cloud") and Microsoft declares this table Scope = OnPrem, so
//       `Record "Object"` does not compile there (AL0296).
//     * The RecordRef escape hatch is refused at RUNTIME too. 2000000001 is a member of
//       Microsoft.Dynamics.Nav.Types.SystemTables.InternalTables (read off the shipped
//       Types assembly), and NavRecordRef.IsSystemTableAllowedForRecordRefUsage returns
//       false for every id in that set, so NavRecordRef.CheckIsOpenAllowed throws
//       "You cannot open record ... when you are using target Cloud".
//
//   That refusal was MEASURED on the sibling id, not reasoned about: corpus PR
//   StefanMaron/BusinessCentral.AL.Language.Tests#153 tried the RecordRef route for
//   2000000071 and was withdrawn after all 8 BC legs of run 33968379281 refused it. The
//   mechanism is set membership in the one FrozenSet, and 2000000001 is in the same set,
//   so the same PR against this id would be refused the same way.
//
//   SO NO SERVICE TIER HAS CONFIRMED THE ROW SET THIS SUITE OBSERVES, and the assertions
//   below are about what the RUNNER does, which is what a runner suite is for.
//
//   What IS runner-specific, and what these tests actually pin: on a real tier these rows
//   exist because something wrote them into a SQL table in the application database. The
//   runner has no application database and never publishes anything, so it projects its own
//   object inventory — the same one AllObj (2000000038) and AllObjWithCaption (2000000058)
//   are answered from — into Object's column shape. "The rows are there with no database
//   behind them, and Object and AllObj cannot disagree about which objects exist" is a claim
//   about the runner.
//
//   The columns with no runner source are left at BC's own default rather than fabricated.
//   That is a DECLARED divergence, recorded in docs/limitations.md and asserted below so it
//   cannot change quietly; issue #2771 tracks making such columns refuse by name instead.
codeunit 65551 "OST Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "OST Assert";

    [Test]
    procedure RowSet_ListsObjectsTheRunnerKnows_WithNoApplicationDatabase()
    var
        Obj: Record "Object";
    begin
        // The runner has no SQL, no publish step and no restored backup here, yet the objects
        // it compiled moments ago are listed. That is the whole fix for #2774: before it this
        // table was empty, so every read of it silently answered "no such object".
        //
        // Asserting the concrete id AND the concrete name is what makes this fail against a
        // provider that emits one placeholder row, or that leaves Name at its default.
        Assert.IsTrue(
            Obj.Get(Obj.Type::Codeunit, '', 65551),
            'Object must have a row for codeunit 65551, the test codeunit running this line.');
        Assert.AreEqual('OST Tests', Obj.Name, 'Object.Name must be the object''s real name.');
        Assert.AreEqual(65551, Obj.ID, 'Object.ID must be the object''s real id.');
        Assert.AreEqual('', Obj."Company Name", 'Object."Company Name" must be blank for an application object.');

        // A second kind, so a provider that maps every object to one option ordinal fails.
        Assert.IsTrue(
            Obj.Get(Obj.Type::Table, '', 2000000001),
            'Object must have a row for table 2000000001, its own table id.');
        Assert.AreEqual('Object', Obj.Name, 'Object.Name for table 2000000001 must be "Object".');
    end;

    [Test]
    procedure Key_TypeIsPartOfTheKey_AndUnknownObjectsAreAbsent()
    var
        Obj: Record "Object";
    begin
        // Anchor first, so this test cannot pass vacuously against an EMPTY table — which is
        // exactly what it did before the fix, and what every negative-only test does.
        Assert.IsTrue(
            Obj.Get(Obj.Type::Codeunit, '', 65551),
            'Object must list codeunit 65551 before any of the negatives below mean anything.');

        // Negative arm 1: 65551 IS a codeunit and is NOT a table. A provider that ignored
        // Type — or wrote one ordinal for every object — would answer true here.
        Assert.IsFalse(
            Obj.Get(Obj.Type::Table, '', 65551),
            'Object must not list 65551 as a table; it is a codeunit.');

        // Negative arm 2: an id nothing in this run declares, of a kind that does exist.
        Assert.IsFalse(
            Obj.Get(Obj.Type::Codeunit, '', 65559),
            'Object must not list codeunit 65559; no object with that id exists in this run.');

        // Negative arm 3: a non-blank company name. Every row the runner projects is
        // company-independent, so naming a company must not find one.
        Assert.IsFalse(
            Obj.Get(Obj.Type::Codeunit, 'CRONUS', 65551),
            'Object must not answer a company-qualified key; the rows carry a blank company.');
    end;

    [Test]
    procedure KindsTheTypeOptionCannotName_GetNoRow()
    var
        Obj: Record "Object";
    begin
        // Object's own "Type" option is TableData,Table,,Report,,Codeunit,XMLport,MenuSuite,
        // Page,Query,System,FieldNumber — there is no Enum member. Enum 65552 is in the same
        // inventory AllObj is answered from, so a mapping that matched by NAME skips it and a
        // mapping that invented an ordinal (0, or "the next one") would emit a row.
        //
        // Anchored on a neighbouring id from the SAME id range and the same bundle, so an
        // empty table fails this test rather than passing it: 65551 is a codeunit Object can
        // name, 65552 is an enum it cannot, and both were compiled by the same run.
        Obj.SetRange(ID, 65551);
        Assert.AreEqual(1, Obj.Count(), 'Object must list codeunit 65551 exactly once.');

        Obj.SetRange(ID, 65552);
        Assert.AreEqual(
            0, Obj.Count(),
            'Object must not list enum 65552 under any Type; its "Type" option cannot name an enum.');
    end;

    [Test]
    procedure ObjectAndAllObj_AgreeAboutWhichObjectsExist()
    var
        Obj: Record "Object";
        AllObj: Record AllObj;
    begin
        // Both tables are projected from the ONE runner inventory, which is the reason to add
        // Object as a projection rather than build a second registry. Checked in both
        // directions on the same concrete ids so a divergence in either shows up.
        Assert.IsTrue(
            AllObj.Get(AllObj."Object Type"::Codeunit, 65551),
            'AllObj must list codeunit 65551.');
        Assert.IsTrue(
            Obj.Get(Obj.Type::Codeunit, '', 65551),
            'Object must list every codeunit AllObj lists.');
        Assert.AreEqual(
            AllObj."Object Name", Obj.Name,
            'Object and AllObj must report the same name for the same object.');

        Assert.IsFalse(
            AllObj.Get(AllObj."Object Type"::Codeunit, 65559),
            'AllObj must not list codeunit 65559.');
        Assert.IsFalse(
            Obj.Get(Obj.Type::Codeunit, '', 65559),
            'Object must not list a codeunit AllObj does not list.');
    end;

    [Test]
    procedure ColumnsWithNoRunnerSource_ReadBlank_DeclaredDivergence()
    var
        Obj: Record "Object";
    begin
        // On a real tier these carry what the object registry stored: the object's compiled
        // BLOB, its date/time stamp, its version list, its caption, its lock state. The runner
        // has no such registry, so it leaves them at BC's own default rather than fabricating
        // one. Asserting the exact blanks is what makes that a DECLARED divergence rather than
        // a silent one — see docs/limitations.md and issue #2771.
        Assert.IsTrue(Obj.Get(Obj.Type::Codeunit, '', 65551), 'Object must have a row for codeunit 65551.');

        Assert.IsFalse(Obj.Modified, '"Modified" must read false.');
        Assert.IsFalse(Obj.Compiled, '"Compiled" must read false.');
        Assert.AreEqual(0, Obj."BLOB Size", '"BLOB Size" must read 0.');
        Assert.AreEqual(0, Obj."DBM Table No.", '"DBM Table No." must read 0.');
        Assert.AreEqual(0D, Obj.Date, '"Date" must read 0D.');
        Assert.AreEqual(0T, Obj.Time, '"Time" must read 0T.');
        Assert.AreEqual('', Obj."Version List", '"Version List" must read empty.');
        Assert.AreEqual('', Obj.Caption, '"Caption" must read empty.');
        Assert.IsFalse(Obj.Locked, '"Locked" must read false.');
        Assert.AreEqual('', Obj."Locked By", '"Locked By" must read empty.');

        Obj.CalcFields("BLOB Reference");
        Assert.IsFalse(Obj."BLOB Reference".HasValue(), '"BLOB Reference" must carry no payload.');
    end;
}
