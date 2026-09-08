// The CONTROL for this suite: the same two data-item BOUND shapes as the dependency's
// report 65821, but source-compiled HERE, so BC's own emit captures its metadata and
// AlReportMetadataRegistry holds it. Nothing about this report is reconstructed or
// synthesized, so it must keep running normally — and must honour both declared bounds.
//
// It is what stops the refusal in NavReportSync from passing by refusing every report.
report 65842 "RSM Local Report"
{
    ProcessingOnly = true;
    UseRequestPage = false;

    dataset
    {
        // Bounded ONLY by MaxIteration: the view states a sorting and nothing that
        // restricts rows, so 1 here is a measurement of the property. The test seeds
        // twelve rows, a count distinct from both 1 and 3.
        dataitem(BoundedByMaxIteration; "RSM Row")
        {
            DataItemTableView = sorting("No.");
            MaxIteration = 1;

            trigger OnAfterGetRecord()
            begin
                Track('MAXITER');
            end;
        }

        // Bounded ONLY by its filter, with no MaxIteration at all.
        dataitem(BoundedByFilter; "RSM Row")
        {
            DataItemTableView = sorting("No.") where("No." = filter(1 .. 3));

            trigger OnAfterGetRecord()
            begin
                Track('FILTER');
            end;
        }
    }

    local procedure Track(LoopName: Code[20])
    var
        Counter: Record "RSM Counter";
        NextNo: Integer;
    begin
        NextNo := 1;
        if Counter.FindLast() then
            NextNo := Counter."Entry No." + 1;
        Counter.Init();
        Counter."Entry No." := NextNo;
        Counter."Loop Name" := LoopName;
        Counter.Insert();
    end;
}
