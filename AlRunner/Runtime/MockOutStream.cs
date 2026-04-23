namespace AlRunner.Runtime;

using System.Text;

/// <summary>
/// In-memory OutStream replacement for NavOutStream.
/// Implements writing to a byte buffer that flushes back to MockBlob.
/// </summary>
public class MockOutStream
{
    private List<byte> _buffer = new();
    internal Action<byte[]>? OnFlush;

    public void Init()
    {
        _buffer.Clear();
    }

    /// <summary>
    /// Static factory matching NavOutStream.Default(ITreeObject parent).
    /// The parent is unused in standalone mode.
    /// </summary>
    public static MockOutStream Default(object? parent = null) => new MockOutStream();

    /// <summary>Write text as UTF-8 bytes.</summary>
    public void WriteText(string text)
    {
        _buffer.AddRange(Encoding.UTF8.GetBytes(text));
        OnFlush?.Invoke(_buffer.ToArray());
    }

    /// <summary>Write raw bytes.</summary>
    public void Write(byte[] data, int offset, int count)
    {
        for (int i = offset; i < offset + count; i++)
            _buffer.Add(data[i]);
        OnFlush?.Invoke(_buffer.ToArray());
    }

    /// <summary>
    /// ALAssign — AL: OutStr2 := OutStr1 — makes this stream share the same buffer and flush callback.
    /// BC compiler emits <c>outStream.ALAssign(otherOutStream)</c> for assignment.
    /// </summary>
    public void ALAssign(MockOutStream other)
    {
        _buffer = other._buffer;
        OnFlush = other.OnFlush;
    }

    /// <summary>
    /// AL's Clear(OutStream) — rewriter emits outStream.Clear().
    /// Resets the stream to its initial empty state.
    /// </summary>
    public void Clear()
    {
        _buffer.Clear();
        OnFlush = null;
    }

    /// <summary>Return the current buffered bytes (used by ALCopyStream).</summary>
    internal byte[] GetBytes() => _buffer.ToArray();
}
