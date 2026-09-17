// #3965. Two procedures, and the split is the point: Twice() is called by the consuming
// bundle's test and Never() is not. A report that attributes this file must show a hit on
// Twice's statement AND no hit on Never's, so a fix that simply marks every dependency
// line covered fails as loudly as the drop it replaces.
codeunit 70860 "CDS Subject"
{
    procedure Twice(Value: Integer): Integer
    begin
        exit(Value * 2);
    end;

    procedure Never(Value: Integer): Integer
    begin
        exit(Value * 3);
    end;
}
