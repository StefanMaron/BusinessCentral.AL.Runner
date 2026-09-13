report 71843 "WER Bounded"
{
    ProcessingOnly = true;
    UseRequestPage = false;

    dataset
    {
        dataitem(Bounded; "WER Row")
        {
            DataItemTableView = sorting("No.");
            MaxIteration = 1;

            trigger OnAfterGetRecord()
            var
                Counter: Record "WER Counter";
            begin
                Counter.Init();
                Counter."Entry No." := Counter.Count() + 1;
                Counter.Insert();
            end;
        }
    }
}
