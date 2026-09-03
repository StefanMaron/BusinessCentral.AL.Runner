// #2056: AlIterationSegmenter, the state machine behind iterationTracking, driven with
// synthetic hit streams. Each stream is the statement-id sequence BC actually emits for
// that loop shape (read off al-runner's statement table for the AL in
// ServerExecuteIterationsTests). No BC artifact needed.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class AlIterationSegmenterTests
{
    // --- Fixture helpers -------------------------------------------------------------

    private static AlLoopSiteTable Site(
        int index, AlLoopKind kind, int[] header, int[] body,
        int? marker = null, int? markerNested = null, string? loopVar = null,
        int? parent = null, string? unsegmentable = null) =>
        new(index, kind, loopVar, startLine: 10 + index, startColumn: 8, endLine: 20 + index, endColumn: 12,
            headerIds: header.ToHashSet(), bodyIds: body.ToHashSet(),
            markerStatementId: marker, markerNestedSiteIndex: markerNested,
            parentIndex: parent, unsegmentable: unsegmentable);

    private static AlLoopScopeTable Table(params AlLoopSiteTable[] sites) =>
        new(sites, spans: Array.Empty<long>());

    private static AlCapturedValue V(string name, object value, int stmt) =>
        new("S", name, value, stmt);

    private static readonly IReadOnlyList<AlCapturedValue> None = Array.Empty<AlCapturedValue>();

    /// <summary>Feeds a plain hit stream (no captures) for ONE scope instance.</summary>
    private static void Hits(AlIterationSegmenter seg, object scope, AlLoopScopeTable table, params int[] ids)
    {
        foreach (var id in ids) seg.OnHit(scope, table, id, None);
    }

    private static int[] Stmts(AlIterationSegmenter.Step s) => s.StatementIds.Distinct().OrderBy(x => x).ToArray();

    // --- for: one header hit at entry, body per iteration -----------------------------

    [Fact]
    public void ForLoop_ThreeBodyHits_ThreeIterations_ExitOnFirstOutsideHit()
    {
        // for i := 1 to 3 do t := t + i;   t := t * 10;
        // ids: 0 = t := 0 | 1 = for (once) | 2 = body | 3 = t := t * 10
        var table = Table(Site(0, AlLoopKind.For, header: new[] { 1 }, body: new[] { 2 }, marker: 2, loopVar: "i"));
        var seg = new AlIterationSegmenter();
        var scope = new object();
        Hits(seg, scope, table, 0, 1, 2, 2, 2, 3);

        var loops = seg.Finish();
        var loop = Assert.Single(loops);
        Assert.Equal(0, loop.Id);
        Assert.Equal(3, loop.IterationCount);
        Assert.Equal(new[] { 1, 2, 3 }, loop.Steps.Select(s => s.Iteration).ToArray());
        Assert.All(loop.Steps, s => Assert.Equal(new[] { 2 }, Stmts(s)));
        Assert.Null(loop.ParentId);
        Assert.Null(loop.ParentIteration);
        Assert.Same(scope, loop.ScopeInstance);
    }

    [Fact]
    public void ForLoop_HeaderHitOnly_ReportsZeroIterations_NotAbsent()
    {
        // for i := 1 to 0 do s := s + 1;   s := 7;   → header hit, body never, next stmt.
        var table = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1 }, marker: 1, loopVar: "i"));
        var seg = new AlIterationSegmenter();
        Hits(seg, new object(), table, 0, 2);

        var loop = Assert.Single(seg.Finish());
        Assert.Equal(0, loop.IterationCount);
        Assert.Empty(loop.Steps);
    }

    // --- while: condition hit per evaluation, including the final false one -----------

    [Fact]
    public void WhileLoop_ConditionHitPerEvaluation_CountsBodyEntriesOnly()
    {
        // n := 3; while n > 0 do begin n := n - 1; end; n := 99;
        // ids: 0 | 1 = cond (x4) | 2 = body (x3) | 3 = block end | 4
        var table = Table(Site(0, AlLoopKind.While, header: new[] { 1 }, body: new[] { 2 }, marker: 2));
        var seg = new AlIterationSegmenter();
        Hits(seg, new object(), table, 0, 1, 2, 1, 2, 1, 2, 1, 3, 4);

        var loop = Assert.Single(seg.Finish());
        Assert.Equal(3, loop.IterationCount);
        // The condition hit that ENDS an iteration is recorded in that iteration; the
        // very first evaluation (before iteration 1) belongs to no iteration.
        Assert.All(loop.Steps, s => Assert.Equal(new[] { 1, 2 }, Stmts(s)));
    }

    // --- repeat: body first, until-condition after each pass --------------------------

    [Fact]
    public void RepeatLoop_UntilConditionAfterBody_ThreeIterations()
    {
        // n := 0; repeat n := n + 1; until n >= 3; n := 99;
        // ids: 0 | 1 = body (x3) | 2 = until cond (x3) | 3
        var table = Table(Site(0, AlLoopKind.Repeat, header: new[] { 2 }, body: new[] { 1 }, marker: 1));
        var seg = new AlIterationSegmenter();
        Hits(seg, new object(), table, 0, 1, 2, 1, 2, 1, 2, 3);

        var loop = Assert.Single(seg.Finish());
        Assert.Equal(3, loop.IterationCount);
        Assert.All(loop.Steps, s => Assert.Equal(new[] { 1, 2 }, Stmts(s)));
    }

    // --- nesting -----------------------------------------------------------------------

    [Fact]
    public void NestedFor_OneInnerInstancePerOuterIteration_WithParentLinkage()
    {
        // for i := 1 to 2 do begin s += 100; for j := 1 to 2 do s += j; s += 1000; end;
        // ids: 0 = outer for | 1 | 2 = inner for | 3 = inner body | 4 | 5 = block end
        var outer = Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1, 2, 3, 4 }, marker: 1, loopVar: "i");
        var inner = Site(1, AlLoopKind.For, header: new[] { 2 }, body: new[] { 3 }, marker: 3, loopVar: "j", parent: 0);
        var table = Table(outer, inner);
        var seg = new AlIterationSegmenter();
        Hits(seg, new object(), table, 0, 1, 2, 3, 3, 4, 1, 2, 3, 3, 4, 5);

        var loops = seg.Finish();
        Assert.Equal(3, loops.Count); // one outer instance + one inner instance per outer iteration
        var o = loops[0];
        Assert.Equal(2, o.IterationCount);
        Assert.Null(o.ParentId);
        Assert.Equal(new[] { 1, 2, 3, 4 }, Stmts(o.Steps[0]));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Stmts(o.Steps[1]));

        var i1 = loops[1];
        Assert.Equal(o.Id, i1.ParentId);
        Assert.Equal(1, i1.ParentIteration);
        Assert.Equal(2, i1.IterationCount);
        Assert.All(i1.Steps, s => Assert.Equal(new[] { 3 }, Stmts(s)));

        var i2 = loops[2];
        Assert.Equal(o.Id, i2.ParentId);
        Assert.Equal(2, i2.ParentIteration);
        Assert.Equal(2, i2.IterationCount);
        Assert.NotEqual(i1.Id, i2.Id);
    }

    [Fact]
    public void NestedWhileAsFirstBodyStatement_OuterIterationOpensOnInnerEntry_NotOnEveryConditionHit()
    {
        // for i := 1 to 2 do begin while n < 2 do n := n + 1; n := 0; s := s + 1; end;
        // ids: 0 = outer for | 1 = while cond (x3 per outer iter) | 2 = while body | 3 | 4 | 5 = end
        var outer = Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1, 2, 3, 4 }, markerNested: 1, loopVar: "i");
        var inner = Site(1, AlLoopKind.While, header: new[] { 1 }, body: new[] { 2 }, marker: 2, parent: 0);
        var table = Table(outer, inner);
        var seg = new AlIterationSegmenter();
        Hits(seg, new object(), table,
            0,
            1, 2, 1, 2, 1, 3, 4,   // outer iteration 1: inner runs twice (3 cond evals)
            1, 2, 1, 2, 1, 3, 4,   // outer iteration 2
            5);

        var loops = seg.Finish();
        Assert.Equal(3, loops.Count);
        Assert.Equal(2, loops[0].IterationCount); // NOT 6 — inner condition hits are not outer boundaries
        Assert.Equal(2, loops[1].IterationCount);
        Assert.Equal(1, loops[1].ParentIteration);
        Assert.Equal(2, loops[2].IterationCount);
        Assert.Equal(2, loops[2].ParentIteration);
    }

    [Fact]
    public void RepeatWhoseFirstStatementIsAFor_SingleHitEntersBothLoops()
    {
        // repeat for k := 1 to 2 do x += k; n += 1; until n >= 2;
        // ids: 0 = inner for (header, once per outer pass) | 1 = inner body | 2 = n += 1 | 3 = until
        var outer = Site(0, AlLoopKind.Repeat, header: new[] { 3 }, body: new[] { 0, 1, 2 }, markerNested: 1);
        var inner = Site(1, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1 }, marker: 1, loopVar: "k", parent: 0);
        var table = Table(outer, inner);
        var seg = new AlIterationSegmenter();
        Hits(seg, new object(), table, 0, 1, 1, 2, 3, 0, 1, 1, 2, 3, 4);

        var loops = seg.Finish();
        Assert.Equal(3, loops.Count);
        Assert.Equal(2, loops[0].IterationCount);
        Assert.Equal(2, loops[1].IterationCount);
        Assert.Equal(1, loops[1].ParentIteration);
        Assert.Equal(2, loops[2].IterationCount);
        Assert.Equal(2, loops[2].ParentIteration);
    }

    // --- early exits ---------------------------------------------------------------------

    [Fact]
    public void Break_FirstHitOutsideTheLoopClosesTheLastIteration_AndTakesItsCaptures()
    {
        // for i := 1 to 10 do begin s := s + i; if i = 2 then break; end; s := -1;
        // ids: 0 = for | 1 = s := s + i | 2 = if cond | (break: no id) | 3 = block end | 4
        var table = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1, 2 }, marker: 1, loopVar: "i"));
        var seg = new AlIterationSegmenter();
        var scope = new object();
        seg.OnHit(scope, table, 0, None);
        seg.OnHit(scope, table, 1, new[] { V("i", 1, 0) });
        seg.OnHit(scope, table, 2, new[] { V("s", 1, 1) });
        seg.OnHit(scope, table, 1, new[] { V("i", 2, 2) });
        seg.OnHit(scope, table, 2, new[] { V("s", 3, 1) });
        seg.OnHit(scope, table, 3, None);                 // block end: first id outside header/body
        seg.OnHit(scope, table, 4, new[] { V("s", -1, 3) });

        var loop = Assert.Single(seg.Finish());
        Assert.Equal(2, loop.IterationCount);
        Assert.Equal(new object[] { 1, 1 }, loop.Steps[0].Captures.Select(c => c.Value).ToArray()); // i=1, s=1
        Assert.Equal(new object[] { 2, 3 }, loop.Steps[1].Captures.Select(c => c.Value).ToArray()); // i=2, s=3
        // s := -1 ran AFTER the loop: not in any step.
        Assert.DoesNotContain(loop.Steps.SelectMany(s => s.Captures), c => Equals(c.Value, -1));
    }

    [Fact]
    public void ScopeExit_ClosesOpenLoop_AndAttachesTheFinalCapturesToItsLastIteration()
    {
        // for i := 1 to 10 do begin s := s + i; if i = 2 then exit; end; s := -1;
        // ids: 0 = for | 1 | 2 = if cond | 3 = exit | 4 = end (never hit) | 5 (never hit)
        var table = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1, 2, 3 }, marker: 1, loopVar: "i"));
        var seg = new AlIterationSegmenter();
        var scope = new object();
        Hits(seg, scope, table, 0, 1, 2, 1, 2, 3);
        seg.OnScopeExit(scope, new[] { V("s", 3, 3) });

        var loop = Assert.Single(seg.Finish());
        Assert.Equal(2, loop.IterationCount);
        Assert.Equal(new[] { 1, 2, 3 }, Stmts(loop.Steps[1]));
        var last = Assert.Single(loop.Steps[1].Captures);
        Assert.Equal("s", last.VariableName);
        Assert.Equal(3, last.Value);
    }

    [Fact]
    public void Finish_WithoutScopeExit_StillClosesEverything()
    {
        // An AL Error thrown mid-loop may never reach a StmtHit outside the loop.
        var table = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1 }, marker: 1, loopVar: "i"));
        var seg = new AlIterationSegmenter();
        Hits(seg, new object(), table, 0, 1, 1);

        var loop = Assert.Single(seg.Finish());
        Assert.Equal(2, loop.IterationCount);
        Assert.Equal(2, loop.Steps.Count);
    }

    // --- capture placement -----------------------------------------------------------------

    [Fact]
    public void ForLoop_CapturesObservedAtIterationStart_LoopVariableGoesToNewIteration_RestToPrevious()
    {
        // for i := 1 to 3 do s := s + i;   (s's change is observed at the NEXT body hit,
        // together with i's increment — they must land in different iterations)
        var table = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1 }, marker: 1, loopVar: "i"));
        var seg = new AlIterationSegmenter();
        var scope = new object();
        seg.OnHit(scope, table, 0, None);
        seg.OnHit(scope, table, 1, new[] { V("i", 1, 0) });
        seg.OnHit(scope, table, 1, new[] { V("s", 1, 1), V("i", 2, 1) });
        seg.OnHit(scope, table, 1, new[] { V("s", 3, 1), V("i", 3, 1) });
        seg.OnHit(scope, table, 2, new[] { V("s", 6, 1) });

        var loop = Assert.Single(seg.Finish());
        Assert.Equal(3, loop.IterationCount);
        Assert.Equal(new[] { "i=1", "s=1" }, loop.Steps[0].Captures.Select(c => $"{c.VariableName}={c.Value}").ToArray());
        Assert.Equal(new[] { "i=2", "s=3" }, loop.Steps[1].Captures.Select(c => $"{c.VariableName}={c.Value}").ToArray());
        Assert.Equal(new[] { "i=3", "s=6" }, loop.Steps[2].Captures.Select(c => $"{c.VariableName}={c.Value}").ToArray());
    }

    [Fact]
    public void ForLoop_LoopVariableObservedAtTheHeaderHit_BelongsToIterationOne()
    {
        // Measured: BC assigns `i := 1` BEFORE the `for` statement's own StmtHit, so the
        // loop variable's initial value is observed together with the pre-loop statement's
        // effect at the header hit, attributed to the statement before the loop.
        var table = Table(Site(0, AlLoopKind.For, header: new[] { 1 }, body: new[] { 2 }, marker: 2, loopVar: "i"));
        var seg = new AlIterationSegmenter();
        var scope = new object();
        seg.OnHit(scope, table, 0, None);
        seg.OnHit(scope, table, 1, new[] { V("t", 0, 0), V("i", 1, 0) }); // header: t is pre-loop, i is iteration 1's
        seg.OnHit(scope, table, 2, None);
        seg.OnHit(scope, table, 2, new[] { V("s", 1, 2), V("i", 2, 2) });
        seg.OnHit(scope, table, 3, new[] { V("s", 3, 2) });

        var loop = Assert.Single(seg.Finish());
        Assert.Equal(2, loop.IterationCount);
        Assert.Equal(new[] { "i=1", "s=1" }, loop.Steps[0].Captures.Select(c => $"{c.VariableName}={c.Value}").ToArray());
        Assert.Equal(new[] { "i=2", "s=3" }, loop.Steps[1].Captures.Select(c => $"{c.VariableName}={c.Value}").ToArray());
        Assert.DoesNotContain(loop.Steps.SelectMany(s => s.Captures), c => c.VariableName == "t");
    }

    [Fact]
    public void NestedFor_InnerLoopVariableObservedAtItsHeaderHit_BelongsToTheInnerFirstIteration_NotTheOuterStep()
    {
        // for i := 1 to 2 do begin s := s + 100; for j := 1 to 2 do s := s + j; s := s + 1000; end;
        // At the inner `for`'s header hit the observation holds BOTH the outer body's
        // `s := s + 100` effect (outer iteration) AND `j := 1` (inner iteration 1).
        var outer = Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1, 2, 3, 4 }, marker: 1, loopVar: "i");
        var inner = Site(1, AlLoopKind.For, header: new[] { 2 }, body: new[] { 3 }, marker: 3, loopVar: "j", parent: 0);
        var table = Table(outer, inner);
        var seg = new AlIterationSegmenter();
        var scope = new object();
        seg.OnHit(scope, table, 0, new[] { V("i", 1, 0) });
        seg.OnHit(scope, table, 1, None);
        seg.OnHit(scope, table, 2, new[] { V("s", 100, 1), V("j", 1, 1) });
        seg.OnHit(scope, table, 3, None);
        seg.OnHit(scope, table, 3, new[] { V("s", 101, 3), V("j", 2, 3) });
        seg.OnHit(scope, table, 4, new[] { V("s", 103, 3) });
        seg.OnHit(scope, table, 5, new[] { V("s", 1103, 4) });

        var loops = seg.Finish();
        Assert.Equal(2, loops.Count);
        Assert.Equal(new[] { "i=1", "s=100", "s=1103" }, loops[0].Steps[0].Captures.Select(c => $"{c.VariableName}={c.Value}").ToArray());
        Assert.Equal(new[] { "j=1", "s=101" }, loops[1].Steps[0].Captures.Select(c => $"{c.VariableName}={c.Value}").ToArray());
        Assert.Equal(new[] { "j=2", "s=103" }, loops[1].Steps[1].Captures.Select(c => $"{c.VariableName}={c.Value}").ToArray());
    }

    [Fact]
    public void WhileLoop_CapturesAtConditionCloseTheIteration_CapturesAtBodyStartOpenTheNext()
    {
        // n := 3; while Next(n) do n := n - 1;   — a condition with a side effect (r)
        // ids: 0 | 1 = cond | 2 = body | 3
        var table = Table(Site(0, AlLoopKind.While, header: new[] { 1 }, body: new[] { 2 }, marker: 2));
        var seg = new AlIterationSegmenter();
        var scope = new object();
        seg.OnHit(scope, table, 0, None);
        seg.OnHit(scope, table, 1, new[] { V("n", 3, 0) });     // pre-loop statement's effect: no iteration
        seg.OnHit(scope, table, 2, new[] { V("r", 1, 1) });     // condition's own side effect → iteration 1
        seg.OnHit(scope, table, 1, new[] { V("n", 2, 2) });     // body's effect → iteration 1
        seg.OnHit(scope, table, 2, new[] { V("r", 2, 1) });     // → iteration 2
        seg.OnHit(scope, table, 1, new[] { V("n", 1, 2) });     // → iteration 2
        seg.OnHit(scope, table, 3, new[] { V("r", 0, 1) });     // the terminating evaluation's effect: last pass

        var loop = Assert.Single(seg.Finish());
        Assert.Equal(2, loop.IterationCount);
        Assert.Equal(new[] { "r=1", "n=2" }, loop.Steps[0].Captures.Select(c => $"{c.VariableName}={c.Value}").ToArray());
        Assert.Equal(new[] { "r=2", "n=1", "r=0" }, loop.Steps[1].Captures.Select(c => $"{c.VariableName}={c.Value}").ToArray());
        Assert.DoesNotContain(loop.Steps.SelectMany(s => s.Captures), c => c.VariableName == "n" && Equals(c.Value, 3));
    }

    // --- cross-scope (a loop body calling a procedure that itself loops) ------------------

    [Fact]
    public void CalleeLoop_GetsTheCallersActiveLoopAsDynamicParent_AndCallerStateSurvivesTheCall()
    {
        // caller: for i := 1 to 2 do s := s + Inner();   callee Inner: for k := 1 to 2 do s := s + k;
        var callerTable = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1 }, marker: 1, loopVar: "i"));
        var calleeTable = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1 }, marker: 1, loopVar: "k"));
        var seg = new AlIterationSegmenter();
        var caller = new object();

        seg.OnHit(caller, callerTable, 0, None);
        for (int outer = 1; outer <= 2; outer++)
        {
            seg.OnHit(caller, callerTable, 1, None);        // caller iteration `outer` opens, then calls Inner()
            var callee = new object();                        // a fresh scope instance per call
            Hits(seg, callee, calleeTable, 0, 1, 1, 2);       // callee's own loop: 2 iterations, then exit(s)
            seg.OnScopeExit(callee, None);
        }
        seg.OnHit(caller, callerTable, 2, None);              // past the caller's loop
        seg.OnScopeExit(caller, None);

        var loops = seg.Finish();
        Assert.Equal(3, loops.Count);
        var callerLoop = loops[0];
        Assert.Equal(2, callerLoop.IterationCount);
        Assert.Null(callerLoop.ParentId);
        // The callee's own statements never leak into the caller's steps.
        Assert.All(callerLoop.Steps, s => Assert.Equal(new[] { 1 }, Stmts(s)));

        Assert.Equal(callerLoop.Id, loops[1].ParentId);
        Assert.Equal(1, loops[1].ParentIteration);
        Assert.Equal(2, loops[1].IterationCount);
        Assert.Equal(callerLoop.Id, loops[2].ParentId);
        Assert.Equal(2, loops[2].ParentIteration);
        Assert.Equal(2, loops[2].IterationCount);
    }

    // --- messages ------------------------------------------------------------------------------

    [Fact]
    public void Messages_AttachToTheInnermostOpenIteration_NeverToAClosedOne()
    {
        var table = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1 }, marker: 1, loopVar: "i"));
        var seg = new AlIterationSegmenter();
        var scope = new object();
        Hits(seg, scope, table, 0, 1);
        seg.OnMessage(new AlCapturedMessage("first", "S", 1));
        Hits(seg, scope, table, 1);
        seg.OnMessage(new AlCapturedMessage("second", "S", 1));
        Hits(seg, scope, table, 2);
        seg.OnMessage(new AlCapturedMessage("after", "S", 2));

        var loop = Assert.Single(seg.Finish());
        Assert.Equal(new[] { "first" }, loop.Steps[0].Messages.Select(m => m.Text).ToArray());
        Assert.Equal(new[] { "second" }, loop.Steps[1].Messages.Select(m => m.Text).ToArray());
        Assert.DoesNotContain(loop.Steps.SelectMany(s => s.Messages), m => m.Text == "after");
    }

    // --- negative direction ------------------------------------------------------------------

    [Fact]
    public void UnsegmentableSite_IsReportedWithNullCountAndTheReason_NeverAsZeroIterations()
    {
        // for i := 1 to 3 do ;   — an empty body has no instrumented statement to count on.
        var table = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: Array.Empty<int>(),
            loopVar: "i", unsegmentable: "loop body has no instrumented statement"));
        var seg = new AlIterationSegmenter();
        Hits(seg, new object(), table, 0, 1);

        var loop = Assert.Single(seg.Finish());
        Assert.Null(loop.IterationCount);
        Assert.Empty(loop.Steps);
        Assert.Equal("loop body has no instrumented statement", loop.Site.Unsegmentable);
    }

    [Fact]
    public void ScopeWithNoLoops_ProducesNoInstances()
    {
        var seg = new AlIterationSegmenter();
        Hits(seg, new object(), Table(), 0, 1, 2, 3);
        Assert.Empty(seg.Finish());
    }

    [Fact]
    public void HitsOutsideAnyLoop_AreNotRecordedAnywhere()
    {
        var table = Table(Site(0, AlLoopKind.For, header: new[] { 1 }, body: new[] { 2 }, marker: 2, loopVar: "i"));
        var seg = new AlIterationSegmenter();
        Hits(seg, new object(), table, 0, 1, 2, 3, 4, 5);

        var loop = Assert.Single(seg.Finish());
        Assert.Equal(1, loop.IterationCount);
        Assert.Equal(new[] { 2 }, Stmts(loop.Steps[0]));
    }

    // --- round 3: re-entry, stale frames, conditions, carries ------------------------------

    [Fact]
    public void SoleBodyNestedFor_HeaderHitOnTheActiveChild_IsAReentry_ThatOpensTheNextOuterPass()
    {
        // for i := 1 to 2 do for j := 1 to 2 do x += 1;   ids: 0 outer for | 1 inner for | 2 body | 3 after
        // No outer-owned id between passes: the second inner header hit is the only signal.
        var outer = Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1, 2 }, markerNested: 1, loopVar: "i");
        var inner = Site(1, AlLoopKind.For, header: new[] { 1 }, body: new[] { 2 }, marker: 2, loopVar: "j", parent: 0);
        var table = Table(outer, inner);
        var seg = new AlIterationSegmenter();
        Hits(seg, new object(), table, 0, 1, 2, 2, 1, 2, 2, 3);

        var loops = seg.Finish();
        Assert.Equal(3, loops.Count);
        Assert.Equal(2, loops[0].IterationCount);
        Assert.Equal(2, loops[1].IterationCount);
        Assert.Equal(1, loops[1].ParentIteration);
        Assert.Equal(2, loops[2].IterationCount);
        Assert.Equal(2, loops[2].ParentIteration);
    }

    [Fact]
    public void SoleBodyNestedWhile_ConditionAfterItsOwnBodyIsAReevaluation_AfterItsOwnHeaderIsAReentry()
    {
        // for i := 1 to 2 do while n < 2 do n := n + 1;   ids: 0 outer for | 1 cond | 2 body | 3 after
        var outer = Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1, 2 }, markerNested: 1, loopVar: "i");
        var inner = Site(1, AlLoopKind.While, header: new[] { 1 }, body: new[] { 2 }, marker: 2, parent: 0);
        var table = Table(outer, inner);
        var seg = new AlIterationSegmenter();
        Hits(seg, new object(), table,
            0,
            1, 2, 1, 2, 1,   // outer pass 1: two inner passes, then the false evaluation
            1, 2, 1, 2, 1,   // outer pass 2: the first `1` follows the inner's own header -> re-entry
            3);

        var loops = seg.Finish();
        Assert.Equal(3, loops.Count);
        Assert.Equal(2, loops[0].IterationCount);
        Assert.Equal(2, loops[1].IterationCount);
        Assert.Equal(2, loops[2].IterationCount);
        Assert.Equal(2, loops[2].ParentIteration);
    }

    [Fact]
    public void StaleCalleeInstance_IsUnwoundWhenTheCallerResumes_WithoutADuplicateCaller()
    {
        // The callee errors inside its loop; the error is caught by the caller and no scope
        // exit for the callee reaches us. The caller's next hit must not be parented to it.
        var callerTable = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1, 2 }, marker: 1, loopVar: "i"));
        var calleeTable = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1 }, marker: 1, loopVar: "k"));
        var seg = new AlIterationSegmenter();
        var caller = new object();
        var callee = new object();
        Hits(seg, caller, callerTable, 0, 1);
        Hits(seg, callee, calleeTable, 0, 1);      // errors here: no exit for the callee
        Hits(seg, caller, callerTable, 2, 1, 2, 3); // caller continues: rest of pass 1, pass 2, exit

        var loops = seg.Finish();
        Assert.Equal(2, loops.Count);
        Assert.Equal(2, loops[0].IterationCount);
        Assert.Equal(AlLoopEnd.Exit, loops[0].ClosedBy);
        Assert.Equal(new[] { 1, 2 }, Stmts(loops[0].Steps[0]));
        Assert.Equal(AlLoopEnd.Unfinished, loops[1].ClosedBy);
        Assert.Equal(1, loops[1].IterationCount);
    }

    [Fact]
    public void ScopeExit_WithStaleFramesAbove_ClosesThemAndTheScopesOwnInstances()
    {
        var callerTable = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1 }, marker: 1, loopVar: "i"));
        var calleeTable = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1 }, marker: 1, loopVar: "k"));
        var seg = new AlIterationSegmenter();
        var caller = new object();
        var callee = new object();
        Hits(seg, caller, callerTable, 0, 1);
        Hits(seg, callee, calleeTable, 0, 1);
        seg.OnScopeExit(caller, None);

        var loops = seg.Finish();
        Assert.Equal(AlLoopEnd.ScopeExit, loops[0].ClosedBy);
        Assert.Equal(AlLoopEnd.Unfinished, loops[1].ClosedBy);
    }

    [Fact]
    public void ChildEnteredFromACondition_GetsTheParentIterationThatConditionOpens()
    {
        // while Helper() do x += 1;   Helper has its own loop.
        var callerTable = Table(Site(0, AlLoopKind.While, header: new[] { 1 }, body: new[] { 2 }, marker: 2));
        var calleeTable = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1 }, marker: 1, loopVar: "k"));
        var seg = new AlIterationSegmenter();
        var caller = new object();
        void Helper()
        {
            var callee = new object();
            Hits(seg, callee, calleeTable, 0, 1, 2);
            seg.OnScopeExit(callee, None);
        }
        Hits(seg, caller, callerTable, 0, 1); Helper();   // condition 1 -> true
        Hits(seg, caller, callerTable, 2, 1); Helper();   // body pass 1; condition 2 -> true
        Hits(seg, caller, callerTable, 2, 1); Helper();   // body pass 2; condition 3 -> false
        Hits(seg, caller, callerTable, 3);
        seg.OnScopeExit(caller, None);

        var loops = seg.Finish();
        Assert.Equal(4, loops.Count);
        Assert.Equal(2, loops[0].IterationCount);
        Assert.Equal(1, loops[1].ParentIteration);   // opened pass 1
        Assert.Equal(2, loops[2].ParentIteration);   // opened pass 2
        Assert.Equal(2, loops[3].ParentIteration);   // terminating evaluation: the pass that ended
    }

    [Fact]
    public void ChildEnteredBeforeTheParentsFirstPass_AndTheLoopNeverRuns_HasNoParentIteration()
    {
        var callerTable = Table(Site(0, AlLoopKind.While, header: new[] { 1 }, body: new[] { 2 }, marker: 2));
        var calleeTable = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1 }, marker: 1, loopVar: "k"));
        var seg = new AlIterationSegmenter();
        var caller = new object();
        var callee = new object();
        Hits(seg, caller, callerTable, 0, 1);
        Hits(seg, callee, calleeTable, 0, 1, 2);
        seg.OnScopeExit(callee, None);
        Hits(seg, caller, callerTable, 3);

        var loops = seg.Finish();
        Assert.Equal(0, loops[0].IterationCount);
        Assert.Equal(loops[0].Id, loops[1].ParentId);
        Assert.Null(loops[1].ParentIteration);       // never 0
    }

    [Fact]
    public void MessagesAndValuesDuringACondition_LandInTheIterationItOpens_OrTheLastOneWhenItEnds()
    {
        var table = Table(Site(0, AlLoopKind.While, header: new[] { 1 }, body: new[] { 2 }, marker: 2));
        var seg = new AlIterationSegmenter();
        var scope = new object();
        Hits(seg, scope, table, 0, 1);
        seg.OnMessage(new AlCapturedMessage("m1", "S", 1));
        Hits(seg, scope, table, 2, 1);
        seg.OnMessage(new AlCapturedMessage("m2", "S", 1));
        Hits(seg, scope, table, 2, 1);
        seg.OnMessage(new AlCapturedMessage("m3", "S", 1));   // during the terminating evaluation
        Hits(seg, scope, table, 3);

        var loop = Assert.Single(seg.Finish());
        Assert.Equal(new[] { "m1" }, loop.Steps[0].Messages.Select(m => m.Text).ToArray());
        Assert.Equal(new[] { "m2", "m3" }, loop.Steps[1].Messages.Select(m => m.Text).ToArray());
    }

    [Fact]
    public void ZeroIterationNestedFor_ItsCarriedLoopVariable_GoesToTheEnclosingIteration()
    {
        // for i := 1 to 2 do begin for j := 1 to 0 do x += 1; y += 1; end;
        // ids: 0 outer | 1 inner for | 2 inner body (never) | 3 y += 1 | 4 end
        var outer = Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1, 2, 3 }, markerNested: 1, loopVar: "i");
        var inner = Site(1, AlLoopKind.For, header: new[] { 1 }, body: new[] { 2 }, marker: 2, loopVar: "j", parent: 0);
        var table = Table(outer, inner);
        var seg = new AlIterationSegmenter();
        var scope = new object();
        seg.OnHit(scope, table, 0, new[] { V("i", 1, 0) });
        seg.OnHit(scope, table, 1, new[] { V("j", 1, 1) });
        seg.OnHit(scope, table, 3, None);
        seg.OnHit(scope, table, 4, new[] { V("y", 1, 3) });

        var loops = seg.Finish();
        Assert.Equal(0, loops[1].IterationCount);
        Assert.Equal(new[] { "i=1", "j=1", "y=1" }, loops[0].Steps[0].Captures.Select(c => $"{c.VariableName}={c.Value}").ToArray());
    }

    [Fact]
    public void LoopVariable_IsMatchedCaseInsensitively_LikeALIdentifiers()
    {
        var table = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1 }, marker: 1, loopVar: "I"));
        var seg = new AlIterationSegmenter();
        var scope = new object();
        seg.OnHit(scope, table, 0, new[] { V("i", 1, 0) });
        seg.OnHit(scope, table, 1, None);
        seg.OnHit(scope, table, 1, new[] { V("s", 1, 1), V("i", 2, 1) });
        seg.OnHit(scope, table, 2, new[] { V("s", 3, 1) });

        var loop = Assert.Single(seg.Finish());
        Assert.Equal(new[] { "i=1", "s=1" }, loop.Steps[0].Captures.Select(c => $"{c.VariableName}={c.Value}").ToArray());
        Assert.Equal(new[] { "i=2", "s=3" }, loop.Steps[1].Captures.Select(c => $"{c.VariableName}={c.Value}").ToArray());
    }

    [Fact]
    public void ClosedBy_SaysHowEachInstanceEnded()
    {
        var table = Table(Site(0, AlLoopKind.For, header: new[] { 0 }, body: new[] { 1 }, marker: 1, loopVar: "i"));
        var seg = new AlIterationSegmenter();
        var a = new object(); var b = new object(); var c = new object();
        Hits(seg, a, table, 0, 1, 2);            // ended by the first statement after the loop
        Hits(seg, b, table, 0, 1); seg.OnScopeExit(b, None);
        Hits(seg, c, table, 0, 1);               // still open at Finish

        var loops = seg.Finish();
        Assert.Equal(AlLoopEnd.Exit, loops[0].ClosedBy);
        Assert.Equal(AlLoopEnd.ScopeExit, loops[1].ClosedBy);
        Assert.Equal(AlLoopEnd.Unfinished, loops[2].ClosedBy);
    }
}
