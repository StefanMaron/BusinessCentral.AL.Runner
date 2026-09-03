// AlScopeFrames - per-scope-INSTANCE state for the StmtHit/Exit hooks (issue #2056).
//
// AlValueCapture used to keep one last-known map and one last statement id for the
// whole process. Inside one `execute` run that is wrong as soon as the top-level scope
// calls a procedure: the callee's locals were diffed against the caller's same-named
// locals (both are `[NavName]` fields keyed by AL name), the callee's first observation
// was never treated as a baseline, and the callee's first records were attributed to
// the CALLER's last statement id. Every scope instance gets its own frame here.
//
// Frames form a stack that mirrors the AL call stack. A hit for a scope that is not on
// top means one of two things: a callee just started (push), or a callee ended without
// its Exit() reaching us (an AL Error unwinding through it) and the caller resumed -
// then the stale frames above the caller's are discarded. Pop() on Exit() takes the
// scope's frame and anything stale above it. Generic over the state so the stack logic
// is testable without a NavMethodScope (AlValueCaptureWriteSetTests).
namespace AlRunner.Infrastructure;

public sealed class AlScopeFrames<TState>
{
    public sealed class Frame
    {
        internal Frame(object scope, TState state)
        {
            Scope = scope;
            State = state;
        }

        public object Scope { get; }
        public TState State { get; set; }
    }

    private readonly List<Frame> _frames = new();
    private readonly Func<TState> _create;

    public AlScopeFrames(Func<TState> createState) => _create = createState;

    public int Depth => _frames.Count;

    /// <summary>The frame for <paramref name="scope"/> (reference identity): the one on
    /// the stack if it is there (discarding stale frames above it), else a new one
    /// pushed on top.</summary>
    public Frame GetOrPush(object scope)
    {
        for (int i = _frames.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_frames[i].Scope, scope)) continue;
            if (i < _frames.Count - 1) _frames.RemoveRange(i + 1, _frames.Count - i - 1);
            return _frames[i];
        }
        var frame = new Frame(scope, _create());
        _frames.Add(frame);
        return frame;
    }

    /// <summary>Removes and returns <paramref name="scope"/>'s frame (and any stale
    /// frames above it); null, with the stack untouched, when it has none.</summary>
    public Frame? Pop(object scope)
    {
        for (int i = _frames.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_frames[i].Scope, scope)) continue;
            var frame = _frames[i];
            _frames.RemoveRange(i, _frames.Count - i);
            return frame;
        }
        return null;
    }

    public void Clear() => _frames.Clear();
}
