// Fixture report: one data item over RSS Sample with concrete columns.
// Carries an RDLC layout so (a) the report is NOT processing-only and
// (b) the negative test can prove the factory-fork OOS throw for RDLC
// rendering (reason report-rendering-external).
report 60702 "RSS Fixture Report"
{
    UsageCategory = None;
    ProcessingOnly = false;
    DefaultRenderingLayout = RdlcFixture;

    dataset
    {
        dataitem(Sample; "RSS Sample")
        {
            column(EntryNo; "Entry No.") { }
            column(Description; Description) { }
            column(Amount; Amount) { }
        }
    }

    rendering
    {
        layout(RdlcFixture)
        {
            Type = RDLC;
            LayoutFile = './FixtureLayout.rdlc';
        }
    }
}
