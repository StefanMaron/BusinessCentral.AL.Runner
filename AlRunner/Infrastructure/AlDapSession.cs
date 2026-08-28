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
// AlValueCapture (#1640)'s ORIGINAL design, which had to move OFF a "keep the latest
// StmtHit" snapshot and onto Exit() because that shape is always one statement stale
// (BC calls StmtHit(N) BEFORE statement N's own side effect runs). A breakpoint does
// not have that problem: "stopped at line L" is CONVENTIONALLY DEFINED, in every
// mainstream debugger, as "about to execute L; every statement before L has
// completed". That is exactly what is true the instant StmtHit(N) fires — statement
// N-1's effects are already visible on the scope's fields, statement N's are not yet.
// So pausing here, and reading the live scope's fields from that exact instant
// (AlScopeInspector), is not an approximation the way the ORIGINAL --capture-values
// StmtHit-based prototype was — it is the correct pause point by definition. No
// Exit()-style redesign is needed for pause. (#2074 later brought AlValueCapture back
// onto StmtHit too, but attributing each observation to the PREVIOUS statement id
// rather than overwriting a single "latest" slot — see AlValueCapture's own file
// header; that redesign does not change anything about THIS session's live-read
// timing, which was always correct.)
//
// STEP GRANULARITY (issue #2045) — next/stepIn/stepOut arm a SECOND, orthogonal gate:
// "pause at the next qualifying StmtHit regardless of the registered breakpoint set".
//
// "Qualifying" is defined in terms of NESTING DEPTH — but NOT NavMethodScope's own
// internal StackDepth property, which the issue originally proposed. Measured
// (instrumented, running the DapStepping fixture's nested call directly): a LOCAL
// procedure call within the SAME codeunit gets its own NavMethodScope instance — a
// genuinely different Type, own fields, own statement numbering restarting at 0,
// exactly the frame AlDapStackWalker already treats as a distinct stack frame — but
// StackDepth on that instance comes back IDENTICAL to its caller's, not caller+1.
// (StackDepth increments crossing an application-object boundary — Codeunit.Run,
// eventing, TryFunction — not for a same-object local-procedure call lowered to a
// plain call at compile time.) A depth check built on StackDepth alone therefore could
// not tell "the call this statement just made" apart from "the next statement in the
// same procedure" for exactly the nested-call case this feature has to get right, and
// measurably didn't: it under-stepped (see the PR description for the concrete
// pre-fix "next landed inside the callee instead of after it" failure).
//
// The nesting signal that DOES distinguish them, still needing no reflection, is the
// same one AlDapStackWalker.Walk already uses to build stack frames: walk
// NavMethodScope.ParentScope (public) until IsRootScope (public), counting hops.
// ComputeChainDepth below does exactly that. Confirmed on the same fixture: the outer
// scope's chain depth is 1, the nested call's is 2, its ParentScope is literally the
// outer scope instance — the caller/callee relationship AlDapStackWalker's frame list
// already relies on. Chain depth increases by exactly 1 per NavMethodScope frame
// regardless of whether the call crosses an application-object boundary, so it is
// correct through recursion too (a recursive call is still one MORE frame, whatever
// StackDepth says about it).
//
// Given D = the chain depth of the scope the step command was issued from:
//   - stepIn:  ANY next StmtHit qualifies, at any depth — "stop at the very next
//              statement, wherever it runs".
//   - next (step over): chain depth <= D. A nested call the current statement makes
//              runs to completion unobserved (its own StmtHits are all > D); the first
//              StmtHit back at D itself, OR at any shallower depth if the current
//              scope returns without another statement of its own (matches every
//              mainstream debugger: stepping over the LAST statement of a function
//              lands you in the caller, not nowhere), satisfies it.
//   - stepOut: chain depth < D strictly — must have returned past the scope stepOut
//              was issued from. This is deliberately NOT "shallower than D's
//              immediate parent" (that would overshoot by one frame): the first
//              statement that runs at any depth shallower than D is, by construction,
//              the caller's next statement, because nothing between D and that depth
//              can run without itself producing a StmtHit that would have satisfied
//              this condition already.
// A step that never finds a qualifying StmtHit before its OWN test method finishes
// simply never pauses again — the test runs to completion normally, same as it would
// after a plain "continue" that outruns every breakpoint. Nothing hangs: the gate is
// only ever waited on from inside OnStmtHit, and if OnStmtHit never fires again there
// is no thread blocked on it. What DOES need explicit handling is that an unconsumed
// step must not silently apply to the NEXT test in the same --dap run — OnTestBoundary
// disarms it between tests (see that method's doc comment).
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

