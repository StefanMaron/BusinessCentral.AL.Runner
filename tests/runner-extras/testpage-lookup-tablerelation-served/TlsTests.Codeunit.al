// Issue #3518 -- the runner-specific half of the TableRelation-lookup contract.
//
// AL puts a lookup in three places: a page control's
// `trigger OnLookup(var Text: Text): Boolean`, a table field's unrelated parameterless
// `trigger OnLookup()`, and -- when neither exists -- the field's TableRelation, which opens
// the related table's lookup page. The first two already run here (issue #2549, pinned by
// testpage-lookup-tablerelation-oos). This bundle is the third.
//
// That real BC opens that page and writes the selection back is plain BC behaviour and is
// proven UPSTREAM, in the al-language corpus, against a real service tier: codeunit 60569
// "TRL Tests". Nothing here re-asserts it.
//
// What IS asserted here is the runner's own dispatch -- which of its paths a given shape
// takes. #2549 drew a boundary and this issue moves it, and the corpus moved it further than
// intended: of the two shapes that have nothing to open, only ONE is a runner boundary.
//
//   no relation at all      -> BC does NOTHING. Measured on all eight cloud legs, run
//                              35445556865. The runner must not refuse, and this bundle pins
//                              that it does not.
//   relation, but no page   -> still refused, by name. The AL genuinely names a related table
//                              and BC's client has a page-picking rule for it that no corpus
//                              test has measured, so the runner says so rather than guessing
//                              silence by analogy with the row above.
codeunit 65796 "Tls Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Tls Assert";
        HandlerRan: Boolean;

    local procedure OpenOn(var Card: TestPage "Tls Card")
    var
        Host: Record "Tls Host";
        Related: Record "Tls Related";
    begin
        HandlerRan := false;

        Related.DeleteAll();
        Related.Init();
        Related."Code" := 'REL-A';
        Related.Descr := 'Alpha';
        Related.Insert();
        Related.Init();
        Related."Code" := 'REL-B';
        Related.Descr := 'Beta';
        Related.Insert();
        // Excluded by "Filtered Code"'s where(Blocked = const(false)), and by nothing else.
        // Its key sorts last, so a lookup that ignored the filter would still show it.
        Related.Init();
        Related."Code" := 'REL-BLOCKED';
        Related.Descr := 'Blocked';
        Related.Blocked := true;
        Related.Insert();

        Host.DeleteAll();
        Host.Init();
        Host."No." := 'H1';
        Host.Insert();

        Card.OpenEdit();
        Card.GoToRecord(Host);
    end;

    // THE SUBJECT. Nothing in this AL names "Tls Related List"; the only route to it is
    // "Served"'s TableRelation to "Tls Related", whose LookupPageId names that page. So a
    // handler that ran can only have been reached by following the relation.
    [Test]
    [HandlerFunctions('RelatedListHandler')]
    procedure Lookup_RelationToTableWithLookupPage_OpensItModally()
    var
        Card: TestPage "Tls Card";
    begin
        OpenOn(Card);

        Card."Served".Lookup();

        Assert.IsTrue(HandlerRan,
            'a lookup whose only source is a TableRelation must open the related table''s LookupPageId page modally');

        Card.Close();
    end;

    // The write-back, asserted by VALUE. The handler moves to REL-B and presses OK, so a
    // runner that opened the page but dropped the selection leaves the field blank and fails
    // here while still passing the test above.
    [Test]
    [HandlerFunctions('RelatedListPicksBHandler')]
    procedure Lookup_RelationToTableWithLookupPage_WritesTheSelectionBack()
    var
        Card: TestPage "Tls Card";
    begin
        OpenOn(Card);

        Card."Served".Lookup();

        Assert.AreEqual('REL-B', Card."Served".Value,
            'the row the lookup page''s handler left selected must be written back into the host field');

        Card.Close();
    end;

    // The write-back is CONDITIONAL. This handler moves to REL-B exactly as the previous one
    // does and differs only in pressing Cancel, so a runner that assigns unconditionally
    // passes the test above and fails this one.
    [Test]
    [HandlerFunctions('RelatedListCancelsHandler')]
    procedure Lookup_RelationLookupCancelled_LeavesTheFieldUnchanged()
    var
        Card: TestPage "Tls Card";
    begin
        OpenOn(Card);

        Card."Served".Lookup();

        Assert.AreEqual('', Card."Served".Value,
            'a cancelled lookup must leave the host field unchanged, whatever row the handler moved to');

        Card.Close();
    end;

    // GREEN CONTROL 1 -- the refusal that must SURVIVE. The relation resolves, the related
    // table declares no LookupPageId, so there is genuinely no page to open. The message must
    // say THAT, and must not claim there is no relation.
    [Test]
    procedure Lookup_RelationToTableWithNoLookupPage_IsRefusedNamingThatCause()
    var
        Card: TestPage "Tls Card";
    begin
        OpenOn(Card);

        asserterror Card."No Page".Lookup();

        Assert.ExpectedError('out-of-scope:');
        Assert.ExpectedError('testpage-lookup');
        // The cause, specifically: a relation exists and its target has no lookup page.
        Assert.ExpectedError('declares no LookupPageId or DrillDownPageId');
        // ...and NOT the other refusal's cause. Without this the two could share one message
        // and both tests would still pass.
        Assert.ErrorDoesNotContain('no TableRelation arm');

        Card.Close();
    end;

    // GREEN CONTROL 2 -- the shape that must NOT refuse, which is the opposite of what this
    // bundle first asserted.
    //
    // A field with neither a trigger nor a TableRelation does nothing on real BC: corpus
    // codeunit 60569 measured it on all eight cloud legs (run 35445556865) and BC raised no
    // error and opened no page. So the runner returning silently is faithful here, and an
    // out-of-scope refusal would be the runner inventing a boundary BC does not have.
    //
    // That claim is proven upstream, not here. What this arm adds is the RUNNER-side half the
    // corpus cannot see: that the silence is reached by the relation route returning, and not
    // by the lookup being a no-op for every shape -- which the three positive tests above and
    // the refusal below rule out together.
    [Test]
    procedure Lookup_NoRelationAtAll_DoesNothingRatherThanRefusing()
    var
        Card: TestPage "Tls Card";
    begin
        OpenOn(Card);
        Card."No Relation".SetValue('KEEP');

        // No asserterror: refusing here would be a boundary BC does not have.
        Card."No Relation".Lookup();

        Assert.AreEqual('KEEP', Card."No Relation".Value,
            'a lookup with neither a trigger nor a TableRelation must leave the field exactly as it was');

        Card.Close();
    end;

    // CLAIM: the relation's own where() arm narrows the lookup page. "Filtered Code" relates
    // to the same table as "Served" and adds where(Blocked = const(false)), so the page must
    // show REL-A and REL-B and NOT REL-BLOCKED.
    //
    // The handler asserts on the page rather than the test doing so afterwards, because the
    // rowset only exists while the modal page is open. It walks to the last row: with the
    // filter applied that is REL-B, without it REL-BLOCKED — so an unfiltered lookup fails
    // here naming the row it should never have been offered.
    [Test]
    [HandlerFunctions('FilteredListAssertsLastRowHandler')]
    procedure Lookup_RelationWithWhereArm_ShowsOnlyTheRowsTheRelationAllows()
    var
        Card: TestPage "Tls Card";
    begin
        OpenOn(Card);

        Card."Filtered Code".Lookup();

        Assert.IsTrue(HandlerRan, 'the filtered lookup must have opened its page');
        Card.Close();
    end;

    [ModalPageHandler]
    procedure FilteredListAssertsLastRowHandler(var Modal: TestPage "Tls Related List")
    begin
        HandlerRan := true;
        Modal.Last();
        Assert.AreEqual('REL-B', Modal."Code".Value,
            'the lookup page must show only the rows the relation''s where() allows; REL-BLOCKED must not be offered');
        Modal.OK().Invoke();
    end;

    [ModalPageHandler]
    procedure RelatedListHandler(var Modal: TestPage "Tls Related List")
    begin
        HandlerRan := true;
        Modal.OK().Invoke();
    end;

    [ModalPageHandler]
    procedure RelatedListPicksBHandler(var Modal: TestPage "Tls Related List")
    begin
        HandlerRan := true;
        Modal.GoToKey('REL-B');
        Modal.OK().Invoke();
    end;

    [ModalPageHandler]
    procedure RelatedListCancelsHandler(var Modal: TestPage "Tls Related List")
    begin
        HandlerRan := true;
        Modal.GoToKey('REL-B');
        Modal.Cancel().Invoke();
    end;
}
