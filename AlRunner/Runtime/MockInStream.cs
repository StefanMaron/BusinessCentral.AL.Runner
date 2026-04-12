namespace AlRunner.Runtime;

using System.Text;

/// <summary>
/// In-memory InStream replacement for NavInStream.
/// Implements reading from a byte buffer (typically populated by MockBlob.ALCreateInStream).
/// </summary>
public class MockInStream
{
    private byte[] _data = Array.Empty<byte>();
    private int _pos = 0;

    public void Init(byte[] data)
    {
        _data = data;
        _pos = 0;
    }

    /// <summary>
    /// Static factory matching NavInStream.Default(ITreeObject parent).
    /// The parent is unused in standalone mode.
    /// </summary>
    public static MockInStream Default(object? parent = null) => new MockInStream();

    /// <summary>Read remaining bytes as UTF-8 text into a ref string.</summary>
    public int ReadText(ref string text, int maxLength = int.MaxValue)
    {
        int available = _data.Length - _pos;
        int toRead = Math.Min(available, maxLength);
        text = Encoding.UTF8.GetString(_data, _pos, toRead);
        _pos += toRead;
        return toRead;
    }

    /// <summary>Check if at end of stream.</summary>
    public bool EOS => _pos >= _data.Length;

    /// <summary>
    /// ALAssign — AL: InStr2 := InStr1 — copies the other stream's buffer and position.
    /// BC compiler emits <c>inStream.ALAssign(otherInStream)</c> for assignment.
    /// </summary>
    public void ALAssign(MockInStream other)
    {
        _data = other._data;
        _pos = other._pos;
    }

    /// <summary>Read raw bytes into a buffer.</summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        int available = _data.Length - _pos;
        int toRead = Math.Min(available, count);
        Array.Copy(_data, _pos, buffer, offset, toRead);
        _pos += toRead;
        return toRead;
    }
}
