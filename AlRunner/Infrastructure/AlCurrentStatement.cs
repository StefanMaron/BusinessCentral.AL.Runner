// AlCurrentStatement — "which AL statement is executing right now", tracked off the
// SAME Cecil-rewritten NavMethodScope.StmtHit(int) hook AlCoverageTracker.OnStmtHit
// already receives on every AL statement of every run (issue #2117).
//
// WHY NOT NavSession.CurrentMethodScope — the obvious-looking alternative
//   NavMethodScope's own constructor does `session.CurrentMethodScope = this;`, so
//   reading `NavCurrentThread.Session.CurrentMethodScope` looks like it should give
//   "the scope currently running". Empirically (a probe with Console.Error debug output
//   under AL_RUNNER_VERBOSE=1) it does NOT: for a codeunit's OnRun trigger, invoked via
//   NavTriggerMethodScope<T>, `session.CurrentMethodScope` stayed the process's
//   RootMethodScope throughout — trigger scopes evidently do not become
//   `CurrentMethodScope` the way an ordinary procedure call's scope would. Chasing
//   the exact reason further wasn't worth it: StmtHit's own hook ALREADY hands us the
//   right scope directly as its first argument, with no session-tracking involved at
//   all — the same "receive the scope as a call argument, don't go hunting for it via
//   session" approach AlValueCapture's Exit() hook and AlCoverageTracker's own
//   OnStmtHit already use for the SAME reason.
//
// UNCONDITIONAL, NOT GATED BY AlCoverageTracker.Enabled
//   The StmtHit hook call itself is unconditional in the rewritten IL (see
//   AlCoverageTracker's doc comment); this class updates on EVERY call regardless of
//   Enabled, so a caller (RunnerClientCallback) can resolve "the statement that just
//   called Message()" even when `coverage:true` was never requested — matching
//   `messages`' own no-opt-in design (see AlMessageCapture's header).
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

public static class AlCurrentStatement
{
    // Single process-global slot, NOT per-scope/per-thread — same "the runner invokes
    // exactly one such call at a time" assumption AlValueCapture's snapshot slot and
    // AlCallStackCapture's single-slot AL stack already make.
    private static volatile NavMethodScope? _scope;
    private static volatile int _statementId = -1;

    /// <summary>Called unconditionally from AlCoverageTracker.OnStmtHit — see that
    /// method's own doc comment for why the underlying hook call is unconditional.
    /// <paramref name="statementId"/> of <c>int.MaxValue</c> (NavMethodScope.
    /// ExitStatementNumber, written by Exit()) is ignored, same guard
    /// AlCoverageTracker.OnStmtHit already applies, for the same reason: it is never a
    /// real statement a caller should be told about.</summary>
    public static void Update(NavMethodScope scope, int statementId)
    {
        if (statementId == int.MaxValue) return;
        _scope = scope;
        _statementId = statementId;
    }

    /// <summary>The scope + statement id as of the last StmtHit call, or (null, -1) if
    /// none has fired yet in this process.</summary>
    public static (NavMethodScope? Scope, int StatementId) Current => (_scope, _statementId);
}
