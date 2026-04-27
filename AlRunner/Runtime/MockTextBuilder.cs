namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Replacement for NavTextBuilder in standalone mode.
/// NavTextBuilder wraps StringBuilder but uses TrappableOperationExecutor and NavEnvironment
/// for error handling, which crashes without a BC service tier.
/// This mock provides the same API surface using a plain StringBuilder.
/// </summary>
public class MockTextBuilder
{
    private readonly System.Text.StringBuilder _sb = new();

    /// <summary>
    /// Default constructor — replaces NavTextBuilder.Default.
    /// </summary>
    public static MockTextBuilder Default => new MockTextBuilder();

    public void ALAppend(DataError errorLevel, NavText text)
    {
        _sb.Append((string)text);
    }

    // BC's TextBuilder.AppendLine always appends a bare LF (Char(10)) on every OS —
    // do NOT use StringBuilder.AppendLine, which emits Environment.NewLine (CRLF on Windows).
    public void ALAppendLine(DataError errorLevel, NavText text)
    {
        _sb.Append((string)text).Append('\n');
    }

    public void ALAppendLine(DataError errorLevel)
    {
        _sb.Append('\n');
    }

    public void ALAppendLine()
    {
        _sb.Append('\n');
    }

    /// <summary>
    /// Returns the accumulated text as a NavText value.
    /// </summary>
    public NavText ALToText()
    {
        return new NavText(_sb.ToString());
    }

    /// <summary>
    /// ALToText(startIndex, count) — BC emits this for TextBuilder.ToText(StartIndex, Count).
    /// Extracts a substring of <paramref name="count"/> characters starting at the 1-based
    /// <paramref name="startIndex"/> (matching BC's 1-based string indexing convention).
    /// If startIndex is out of range, returns empty; if count exceeds available characters,
    /// returns the remaining text.
    /// Resolves the not-tested gap — issue #1400.
    /// </summary>
    public NavText ALToText(DataError errorLevel, int startIndex, int count)
    {
        int len = _sb.Length;
        int idx0 = startIndex - 1;  // Convert 1-based to 0-based
        if (idx0 < 0 || idx0 >= len) return new NavText("");
        int available = len - idx0;
        int toTake = Math.Min(count, available);
        return new NavText(_sb.ToString(idx0, toTake));
    }

    /// <summary>ALToText(int, int) — overload without DataError.</summary>
    public NavText ALToText(int startIndex, int count)
        => ALToText(DataError.ThrowError, startIndex, count);

    // ── New methods (#725) ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns / sets the number of characters in the builder.
    /// BC emits <c>tb.ALLength</c> for both <c>TextBuilder.Length</c> reads and
    /// <c>TextBuilder.Length := N</c> assignments. Setting a smaller value truncates
    /// the buffer; StringBuilder.Length already throws ArgumentOutOfRangeException
    /// for negative or over-capacity values, matching BC's error behaviour.
    /// </summary>
    public int ALLength
    {
        get => _sb.Length;
        set => _sb.Length = value;
    }

    /// <summary>
    /// Returns the current capacity of the underlying StringBuilder.
    /// BC emits <c>tb.ALCapacity</c> for <c>TextBuilder.Capacity</c>.
    /// </summary>
    public int ALCapacity => _sb.Capacity;

    /// <summary>
    /// Returns the maximum capacity of the underlying StringBuilder.
    /// BC emits <c>tb.ALMaxCapacity</c> for <c>TextBuilder.MaxCapacity</c>.
    /// </summary>
    public int ALMaxCapacity => _sb.MaxCapacity;

    /// <summary>
    /// Clears the builder.
    /// BC emits <c>tb.ALClear(DataError)</c> or <c>tb.ALClear()</c>.
    /// </summary>
    public void ALClear(DataError errorLevel) => _sb.Clear();
    public void ALClear() => _sb.Clear();

    /// <summary>
    /// Ensures the builder has at least the specified capacity.
    /// BC emits <c>tb.ALEnsureCapacity(DataError, capacity)</c>.
    /// </summary>
    public void ALEnsureCapacity(DataError errorLevel, int capacity)
    {
        if (_sb.Capacity < capacity)
            _sb.Capacity = capacity;
    }
    public void ALEnsureCapacity(int capacity)
    {
        if (_sb.Capacity < capacity)
            _sb.Capacity = capacity;
    }

    /// <summary>
    /// Inserts text at the specified zero-based position.
    /// BC emits <c>tb.ALInsert(DataError, index, text)</c>.
    /// </summary>
    public void ALInsert(DataError errorLevel, int index, NavText text)
        => _sb.Insert(index, (string)text);
    public void ALInsert(DataError errorLevel, int index, string text)
        => _sb.Insert(index, text);
    public void ALInsert(int index, NavText text)
        => _sb.Insert(index, (string)text);
    public void ALInsert(int index, string text)
        => _sb.Insert(index, text);

    /// <summary>
    /// Removes <paramref name="count"/> characters starting at zero-based <paramref name="startIndex"/>.
    /// BC emits <c>tb.ALRemove(DataError, startIndex, count)</c>.
    /// </summary>
    public void ALRemove(DataError errorLevel, int startIndex, int count)
        => _sb.Remove(startIndex, count);
    public void ALRemove(int startIndex, int count)
        => _sb.Remove(startIndex, count);

    /// <summary>
    /// Replaces all occurrences of <paramref name="oldValue"/> with <paramref name="newValue"/>.
    /// BC emits <c>tb.ALReplace(DataError, oldValue, newValue)</c>.
    /// </summary>
    public void ALReplace(DataError errorLevel, NavText oldValue, NavText newValue)
        => _sb.Replace((string)oldValue, (string)newValue);
    public void ALReplace(DataError errorLevel, string oldValue, string newValue)
        => _sb.Replace(oldValue, newValue);
    public void ALReplace(NavText oldValue, NavText newValue)
        => _sb.Replace((string)oldValue, (string)newValue);
    public void ALReplace(string oldValue, string newValue)
        => _sb.Replace(oldValue, newValue);

    /// <summary>
    /// ALReplace(DataError, oldValue, newValue, startIndex, count) — BC emits this for
    /// TextBuilder.Replace(OldValue, NewValue, StartIndex, Count).
    /// Replaces occurrences within the specified range only.
    /// Resolves the not-tested gap — issue #1400.
    /// </summary>
    /// <summary>
    /// ALReplace with 1-based startIndex (matching BC's TextBuilder.Replace convention).
    /// </summary>
    public void ALReplace(DataError errorLevel, string oldValue, string newValue, int startIndex, int count)
    {
        int idx0 = startIndex - 1;  // Convert 1-based to 0-based
        if (idx0 < 0 || idx0 >= _sb.Length) return;
        int end = Math.Min(idx0 + count, _sb.Length);
        string segment = _sb.ToString(idx0, end - idx0);
        string replaced = segment.Replace(oldValue, newValue);
        _sb.Remove(idx0, end - idx0);
        _sb.Insert(idx0, replaced);
    }

    public void ALReplace(string oldValue, string newValue, int startIndex, int count)
        => ALReplace(DataError.ThrowError, oldValue, newValue, startIndex, count);

    /// <summary>
    /// Implicit conversion to string for formatting contexts.
    /// </summary>
    public override string ToString() => _sb.ToString();
}
