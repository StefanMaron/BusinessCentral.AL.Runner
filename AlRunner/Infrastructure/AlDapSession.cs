// AlDapSession — the runtime side of --dap (issue #1642): registers breakpoints as
// (scope Type, statement index) pairs and blocks the AL execution thread when
// NavMethodScope.StmtHit/CStmtHit fires for one of them, via a THIRD unconditional
// Cecil-rewrite prepend on the same StmtHit/CStmtHit methods --coverage (#1922)
// already hooks — see NclCecilRewrite's "DAP breakpoint hook" block. Process-global,
// like AlCoverageTracker/AlValueCapture: al-runner runs one AL statement at a time on
// one thread, so a single active session is the correct model (matches
// docs/archive/dap.md's v1 design note: "Single SemaphoreSlim for pause/resume").
//
// WHY pausing AT StmtHit(N) is the RIGHT boundary for a debugger — unlike
// AlValueCapture (#1640), which had to move OFF StmtHit and onto Exit() because a
// "keep the latest StmtHit" snapshot is always one statement stale (BC calls
// StmtHit(N) BEFORE statement N's own side effect runs). A breakpoint does not have
// that problem: "stopped at line L" is CONVENTIONALLY DEFINED, in every mainstream
// debugger, as "about to execute L; every statement before L has completed". That is
// exactly what is true the instant StmtHit(N) fires — statement N-1's effects are
// already visible on the scope's fields, statement N's are not yet. So pausing here,
// and reading the live scope's fields from that exact instant (AlScopeInspector), is
// not an approximation the way --capture-values' StmtHit-based prototype was — it is
// the correct pause point by definition. No Exit()-style redesign is needed for pause.
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

public static class AlDapSession
{
    /// <summary>True only while a --dap run is executing tests. Gates OnStmtHit; the
    /// Cecil-rewritten StmtHit/CStmtHit call is unconditional, this flag is not — same
    /// pattern as AlCoverageTracker.Enabled / AlValueCapture.Enabled.</summary>
    public static volatile bool Enabled;

    private static readonly HashSet<(Type ScopeType, int Stmt)> _breakpoints = new();
    private static readonly object _bpLock = new();

    private static volatile System.Threading.SemaphoreSlim? _pauseGate;
    private static volatile NavMethodScope? _pausedScope;
    private static volatile int _pausedStatement = -1;

    /// <summary>Set once by Detach() (disconnect/terminate path) so a StmtHit that
    /// arrives after the DAP session has gone away runs straight through instead of
    /// registering a new pause nothing will ever release.</summary>
    private static volatile bool _detached;

    /// <summary>
    /// Fired synchronously ON THE AL EXECUTION THREAD, before it blocks — the caller
    /// (DapServer) uses this to push the DAP "stopped" event over the wire. Must not
    /// throw: an exception here would propagate into BC's own StmtHit call.
    /// </summary>
    public static event Action<NavMethodScope, int>? Stopped;

    /// <summary>Resets all state for a new session — breakpoints, any stale pause, the
    /// detached flag. Call before a fresh --dap run starts.</summary>
    public static void Reset()
    {
        lock (_bpLock) _breakpoints.Clear();
        _pauseGate = null;
        _pausedScope = null;
        _pausedStatement = -1;
        _detached = false;
        Stopped = null;
    }

    public static void SetBreakpoint(Type scopeType, int statementIndex)
    {
        lock (_bpLock) _breakpoints.Add((scopeType, statementIndex));
    }

    public static void ClearBreakpoints(Type scopeType)
    {
        lock (_bpLock) _breakpoints.RemoveWhere(k => k.ScopeType == scopeType);
    }

    /// <summary>The scope currently paused, or null when nothing is paused.</summary>
    public static NavMethodScope? PausedScope => _pausedScope;

    /// <summary>The statement index the paused scope stopped at, or -1 when nothing is paused.</summary>
    public static int PausedStatement => _pausedStatement;

    public static bool IsPaused => _pausedScope != null;

    /// <summary>Releases a paused AL execution thread (DAP `continue`/`next`/`stepIn`/
    /// `stepOut` — this first slice treats all four identically, see the issue's PR
    /// description for why real step granularity is follow-up work).</summary>
    public static void Continue() => _pauseGate?.Release();

    /// <summary>Permanently stops pausing (DAP `disconnect`/`terminate`) and releases
    /// any thread currently blocked — an AL execution thread must never be left stuck
    /// forever just because the debug client went away (.claude/rules/loud-failures.md:
    /// no silent hang is acceptable either).</summary>
    public static void Detach()
    {
        _detached = true;
        _pauseGate?.Release();
    }

    /// <summary>
    /// Hook target for the Cecil-rewritten NavMethodScope.StmtHit(int)/CStmtHit(int[,
    /// bool]) — public static, exactly (NavMethodScope, int) so the rewrite can forward
    /// `ldarg.0; ldarg.1; call` unboxed, same shape as AlCoverageTracker.OnStmtHit. Runs
    /// on EVERY AL statement of every test, --dap or not — must stay near-zero-cost when
    /// disabled.
    /// </summary>
    public static void OnStmtHit(NavMethodScope scope, int currentStatementNumber)
    {
        if (!Enabled || _detached) return;
        // Same ExitStatementNumber guard as AlCoverageTracker.OnStmtHit — Exit() writes
        // int.MaxValue directly, StmtHit never receives it from generated code.
        if (currentStatementNumber == int.MaxValue) return;

        bool hit;
        lock (_bpLock) hit = _breakpoints.Contains((scope.GetType(), currentStatementNumber));
        if (!hit) return;

        var gate = new System.Threading.SemaphoreSlim(0, 1);
        _pauseGate = gate;
        _pausedScope = scope;
        _pausedStatement = currentStatementNumber;
        try
        {
            Stopped?.Invoke(scope, currentStatementNumber);
            gate.Wait(); // blocks the AL execution thread until Continue()/Detach()
        }
        finally
        {
            _pausedScope = null;
            _pausedStatement = -1;
            _pauseGate = null;
        }
    }
}
