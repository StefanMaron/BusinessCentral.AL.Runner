// AlDapWire — the DAP wire shape of a stack frame and of a variables handle, lifted out of
// RunDapLoop so a test can drive it without a live session (#3901, #3906). The Debug Adapter
// Protocol specification is the contract here, not BC; clause citations are on each member.
namespace AlRunner.Infrastructure;

public static class AlDapWire
{
    /// <summary>
    /// A frame has a location only when it names a file AND a line inside it. DAP, `StackFrame`:
    /// "<c>line</c>: The line within the source of the frame. If the source attribute is missing
    /// or doesn't exist, line is 0 and should be ignored by the client", and the same for
    /// <c>column</c>. A frame with a path but no resolved line (the walker's 0) is treated as
    /// source-less too: for a 0-based client that 0 is also the first line of a file, so
    /// <c>source</c> + <c>line: 0</c> would read as a real location there.
    /// </summary>
    public static bool HasLocation(AlDapFrame f) => f.SourcePath != null && f.Line > 0;

    /// <summary>
    /// The `stackTrace` entry for <paramref name="f"/>. The frame is always kept (loud-failures.md:
    /// never silently omit a frame the caller could act on); only its coordinates are withheld
    /// when <see cref="HasLocation"/> is false, as <c>source</c> omitted (the transport drops
    /// nulls) with <c>line</c> and <c>column</c> 0. <paramref name="toClientLine"/> and
    /// <paramref name="toClientColumn"/> convert from the runner's 1-based numbers to the base the
    /// client negotiated in `initialize`.
    /// </summary>
    public static object StackFrame(AlDapFrame f, Func<int, int> toClientLine, Func<int, int> toClientColumn)
    {
        if (!HasLocation(f))
            return new { id = f.Id, name = f.ScopeName, source = (object?)null, line = 0, column = 0 };
        return new
        {
            id = f.Id,
            name = f.ScopeName,
            source = (object?)new { path = f.SourcePath, name = Path.GetFileName(f.SourcePath) },
            line = toClientLine(f.Line),
            // The first column of the line (#3881): converted, not a literal 1.
            column = toClientColumn(1),
        };
    }

    /// <summary>
    /// The non-standard <c>line</c> on the `stopped` event: the paused frame's line in the
    /// client's base, or null (omitted) when there is none to report — a failed walk, or a top
    /// frame with no location. A 0 sentinel there is a legal first-line coordinate for a 0-based
    /// client, and converting it would send -1 (#3901); omitting keeps "no line" distinguishable
    /// from "line 0". The location itself is carried by `stackTrace`, as DAP intends.
    /// </summary>
    public static int? StoppedLine(IReadOnlyList<AlDapFrame> frames, Func<int, int> toClientLine)
        => frames.Count > 0 && HasLocation(frames[0]) ? toClientLine(frames[0].Line) : null;
}

/// <summary>
/// `variablesReference` handles for one stop. DAP: `Scope.variablesReference` is the value to pass
/// to the `variables` request for that scope; on `Variable`, "If variablesReference is > 0, the
/// variable is structured and its children can be retrieved" — so 0 means no children, and a
/// client never requests it (#3906). Frame ids start at 0 (AlDapStackWalker), so a frame id
/// cannot stand in for a handle; handles come from a counter that starts at 1 and map back to
/// the frame they were issued for.
/// <para>
/// DAP, `VariablesArguments.variablesReference`: "The variablesReference must have been obtained
/// in the current suspended state", so the table is cleared at every stop. The counter is NOT
/// reset, so a handle held across a stop can only miss, never silently resolve to a different
/// frame of the new stop.
/// </para>
/// </summary>
public sealed class AlDapVariableHandles
{
    private readonly object _gate = new();
    private int _next = 1;
    private readonly Dictionary<int, int> _frameByHandle = new();
    private readonly Dictionary<int, int> _handleByFrame = new();

    /// <summary>The positive handle for <paramref name="frameId"/>'s Locals scope; the same
    /// frame asked twice within one stop gets the same handle.</summary>
    public int ForFrame(int frameId)
    {
        lock (_gate)
        {
            if (_handleByFrame.TryGetValue(frameId, out var existing)) return existing;
            var handle = _next++;
            _frameByHandle[handle] = frameId;
            _handleByFrame[frameId] = handle;
            return handle;
        }
    }

    /// <summary>The frame a handle was issued for in the CURRENT stop; false for 0, a handle
    /// from an earlier stop, or one never issued.</summary>
    public bool TryResolveFrame(int handle, out int frameId)
    {
        lock (_gate) return _frameByHandle.TryGetValue(handle, out frameId);
    }

    /// <summary>Invalidates every outstanding handle — called when a new stop begins.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _frameByHandle.Clear();
            _handleByFrame.Clear();
        }
    }
}
