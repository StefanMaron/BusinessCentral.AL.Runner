/// <summary>
/// Declares a floor (1.0.0.0) that every BC the runner supports satisfies, so it must run in
/// every configuration. Its passing is what proves the skip below is SELECTIVE — a runner that
/// dropped the whole bundle would satisfy the exit-code assertion while covering nothing.
/// </summary>
codeunit 60810 "BC Floor Skip Healthy"
{
    Subtype = Test;

    [Test]
    procedure BcFloorSkip_HealthySibling_StillRuns()
    var
        Sum: Integer;
    begin
        Sum := 1 + 2;
        if Sum <> 3 then
            Error('the healthy sibling suite must compile and run');
    end;
}
