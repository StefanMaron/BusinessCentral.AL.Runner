using System.Collections.Generic;
using AlRunner;
using AlRunner.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Unit tests for per-scope-type last-statement tracking in <see cref="AlScope"/>.
///
/// The runner emits StmtHit(N) for every AL statement. For a useful AL-level call
/// stack, each scope class on the .NET call stack needs to remember the last
/// statement it executed — not just the single global "most-recent" statement.
/// This enables FormatStackFrames to emit:
///   at Codeunit "Name".Proc() line N in File.al
/// for each frame.
/// </summary>
public class AlScopeTrackingTests
{
    /// <summary>
    /// Minimal concrete scope used to exercise protected StmtHit/CStmtHit methods.
    /// </summary>
    private sealed class StubScopeA : AlScope
    {
        protected override void OnRun() { }
        public void HitStmt(int n) => StmtHit(n);
        public bool HitCStmt(int n) => CStmtHit(n);
    }

    private sealed class StubScopeB : AlScope
    {
        protected override void OnRun() { }
        public void HitStmt(int n) => StmtHit(n);
    }

    public AlScopeTrackingTests()
    {
        // Reset between tests to avoid interference.
        AlScope.ResetLastStatement();
    }

    // ── Positive cases ─────────────────────────────────────────────────────

    /// <summary>
    /// Positive: StmtHit updates GetLastStmtForScope for the calling scope type.
    /// </summary>
    [Fact]
    public void StmtHit_SetsLastStmtForScope()
    {
        var scope = new StubScopeA();
        scope.HitStmt(42);

        var result = AlScope.GetLastStmtForScope("StubScopeA");
        Assert.Equal(42, result);
    }

    /// <summary>
    /// Positive: CStmtHit also updates GetLastStmtForScope.
    /// </summary>
    [Fact]
    public void CStmtHit_SetsLastStmtForScope()
    {
        var scope = new StubScopeA();
        scope.HitCStmt(7);

        Assert.Equal(7, AlScope.GetLastStmtForScope("StubScopeA"));
    }

    /// <summary>
    /// Positive: each subsequent StmtHit overwrites the previous entry for the same scope.
    /// </summary>
    [Fact]
    public void StmtHit_OverwritesPreviousEntry()
    {
        var scope = new StubScopeA();
        scope.HitStmt(1);
        scope.HitStmt(2);
        scope.HitStmt(99);

        Assert.Equal(99, AlScope.GetLastStmtForScope("StubScopeA"));
    }

    /// <summary>
    /// Positive: two different scope classes are tracked independently.
    /// Simulates two procedures on the call stack, each at a different statement.
    /// </summary>
    [Fact]
    public void TwoScopes_TrackedIndependently()
    {
        var scopeA = new StubScopeA();
        var scopeB = new StubScopeB();

        scopeA.HitStmt(10);
        scopeB.HitStmt(20);

        Assert.Equal(10, AlScope.GetLastStmtForScope("StubScopeA"));
        Assert.Equal(20, AlScope.GetLastStmtForScope("StubScopeB"));
    }

    /// <summary>
    /// Positive: GetScopeTracking returns a snapshot of the current state.
    /// The snapshot is independent of subsequent changes.
    /// </summary>
    [Fact]
    public void GetScopeTracking_ReturnsSnapshot()
    {
        var scope = new StubScopeA();
        scope.HitStmt(5);

        var snapshot = AlScope.GetScopeTracking();
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.ContainsKey("StubScopeA"));
        Assert.Equal(5, snapshot["StubScopeA"]);

        // Modifying the snapshot does not affect the live state
        snapshot["StubScopeA"] = 999;
        Assert.Equal(5, AlScope.GetLastStmtForScope("StubScopeA"));
    }

    /// <summary>
    /// Positive: SetScopeTracking restores scope state — simulates cross-thread propagation.
    /// </summary>
    [Fact]
    public void SetScopeTracking_RestoresState()
    {
        var propagated = new Dictionary<string, int>
        {
            ["StubScopeA"] = 77,
            ["StubScopeB"] = 88
        };

        AlScope.SetScopeTracking(propagated);

        Assert.Equal(77, AlScope.GetLastStmtForScope("StubScopeA"));
        Assert.Equal(88, AlScope.GetLastStmtForScope("StubScopeB"));
    }

    // ── Negative cases ────────────────────────────────────────────────────

    /// <summary>
    /// Negative: GetLastStmtForScope returns null for a scope that has not hit any statement.
    /// </summary>
    [Fact]
    public void GetLastStmtForScope_UnknownScope_ReturnsNull()
    {
        Assert.Null(AlScope.GetLastStmtForScope("NonExistentScope_Scope_abc123"));
    }

    /// <summary>
    /// Negative: ResetLastStatement clears ALL per-scope tracking (not just LastStatementHit).
    /// After reset, GetLastStmtForScope returns null even for a previously hit scope.
    /// </summary>
    [Fact]
    public void ResetLastStatement_ClearsScopeTracking()
    {
        var scope = new StubScopeA();
        scope.HitStmt(42);

        // Confirm it was set
        Assert.Equal(42, AlScope.GetLastStmtForScope("StubScopeA"));

        AlScope.ResetLastStatement();

        // After reset, scope tracking is gone
        Assert.Null(AlScope.GetLastStmtForScope("StubScopeA"));
        Assert.Null(AlScope.LastStatementHit);
    }

    /// <summary>
    /// Negative: GetScopeTracking returns null when no statements have been hit.
    /// </summary>
    [Fact]
    public void GetScopeTracking_NoHits_ReturnsNull()
    {
        // After reset, no scope tracking exists
        Assert.Null(AlScope.GetScopeTracking());
    }

    /// <summary>
    /// Negative: SetScopeTracking(null) clears all scope tracking.
    /// </summary>
    [Fact]
    public void SetScopeTracking_Null_ClearsState()
    {
        var scope = new StubScopeA();
        scope.HitStmt(10);

        AlScope.SetScopeTracking(null);

        Assert.Null(AlScope.GetLastStmtForScope("StubScopeA"));
    }
}
