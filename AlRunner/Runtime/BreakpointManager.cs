namespace AlRunner.Runtime;

/// <summary>
/// Arguments passed to the <see cref="BreakpointManager.BreakpointHit"/> event when
/// execution pauses at a registered breakpoint.
/// </summary>
public sealed class BreakpointHitArgs
{
    /// <summary>The C# scope class name (e.g. <c>Codeunit1_MyProc_Scope_abc</c>).</summary>
    public required string ScopeName { get; init; }

    /// <summary>The statement ID emitted by the BC compiler at the paused statement.</summary>
    public required int StmtId { get; init; }
}

/// <summary>
/// Runtime breakpoint coordinator for al-runner's DAP debugger support.
///
/// Design:
/// - Callers register (scopeName, stmtId) pairs as breakpoints.
/// - The BC compiler emits <c>StmtHit(N)</c> / <c>CStmtHit(N)</c> at every
///   AL statement; <see cref="AlScope.StmtHit"/> calls
///   <see cref="CheckHit"/> so the executing thread pauses if a matching
///   breakpoint is registered.
/// - A single <see cref="SemaphoreSlim"/> serialises pause/resume so only
///   one thread can be paused at a time — sufficient for al-runner's
///   single-threaded test executor.
/// - The DAP server layer maps (AL source file, AL line) → (scopeName, stmtId)
///   before calling <see cref="RegisterBreakpoint"/>; this class is
///   intentionally unaware of source-file paths.
/// </summary>
public static class BreakpointManager
{
    private static volatile bool _enabled;
    private static volatile bool _isPaused;

    // Maps registered (scopeName, stmtId) → true
    private static readonly HashSet<(string Scope, int StmtId)> _breakpoints = new();
    private static readonly object _lock = new();

    // Semaphore that the executing thread waits on when a breakpoint is hit.
    // Initial count 0 → the wait blocks immediately. Continue() releases once.
    private static SemaphoreSlim _pauseSemaphore = new(0, 1);

    /// <summary>
    /// Raised on the executing thread immediately before it blocks.
    /// Subscribers (e.g. the DAP server) can use this to send a DAP
    /// <c>stopped</c> event to the connected IDE.
    /// </summary>
    public static event Action<BreakpointHitArgs>? BreakpointHit;

    /// <summary>True while the executing thread is paused at a breakpoint.</summary>
    public static bool IsPaused => _isPaused;

    /// <summary>Enable breakpoint checking. No-op when already enabled.</summary>
    public static void Enable() => _enabled = true;

    /// <summary>Disable breakpoint checking. Running code is never blocked.</summary>
    public static void Disable() => _enabled = false;

    /// <summary>
    /// Register a (scopeName, stmtId) pair as a breakpoint.
    /// Duplicate registrations are silently ignored.
    /// </summary>
    public static void RegisterBreakpoint(string scopeName, int stmtId)
    {
        lock (_lock)
            _breakpoints.Add((scopeName, stmtId));
    }

    /// <summary>Remove all registered breakpoints.</summary>
    public static void ClearBreakpoints()
    {
        lock (_lock)
            _breakpoints.Clear();
    }

    /// <summary>Remove all subscribers from the <see cref="BreakpointHit"/> event.</summary>
    public static void ClearBreakpointHitHandlers() => BreakpointHit = null;

    /// <summary>
    /// Called from <see cref="AlScope.StmtHit"/> / <see cref="AlScope.CStmtHit"/>
    /// on every executed statement. Blocks the calling thread if a matching
    /// breakpoint is registered and the manager is enabled.
    /// </summary>
    public static void CheckHit(string scopeName, int stmtId)
    {
        if (!_enabled) return;

        bool shouldPause;
        lock (_lock)
            shouldPause = _breakpoints.Contains((scopeName, stmtId));

        if (!shouldPause) return;

        _isPaused = true;
        BreakpointHit?.Invoke(new BreakpointHitArgs { ScopeName = scopeName, StmtId = stmtId });
        _pauseSemaphore.Wait();
        _isPaused = false;
    }

    /// <summary>
    /// Unblock the paused execution thread. No-op if nothing is paused.
    /// Called by the DAP server when the IDE sends a <c>continue</c> request.
    /// </summary>
    public static void Continue()
    {
        if (_pauseSemaphore.CurrentCount == 0)
            _pauseSemaphore.Release();
    }

    /// <summary>
    /// Reset all state: clears breakpoints, releases any paused thread,
    /// and disables checking. Called between test runs and in tests.
    /// </summary>
    public static void Reset()
    {
        _enabled = false;
        ClearBreakpoints();
        // Drain the semaphore so a previously paused thread can exit
        while (_pauseSemaphore.CurrentCount < 1)
            _pauseSemaphore.Release();
        _isPaused = false;
        BreakpointHit = null;
        // Replace semaphore to reset count to 0 for the next Enable()
        _pauseSemaphore = new SemaphoreSlim(0, 1);
    }
}
