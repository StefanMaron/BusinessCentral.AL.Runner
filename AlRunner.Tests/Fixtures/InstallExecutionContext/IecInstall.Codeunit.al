codeunit 70901 "IEC Install"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        Observation: Record "IEC Observation";
    begin
        if Observation.Get('INSTALL') then
            Observation.Delete();
        Observation.Init();
        Observation."Code" := 'INSTALL';
        Observation."Exec Ctx" := Format(Session.GetExecutionContext());
        Observation."Module Exec Ctx" := Format(Session.GetCurrentModuleExecutionContext());
        Observation.Insert();
    end;
}
