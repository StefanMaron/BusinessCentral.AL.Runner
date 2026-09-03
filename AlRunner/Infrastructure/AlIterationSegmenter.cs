// Turns BC's StmtHit stream into loop instances and iterations (#2056) using
// AlLoopScopeTable. Pure C#, unit-tested in AlIterationSegmenterTests.
//
// A loop instance is one dynamic entry into a loop site; its parent is whatever instance
// was active when it was entered, across procedure calls. Active instances form one
// stack, and a hit in scope S only affects instances of S.
//
// Per hit: pop this scope's instances that do not own the id (the first id outside a
// loop ends it); then a header id closes the open iteration and marks the next body hit
// as a boundary, a body id opens an iteration when it is the marker or none is open, and
// a nested site owning the id is pushed.
//
// Captured values arrive as the effect of the PREVIOUS statement (AlValueCapture), so at
// a boundary they belong to the iteration just closed, except a for/foreach loop
// variable, whose new value belongs to the iteration it opens. Messages attach to the
// innermost open iteration; executed ids are recorded in every open iteration of the scope.
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

    /// <summary>One dynamic entry into a loop site.</summary>
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
        /// <summary>Passes so far; null when the site is unsegmentable.</summary>
        public int? IterationCount => Site.Unsegmentable != null ? null : Count;

        internal int Count { get; private set; }
        internal Step? Open { get; private set; }
        internal bool PendingNew { get; set; }
        // Loop-variable values observed at the header hit, before the first iteration opens
        // (BC assigns a for variable before the for statement's own StmtHit).
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

        /// <summary>A header hit: values go to the open iteration, or (for/foreach entry) the loop variable is carried into the first one.</summary>
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
                // for/foreach: no header hit between passes, so split by the loop variable.
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
                // while/repeat: after a header hit the observation is the condition's effect;
                // at a repeat's entry it is the pre-loop statement's.
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

        // 1. Exits: the first id outside a loop ends it; observed values go to the innermost closed loop.
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

            // Entering a nested for/foreach: its loop variable's initial value arrives with
            // this hit; split it off for the child.
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

    /// <summary>Closes what is still open; instances in entry order.</summary>
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

    // Every open iteration of this scope records the hit; other scopes' never do.
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
