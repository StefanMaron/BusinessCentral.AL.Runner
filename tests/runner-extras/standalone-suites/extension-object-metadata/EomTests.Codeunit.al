/// AL-level RED→GREEN for #1710 and #1711(b), plus the OptionMembers half of #1711(a).
///
/// The AutoIncrement half of #1711(a) has no AL-level test on purpose: the AL compiler
/// rejects `AutoIncrement` inside a tableextension outright (AL0404 — "Property
/// 'AutoIncrement' is not allowed on a table extension"), so no legal AL can reach it.
/// It is pinned at parser level in AlRunner.Tests/PageExtensionParserTests.cs instead.
codeunit 64227 "EOM Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "EOM Assert";

    /// Leaves exactly ONE row in the table, so `TestPage.First()` lands on it. Tests share the
    /// in-memory table, and First() sorts by the primary key — without this, a row seeded by an
    /// earlier test wins and every assertion reads the wrong record.
    local procedure SeedItem(ItemCode: Code[20]; Note: Text[100])
    var
        Item: Record "EOM Item";
    begin
        Item.DeleteAll();
        Item.Init();
        Item."Code" := ItemCode;
        Item."Description" := 'desc of ' + ItemCode;
        Item."Note" := Note;
        Item.Insert();
    end;

    // ── #1710: page 64223 and pageextension 64223 are different objects ───────

    [Test]
    procedure PageKeepsItsSourceTable_WhenAPageExtensionSharesItsId()
    var
        Card: TestPage "EOM Card";
    begin
        // [GIVEN] page 64223 "EOM Card" (SourceTable = "EOM Item") and, in the same app,
        //         pageextension 64223 "EOM Other Ext" extending a DIFFERENT page
        SeedItem('ITEM-A', 'note-a');

        // [WHEN] the page is opened as a TestPage
        Card.OpenView();
        Assert.IsTrue(Card.First(), 'the page must find the seeded row');

        // [THEN] it is still bound to "EOM Item" and its own controls still resolve.
        // Before the split the pageextension's entry won and brought SourceTableName = ""
        // plus an empty control map, so the page resolved no source table at all.
        Assert.AreEqual('ITEM-A', Card.CodeField.Value(), 'the page''s own control must resolve');
        Assert.AreEqual('desc of ITEM-A', Card.DescField.Value(), 'the second control must resolve');
        Card.Close();
    end;

    [Test]
    procedure PageOfTheOtherObject_IsStillItsOwnPage()
    var
        Other: TestPage "EOM Other";
    begin
        // Opposite direction: fixing the collision must not cost "EOM Other" the control its
        // pageextension 64223 adds. Both pages resolve, each with its own controls.
        SeedItem('ITEM-B', 'note-b');
        Other.OpenView();
        Assert.IsTrue(Other.First(), 'the other page must find the seeded row');
        Assert.AreEqual('ITEM-B', Other.OtherCodeField.Value(), 'its own control must resolve');
        Assert.AreEqual('note-b', Other.OtherNoteField.Value(),
            'the control its pageextension adds must resolve');
        Other.Close();
    end;

    // ── #1711(b): a control added by a pageextension ─────────────────────────

    [Test]
    procedure PageExtensionAddedControl_ResolvesToItsTableField()
    var
        Card: TestPage "EOM Card";
    begin
        // [GIVEN] pageextension 64225 adds `field(NoteField; Rec."Note")` via addlast
        SeedItem('ITEM-C', 'note-c');

        // [WHEN] the test drives that control
        Card.OpenView();
        Assert.IsTrue(Card.First(), 'the page must find the seeded row');

        // [THEN] it reads the row's Note. Before the fix every pageextension stored an empty
        // control map, so this threw "testpage-control-binding — this control is bound
        // neither to a field of the page's source table nor to a page variable".
        Assert.AreEqual('note-c', Card.NoteField.Value(), 'extension-added control must read Rec.Note');
        Card.Close();
    end;

    [Test]
    procedure PageExtensionAddedControl_WritesThroughToTheRecord()
    var
        Item: Record "EOM Item";
        Card: TestPage "EOM Card";
    begin
        SeedItem('ITEM-D', 'before');

        Card.OpenEdit();
        Assert.IsTrue(Card.First(), 'the page must find the seeded row');
        Card.NoteField.SetValue('after');
        Card.Close();

        Item.Get('ITEM-D');
        Assert.AreEqual('after', Item."Note", 'the extension-added control must write to Rec.Note');
    end;

    [Test]
    procedure PageExtensionModifyBlock_DoesNotReKeyTheBaseControl()
    var
        Card: TestPage "EOM Card";
    begin
        // Opposite direction: `modify(DescField)` in pageextension 64225 declares no NEW
        // control, so DescField must still resolve in the BASE page's id space. Keying every
        // control seen inside an extension under the extension's id would break exactly this.
        SeedItem('ITEM-E', 'note-e');
        Card.OpenView();
        Assert.IsTrue(Card.First(), 'the page must find the seeded row');
        Assert.AreEqual('desc of ITEM-E', Card.DescField.Value(), 'modify() must not re-key the control');
        Card.Close();
    end;

    // ── #1711(a): a tableextension's Option field keeps its OptionMembers ────

    [Test]
    procedure TableExtensionOptionField_FormatsItsMemberNames()
    var
        Item: Record "EOM Item";
    begin
        // [GIVEN] tableextension 64221 adds `field(50000; "EOM Status"; Option)` with
        //         OptionMembers = ,Open,Released  (blank member first)
        SeedItem('ITEM-F', 'note-f');
        Item.Get('ITEM-F');

        // [THEN] the member COUNT is right, not just the names. `::Released` compiles to
        // ordinal 2, which only formats as 'Released' if the blank first member is present in
        // the runner's option string too — drop it and ordinal 2 runs off the end. Without
        // the extension field's OptionMembers at all there was no name to format to.
        Item."EOM Status" := Item."EOM Status"::Released;
        Item.Modify();
        Item.Get('ITEM-F');
        Assert.AreEqual('Released', Format(Item."EOM Status"), 'ordinal 2 must format as Released');

        Item."EOM Status" := Item."EOM Status"::Open;
        Assert.AreEqual('Open', Format(Item."EOM Status"), 'ordinal 1 must format as Open');
    end;

    [Test]
    procedure TableExtensionOptionField_RejectsAMemberItDoesNotDeclare()
    var
        Item: Record "EOM Item";
    begin
        // Opposite direction: OptionMembers establishes the member SET, so a name outside it
        // must not evaluate. An empty option string would reject everything, which is why the
        // accepted name is pinned in the same test.
        SeedItem('ITEM-G', 'note-g');
        Item.Get('ITEM-G');

        Assert.IsTrue(not Evaluate(Item."EOM Status", 'Archived'),
            'Evaluate must reject a member the field does not declare');
        Assert.IsTrue(Evaluate(Item."EOM Status", 'Open'),
            'Evaluate must accept a declared member');
        Assert.AreEqual('Open', Format(Item."EOM Status"), 'the accepted member must round-trip');
    end;

    [Test]
    procedure TableExtensionPlainField_KeepsItsTypeDefault()
    var
        Item: Record "EOM Item";
    begin
        // Opposite direction for the parse as a whole: giving extension fields their full
        // property set must not give a field properties it never declared.
        SeedItem('ITEM-H', 'note-h');
        Item.Get('ITEM-H');
        Assert.AreEqual(0, Item."EOM Plain", 'a plain extension field must stay at its type default');
    end;
}
