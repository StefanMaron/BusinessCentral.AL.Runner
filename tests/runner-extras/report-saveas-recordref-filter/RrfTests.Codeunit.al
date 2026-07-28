/// <summary>
/// The RecordRef handed to Report.SaveAs must filter the report's matching data item.
///
/// This is the overload every "print this one document" path in AL uses:
/// <c>Report.SaveAs(id, params, format, stream, recordRef)</c> over a RecordRef whose record
/// carries SetRecFilter(). BC copies those filters onto the data item built over the same
/// table. Dropping them is not a cosmetic loss — a document report that guards against an
/// unfiltered run refuses to run at all, and one without such a guard quietly renders every
/// row in the table instead of the one that was asked for.
///
/// Both directions:
///   * with a filtered RecordRef  -> the report runs and the dataset holds ONLY that row;
///   * with no RecordRef at all   -> the report's own unfiltered guard fires, so the filter
///     is demonstrably what made the difference rather than the report being permissive.
/// </summary>
codeunit 62032 "RRF Tests"
{
    Subtype = Test;

    local procedure Seed()
    var
        Row: Record "RRF Row";
    begin
        Row.DeleteAll();
        Row.Init();
        Row."No." := 'DOC-1';
        Row.Name := 'first document';
        Row.Insert();
        Row.Init();
        Row."No." := 'DOC-2';
        Row.Name := 'second document';
        Row.Insert();
    end;

    local procedure ReadAll(var TempBlob: Codeunit "Temp Blob") Result: Text
    var
        DatasetInStream: InStream;
        Line: Text;
    begin
        TempBlob.CreateInStream(DatasetInStream);
        while not DatasetInStream.EOS() do begin
            DatasetInStream.ReadText(Line);
            Result += Line;
        end;
    end;

    [Test]
    procedure FilteredRecordRef_LimitsTheDatasetToThatRecord()
    var
        Row: Record "RRF Row";
        RowRef: RecordRef;
        TempBlob: Codeunit "Temp Blob";
        DatasetOutStream: OutStream;
        Dataset: Text;
        Ok: Boolean;
    begin
        Seed();
        Row.Get('DOC-1');
        Row.SetRecFilter();
        RowRef.GetTable(Row);

        TempBlob.CreateOutStream(DatasetOutStream);
        Ok := Report.SaveAs(Report::"RRF Document Report", '', ReportFormat::Xml, DatasetOutStream, RowRef);
        if not Ok then
            Error('Report.SaveAs refused to run with a filtered RecordRef — the record filter did not reach the data item.');

        Dataset := ReadAll(TempBlob);
        if StrPos(Dataset, 'first document') = 0 then
            Error('The filtered-in row is missing from the dataset: %1', CopyStr(Dataset, 1, 400));
        if StrPos(Dataset, 'second document') > 0 then
            Error('The dataset contains a row the RecordRef filtered OUT: %1', CopyStr(Dataset, 1, 400));
    end;

    [Test]
    procedure NoRecordRef_LeavesTheDataItemUnfiltered()
    var
        TempBlob: Codeunit "Temp Blob";
        DatasetOutStream: OutStream;
    begin
        // The control: without a RecordRef the data item has no filter, so the report's own
        // guard must fire. Without this, a runner that filtered NOTHING and a runner that
        // filtered correctly would be indistinguishable from the test above alone.
        Seed();
        TempBlob.CreateOutStream(DatasetOutStream);
        // Same entry point as the test above, minus the RecordRef — so the ONLY difference
        // between the two outcomes is the record filter.
        if Report.SaveAs(Report::"RRF Document Report", '', ReportFormat::Xml, DatasetOutStream) then
            Error('The report ran unfiltered — its own guard against printing all documents did not fire, so the test above proves nothing about filtering.');
    end;
}
