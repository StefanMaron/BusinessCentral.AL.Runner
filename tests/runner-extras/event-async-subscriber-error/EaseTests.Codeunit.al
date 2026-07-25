/// <summary>
/// An error raised two event-levels down must reach the original publisher's
/// caller. See app.json for the measured real-world failure this reproduces.
/// </summary>
codeunit 61933 "EASE Tests"
{
    Subtype = Test;

    [Test]
    procedure ErrorRaisedUnderAnAsyncEmittedSubscriber_ReachesTheCaller()
    var
        Publisher: Codeunit "EASE Level1 Publisher";
    begin
        // The leaf subscriber calls Error(). Between it and this test sits a
        // subscriber that raises its own event, which BC emits as an async state
        // machine — so the error surfaces on that state machine's returned task
        // rather than propagating out of the invocation. If the runner discards
        // that task, this call returns NORMALLY and the failure is invisible.
        asserterror Publisher.Publish('raise');

        if StrPos(GetLastErrorText(), 'LEAF-RAISED-THIS') = 0 then
            Error('Expected the leaf subscriber''s error to reach the caller, got: "%1"', GetLastErrorText());
    end;

    [Test]
    procedure SuccessfulChain_StillReportsItsResult()
    var
        Publisher: Codeunit "EASE Level1 Publisher";
    begin
        // Positive control: the same two-level chain must still run to completion
        // and report back through the by-var parameter when nothing raises. Without
        // this, "make every dispatch throw" would pass the test above.
        if not Publisher.Publish('ok') then
            Error('The leaf subscriber set Handled := true, but the caller observed false — the two-level chain did not complete.');
    end;
}
