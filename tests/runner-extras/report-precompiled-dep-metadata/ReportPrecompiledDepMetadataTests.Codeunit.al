// Regression test — CreateReportInstance / DataItemIterator.FinalizeDataItemLoading NRE.
//
// This suite reproduces the bug HERMETICALLY: "RPD Precompiled Report Dep" (see
// this folder's .alpackages/*.app + .deps-bin/*.dll, and app.json's dependency
// entry) ships report 61502 with a real two-level nested data-item tree
// (Header -> Line) as a Tier-1 precompiled DLL — DependencyLoader.LoadOne finds
// it at <bucketRoot>/.deps-bin/AL_Runner_Fixtures_RPD_Precompiled_Report_Dep_1.0.0.0.dll
// and loads the compiled assembly directly (Assembly.Load(bytes)), WITHOUT ever
// extracting/compiling its AL source. This is the exact class of gap the real
// CU50364 bug (Pageworks, report 1306 in Base Application) exercises, without
// this suite needing an externally-provisioned Base Application: no
// --package-cache, no ~/.bcartifacts.cache, nothing outside this folder.
//
// RED (before the fix): the dep's report never gets its metadata XML
// emit-captured (the runner never source-compiles it — see above), so
// NavReportSync.StubInitializeMetadata falls back to a GetUninitializedObject
// skeleton MetaReport whose `dataItems` field initializer never ran, so
// MetaReport.DataItems returned null. ReportAdd (the runner's faithful replica
// of NavReport.Add) read that as "no real metadata" (metadataIsReal=false) and
// left DataItem.MetaData unset for both data items. Report.SaveAs(61502, ...)
// then threw an UNHANDLED NullReferenceException (NOT a normal AL-catchable
// error — it is a .NET NullReferenceException, not a NavBaseException, so it
// is not caught by SaveAsAsync's own TrapError handling and instead crashes
// the whole test):
//   at AlRunnerV2.NavReportSync.CreateReportInstance
//   (DataItemIterator.FinalizeDataItemLoading -> FindDataItemChildrenAndParent
//   dereferencing DataItem.MetaData.ChildDataItems on a null MetaData)
// No rows are needed to hit this — DataItemIterator.FinalizeDataItemLoading runs
// once per report instance, before any row iteration.
//
// GREEN (after the fix): StubInitializeMetadata gives the skeleton MetaReport a
// real (empty) DataItems list, and ReportAdd — recognizing the stub — builds a
// real MetaDataItem for each not-yet-seen data-item name as the compiled report
// adds it (BuildSyntheticFlatMetaDataItem in NavReportSync.cs), so
// FinalizeDataItemLoading no longer dereferences a null MetaData.
// Report.SaveAs(61502, ...) then completes normally and returns a plain AL
// boolean — false here, because stub metadata is (separately, pre-existingly,
// and out of scope for this fix — see the comment on the ProcessingOnly stamp
// in StubInitializeMetadata) always marked ProcessingOnly, and real BC's own
// SaveReportAsFormatCoreAsync refuses SaveAs for ProcessingOnly reports. The
// crash is gone either way: what changes is "unhandled NullReferenceException
// that fails the WHOLE test" -> "a normal, catchable-shaped `false` return".
codeunit 61101 "RPD Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "RPD Assert";

    // Positive (no-crash claim, explicit per this suite's own naming rule):
    // SaveAs on a report living entirely in a Tier-1 precompiled dependency,
    // with a real two-level data-item tree the runner never emit-captured,
    // must complete as a normal AL call — not abort the test with an unhandled
    // runner exception. A controlled, documented refusal (the pre-existing,
    // out-of-scope ProcessingOnly stub limitation) sets a SPECIFIC,
    // recognizable last-error message. An unhandled NullReferenceException
    // (the original bug) does NOT reach this line at all — it aborts the whole
    // test with a raw C# stack instead, as the RED run (temporarily reverting
    // the fix) proves.
    [Test]
    procedure SaveAsXml_NestedDataItemPrecompiledDepReport_NoThrow()
    var
        BlobRec: Record "RPD Blob Holder";
        OutStr: OutStream;
        Result: Boolean;
    begin
        BlobRec."Blob Data".CreateOutStream(OutStr);

        // Report 61502 = "RPD Precompiled Report Dep"'s "RPDDep Nested Report"
        // — two nested data items (Header -> Line), zero rows inserted (none
        // needed: the crash site runs before row iteration starts).
        Result := Report.SaveAs(61502, '', ReportFormat::Xml, OutStr);

        Assert.IsFalse(Result,
            'Report.SaveAs(61502, Xml) is expected to return false (stub metadata is marked ProcessingOnly — see NavReportSync.cs) — a TRUE here or an escaped exception both indicate a regression in how this path is handled.');
        Assert.Contains(GetLastErrorText(), 'processing-only',
            'a controlled false return must carry the documented ProcessingOnly refusal text, proving SaveAs completed normally instead of crashing');
    end;

    // Negative: a genuinely unknown report id must still fail loudly — proves
    // the fix did not turn "no metadata available" into a silent no-op success
    // for objects that do not exist at all (as opposed to objects that exist
    // but live in a precompiled dependency, covered by the positive test above).
    [Test]
    procedure SaveAsXml_UnknownReportId_ThrowsRealError()
    var
        BlobRec: Record "RPD Blob Holder";
        OutStr: OutStream;
    begin
        BlobRec."Blob Data".CreateOutStream(OutStr);
        asserterror Report.SaveAs(99999999, '', ReportFormat::Xml, OutStr);
        Assert.Contains(GetLastErrorText(), '99999999',
            'SaveAs on a genuinely unknown report id must throw a real, specific error naming the id — not silently succeed/fail');
    end;
}
