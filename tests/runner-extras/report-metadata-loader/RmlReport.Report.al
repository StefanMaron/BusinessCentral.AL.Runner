// Fixture report: one data item over RML Sample. Source-compiled by the
// runner (same bundle) so AlReportMetadataRegistry captures its real
// metadata XML at emit time — the case RunnerXmlMetadataLoader is meant to
// serve (as opposed to report-precompiled-dep-metadata's precompiled-dep
// stub-metadata case).
report 61601 "RML Fixture Report"
{
    UsageCategory = None;
    ProcessingOnly = false;

    dataset
    {
        dataitem(Sample; "RML Sample")
        {
            column(EntryNo; "Entry No.") { }
            column(SampleDescription; Description) { }
        }
    }
}
