// ReportExtension that references the BASE report's dataitem record from
// a non-override method. BC compiler emits `this.CurrReport.GetDataItem(...)`
// and may emit `this.ParentObject` in such cases. Without stubs for those
// members the rewriter's stripped ReportExtension class triggers CS1061
// (issue #1212).
reportextension 70701 "RptExt GetDI" extends "RptExt GetDI Base"
{
    dataset
    {
        addafter(Cust)
        {
            dataitem(Itm; "RptExt GetDI Item")
            {
                column(CustName; CustNameHolder) { }
                trigger OnAfterGetRecord()
                begin
                    // Accessing the base dataitem Cust from a trigger on an
                    // added dataitem — BC emits
                    //   this.Parent.CurrReport.GetDataItem("Cust").Record.GetFieldValueSafe(...)
                    // in the generated scope class.
                    CustNameHolder := Cust.Name;
                    HitCount += 1;
                end;
            }
        }
    }

    var
        HitCount: Integer;
        CustNameHolder: Text[100];

    procedure GetHitCount(): Integer
    begin
        exit(HitCount);
    end;

    procedure GetBaseCustName(): Text
    begin
        // Accessing the base report's dataitem record from a non-override
        // extension procedure also emits `this.CurrReport.GetDataItem("Cust")`.
        exit(Cust.Name);
    end;
}
