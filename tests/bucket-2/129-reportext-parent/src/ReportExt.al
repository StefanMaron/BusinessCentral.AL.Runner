// Report extension with triggers that access parent-scope variables.
// The BC compiler generates scope classes for triggers in reportextensions.
// These scope classes reference `.Parent` to access variables defined on the
// reportextension. After the rewriter strips NavReportExtension base, the
// Parent property must still be available to avoid CS1061.
reportextension 56290 "TestCustExt" extends "TestReportWithColumnsExt"
{
    dataset
    {
        modify(TestCustomer)
        {
            trigger OnAfterAfterGetRecord()
            begin
                TriggerCount += 1;
            end;
        }
    }

    var
        TriggerCount: Integer;
}
