// Report extension that modifies the "Header" dataitem.
// This exactly matches the telemetry pattern from issue #1013:
//   ReportExtension50500.Header_a45_OnAfterAfterGetRecord_Scope missing 'Parent'.
// The BC compiler generates a scope class Header_a45_OnAfterAfterGetRecord_Scope
// that inherits NavTriggerMethodScope<ReportExtension57100> and uses .Parent to
// access ExtCounter. After the rewriter strips NavReportExtension and replaces
// the base with AlScope, Parent must be available via the injected property.
reportextension 57100 "TestReportExtForScope" extends "TestBaseReportForExt"
{
    dataset
    {
        modify(Header)
        {
            trigger OnAfterAfterGetRecord()
            begin
                // Accessing ExtCounter via the Parent chain in the scope class.
                // BC generates: base.Parent.ExtCounter += 1
                // Rewriter converts to: _parent.ExtCounter += 1
                // This requires _parent to be assigned in the scope constructor.
                ExtCounter += 1;
            end;
        }
    }

    var
        ExtCounter: Integer;
}
