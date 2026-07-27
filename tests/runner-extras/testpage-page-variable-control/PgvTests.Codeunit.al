/// <summary>
/// Pins TestPage access to a control bound to a page GLOBAL VARIABLE rather than to a
/// source-table field.
///
/// This is ordinary AL — the standard way to put a mode/filter selector above a
/// repeater — and four Pageworks pages do exactly it. The runner's LiveNavTestPage is a
/// record cursor with a Rec-bound control map, and on a miss ToTableFieldNo falls
/// through to the raw control id. So the control's FNV name hash
/// (IdSpace.GetMemberId(pageId, name)) reaches the record as a field number and BC
/// throws NavNCLFieldNotFoundException naming a number no field could ever have:
/// "The supplied field number '1531114258' cannot be found in the
/// 'PageworksInsertPickerRow' table."
///
/// RED: reading or writing PgvList.Mode throws NavNCLFieldNotFoundException.
/// GREEN: the control reads and writes the page's own variable, and its OnValidate
/// trigger runs.
///
/// The negatives carry real weight here. A runner that satisfied the positives by
/// stashing control values in one shared dictionary — the obvious cheap fix — would
/// fail both of them: writing the page variable must not disturb the record's own
/// fields, and the variable must not survive into a second, independent page instance.
/// </summary>
codeunit 61992 "PGV Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "PGV Assert";

    local procedure SeedRows()
    var
        Row: Record "PGV Row";
    begin
        Row.DeleteAll();

        Row.Init();
        Row."No." := 'A';
        Row.Descr := 'Alpha';
        Row.Insert();

        Row.Init();
        Row."No." := 'B';
        Row.Descr := 'Bravo';
        Row.Insert();
    end;

    // Positive: the control round-trips the page's own variable.
    [Test]
    procedure PageVariableControl_RoundTripsItsValue()
    var
        PgvList: TestPage "PGV List";
    begin
        SeedRows();

        PgvList.OpenEdit();
        PgvList.Mode.SetValue('Blocks');
        Assert.AreEqual('Blocks', PgvList.Mode.Value,
            'the control bound to the page variable SelectedMode must read back what was written to it');
        PgvList.Close();
    end;

    // Positive: setting the control runs the page's AL. Asserting on a row the trigger
    // wrote — observed from OUTSIDE the page — is what separates "the value was stashed
    // and handed back" from "the page actually validated it".
    [Test]
    procedure PageVariableControl_FiresItsOnValidateTrigger()
    var
        Row: Record "PGV Row";
        PgvList: TestPage "PGV List";
    begin
        SeedRows();

        PgvList.OpenEdit();
        PgvList.Mode.SetValue('Fonts');
        PgvList.Close();

        Assert.IsTrue(Row.Get('ECHO'),
            'the control''s OnValidate trigger must have run and inserted the ECHO row');
        Assert.AreEqual('Fonts', Row.Descr,
            'OnValidate must have seen the value that was just assigned to the page variable');
    end;

    // Positive: Rec-bound controls on the same page keep working. A fix that routed every
    // control to the page instance would break these.
    [Test]
    procedure RecBoundControlsStillReadTheRecord()
    var
        PgvList: TestPage "PGV List";
    begin
        SeedRows();

        PgvList.OpenEdit();
        Assert.IsTrue(PgvList.First(), 'the page must be positioned on the first seeded row');
        Assert.AreEqual('A', PgvList."No.".Value, 'the Rec-bound key control must read the record');
        Assert.AreEqual('Alpha', PgvList.Descr.Value, 'the Rec-bound non-key control must read the record');
        PgvList.Close();
    end;

    // Negative: the page variable is NOT a record field. Writing it must leave the
    // current row untouched — a runner that resolved the control to some table field
    // (or wrote through to the record) would corrupt Descr here.
    [Test]
    procedure WritingThePageVariableDoesNotTouchTheRecord()
    var
        Row: Record "PGV Row";
        PgvList: TestPage "PGV List";
    begin
        SeedRows();

        PgvList.OpenEdit();
        Assert.IsTrue(PgvList.First(), 'the page must be positioned on the first seeded row');
        PgvList.Mode.SetValue('Images');
        Assert.AreEqual('Alpha', PgvList.Descr.Value,
            'writing the page variable must not overwrite the current row''s Descr');
        PgvList.Close();

        Row.Get('A');
        Assert.AreEqual('Alpha', Row.Descr,
            'writing the page variable must not have been persisted into row A');
    end;

    // Positive: an Option control is set by its CAPTION, not by its member name. Asserting
    // on Format(SelectedKind) — written by OnValidate and read from outside the page —
    // proves the caption resolved to the right ORDINAL. A runner that stashed the string
    // 'Blocks', or that matched captions against member names positionally by accident,
    // would not produce 'Block' here.
    [Test]
    procedure OptionControl_SetByCaptionResolvesToTheRightMember()
    var
        Row: Record "PGV Row";
        PgvList: TestPage "PGV List";
    begin
        SeedRows();

        PgvList.OpenEdit();
        PgvList.KindSelector.SetValue('Blocks');
        PgvList.Close();

        Assert.IsTrue(Row.Get('KIND'), 'the option control''s OnValidate trigger must have run');
        Assert.AreEqual('Block', Row.Descr,
            'the caption ''Blocks'' must resolve to option member Block, not to its own text');
    end;

    // Positive: a caption whose position differs from the member it names. 'Custom Fields'
    // is caption #5 and must select member Custom — the case that catches an off-by-one or
    // a member-name-only lookup, since no member is called 'Custom Fields'.
    [Test]
    procedure OptionControl_MultiWordCaptionResolvesToItsMember()
    var
        Row: Record "PGV Row";
        PgvList: TestPage "PGV List";
    begin
        SeedRows();

        PgvList.OpenEdit();
        PgvList.KindSelector.SetValue('Custom Fields');
        PgvList.Close();

        Assert.IsTrue(Row.Get('KIND'), 'the option control''s OnValidate trigger must have run');
        Assert.AreEqual('Custom', Row.Descr,
            'the multi-word caption ''Custom Fields'' must resolve to option member Custom');
    end;

    // Negative: a value that is neither a caption nor a member name must be refused, not
    // silently coerced to the first member (ordinal 0), which would make every typo in a
    // test look like a pass.
    [Test]
    procedure OptionControl_RejectsAValueThatIsNeitherCaptionNorMember()
    var
        Row: Record "PGV Row";
        PgvList: TestPage "PGV List";
    begin
        SeedRows();

        PgvList.OpenEdit();
        asserterror PgvList.KindSelector.SetValue('Sprockets');
        Assert.ExpectedError('Sprockets');

        Assert.IsFalse(Row.Get('KIND'),
            'a rejected value must not have run OnValidate, i.e. must not have been assigned at all');
    end;

    // Negative: page state is per-instance. A second page starts with its variable at the
    // AL default, not at whatever the previous instance left behind.
    [Test]
    procedure PageVariableDoesNotLeakIntoASecondPageInstance()
    var
        First: TestPage "PGV List";
        Second: TestPage "PGV List";
    begin
        SeedRows();

        First.OpenEdit();
        First.Mode.SetValue('Custom Fields');
        First.Close();

        Second.OpenEdit();
        Assert.AreEqual('', Second.Mode.Value,
            'a freshly opened page must start with its own variable at the AL default, not the previous instance''s value');
        Second.Close();
    end;
}
