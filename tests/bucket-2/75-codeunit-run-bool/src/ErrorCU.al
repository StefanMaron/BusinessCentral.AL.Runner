codeunit 50750 "Error CU"
{
    trigger OnRun()
    begin
        Error('Intentional error from Error CU');
    end;
}
