/// <summary>
/// Pins TestPage access to a subpage PART — the card-with-lines shape.
///
/// The runner's ITestPage.GetPart returned a bare MockITestPart: Creatable false, PageId 0,
/// Caption "". So BC's NavTestPageBase.ALNew(), which consults TestPage.Creatable, threw
///   "New method failed because Insert is not allowed. Page = , Id = 0."
/// for every part-hosted New(). Eight Pageworks tests fail exactly this way.
///
/// RED: Card.Lines.New() throws NavInsertDeniedPermissionException.
/// GREEN: the part inserts into its own source table, with the SubPageLink applied, and
/// shows only the current header's lines.
///
/// The negatives are what stop a shallow fix passing. A part that ignored SubPageLink
/// entirely would still satisfy the insert-and-read-back positive, so one negative walks
/// the part and asserts no foreign row is ever reachable. And a runner that answered
/// Creatable from the PARENT card would allow an insert through a read-only part, so the
/// other negative hosts a part whose own page declares InsertAllowed = false on a card that
/// is itself insertable.
/// </summary>
codeunit 61965 "TSP Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "TSP Assert";

    // Header 100 owns lines 1 and 2; header 200 owns line 1. The overlapping LineNo is
    // deliberate: a part that filtered on nothing, or on the wrong field, would surface
    // header 200's row as if it were header 100's.
    local procedure Seed()
    var
        Header: Record "TSP Header";
        Line: Record "TSP Line";
    begin
        Header.DeleteAll();
        Line.DeleteAll();

        Header.Init();
        Header.ReportId := 100;
        Header.Descr := 'First';
        Header.Insert();

        Header.Init();
        Header.ReportId := 200;
        Header.Descr := 'Second';
        Header.Insert();

        Line.Init();
        Line.ReportId := 100;
        Line.LineNo := 1;
        Line.Name := 'Alpha';
        Line.Insert();

        Line.Init();
        Line.ReportId := 100;
        Line.LineNo := 2;
        Line.Name := 'Bravo';
        Line.Insert();

        Line.Init();
        Line.ReportId := 200;
        Line.LineNo := 1;
        Line.Name := 'Foreign';
        Line.Insert();
    end;

    local procedure OpenCardOn(ReportId: Integer; var Card: TestPage "TSP Card")
    var
        Header: Record "TSP Header";
    begin
        Header.Get(ReportId);
        Card.OpenEdit();
        Card.GoToRecord(Header);
    end;

    // Positive: New() through the part inserts into the part's own table, and the
    // SubPageLink stamps the parent's key onto the new row. Asserting ReportId = 100 on a
    // value the test never set is what proves the link was applied rather than defaulted.
    [Test]
    procedure PartNewInsertsALineLinkedToTheParentHeader()
    var
        Line: Record "TSP Line";
        Card: TestPage "TSP Card";
    begin
        Seed();
        OpenCardOn(100, Card);

        Card.Lines.New();
        Card.Lines.LineNo.SetValue(7);
        Card.Lines.Name.SetValue('Charlie');
        Card.Close();

        Assert.IsTrue(Line.Get(100, 7), 'New() through the part must have inserted line 7 under header 100');
        Assert.AreEqual('Charlie', Line.Name, 'the value set through the part must have been persisted');
    end;

    // Positive: the part reads the parent's existing rows, in key order.
    [Test]
    procedure PartReadsTheParentsExistingLines()
    var
        Card: TestPage "TSP Card";
    begin
        Seed();
        OpenCardOn(100, Card);

        Assert.IsTrue(Card.Lines.First(), 'the part must be positioned on the header''s first line');
        Assert.AreEqual('Alpha', Card.Lines.Name.Value, 'the part''s first row must be header 100''s line 1');
        Assert.IsTrue(Card.Lines.Next(), 'the part must advance to the header''s second line');
        Assert.AreEqual('Bravo', Card.Lines.Name.Value, 'the part''s second row must be header 100''s line 2');
        Card.Close();
    end;

    // Negative: SubPageLink actually filters. Header 200's line shares LineNo 1 with
    // header 100's, so a part that ignored the link would surface 'Foreign' here.
    [Test]
    procedure PartNeverShowsAnotherHeadersLines()
    var
        Card: TestPage "TSP Card";
        Seen: Integer;
    begin
        Seed();
        OpenCardOn(100, Card);

        Assert.IsTrue(Card.Lines.First(), 'the part must have at least one row');
        repeat
            Seen += 1;
            Assert.IsFalse(Card.Lines.Name.Value = 'Foreign',
                'the part must never surface a line belonging to a different header');
        until not Card.Lines.Next();

        Assert.AreEqual('2', Format(Seen), 'the part must show exactly header 100''s two lines');
        Card.Close();
    end;

    // Negative: New() is refused when the PART's own page declares InsertAllowed = false —
    // the hosting card is insertable, so answering from the parent would wrongly allow it.
    [Test]
    procedure PartNewIsRefusedWhenThePartsOwnPageForbidsInsert()
    var
        Line: Record "TSP Line";
        Card: TestPage "TSP Card RO Lines";
        Header: Record "TSP Header";
    begin
        Seed();
        Header.Get(100);
        Card.OpenEdit();
        Card.GoToRecord(Header);

        asserterror Card.Lines.New();
        Assert.ExpectedError('Insert is not allowed');

        Line.SetRange(ReportId, 100);
        Assert.AreEqual('2', Format(Line.Count()),
            'a refused New() must not have inserted anything under header 100');
    end;
}