/// <summary>Which DAP step command is currently armed. <c>None</c> means only
/// registered breakpoints can pause execution (or nothing is armed at all).</summary>
internal enum AlDapStepKind
{
    None,
    In,
    Over,
    Out,
}

public static class AlDapSession
{
    /// <summary>True only while a --dap run is executing tests. Gates OnStmtHit; the
    /// Cecil-rewritten StmtHit/CStmtHit call is unconditional, this flag is not — same
    /// pattern as AlCoverageTracker.Enabled / AlValueCapture.Enabled.</summary>
    public static volatile bool Enabled;

    // Diagnostic-only trace for issue #2070 (step-over test timing out on CI under
    // load): opt-in via AL_DAP_STEP_TRACE=1, off by default so a real --dap session's
    // stderr stays quiet. Only ever consulted from code paths already gated behind
    // Enabled (an active --dap session), so this adds no cost to the !Enabled fast
    // path a 2130-test corpus run takes on every statement. Emits to stderr with BOTH
    // a monotonic per-process elapsed time (useful for intra-server ordering) AND a
    // wall-clock UTC timestamp — the wall clock is the load-bearing half: this trace
    // runs in the SPAWNED al-runner --dap CHILD process, a different process from the
    // DapClient test harness that has its own independent trace (see DapClient.cs),
    // and two different processes' Stopwatch.StartNew() epochs are NOT comparable to
    // each other. Wall-clock UTC (same machine, same clock) is what lets a reader put
    // "server FIRE'd at T" and "client gave up waiting at T+60s" on one timeline and
    // answer issue #2070's actual question: did the server do its job and the client
    // simply not get scheduled to read it, or did the server never fire at all.
    private static readonly bool _traceEnabled = Environment.GetEnvironmentVariable("AL_DAP_STEP_TRACE") == "1";
    private static readonly System.Diagnostics.Stopwatch _traceClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// internal, not private: issue #2070's follow-up found the actual bug this whole
    /// trace exists to catch lives OUTSIDE this class — Program.cs's Stopped-event
    /// handler (RunDapLoop) swallows an AlDapStackWalker.Walk exception via a bare
    /// Console.Error.WriteLine and returns normally, which OnStmtHit then reads as "the
    /// stop was reported" and proceeds straight into gate.Wait() — a silently-lost
    /// "stopped" event, not a missed step and not client-side starvation (see that
    /// handler's own trace calls for the three-way split: Walk threw, WriteEvent threw,
    /// or the write genuinely completed and the bytes did not arrive). Exposed here so
    /// that handler's diagnostics land in the SAME trace stream instead of a second,
    /// differently-gated Console.Error path that the original bare
    /// Console.Error.WriteLine used — a second path a two-reader DapClient bug (fixed
    /// earlier in this issue) could just as easily have eaten silently, which is
    /// exactly why this failure mode went unnoticed for as long as it did.
    /// </summary>
    internal static void Trace(string msg)
    {
        if (!_traceEnabled) return;
        // InvariantCulture explicitly: ":" in a custom DateTime format string is the
        // CURRENT CULTURE's time-separator placeholder, not a literal colon — caught
        // this rendering as "08.28.01.165Z" (dots) while building this trace on a
        // machine whose OS locale (en-DK) uses "." as its time separator. Comparing
        // this against DapClient's own wall-clock trace only works if both use the
        // exact same, culture-independent rendering.
        var wall = DateTime.UtcNow.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        // Same InvariantCulture trap as the wall-clock stamp above: interpolation's
        // ":F1" shorthand uses CURRENT CULTURE's decimal separator too (would render
        // "13053,9" on a comma-decimal locale). This half of the line is intra-process
        // only (never compared against another process's Stopwatch epoch) so it isn't
        // load-bearing for #2070's cross-process comparison the way "wall" is, but a
        // decimal point that silently isn't one is still a footgun worth closing here.
        var elapsedMs = _traceClock.Elapsed.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        Console.Error.WriteLine($"[dap-step-trace] t={elapsedMs}ms wall={wall}Z {msg}");
    }

    private static readonly HashSet<(Type ScopeType, int Stmt)> _breakpoints = new();
    private static readonly object _bpLock = new();

    private static volatile System.Threading.SemaphoreSlim? _pauseGate;
    private static volatile NavMethodScope? _pausedScope;
    private static volatile int _pausedStatement = -1;

