/// <summary>
/// Applying a filter through <c>TestPage.Filter.SetFilter</c> changes which rows the page has.
/// A page left sitting on a row the new filter excludes is not merely stale — it reports values
/// from a record that is not on the page at all.
///
/// That failure mode is the dangerous kind: the test reads a real, plausible-looking value
/// belonging to the wrong row, so it fails claiming the data is wrong rather than the cursor.
/// The Pageworks picker hit exactly this — after filtering to two named blocks it read the
/// token of a third block that the filter excluded entirely.
/// </summary>
codeunit 62121 "TFP Tests"
{
    Subtype = Test;

    local procedure Seed()
    var
        Row: Record "TFP Row";
    begin
        Row.DeleteAll();
        Insert1('A', 'Alpha');
        Insert1('B', 'Bravo');
        Insert1('C', 'Charlie');
    end;

    local procedure Insert1(No: Code[20]; Name: Text[50])
    var
        Row: Record "TFP Row";
    begin
        Row.Init();
        Row."No." := No;
        Row.Name := Name;
        Row.Insert();
    end;

    [Test]
    procedure SetFilter_ExcludingTheCurrentRow_MovesToTheFirstMatch()
    var
        List: TestPage "TFP List";
    begin
        Seed();

        List.OpenEdit();
        List.First();
        if List."No.".Value() <> 'A' then
            Error('Precondition: the page opened on <%1>, expected A.', List."No.".Value());

        // A is now excluded. Reading the page must not still answer from it.
        List.Filter.SetFilter("No.", 'B|C');
        if List."No.".Value() <> 'B' then
            Error('After filtering to B|C the page read <%1>, expected B — the cursor was left ' +
                  'on a row the filter excludes.', List."No.".Value());
        if List.Name.Value() <> 'Bravo' then
            Error('After filtering to B|C the Name read <%1>, expected Bravo.', List.Name.Value());

        List.Close();
    end;

    [Test]
    procedure SetFilter_KeepingTheCurrentRow_StaysOnIt()
    var
        List: TestPage "TFP List";
    begin
        Seed();

        List.OpenEdit();
        List.First();
        List.Next();
        if List."No.".Value() <> 'B' then
            Error('Precondition: expected to be on B, was on <%1>.', List."No.".Value());

        // The load-bearing negative: a filter the current row still satisfies must NOT move
        // the cursor. A fix that simply jumped to the first row after every SetFilter would
        // pass the test above and silently break every "filter, then keep reading here"
        // sequence — which is the far more common shape.
        List.Filter.SetFilter("No.", 'A|B|C');
        if List."No.".Value() <> 'B' then
            Error('A filter that still admits the current row moved the cursor to <%1>, ' +
                  'expected to stay on B.', List."No.".Value());

        List.Close();
    end;

    [Test]
    procedure SetFilter_MatchingNothing_LeavesNoRow()
    var
        List: TestPage "TFP List";
    begin
        Seed();

        List.OpenEdit();
        List.First();

        // The other direction: an empty result must be reported as empty, not as "still on the
        // last row I remember". This is what stops a repositioning fix from inventing a row.
        List.Filter.SetFilter("No.", 'NOTHING-MATCHES');
        if List.First() then
            Error('First() found a row on a page whose filter matches nothing: <%1>.',
                List."No.".Value());

        List.Close();
    end;
}
