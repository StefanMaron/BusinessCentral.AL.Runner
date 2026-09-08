// OnOpenPage and OnAfterGetRecord must reach the extended page's flags. Two triggers, not the
// nine a pageextension may declare, so an implementation that answered "true" wholesale fails.
//
// BC also EXCLUDES OnFindRecord, OnNextRecord and OnInit from its pageextension pass, and that
// arm is not testable from AL: the compiler refuses all three here with AL0162, "not a valid
// trigger for this object type" (measured on BC 28.1).
pageextension 70663 "PTM Plain Ext" extends "PTM Plain"
{
    trigger OnOpenPage() begin end;

    trigger OnAfterGetRecord() begin end;
}