    /// <summary>Set once by Detach() (disconnect/terminate path) so a StmtHit that
    /// arrives after the DAP session has gone away runs straight through instead of
    /// registering a new pause nothing will ever release.</summary>
    private static volatile bool _detached;

    /// <summary>The currently-armed step command, or None. Written by StepOver/StepIn/
    /// StepOut (the DAP-loop thread, while the AL thread is blocked in OnStmtHit) and by
    /// Continue()/OnTestBoundary() to disarm; read by OnStmtHit (the AL thread). Safe
    /// without a lock: writers only ever run while the AL thread is either blocked on
    /// the semaphore or not running at all (between tests), and SemaphoreSlim.Release/
    /// Wait supply the happens-before edge that makes the write visible before the
    /// woken thread's next read.</summary>
    private static volatile AlDapStepKind _stepKind = AlDapStepKind.None;

    /// <summary>ParentScope-chain depth (see ComputeChainDepth) of the scope a step
    /// command was issued from — the D in the qualifying-depth comparisons documented
    /// in this file's header.</summary>
    private static volatile int _stepFromDepth = int.MinValue;

    /// <summary>
    /// Fired synchronously ON THE AL EXECUTION THREAD, before it blocks — the caller
    /// (DapServer) uses this to push the DAP "stopped" event over the wire. Must not
    /// throw: an exception here would propagate into BC's own StmtHit call. The reason
    /// is "breakpoint" or "step" (DAP's StoppedEvent.reason values), matching whichever
    /// condition actually caused this particular pause — see OnStmtHit.
    /// </summary>
    public static event Action<NavMethodScope, int, string>? Stopped;

