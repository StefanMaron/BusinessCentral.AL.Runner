codeunit 70903 "IEC Worker"
{
    trigger OnRun()
    var
        Observation: Record "IEC Observation";
    begin
        Observation.Init();
        Observation."Code" := 'WORKER';
        Observation.Insert();
    end;
}
