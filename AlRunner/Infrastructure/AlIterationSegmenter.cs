// AlIterationSegmenter — the runtime half of `iterationTracking` (issue #2056): turns
// BC's own StmtHit(N) stream into loop INSTANCES and their ITERATIONS, using the
// per-scope AlLoopScopeTable (AlLoopModel.cs) to know which ids are a loop's header,
// which are its body, and which event opens an iteration.
//
// Pure C# — no NavMethodScope, no reflection — so the whole state machine is unit-
// testable with synthetic hit streams (AlIterationSegmenterTests). AlIterationTracker
// is the thin NavMethodScope-facing wrapper that resolves tables and forwards here.
//
// MODEL
//   A loop INSTANCE is one dynamic entry into a loop site: a `for` inside a procedure
//   called three times yields three instances (distinct ids), each with its own
//   iteration count and its own parent — the loop instance that was ACTIVE when this
//   one was entered, across procedure calls (dynamic nesting), not the one enclosing it
//   in source. That is what lets "a loop whose body calls a procedure that itself
//   loops" report correct parentLoopId/parentIteration with no special casing.
//
//   Active instances form one global stack. Each instance remembers the SCOPE INSTANCE
//   (the NavMethodScope object, opaque here) whose hits drive it; a hit in scope S only
//   ever pushes/pops/advances instances of S, so a callee's hits neither exit nor
//   iterate the caller's loops, and the caller's instances are still on the stack (below
//   the callee's) when the callee returns.
//
// ONE HIT, IN ORDER
//   1. EXIT: pop instances of this scope from the top while they do not own the id —
//      the first id outside a loop's header+body is what ends it (a `for`'s trailing
//      block-`end` id, the statement after a `while`, the `exit` after a `break`...).
//   2. DESCEND: if no instance of this scope is on top, the id must belong to a root
//      site to matter at all (push it); then, per level: a HEADER id (while/until
//      condition; a `for` statement's own id) closes the open iteration and marks the
//      next body hit as an iteration boundary; a BODY id opens an iteration when it is
//      the marker, when none is open, or when a header hit marked one pending, and is
//      otherwise part of the current iteration; if a nested site owns the id, push an
//      instance of it and repeat one level down.
//   Scope exit pops every instance of that scope. Finish() pops everything left (an AL
//   Error thrown mid-loop never reaches a StmtHit outside it).
//
// WHERE A CAPTURED VALUE LANDS
//   AlValueCapture reads values at StmtHit(N) and attributes them to the statement that
//   ran BEFORE N (its file header explains why). So the values handed to OnHit with id N
//   are the effect of the PREVIOUS statement — which is inside the iteration that just
//   ended when N opens a new one. Rules, per event:
//     exit hit / header hit      → the open iteration (the last body statement's effect).
//     body hit, same iteration   → the open iteration.
//     body hit opening the next  → for/foreach: the loop VARIABLE's change goes to the
//                                  NEW iteration (the increment is the only thing that
//                                  runs between the last body statement and the first
//                                  one of the next pass — there is no header hit to
//                                  separate the two), everything else to the one just
//                                  closed. while/repeat: the header hit already closed
//                                  the previous iteration, so everything observed here
//                                  (the condition's own side effects, e.g. Rec.Next())
//                                  belongs to the NEW iteration.
//     body hit opening the FIRST → for/foreach: the loop variable's initial value only
//                                  (the rest is the pre-loop statement's effect);
//                                  repeat: nothing (same reason — no header ran);
//                                  while: the condition's side effects (a header ran).
//   Messages attach to the innermost open iteration at the moment Message() is called.
//   Executed statement ids are recorded into every open iteration of the SAME scope on
//   the stack, so an outer iteration's linesExecuted includes its nested loops' lines.
using System.Linq;

namespace AlRunner.Infrastructure;

internal sealed class AlIterationSegmenter
{
    /// <summary>One iteration of one loop instance.</summary>
    internal sealed class Step
    {
        public int Iteration { get; init; }
        public List<AlCapturedValue> Captures { get; } = new();
        public List<AlCapturedMessage> Messages { get; } = new();
        public List<int> StatementIds { get; } = new();
    }

