// #4447: a report in this app group, with ONE named data item, so both Report Metadata
// (2000000139) and Report Data Items (2000000203) have a subject in every group.
report 62624 "AGV C Report"
{
    Caption = 'AGV C Report Caption';
    ProcessingOnly = true;

    dataset
    {
        dataitem(AgvCRows; "AGV C Table")
        {
            column(CodeCol; "Code") { }
        }
    }
}
