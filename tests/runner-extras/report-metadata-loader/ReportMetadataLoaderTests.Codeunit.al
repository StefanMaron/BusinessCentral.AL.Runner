// Regression test — NCLMetaReport.LoadMetadata/GetMetadataFromLoader NRE for
// SOURCE-COMPILED reports (distinct from report-precompiled-dep-metadata's
// precompiled-dependency stub-metadata gap).
//
// RED (before the fix): RecordPatches.NclMetaFormReportBuilder.BuildNCLMetaReport
// built every skeleton NCLMetaReport via NCLMetaReport.CreateEmptyNCLMetaReport
// with loader=null. NCLMetadata.GetMetaApplicationObject(Report, id) itself
// succeeded (the skeleton entry exists), but any AL surface that needs the
// report's real dataset/column shape — Report.WordXmlPart, Report.DefaultLayout,
// NavGlobal.MetadataProvider.GetReportMetadata(id) — calls
// NCLMetaReport.LoadMetadata() -> GetMetadataFromLoader() -> ObjectLoader.
// XmlMetadataLoader.GetMetaObjectXmlMetadata(...), which threw an UNHANDLED
// NullReferenceException (ObjectLoader was null) — even though this report
// IS source-compiled by the runner and AlReportMetadataRegistry already holds
// its real emit-captured metadata XML.
//
// GREEN (after the fix): BuildNCLMetaReport passes
// RunnerMetaApplicationObjectLoader.Instance instead of null.
// GetMetadataFromLoader() resolves through RunnerXmlMetadataLoader, which
// answers GetMetaObjectXmlMetadata from AlReportMetadataRegistry — the same
// emit-captured XML NavReportSync.GetRealMetaReport already uses elsewhere —
// so Report.DefaultLayout returns the report's real declared layout kind
// instead of crashing.
//
// Uses Report.DefaultLayout rather than Report.WordXmlPart: both reach the
// same GetMetadataFromLoader() path this fix targets, but WordXmlPart ALSO
// calls OfficeCustomXmlPart.Generate, which NREs on a SEPARATE, unrelated,
// not-yet-implemented gap even once GetReportMetadata itself succeeds.
// DefaultLayout is the minimal AL surface that isolates just this fix's claim.
codeunit 61601 "RML Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "RML Assert";

    // Positive: DefaultLayout must report the report's REAL declared layout
    // kind — not just "did not throw". A stub/default-returning implementation
    // would report None (BC's zero-value), not RDLC.
    [Test]
    procedure DefaultLayout_SourceCompiledReport_ReturnsRealLayoutKind()
    var
        LayoutText: Text;
    begin
        LayoutText := Format(Report.DefaultLayout(61601));

        Assert.Contains(LayoutText, 'RDLC',
            'Report.DefaultLayout(61601) must report the real declared layout kind (RDLC is BC''s default when no rendering{} block is declared) — proves GetMetaObjectXmlMetadata served the emit-captured XML, not an empty/default stub that would leave DefaultLayout unset/None');
    end;

    // Negative: an unknown report id must still fail loudly, not silently
    // succeed — proves the fix did not turn "no metadata registered" into a
    // silent no-op for objects that genuinely do not exist.
    [Test]
    procedure DefaultLayout_UnknownReportId_ThrowsRealError()
    var
        LayoutText: Text;
    begin
        asserterror LayoutText := Format(Report.DefaultLayout(99999998));
    end;
}
