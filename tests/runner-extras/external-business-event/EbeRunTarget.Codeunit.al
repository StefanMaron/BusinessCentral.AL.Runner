namespace AlRunner.Extras.ExternalBusinessEvent;

codeunit 65662 "Ebe Run Target"
{
    // Run through a guarded Codeunit.Run, the shape that hid the NRE in the reporting workload:
    // Run returned false with an empty GetLastErrorText().
    trigger OnRun()
    var
        Publisher: Codeunit "Ebe Publisher";
    begin
        Publisher.RaiseBetweenSteps();
        if Publisher.Steps() <> 2 then
            Error('Ebe run target: expected 2 steps, got %1', Publisher.Steps());
    end;
}
