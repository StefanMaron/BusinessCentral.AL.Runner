// #3571 / #3572 — a query living in a PRECOMPILED dependency, so its MetaQuery design is built
// from the SymbolReference derivation (RecordPatches.NclMetaQueryBuilder.BuildMetaQueryDesign)
// rather than from BC's emitted metadata document. That derivation is the arm both issues
// report and the arm PR #3617's conversion does not reach.
//
// The rows inserted below are the instrument in both tests: each query would answer a
// DIFFERENT, specific wrong value if its property were dropped, so neither assertion can pass
// on a no-op.
codeunit 65871 "QDF Tests"
{
    Subtype = Test;

    /// <summary>
    /// #3571 — `DataItemTableFilter = Status = const(Open)` restricts the dataitem's own table
    /// rows. Two rows are inserted differing ONLY in Status, so the filter is the only thing
    /// that can tell them apart: dropped, the query answers both rows (and the CLOSED one
    /// first by primary key, which is what the runner used to return).
    /// </summary>
    [Test]
    procedure PrecompiledDepQueryAppliesStaticDataItemTableFilter()
    var
        Row: Record "QDF Filter Row";
        Probe: Query "QDF Open Rows";
        Assert: Codeunit "QDF Assert";
        Seen: Integer;
        LastCode: Code[20];
    begin
        Row.Init();
        Row.Code := 'CLOSED';
        Row.Status := Row.Status::Closed;
        Row.Insert();
        Row.Init();
        Row.Code := 'OPEN';
        Row.Status := Row.Status::Open;
        Row.Insert();

        Probe.Open();
        while Probe.Read() do begin
            Seen := Seen + 1;
            LastCode := Probe.RowCode;
        end;
        Probe.Close();

        Assert.AreEqual(1, Seen, 'rows returned by a query whose dataitem filters Status = const(Open)');
        Assert.AreEqual('OPEN', LastCode, 'the one row returned');
    end;

    /// <summary>
    /// #3572 — a two-field `DataItemLink` must constrain the join on BOTH equalities. Two
    /// headers share "No." = 'H1' and differ only in "Variant Code", and exactly one line
    /// exists, for the RED variant. Honouring only the first equality joins the line to BOTH
    /// headers (2 rows); honouring both joins it to one (1 row). Before the fix the property
    /// parsed to a source field named `No.", "Variant Code" = Hdr."Variant Code`, which
    /// resolves to nothing, so the WHOLE build was abandoned and the query NRE'd instead.
    /// </summary>
    [Test]
    procedure PrecompiledDepQueryJoinsOnEveryDataItemLinkEquality()
    var
        Hdr: Record "QDF Link Header";
        Ln: Record "QDF Link Line";
        Probe: Query "QDF Composite Link";
        Assert: Codeunit "QDF Assert";
        Seen: Integer;
        LastDescr: Text[50];
    begin
        Hdr.Init();
        Hdr."No." := 'H1';
        Hdr."Variant Code" := 'BLUE';
        Hdr.Descr := 'H1-BLUE';
        Hdr.Insert();
        Hdr.Init();
        Hdr."No." := 'H1';
        Hdr."Variant Code" := 'RED';
        Hdr.Descr := 'H1-RED';
        Hdr.Insert();

        Ln.Init();
        Ln."Header No." := 'H1';
        Ln."Variant Code" := 'RED';
        Ln."Line No." := 10000;
        Ln.Tag := 'RED-LINE';
        Ln.Insert();

        Probe.Open();
        while Probe.Read() do begin
            Seen := Seen + 1;
            LastDescr := Probe.HdrDescr;
        end;
        Probe.Close();

        Assert.AreEqual(1, Seen, 'rows returned by an InnerJoin linked on BOTH "Header No." and "Variant Code"');
        Assert.AreEqual('H1-RED', LastDescr, 'the header the single line joined to');
    end;
}
