/// A codeunit that exists solely to be the TARGET of an action's `RunObject = codeunit ...`.
///
/// It is a SIBLING of "Par Noop Report", and the reason there are now four of these rather
/// than one is the shape rule: `RunObject` accepts five object kinds, the runner performs
/// exactly one of them (Page), and until this bundle grew the other three targets only the
/// REPORT arm of that refusal had a test. A regression that made a codeunit, xmlport or query
/// target do nothing quietly — the exact failure `loud-failures.md` exists to prevent — would
/// have gone uncaught, because nothing invoked one.
///
/// TableNo is deliberately absent. `Codeunit.Run` on a codeunit with no TableNo takes no
/// record, so nothing here depends on which record BC would hand it — the question the corpus
/// PR asks a service tier. This fixture's only job is to be a codeunit-kind target, so it
/// stays silent on everything that is still unmeasured.
///
/// Nothing invokes it as a codeunit: if the refusal it is here to prove ever regresses into
/// actually running it, OnRun writes a log row and the test that watches for that fails
/// loudly, which is what makes the refusal falsifiable rather than merely expected.
codeunit 64549 "Par Noop Runner"
{
    trigger OnRun()
    var
        OpenLog: Record "Par Open Log";
    begin
        OpenLog.Log('CODEUNIT-RAN');
    end;
}
