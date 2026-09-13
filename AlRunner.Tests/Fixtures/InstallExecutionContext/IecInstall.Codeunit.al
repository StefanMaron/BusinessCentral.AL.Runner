codeunit 70901 "IEC Install"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        Observation: Record "IEC Observation";
        SessionId: Integer;
    begin
        if Observation.Get('INSTALL') then
            Observation.Delete();
        Observation.Init();
        Observation."Code" := 'INSTALL';
        Observation."Exec Ctx" := Format(Session.GetExecutionContext());
        Observation."Module Exec Ctx" := Format(Session.GetCurrentModuleExecutionContext());
        SessionId := 777;
        Observation."Start Session Result" := StartSession(SessionId, Codeunit::"IEC Worker");
        Observation."Session Id After" := SessionId;
        Observation.Insert();
    end;
}
