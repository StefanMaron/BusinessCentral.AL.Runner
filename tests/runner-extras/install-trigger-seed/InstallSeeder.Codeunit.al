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
    begin
        Seed.Init();
        Seed."Code" := 'COMPANY1';
        Seed."Value" := 11;
        Seed.Insert();

        Seed.Init();
        Seed."Code" := 'COMPANY2';
        Seed."Value" := 22;
        Seed.Insert();

        // #2805's guard refuses StartSession from inside a [Test] unless the TestRunner
        // declares TestIsolation = Disabled. An install trigger is NOT a test, so the guard
        // must be inert here and this must dispatch normally. Nothing outside a [Test] had
        // ever exercised it, and "inert by construction" (BcRuntime.InTestExecutionScope is
        // false) is exactly the claim that stops being true when someone changes the
        // construction. "ITS StartSession Outside Test" reads the row the worker writes.
        StartSession(SessionId, Codeunit::"ITS Session Worker");
    end;

    var
        SessionId: Integer;
}