    /// <summary>Resets all state for a new session — breakpoints, any stale pause, the
    /// detached flag, any armed step. Call before a fresh --dap run starts.</summary>
    public static void Reset()
    {
        lock (_bpLock) _breakpoints.Clear();
        _pauseGate = null;
        _pausedScope = null;
        _pausedStatement = -1;
        _detached = false;
        _stepKind = AlDapStepKind.None;
        _stepFromDepth = int.MinValue;
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

    /// <summary>Releases a paused AL execution thread (DAP `continue`) without arming
    /// any step — only a registered breakpoint can pause it again.</summary>
    public static void Continue()
    {
        _stepKind = AlDapStepKind.None;
        _pauseGate?.Release();
    }

    /// <summary>DAP `stepIn`: arms "pause at the very next StmtHit, at any depth" and
    /// releases the paused thread.</summary>
    public static void StepIn() => ArmStep(AlDapStepKind.In);

    /// <summary>DAP `next` (step over): arms "pause at the next StmtHit at the same or
    /// a shallower chain depth than the paused frame" and releases the paused thread.</summary>
    public static void StepOver() => ArmStep(AlDapStepKind.Over);

    /// <summary>DAP `stepOut`: arms "pause at the next StmtHit strictly shallower
    /// (chain depth) than the paused frame" and releases the paused thread.</summary>
    public static void StepOut() => ArmStep(AlDapStepKind.Out);

    private static void ArmStep(AlDapStepKind kind)
    {
        var scope = _pausedScope;
        // If nothing is paused (defensive; the DAP loop only calls this while
        // IsPaused), there is no depth to compare against and no thread to release
        // either — arm nothing rather than compare against a meaningless depth.
        _stepFromDepth = scope != null ? ComputeChainDepth(scope) : int.MinValue;
        _stepKind = kind;
        Trace($"ARM kind={kind} fromDepth={_stepFromDepth} pausedScope={scope?.GetType().Name ?? "<null>"} pausedStmt={_pausedStatement}");
        _pauseGate?.Release();
    }

    /// <summary>Call once per completed test, between tests, in a --dap run (see
    /// RunDapLoop's dapRunStep onTestComplete callback). A step command that never
    /// found a qualifying StmtHit within the test it was issued for must not silently
    /// carry over and pause some LATER, unrelated test purely because that test's
    /// early statements happen to run at a chain depth the old comparison still
    /// accepts — a fresh top-level call always starts back at chain depth 1, so a
    /// stale armed step is a live hazard, not just clutter.</summary>
    public static void OnTestBoundary()
    {
        if (_stepKind != AlDapStepKind.None)
            Trace($"BOUNDARY disarming a step still armed at test end: kind={_stepKind} fromDepth={_stepFromDepth} — MISSED, no qualifying StmtHit arrived before the test finished");
        _stepKind = AlDapStepKind.None;
        _stepFromDepth = int.MinValue;
    }

    /// <summary>Permanently stops pausing (DAP `disconnect`/`terminate`) and releases
    /// any thread currently blocked — an AL execution thread must never be left stuck
    /// forever just because the debug client went away (.claude/rules/loud-failures.md:
    /// no silent hang is acceptable either).</summary>
    public static void Detach()
    {
        if (_stepKind != AlDapStepKind.None)
            Trace($"DETACH with a step still armed: kind={_stepKind} fromDepth={_stepFromDepth}");
        _detached = true;
        _pauseGate?.Release();
    }

    /// <summary>
    /// Hook target for the Cecil-rewritten NavMethodScope.StmtHit(int)/CStmtHit(int[,
    /// bool]) — public static, exactly (NavMethodScope, int) so the rewrite can forward
    /// `ldarg.0; ldarg.1; call` unboxed, same shape as AlCoverageTracker.OnStmtHit. Runs
    /// on EVERY AL statement of every test, --dap or not — must stay near-zero-cost when
    /// disabled. The `!Enabled` check MUST stay first: everything after it, including
    /// the step-depth walk, only runs for an active --dap session.
    /// </summary>
    public static void OnStmtHit(NavMethodScope scope, int currentStatementNumber)
    {
        if (!Enabled || _detached) return;
        // Same ExitStatementNumber guard as AlCoverageTracker.OnStmtHit — Exit() writes
        // int.MaxValue directly, StmtHit never receives it from generated code.
        if (currentStatementNumber == int.MaxValue) return;

        bool breakpointHit;
        lock (_bpLock) breakpointHit = _breakpoints.Contains((scope.GetType(), currentStatementNumber));

        var stepKind = _stepKind;
        bool stepHit = false;
        if (stepKind != AlDapStepKind.None)
        {
            stepHit = StepQualifies(stepKind, scope);
            if (_traceEnabled)
            {
                var depth = ComputeChainDepth(scope);
                Trace($"EVAL kind={stepKind} fromDepth={_stepFromDepth} scope={scope.GetType().Name} stmt={currentStatementNumber} depth={depth} qualifies={stepHit}");
            }
        }

        if (!breakpointHit && !stepHit) return;

        // Breakpoints and steps are independent gates; if a step happens to land on a
        // registered breakpoint's statement too, report it as the more specific/
        // intentional "breakpoint" — matches mainstream debugger UX (an explicit
        // breakpoint always "wins" the displayed reason). Either way, disarm the step:
        // it has either been consumed or superseded by an explicit breakpoint.
        var reason = breakpointHit ? "breakpoint" : "step";
        _stepKind = AlDapStepKind.None;
        Trace($"FIRE reason={reason} scope={scope.GetType().Name} stmt={currentStatementNumber}");

        var gate = new System.Threading.SemaphoreSlim(0, 1);
        _pauseGate = gate;
        _pausedScope = scope;
        _pausedStatement = currentStatementNumber;
        try
        {
            Stopped?.Invoke(scope, currentStatementNumber, reason);
            Trace($"WAIT entering gate.Wait() reason={reason} scope={scope.GetType().Name} stmt={currentStatementNumber}");
            gate.Wait(); // blocks the AL execution thread until Continue()/Step*()/Detach()
            Trace($"RESUME left gate.Wait() reason={reason} scope={scope.GetType().Name} stmt={currentStatementNumber}");
        }
        finally
        {
            _pausedScope = null;
            _pausedStatement = -1;
            _pauseGate = null;
        }
    }

    private static bool StepQualifies(AlDapStepKind kind, NavMethodScope scope)
    {
        if (kind == AlDapStepKind.In) return true;
        var depth = ComputeChainDepth(scope);
        return kind == AlDapStepKind.Over ? depth <= _stepFromDepth : depth < _stepFromDepth;
    }

    /// <summary>Counts NavMethodScope frames from <paramref name="scope"/> outward via
    /// ParentScope (public) until IsRootScope (public) — the SAME walk
    /// AlDapStackWalker.Walk already performs to build stack frames, reused here purely
    /// for its length rather than its content. See this file's header for why this,
    /// not NavMethodScope's own (internal) StackDepth, is the correct nesting signal
    /// for step granularity.</summary>
    private static int ComputeChainDepth(NavMethodScope scope)
    {
        var depth = 0;
        var cur = scope;
        while (cur != null && !cur.IsRootScope)
        {
            depth++;
            cur = cur.ParentScope;
        }
        return depth;
    }
}