    /// <summary>One dynamic entry into a loop site — see the file header.</summary>
    internal sealed class LoopInstance
    {
        public LoopInstance(int id, AlLoopSiteTable site, AlLoopScopeTable table, object scopeInstance,
            int? parentId, int? parentIteration)
        {
            Id = id;
            Site = site;
            Table = table;
            ScopeInstance = scopeInstance;
            ParentId = parentId;
            ParentIteration = parentIteration;
        }

        public int Id { get; }
        public AlLoopSiteTable Site { get; }
        public AlLoopScopeTable Table { get; }
        public object ScopeInstance { get; }
        public int? ParentId { get; }
        public int? ParentIteration { get; }
        public List<Step> Steps { get; } = new();
        /// <summary>Iterations completed or in progress; null when the site is
        /// unsegmentable (entered, but no count can be claimed).</summary>
        public int? IterationCount => Site.Unsegmentable != null ? null : Count;

        internal int Count { get; private set; }
        internal Step? Open { get; private set; }
        internal bool PendingNew { get; set; }
        // Loop-variable values observed before the iteration they belong to has opened:
        // BC assigns a `for` variable's initial value BEFORE the `for` statement's own
        // StmtHit (measured), so it arrives with the header hit, when no iteration is
        // open yet. Held here and prepended to the next iteration's captures.
        private List<AlCapturedValue>? _carry;

        internal bool IsCounted => Site.Kind is AlLoopKind.For or AlLoopKind.ForEach;

        internal bool Owns(int statementId) => Site.Owns(statementId);

        internal void Attach(IReadOnlyList<AlCapturedValue> observed)
        {
            if (Open != null && observed.Count > 0) Open.Captures.AddRange(observed);
        }

        internal void Carry(IEnumerable<AlCapturedValue> loopVariableValues)
        {
            foreach (var v in loopVariableValues) (_carry ??= new List<AlCapturedValue>()).Add(v);
        }

        /// <summary>A header hit: the values observed are the last body statement's
        /// effect (open iteration) - or, at a `for`/`foreach` entry, the loop variable's
        /// initial value, which belongs to the iteration about to open.</summary>
        internal void OnHeader(IReadOnlyList<AlCapturedValue> observed)
        {
            if (Open != null) Attach(observed);
            else if (IsCounted && Site.LoopVariable != null)
                Carry(observed.Where(v => v.VariableName == Site.LoopVariable));
        }

        internal void RecordHit(int statementId) => Open?.StatementIds.Add(statementId);

        internal void CloseStep()
        {
            if (Open == null) return;
            Steps.Add(Open);
            Open = null;
        }

        internal void StartIteration(IReadOnlyList<AlCapturedValue> observed)
        {
            bool isCounted = IsCounted;
            IEnumerable<AlCapturedValue> toNew;
            if (Open != null)
            {
                // No header hit separated the previous pass from this one (for/foreach):
                // split by the loop variable. Any other kind reaching here with an open
                // iteration is unexpected; keep its values with the pass that produced them.
                IEnumerable<AlCapturedValue> toPrev = isCounted
                    ? observed.Where(v => v.VariableName != Site.LoopVariable)
                    : observed;
                toNew = isCounted ? observed.Where(v => v.VariableName == Site.LoopVariable) : Array.Empty<AlCapturedValue>();
                Open.Captures.AddRange(toPrev);
                CloseStep();
            }
            else if (isCounted)
            {
                toNew = observed.Where(v => v.VariableName == Site.LoopVariable);
            }
            else
            {
                // while/repeat: a header hit closed the previous pass (PendingNew) and this
                // observation is the condition's own effect → new iteration; with no header
                // before (repeat entry) the observation is the pre-loop statement's effect.
                toNew = PendingNew ? observed : Array.Empty<AlCapturedValue>();
            }

            Count++;
            Open = new Step { Iteration = Count };
            if (_carry != null) { Open.Captures.AddRange(_carry); _carry = null; }
            Open.Captures.AddRange(toNew);
            PendingNew = false;
        }
    }

    private static readonly IReadOnlyList<AlCapturedValue> NoCaptures = Array.Empty<AlCapturedValue>();

    private readonly List<LoopInstance> _stack = new();
    private readonly List<LoopInstance> _all = new();
    private int _nextId;

