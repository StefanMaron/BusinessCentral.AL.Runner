// Turns BC's StmtHit stream into loop instances and iterations (#2056) using
// AlLoopScopeTable. Pure C#, unit-tested in AlIterationSegmenterTests.
//
// A loop instance is one dynamic entry into a loop site; its parent is whatever instance
// was active when it was entered, across procedure calls. Active instances form one
// stack, and a hit in scope S only affects instances of S. A hit for a scope that is not
// on top means a callee never exited (an error unwound through it): everything above the
// scope's own instances is closed as unfinished first.
//
// Per hit: pop this scope's instances that do not own the id (the first id outside a
// loop ends it); then a header id closes the open iteration and buffers what follows
// until the next body hit (a while/until condition runs there; a for/foreach header on
// an active instance is a re-entry, since it fires once per entry); a body id opens an
// iteration when it is the marker or none is open, and a nested site owning the id is
// pushed.
//
// Captured values arrive as the effect of the PREVIOUS statement (AlValueCapture), so at
// a boundary they belong to the iteration just closed, except a for/foreach loop
// variable, whose new value belongs to the iteration it opens. Values and messages that
// arrive while a condition is being evaluated go to the iteration that condition opens,
// or to the last iteration when it ends the loop; what a repeat's entry observes is the
// state its first pass starts with. Executed ids are recorded in every open iteration of
// the scope.
namespace AlRunner.Infrastructure;

/// <summary>How a loop instance ended: the first statement after the loop, the scope
/// exiting, or nothing (the run ended or an error unwound through it).</summary>
public enum AlLoopEnd { Exit, ScopeExit, Unfinished }

