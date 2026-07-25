// Fixture report declaring TWO named rendering layouts of DIFFERENT Type.
//
//   RlbLayoutOne  — Type = RDLC   (the report's DefaultRenderingLayout)
//   RlbLayoutTwo  — Type = Custom (never selected unless chosen BY NAME)
//
// The differing Type is deliberate: it is the observable that distinguishes
// "the by-name selection actually resolved RlbLayoutTwo" from "the runner fell
// back to the report default". The RDLC fork is external rendering (out of
// scope, throws report-rendering-external); the Custom fork is the in-scope
// custom-document-merger path. Same report, same SaveAs call, different layout
// NAME => different fork.
report 61872 "RLB Fixture Report"
{
    UsageCategory = None;
    ProcessingOnly = false;
    DefaultRenderingLayout = RlbLayoutOne;

    dataset
    {
        dataitem(Sample; "RLB Sample")
        {
            column(EntryNo; "Entry No.") { }
            column(Description; Description) { }
        }
    }

    rendering
    {
        layout(RlbLayoutOne)
        {
            Type = RDLC;
            LayoutFile = './RlbLayoutOne.rdlc';
            Caption = 'RLB layout one (RDLC default)';
            Summary = 'The report default rendering layout.';
        }
        layout(RlbLayoutTwo)
        {
            Type = Custom;
            LayoutFile = './RlbLayoutTwo.rlblayout';
            MimeType = 'application/x-rlb-layout';
            Caption = 'RLB layout two (custom, non-default)';
            Summary = 'Only reachable by selecting it by name.';
        }
    }
}
