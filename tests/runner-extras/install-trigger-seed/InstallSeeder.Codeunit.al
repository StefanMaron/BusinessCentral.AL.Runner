// The Subtype=Install codeunit under test. Real BC fires its lifecycle
// triggers on app install; the runner must fire them once per bundle before
// tests run. Each trigger inserts distinctly-marked rows so the tests can
// prove BOTH triggers fired (not just one).
codeunit 60711 "ITS Installer"
{
    Subtype = Install;

    trigger OnInstallAppPerDatabase()
    var
        Seed: Record "Install Seed";
    begin
        Seed.Init();
        Seed."Code" := 'DATABASE';
        Seed."Value" := 99;
        Seed.Insert();
    end;

    trigger OnInstallAppPerCompany()
    var
        Seed: Record "Install Seed";
        Marker: Record "ITS Session Marker";
    begin
        Seed.Init();
        Seed."Code" := 'COMPANY1';
        Seed."Value" := 11;
        Seed.Insert();

        Seed.Init();
        Seed."Code" := 'COMPANY2';
        Seed."Value" := 22;
        Seed.Insert();

        // StartSession from an install trigger. Two claims ride on it:
        //   * #2805's guard refuses StartSession only inside a [Test]; an install trigger is not
        //     one, so the guard must not throw here. If it did, this trigger would fail loudly
        //     and the INSTALL-RESULT row below would never be written.
        //   * BC skips StartSession while an install runs and returns false without writing
        //     SessionId (#3292; the BC half is pinned upstream by corpus codeunit 60449). The
        //     runner models that with its own install-pass flag, so the worker must not run.
        // "ITS StartSession Outside Test" reads both back.
        SessionId := 777;
        Marker.Init();
        Marker."Code" := 'INSTALL-RESULT';
        if StartSession(SessionId, Codeunit::"ITS Session Worker") then
            Marker."Value" := 1
        else
            Marker."Value" := SessionId;
        Marker.Insert();
    end;

    var
        SessionId: Integer;
}
