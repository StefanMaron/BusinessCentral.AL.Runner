// AlMessageCapture — the runtime side of `--server` `execute`'s `messages` response
// field (issue #2117). Collects every Message() call an OnRun-driven codeunit makes
// during a `server execute` (run-mode) invocation, in call order, each tagged with the
// AL statement that produced it.
//
// WHY THIS EXISTS
//   BC's own NavDialog.ALMessage body (decompiled from Microsoft.Dynamics.Nav.Ncl.dll)
//   is:
//       string message2 = ALSystemString.ALStrSubstNo(ReplaceBackSlashWithCRLF(message), values);
//       if (!session.TestExecution.TestHandleMessage(message2))
//           session.ClientCallbackOrNull?.DialogMessage(message2, automationId);
//   TestHandleMessage only ever finds a [MessageHandler] (or throws BC's own "Unhandled
//   UI") while a [Test] procedure is executing — NavTestExecution.FindHandler's inner
//   lookup is unconditionally `if (executingTestMethod == null) return null;`, and
//   `executingTestMethod` is only set by EnterTestMethod, which runs exactly once per
//   [Test] invocation. `execute`'s OnRun path never calls EnterTestMethod, so
//   TestHandleMessage quietly returns false WITHOUT throwing, and `ClientCallbackOrNull`
//   is null on the runner (no client) — the `?.` swallows the call outright: no
//   dispatch, no exception, nothing. Confirm()/StrMenu() do NOT have this hole: their AL
//   bodies read the non-null-conditional `session.ClientCallback`, which throws
//   NavNCLCallbackNotAllowedException when there is no client — loud, not silent. See
//   AlRunner/Patches/RunnerClientCallback.cs for the fix and the fuller comparison.
//
// STATEMENT ID
//   NavMethodScope.StatementNumber (public, unmodified BC state — StmtHit/Exit already
//   maintain it; see AlCoverageTracker's and AlValueCapture's own doc comments) is read
//   off NavSession.CurrentMethodScope at the moment DialogMessage fires — i.e. the
//   statement whose side effect is "call Message()" is still the CURRENT statement of
//   the innermost executing AL method scope. This is the SAME id-space
//   AlCoverageTracker.AlStatementRecord.StatementId and
//   AlValueCapture.AlCapturedValue.StatementId already use for the SAME scope (see
//   AlStatementTableTests.CapturedValueStatementId_MatchesStatementTableScopeAndId) — no
//   new numbering, reusing exactly the id/position table #2042 built.
namespace AlRunner.Infrastructure;

/// <summary>One Message() call captured during a <c>server execute</c> run, in call
/// order. <c>ScopeName</c> is the AL member (trigger/procedure) whose currently
/// executing statement called Message() — matching <c>AlCapturedValue.ScopeName</c> /
/// <c>AlStatementRecord.ScopeName</c>'s scope-name space. <c>StatementId</c> is -1 only
/// when no AL method scope was active at capture time (should not happen for a real
/// Message() call reached through AL code; recorded rather than thrown so an
/// unanticipated shape does not crash the whole run — loud in the log, not fatal).</summary>
public readonly record struct AlCapturedMessage(string Text, string ScopeName, int StatementId);

public static class AlMessageCapture
{
    private static volatile List<AlCapturedMessage>? _messages;

    /// <summary>Reset once per `execute` call (HandleServerExecute), BEFORE the (possibly
    /// multi-bundle) run — mirrors AlCoverageTracker.Reset's placement: messages must
    /// accumulate across every bundle in ONE execute call, in the order Message() was
    /// actually called, not be scoped per-bundle the way AlValueCapture's per-OnRun
    /// snapshot is.</summary>
    public static void Reset() => _messages = new List<AlCapturedMessage>();

    /// <summary>Called from RunnerClientCallback.DialogMessage. Appends, never replaces —
    /// a loop calling Message() three times must produce three entries, in order.</summary>
    public static void Record(string text, string scopeName, int statementId) =>
        (_messages ??= new List<AlCapturedMessage>()).Add(new AlCapturedMessage(text, scopeName, statementId));

    /// <summary>Everything captured since the last Reset(), in call order. Empty (never
    /// null) whether Reset() was never called or nothing was captured — the caller
    /// (HandleServerExecute / ServerProtocol.Execute) decides whether zero entries means
    /// "omit messages from the wire" (it does — see ServerProtocol.Execute's doc
    /// comment).</summary>
    public static IReadOnlyList<AlCapturedMessage> Snapshot() =>
        (IReadOnlyList<AlCapturedMessage>?)_messages ?? Array.Empty<AlCapturedMessage>();
}
