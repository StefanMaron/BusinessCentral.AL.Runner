// The layout is shipped under a percent-encoded entry name (layout/TP%20Fixture%20Report.rdlc)
// and declared here with a Windows separator and a literal space.
report 70000 "TP Fixture Report"
{
    ApplicationArea = All;
    UsageCategory = ReportsAndAnalysis;
    DefaultRenderingLayout = FixtureLayout;

    dataset
    {
        dataitem(Header; "TP Fixture Header")
        {
            column(No_; "No.") { }
        }
    }

    rendering
    {
        layout(FixtureLayout)
        {
            Type = RDLC;
            LayoutFile = '.\layout\TP Fixture Report.rdlc';
        }
    }
}
