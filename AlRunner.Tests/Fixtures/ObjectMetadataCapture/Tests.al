codeunit 70660 "OMR Capture Tests"
{
    Subtype = Test;

    [Test]
    procedure ThingRoundTrips()
    var
        Thing: Record "OMR Thing";
    begin
        Thing.Init();
        Thing."Entry No." := 42;
        Thing.Insert();

        Thing.Get(42);
        if Thing."Entry No." <> 42 then
            Error('Expected entry 42, got %1', Thing."Entry No.");
    end;
}
