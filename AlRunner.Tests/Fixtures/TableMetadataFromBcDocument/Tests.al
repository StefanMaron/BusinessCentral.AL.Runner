codeunit 70680 "TMD Tests"
{
    Subtype = Test;

    [Test]
    procedure ThingRoundTrips()
    var
        Thing: Record "TMD Thing";
    begin
        Thing.Init();
        Thing."Entry No." := 7;
        Thing.Insert();

        Thing.Get(7);
        if Thing."Entry No." <> 7 then
            Error('Expected entry 7, got %1', Thing."Entry No.");
    end;
}
