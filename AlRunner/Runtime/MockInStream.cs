namespace AlRunner.Runtime;

using System.Text;
using Microsoft.Dynamics.Nav.Types;

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

    public void Clear()
    {
        _data = Array.Empty<byte>();
        _pos = 0;
    }

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

    /// <summary>ALIsEOS — BC's InStream.EOS() mapped to this property.</summary>
    public bool ALIsEOS => EOS;

    /// <summary>ALEOS() — BC emits inStream.ALEOS() for InStr.EOS() calls.</summary>
    public bool ALEOS() => EOS;

    /// <summary>Read all remaining bytes as a UTF-8 string (used by MockJsonHelper.ReadFrom).</summary>
    public string ReadAll()
    {
        int available = _data.Length - _pos;
        if (available <= 0) return string.Empty;
        var text = Encoding.UTF8.GetString(_data, _pos, available);
        _pos = _data.Length;
        return text;
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

    /// <summary>ALLength — BC's InStream.Length property. Returns total byte length.</summary>
    public int ALLength => _data.Length;

    /// <summary>ALPosition — BC's InStream.Position property. Returns current read position.</summary>
    public int ALPosition => _pos;

    /// <summary>ALResetPosition — BC's InStream.ResetPosition(). Resets read position to 0.</summary>
    public void ALResetPosition(DataError errorLevel = DataError.ThrowError)
    {
        _pos = 0;
    }
}
