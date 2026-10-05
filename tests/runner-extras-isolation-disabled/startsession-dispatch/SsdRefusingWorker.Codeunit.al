// The worker for the #5342 test: touches a surface the runner refuses by name (task scheduling is
// permanently out of scope, docs/scope.md#jobs). Source-compiled on purpose — the claim is about
// what StartSession does with a refusal, not about the precompiled dispatch path the suite's
// other tests exist for.
codeunit 61105 "Ssd Refusing Worker"
{
    trigger OnRun()
    var
        Exists: Boolean;
    begin
        Exists := TaskScheduler.TaskExists(CreateGuid());
    end;
}