    public void OnHit(object scopeInstance, AlLoopScopeTable table, int statementId, IReadOnlyList<AlCapturedValue> observed)
    {
        bool consumed = false;

        // 1. Exits — the first id outside a loop ends it (and its enclosing loops if the
        //    id is outside those too). The values observed here are the last body
        //    statement's effect: they belong to the innermost loop being closed.
        while (_stack.Count > 0)
        {
            var top = _stack[^1];
            if (!ReferenceEquals(top.ScopeInstance, scopeInstance) || top.Owns(statementId)) break;
            if (!consumed) { top.Attach(observed); consumed = true; }
            Pop();
        }

        // 2. Descend.
        LoopInstance? cur = _stack.Count > 0 && ReferenceEquals(_stack[^1].ScopeInstance, scopeInstance)
            ? _stack[^1]
            : null;
        while (true)
        {
            if (cur == null)
            {
                var root = table.RootSiteOwning(statementId);
                if (root == null) return; // outside every loop of this scope
                cur = Push(scopeInstance, table, root);
            }
            if (cur.Site.Unsegmentable != null) return; // entered; nothing to count

            AlLoopSiteTable? child = null;
            foreach (var c in cur.Site.Children)
                if (c.Owns(statementId)) { child = c; break; }

            if (child == null && cur.Site.HeaderIds.Contains(statementId))
            {
                if (!consumed) { cur.OnHeader(observed); consumed = true; }
                RecordHit(scopeInstance, statementId);
                cur.CloseStep();
                cur.PendingNew = true;
                return;
            }

            // Entering a nested `for`/`foreach`: its loop variable's initial value arrives
            // with ITS header hit (see OnHeader), mixed with the enclosing body's last
            // effect. Split it off for the child before the enclosing loop consumes the rest.
            List<AlCapturedValue>? childCarry = null;
            if (!consumed && child != null && child.LoopVariable != null
                && child.HeaderIds.Contains(statementId)
                && child.Kind is AlLoopKind.For or AlLoopKind.ForEach)
            {
                childCarry = observed.Where(v => v.VariableName == child.LoopVariable).ToList();
                if (childCarry.Count > 0)
                    observed = observed.Where(v => v.VariableName != child.LoopVariable).ToList();
            }

            bool boundary = cur.Open == null
                || cur.PendingNew
                || (cur.Site.MarkerStatementId is int m && m == statementId)
                || (cur.Site.MarkerNestedSiteIndex is int n && child != null && child.Index == n);
            if (boundary)
            {
                cur.StartIteration(consumed ? NoCaptures : observed);
                consumed = true;
            }
            else if (!consumed)
            {
                cur.Attach(observed);
                consumed = true;
            }
            RecordHit(scopeInstance, statementId);

            if (child == null) return;
            cur = Push(scopeInstance, table, child);
            if (childCarry is { Count: > 0 }) cur.Carry(childCarry);
        }
    }

    public void OnScopeExit(object scopeInstance, IReadOnlyList<AlCapturedValue> observed)
    {
        bool consumed = false;
        while (_stack.Count > 0 && ReferenceEquals(_stack[^1].ScopeInstance, scopeInstance))
        {
            if (!consumed) { _stack[^1].Attach(observed); consumed = true; }
            Pop();
        }
    }

    public void OnMessage(AlCapturedMessage message)
    {
        if (_stack.Count == 0) return;
        _stack[^1].Open?.Messages.Add(message);
    }

    /// <summary>Closes whatever is still open and returns every instance in entry order.</summary>
    public IReadOnlyList<LoopInstance> Finish()
    {
        while (_stack.Count > 0) Pop();
        return _all;
    }

    private LoopInstance Push(object scopeInstance, AlLoopScopeTable table, AlLoopSiteTable site)
    {
        LoopInstance? parent = _stack.Count > 0 ? _stack[^1] : null;
        var inst = new LoopInstance(_nextId++, site, table, scopeInstance,
            parent?.Id,
            parent == null ? null : parent.Open?.Iteration ?? parent.Count);
        _all.Add(inst);
        _stack.Add(inst);
        return inst;
    }

    private void Pop()
    {
        var top = _stack[^1];
        top.CloseStep();
        _stack.RemoveAt(_stack.Count - 1);
    }

    // Every open iteration of THIS scope on the stack sees the hit (an outer iteration's
    // executed lines include its nested loops'); other scopes' instances never do.
    private void RecordHit(object scopeInstance, int statementId)
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            var inst = _stack[i];
            if (!ReferenceEquals(inst.ScopeInstance, scopeInstance)) break;
            inst.RecordHit(statementId);
        }
    }
}
