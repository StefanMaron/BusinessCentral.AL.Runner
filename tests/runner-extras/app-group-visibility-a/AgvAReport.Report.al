// #4447: a report in this app group, with ONE named data item, so both Report Metadata
// (2000000139) and Report Data Items (2000000203) have a subject in every group.
report 62604 "AGV A Report"
{
    Caption = 'AGV A Report Caption';
    ProcessingOnly = true;

    dataset
    {
        dataitem(AgvARows; "AGV A Table")
        {
            column(CodeCol; "Code") { }
        }
    }
}
