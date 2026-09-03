// Per-scope-instance state for the capture hooks (#2056). One global last-known map was
// wrong as soon as OnRun called a procedure: same-named locals were diffed across scopes.
// Frames form a stack mirroring the AL call stack. A hit for a scope below the top means
// a callee ended without its Exit reaching us; the stale frames above are discarded.
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

    /// <summary>The scope's frame (reference identity), discarding stale frames above it; pushed if absent.</summary>
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

    /// <summary>Removes the scope's frame and anything above it; null if it has none.</summary>
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
