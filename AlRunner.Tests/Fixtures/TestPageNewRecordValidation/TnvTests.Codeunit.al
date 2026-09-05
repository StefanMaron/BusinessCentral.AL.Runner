// Four claims about what TestPage.New() through a SubPageLink validates. Each reads a Boolean
// flag that ONLY that field's own OnValidate writes, so a flag can be true only if the trigger
// actually ran.
//
// Read through the part's CONTROLS, not from the table: New() starts a row in the page's buffer
// and does not insert it, so the table is still empty at this point — reading it back would
// assert on a row that does not exist yet. (That is also why the first attempt at this suite
// failed with "New() must have started exactly one row": a real property of New(), not of the
// fix under test.)
//
// Every Boolean assertion compares two CONTROLS against each other rather than against a text
// literal, which is the trick corpus codeunit 60653 uses and for the same reason: reading a
// Boolean through a TestPage control answers 'Yes'/'No' on real BC and 'True'/'False' on the
// runner today (#2795, carried on #2809). Comparing "did this flag move off the same default
// that one still sits at" is independent of the spelling, so this suite cannot go red or, worse,
// green for a reason that has nothing to do with what it is testing.
//
// "Descr Validated" is the never-written control field: no link names Descr and no page control
// writes it, so it holds the Init() default for the whole test.
codeunit 70405 "TNV Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "TNV Assert";

    local procedure OpenOnANewLine(var Card: TestPage "TNV Card")
    var
        Header: Record "TNV Header";
        Line: Record "TNV Line";
    begin
        Line.DeleteAll();
        Header.DeleteAll();

        Header.Init();
        Header."No." := 'H1';
        Header.Insert();

        Card.OpenEdit();
        Card.GoToRecord(Header);
        Card.Lines.New();
    end;

    [Test]
    procedure New_FieldLinkedPrimaryKeyField_IsStampedAndValidated()
    var
        Card: TestPage "TNV Card";
    begin
        OpenOnANewLine(Card);

        // Setup precondition: the value arrived at all. Without it the flag below could be false
        // for the boring reason that nothing was stamped, and the test would be blaming the
        // wrong half.
        Assert.AreEqual('H1', Card.Lines."No.".Value,
            '"No." is in the primary key, so the field(...) link must stamp it');
        Assert.AreNotEqual(Card.Lines."Descr Validated".Value, Card.Lines."No. Validated".Value,
            'New() must run "No."''s OnValidate on the value it stamped, so "No. Validated" must ' +
            'have moved off the default "Descr Validated" still sits at — issue #2551 gap 2, ' +
            'settled against a real service tier by corpus codeunit 60653');
        Card.Close();
    end;

    [Test]
    procedure New_ConstLinkedPrimaryKeyField_IsStampedAndValidated()
    var
        Card: TestPage "TNV Card";
    begin
        OpenOnANewLine(Card);

        // The arm nothing else pins. The corpus test uses a field(...) link, so a fix that
        // validated only field(...)-derived stamps would pass upstream and still be wrong here.
        Assert.AreEqual('K1', Card.Lines.Kind.Value,
            'Kind is in the primary key, so the const(...) link must stamp it');
        Assert.AreNotEqual(Card.Lines."Descr Validated".Value, Card.Lines."Kind Validated".Value,
            'a const(...) link''s stamped value must be validated too, not just a field(...) one');
        Card.Close();
    end;

    [Test]
    procedure New_PrimaryKeyFieldNoLinkNames_IsNotValidated()
    var
        Card: TestPage "TNV Card";
    begin
        OpenOnANewLine(Card);

        // "New() validates" is not the claim — "New() validates the STAMPED SET" is. BC hands
        // ValidateFieldsAsync exactly fieldsInitializedFromFilters, so validating every primary
        // key field would be as wrong as validating none. "Line No." is in the key and no link
        // names it, so it must still sit at the same default the never-written control does.
        Assert.AreEqual(Card.Lines."Descr Validated".Value, Card.Lines."Line No. Validated".Value,
            '"Line No." is in the primary key but no SubPageLink names it, so New() must not validate it');
        Card.Close();
    end;

    [Test]
    procedure New_FieldOutsideThePrimaryKeyAndOutsideTheLink_IsNotValidated()
    var
        Card: TestPage "TNV Card";
    begin
        OpenOnANewLine(Card);

        // Descr is the field behind the never-written control the other tests compare against.
        // What protects that reference from moving is not an assertion here — it is that
        // New_FieldLinkedPrimaryKeyField asserts "No. Validated" DIFFERS from it while
        // New_PrimaryKeyFieldNoLinkNames asserts "Line No. Validated" EQUALS it. If the
        // reference moved, one of those two would have to fail.
        // Descr is Text, so blank reads as the empty string — not 'No'. (It read 'No' in the
        // first draft of this assertion because the value was copied from a Boolean control;
        // the run said so immediately, which is the fixture working.)
        Assert.AreEqual('', Card.Lines.Descr.Value,
            'Descr is neither in the primary key nor stamped, so it must arrive blank');
        Card.Close();
    end;

    [Test]
    procedure New_ValidatesWithCurrFieldNoZero_NotAsAPageWrite()
    var
        Card: TestPage "TNV Card";
    begin
        OpenOnANewLine(Card);

        // A CHOICE, pinned here so it cannot drift silently, and so a future measurement that
        // contradicts it fails loudly instead.
        //
        // ValueControl.SetValue wraps its validate in CurrFieldNo = <the field> (#2705), because
        // that models a PAGE-ORIGINATED write and real BC does set it there. New() is a different
        // shape: BC's step is SourceTable.ValidateFieldsAsync, a record-level call like
        // Rec.Validate, which real BC leaves CurrFieldNo at 0 for. So the runner leaves it at 0.
        //
        // No corpus test pins CurrFieldNo during New(), on any BC leg — this follows BC's call
        // shape rather than a measurement. If a service tier is ever asked and answers non-zero,
        // this assertion is the thing that should go red, and the fix belongs in
        // MockTestPage.ValidateStampedFields.
        //
        // An Integer control, so this one is not affected by the Boolean spelling question.
        Assert.AreEqual('0', Card.Lines."No. CurrFieldNo".Value,
            'New()''s validate is record-level (ValidateFieldsAsync), not a page write, so ' +
            'CurrFieldNo must be 0 inside the stamped field''s OnValidate — see this test''s comment');
        Card.Close();
    end;
}
