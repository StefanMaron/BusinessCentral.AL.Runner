codeunit 70921 "ILP Install"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        Observation: Record "ILP Observation";
    begin
        if Observation.Get('INSTALL') then
            Observation.Delete();
        Observation.Init();
        Observation."Code" := 'INSTALL';
        Observation."Load Succeeded" := TryLoad();
        if not Observation."Load Succeeded" then
            Observation."Error Text" := CopyStr(GetLastErrorText(), 1, MaxStrLen(Observation."Error Text"));
        Observation.Insert();
    end;

    [TryFunction]
    local procedure TryLoad()
    begin
        NavApp.LoadPackageData(Database::"ILP Observation");
    end;
}