internal sealed class AlIterationSegmenter
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

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
        /// <summary>The parent's iteration active at entry; null before its first pass.
        /// Provisional while the parent is evaluating a condition, settled by the pass it opens.</summary>
        public int? ParentIteration { get; internal set; }
        public List<Step> Steps { get; } = new();
        /// <summary>Passes so far; null when the site is unsegmentable.</summary>
        public int? IterationCount => Site.Unsegmentable != null ? null : Count;
        public AlLoopEnd? ClosedBy { get; private set; }

        internal int Count { get; private set; }
        internal Step? Open { get; private set; }
        internal bool Pending { get; private set; }
        internal bool HeaderSeen { get; set; }
        private List<AlCapturedValue>? _carry;
        private List<AlCapturedValue>? _pendingCaptures;
        private List<AlCapturedMessage>? _pendingMessages;
        private List<LoopInstance>? _pendingChildren;

        internal bool IsCounted => Site.IsCounted;
        internal bool Owns(int statementId) => Site.Owns(statementId);
        private bool IsLoopVariable(AlCapturedValue v) => NameComparer.Equals(v.VariableName, Site.LoopVariable);

        /// <summary>Values go to the open iteration, else to the pending condition.</summary>
        internal void Attach(IReadOnlyList<AlCapturedValue> observed)
        {
            if (observed.Count == 0) return;
            if (Open != null) Open.Captures.AddRange(observed);
            else if (Pending) (_pendingCaptures ??= new()).AddRange(observed);
        }

        internal void AttachMessage(AlCapturedMessage message)
        {
            if (Open != null) Open.Messages.Add(message);
            else if (Pending) (_pendingMessages ??= new()).Add(message);
        }

        internal void Carry(IEnumerable<AlCapturedValue> loopVariableValues)
        {
            foreach (var v in loopVariableValues) (_carry ??= new()).Add(v);
        }

        internal IReadOnlyList<AlCapturedValue> TakeCarry()
        {
            var c = _carry;
            _carry = null;
            return c ?? (IReadOnlyList<AlCapturedValue>)Array.Empty<AlCapturedValue>();
        }

        internal void AddPendingChild(LoopInstance child) => (_pendingChildren ??= new()).Add(child);

        /// <summary>A header hit: values go to the open iteration, or (for/foreach entry) the loop
        /// variable is carried into the first one; then the iteration closes and a condition is pending.</summary>
        internal void OnHeader(IReadOnlyList<AlCapturedValue> observed)
        {
            if (Open != null) Attach(observed);
            else if (IsCounted && Site.LoopVariable != null) Carry(observed.Where(IsLoopVariable));
            else if (Pending) Attach(observed);
            CloseStep();
            Pending = true;
            HeaderSeen = true;
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
            IEnumerable<AlCapturedValue> toNew;
            if (Open != null)
            {
                // for/foreach: no header hit between passes, so split by the loop variable.
                IEnumerable<AlCapturedValue> toPrev = IsCounted ? observed.Where(v => !IsLoopVariable(v)) : observed;
                toNew = IsCounted ? observed.Where(IsLoopVariable) : Array.Empty<AlCapturedValue>();
                Open.Captures.AddRange(toPrev);
                CloseStep();
            }
            else if (IsCounted)
            {
                toNew = observed.Where(IsLoopVariable);
            }
            else
            {
                // while/repeat: the condition's effect after a header hit, or the state the
                // loop starts with at a repeat's entry; either way this pass's.
                toNew = observed;
            }

            Count++;
            Open = new Step { Iteration = Count };
            if (_carry != null) { Open.Captures.AddRange(_carry); _carry = null; }
            if (_pendingCaptures != null) { Open.Captures.AddRange(_pendingCaptures); _pendingCaptures = null; }
            if (_pendingMessages != null) { Open.Messages.AddRange(_pendingMessages); _pendingMessages = null; }
            if (_pendingChildren != null)
            {
                foreach (var c in _pendingChildren) c.ParentIteration = Count;
                _pendingChildren = null;
            }
            Open.Captures.AddRange(toNew);
            Pending = false;
        }

        /// <summary>Ends the instance. Effects buffered by a terminating condition go to the last iteration.</summary>
        internal void Close(AlLoopEnd how)
        {
            CloseStep();
            if (Steps.Count > 0)
            {
                var last = Steps[^1];
                if (_pendingCaptures != null) last.Captures.AddRange(_pendingCaptures);
                if (_pendingMessages != null) last.Messages.AddRange(_pendingMessages);
            }
            _pendingCaptures = null;
            _pendingMessages = null;
            _pendingChildren = null;
            Pending = false;
            ClosedBy = how;
        }
    }

    private static readonly IReadOnlyList<AlCapturedValue> NoCaptures = Array.Empty<AlCapturedValue>();

    private readonly List<LoopInstance> _stack = new();
    private readonly List<LoopInstance> _all = new();
    private readonly Dictionary<object, int> _lastHit = new(ReferenceEqualityComparer.Instance);
    private int _nextId;

    public void OnHit(object scopeInstance, AlLoopScopeTable table, int statementId, IReadOnlyList<AlCapturedValue> observed)
    {
        UnwindStaleAbove(scopeInstance);
        bool consumed = false;

        // 1. Exits: the first id outside a loop ends it; observed values go to the innermost closed loop.
        while (TopOf(scopeInstance) is { } top && !top.Owns(statementId))
        {
            if (!consumed) { top.Attach(observed); consumed = true; }
            Pop(AlLoopEnd.Exit);
        }

        // 2. Descend.
        var cur = TopOf(scopeInstance);
        while (true)
        {
            if (cur == null)
            {
                var root = table.RootSiteOwning(statementId);
                if (root == null) break; // outside every loop of this scope
                cur = Push(scopeInstance, table, root);
            }
            if (cur.Site.Unsegmentable != null) break; // entered; nothing to count

            var child = table.ChildOwning(cur.Site, statementId);
            if (child == null && cur.Site.HeaderIds.Contains(statementId))
            {
                if (IsReentry(cur, scopeInstance))
                {
                    // The same site entered again with no outer statement in between (its body
                    // is the enclosing loop's whole body): end this instance and let the parent
                    // open its next pass with a fresh one.
                    if (!consumed) { cur.Attach(observed); consumed = true; }
                    Pop(AlLoopEnd.Exit);
                    cur = TopOf(scopeInstance);
                    continue;
                }
                RecordHit(scopeInstance, statementId);
                if (consumed) cur.OnHeader(NoCaptures);
                else { cur.OnHeader(observed); consumed = true; }
                break;
            }

            // Entering a nested for/foreach: its loop variable's initial value arrives with
            // this hit; split it off for the child.
            List<AlCapturedValue>? childCarry = null;
            if (!consumed && child != null && child.IsCounted && child.LoopVariable != null
                && child.HeaderIds.Contains(statementId))
            {
                childCarry = observed.Where(v => NameComparer.Equals(v.VariableName, child.LoopVariable)).ToList();
                if (childCarry.Count > 0)
                    observed = observed.Where(v => !NameComparer.Equals(v.VariableName, child.LoopVariable)).ToList();
            }

            bool boundary = cur.Open == null
                || cur.Pending
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

            if (child == null) break;
            cur = Push(scopeInstance, table, child);
            if (childCarry is { Count: > 0 }) cur.Carry(childCarry);
        }
        _lastHit[scopeInstance] = statementId;
    }

    // A for/foreach header fires once per entry, so a second hit on an active instance is a
    // re-entry. A while condition re-evaluates mid-pass, but then always right after one of
    // its own body statements. A repeat's until-condition is never an entry.
    private bool IsReentry(LoopInstance cur, object scopeInstance)
    {
        if (!cur.HeaderSeen) return false;
        switch (cur.Site.Kind)
        {
            case AlLoopKind.For:
            case AlLoopKind.ForEach:
                return true;
            case AlLoopKind.While:
                return !(_lastHit.TryGetValue(scopeInstance, out var last) && cur.Site.BodyIds.Contains(last));
            default:
                return false;
        }
    }

    public void OnScopeExit(object scopeInstance, IReadOnlyList<AlCapturedValue> observed)
    {
        UnwindStaleAbove(scopeInstance);
        bool consumed = false;
        while (TopOf(scopeInstance) is { } top)
        {
            if (!consumed) { top.Attach(observed); consumed = true; }
            Pop(AlLoopEnd.ScopeExit);
        }
        _lastHit.Remove(scopeInstance);
    }

    public void OnMessage(AlCapturedMessage message)
    {
        if (_stack.Count > 0) _stack[^1].AttachMessage(message);
    }

    /// <summary>Closes what is still open; instances in entry order.</summary>
    public IReadOnlyList<LoopInstance> Finish()
    {
        while (_stack.Count > 0) Pop(AlLoopEnd.Unfinished);
        return _all;
    }

    private LoopInstance? TopOf(object scopeInstance) =>
        _stack.Count > 0 && ReferenceEquals(_stack[^1].ScopeInstance, scopeInstance) ? _stack[^1] : null;

    // Instances of other scopes above this scope's own belong to callees that never exited.
    private void UnwindStaleAbove(object scopeInstance)
    {
        int deepest = -1;
        for (int i = 0; i < _stack.Count; i++)
            if (ReferenceEquals(_stack[i].ScopeInstance, scopeInstance)) { deepest = i; break; }
        if (deepest < 0) return;
        int own = deepest;
        while (own + 1 < _stack.Count && ReferenceEquals(_stack[own + 1].ScopeInstance, scopeInstance)) own++;
        while (_stack.Count - 1 > own) Pop(AlLoopEnd.Unfinished);
    }

    private LoopInstance Push(object scopeInstance, AlLoopScopeTable table, AlLoopSiteTable site)
    {
        LoopInstance? parent = _stack.Count > 0 ? _stack[^1] : null;
        int? parentIteration = parent == null ? null
            : parent.Open?.Iteration ?? (parent.Count == 0 ? null : parent.Count);
        var inst = new LoopInstance(_nextId++, site, table, scopeInstance, parent?.Id, parentIteration);
        if (parent != null && parent.Open == null && parent.Pending) parent.AddPendingChild(inst);
        _all.Add(inst);
        _stack.Add(inst);
        return inst;
    }

    private void Pop(AlLoopEnd how)
    {
        var top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        top.Close(how);
        // A loop variable observed at a header whose loop never opened a pass belongs to
        // the enclosing iteration of the same scope.
        var carry = top.TakeCarry();
        if (carry.Count > 0 && _stack.Count > 0 && ReferenceEquals(_stack[^1].ScopeInstance, top.ScopeInstance))
            _stack[^1].Attach(carry);
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
