// This source file intentionally documents an AL construct that the runner
// cannot execute — it is NOT part of the main test loop.
//
// Purpose: demonstrate that Notification variables (NavNotification in BC runtime)
// are not in the runner's mock surface. If this codeunit were run through the
// pipeline it would produce a Roslyn compilation failure (exit code 2) because
// the rewriter does not replace NavNotification with a mock type.
//
// The behavioral test that verifies this exit-code behavior lives in
// AlRunner.Tests/RunnerErrorClassificationTests.cs and uses PipelineOptions.RewriterFactory
// to inject a failing rewriter — no need to actually transpile this file in the test.
codeunit 50890 "Notification Gap Example"
{
    trigger OnRun()
    var
        n: Notification;
    begin
        n.Message('This codeunit cannot run in al-runner — Notification is unsupported');
        n.Send();
    end;
}
