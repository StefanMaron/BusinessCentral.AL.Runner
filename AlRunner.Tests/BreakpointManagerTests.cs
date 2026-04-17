using AlRunner.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Tests for BreakpointManager — the runtime pause/resume core for DAP debugger support.
/// These tests prove that:
/// - Execution blocks at a registered breakpoint
/// - Execution resumes after Continue()
/// - Unregistered statements are not blocked
/// - Disable() suppresses all breakpoint checks
/// </summary>
public class BreakpointManagerTests : IDisposable
{
    public BreakpointManagerTests()
    {
        BreakpointManager.Reset();
        BreakpointManager.Enable();
    }

    public void Dispose()
    {
        BreakpointManager.Reset();
    }

    // ── Positive: pause then resume ──────────────────────────────────────────

    [Fact]
    public async Task CheckHit_AtRegisteredBreakpoint_BlocksUntilContinue()
    {
        BreakpointManager.RegisterBreakpoint("Codeunit1_MyProc_Scope_abc", stmtId: 5);

        bool afterBreakpoint = false;
        var executionTask = Task.Run(() =>
        {
            BreakpointManager.CheckHit("Codeunit1_MyProc_Scope_abc", 5);
            afterBreakpoint = true;
        });

        // Give the task time to hit and block
        await Task.Delay(80);
        Assert.False(afterBreakpoint, "Execution should be paused at breakpoint");

        // Unblock
        BreakpointManager.Continue();

        var completed = await Task.WhenAny(executionTask, Task.Delay(2000));
        Assert.Same(executionTask, completed);
        Assert.True(afterBreakpoint, "Execution must continue past breakpoint after Continue()");
    }

    [Fact]
    public async Task CheckHit_FiresBreakpointHitEvent_WithCorrectArgs()
    {
        BreakpointManager.RegisterBreakpoint("Codeunit2_OtherProc_Scope_xyz", stmtId: 12);

        BreakpointHitArgs? capturedArgs = null;
        BreakpointManager.BreakpointHit += args => capturedArgs = args;

        var executionTask = Task.Run(() =>
        {
            BreakpointManager.CheckHit("Codeunit2_OtherProc_Scope_xyz", 12);
        });

        await Task.Delay(80);
        Assert.NotNull(capturedArgs);
        Assert.Equal("Codeunit2_OtherProc_Scope_xyz", capturedArgs.ScopeName);
        Assert.Equal(12, capturedArgs.StmtId);

        BreakpointManager.Continue();
        await executionTask;
    }

    // ── Negative: non-matching statement is not blocked ──────────────────────

    [Fact]
    public async Task CheckHit_AtUnregisteredStatement_DoesNotBlock()
    {
        BreakpointManager.RegisterBreakpoint("Codeunit1_MyProc_Scope_abc", stmtId: 5);

        bool completed = false;
        await Task.Run(() =>
        {
            // Different stmtId — must NOT block
            BreakpointManager.CheckHit("Codeunit1_MyProc_Scope_abc", 99);
            completed = true;
        });

        Assert.True(completed, "Non-matching statement must pass through without blocking");
    }

    [Fact]
    public async Task CheckHit_DifferentScopeMatchingStmtId_DoesNotBlock()
    {
        BreakpointManager.RegisterBreakpoint("Codeunit1_MyProc_Scope_abc", stmtId: 5);

        bool completed = false;
        await Task.Run(() =>
        {
            // Different scope name — must NOT block
            BreakpointManager.CheckHit("Codeunit99_Other_Scope_def", 5);
            completed = true;
        });

        Assert.True(completed, "Different scope with matching stmtId must not block");
    }

    // ── Disable suppresses all breakpoints ───────────────────────────────────

    [Fact]
    public async Task CheckHit_WhenDisabled_NeverBlocks()
    {
        BreakpointManager.RegisterBreakpoint("Codeunit1_MyProc_Scope_abc", stmtId: 5);
        BreakpointManager.Disable();

        bool completed = false;
        await Task.Run(() =>
        {
            BreakpointManager.CheckHit("Codeunit1_MyProc_Scope_abc", 5);
            completed = true;
        });

        Assert.True(completed, "Disabled manager must never block");
    }

    // ── ClearBreakpoints removes all registrations ───────────────────────────

    [Fact]
    public async Task ClearBreakpoints_RemovesAllRegistrations_NoLongerBlocks()
    {
        BreakpointManager.RegisterBreakpoint("Codeunit1_MyProc_Scope_abc", stmtId: 5);
        BreakpointManager.ClearBreakpoints();

        bool completed = false;
        await Task.Run(() =>
        {
            BreakpointManager.CheckHit("Codeunit1_MyProc_Scope_abc", 5);
            completed = true;
        });

        Assert.True(completed, "Cleared breakpoints must not block");
    }

    // ── IsPaused reflects pause state ────────────────────────────────────────

    [Fact]
    public async Task IsPaused_ReturnsTrueWhilePaused_FalseAfterContinue()
    {
        BreakpointManager.RegisterBreakpoint("Codeunit1_MyProc_Scope_abc", stmtId: 5);

        var executionTask = Task.Run(() =>
        {
            BreakpointManager.CheckHit("Codeunit1_MyProc_Scope_abc", 5);
        });

        await Task.Delay(80);
        Assert.True(BreakpointManager.IsPaused, "IsPaused must be true while blocked");

        BreakpointManager.Continue();
        await executionTask;

        Assert.False(BreakpointManager.IsPaused, "IsPaused must be false after Continue()");
    }
}
