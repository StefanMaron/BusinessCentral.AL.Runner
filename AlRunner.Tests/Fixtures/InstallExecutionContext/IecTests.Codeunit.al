codeunit 70902 "IEC Tests"
{
    Subtype = Test;

    [Test]
    procedure IecInstallTriggerSawInstall()
    var
        Observation: Record "IEC Observation";
    begin
        if not Observation.Get('INSTALL') then
            Error('the install trigger did not run');
        if Observation."Exec Ctx" <> Format(ExecutionContext::Install) then
            Error('GetExecutionContext() in the install trigger answered %1', Observation."Exec Ctx");
    end;

    [Test]
    procedure IecInstallTriggerModuleSawInstall()
    var
        Observation: Record "IEC Observation";
    begin
        if not Observation.Get('INSTALL') then
            Error('the install trigger did not run');
        if Observation."Module Exec Ctx" <> Format(ExecutionContext::Install) then
            Error('GetCurrentModuleExecutionContext() in the install trigger answered %1', Observation."Module Exec Ctx");
    end;

    [Test]
    procedure IecContextIsClearedAfterThePass()
    begin
        if Session.GetExecutionContext() <> ExecutionContext::Normal then
            Error('GetExecutionContext() at test time answered %1', Format(Session.GetExecutionContext()));
        if Session.GetCurrentModuleExecutionContext() <> ExecutionContext::Normal then
            Error('GetCurrentModuleExecutionContext() at test time answered %1', Format(Session.GetCurrentModuleExecutionContext()));
    end;

    [Test]
    procedure IecLoadPackageDataOutsideInstallReturns()
    begin
        // Outside install BC's own first line returns; the #4061 refusal is install-only.
        NavApp.LoadPackageData(Database::"IEC Observation");
    end;

    [Test]
    procedure IecStartSessionInsideInstallWasRefused()
    var
        Observation: Record "IEC Observation";
    begin
        if not Observation.Get('INSTALL') then
            Error('the install trigger did not run');
        if Observation."Start Session Result" then
            Error('StartSession inside the install trigger answered true');
        if Observation."Session Id After" <> 777 then
            Error('StartSession inside the install trigger wrote SessionId %1', Observation."Session Id After");
        if Observation.Get('WORKER') then
            Error('the StartSession worker ran from the install trigger');
    end;
}
