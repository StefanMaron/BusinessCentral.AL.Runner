namespace AlRunner.Runtime;

using System.Text;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Replacement for NavBigText in standalone mode.
/// In BC 28+, NavBigText's static initializer loads
/// Microsoft.BusinessCentral.Telemetry.Abstractions which is not available
/// outside the service tier, causing TypeInitializationException.
/// This mock provides the same API surface using a plain StringBuilder.
/// </summary>
public class MockBigText
{
    private readonly StringBuilder _sb;

    public MockBigText()
    {
        _sb = new StringBuilder();
    }

    public MockBigText(string value)
    {
        _sb = new StringBuilder(value);
    }

    /// <summary>ALLength property — returns the number of characters.</summary>
    public int ALLength => _sb.Length;

    /// <summary>
    /// ALAddText(bigText, text) — appends text and returns a new MockBigText.
    /// The BC compiler emits: bigText = NavBigText.ALAddText(bigText, "text");
    /// </summary>
    public static MockBigText ALAddText(MockBigText bigText, string variable)
    {
        bigText._sb.Append(variable);
        return bigText;
    }

    /// <summary>
    /// ALAddText(bigText, text, toPos1Based) — inserts text at position and returns a new MockBigText.
    /// </summary>
    public static MockBigText ALAddText(MockBigText bigText, string variable, int toPos1Based)
    {
        int index = Math.Max(0, Math.Min(toPos1Based - 1, bigText._sb.Length));
        bigText._sb.Insert(index, variable);
        return bigText;
    }

    /// <summary>
    /// ALGetSubText(variable, fromPos1Based, length) — extracts substring.
    /// Sets the ByRef&lt;NavText&gt; variable to the extracted text.
    /// Returns the number of characters extracted.
    /// </summary>
    public int ALGetSubText(ByRef<NavText> variable, int fromPos1Based, int length)
    {
        int from0 = Math.Max(0, fromPos1Based - 1);
        int available = Math.Max(0, _sb.Length - from0);
        int len = Math.Min(length, available);
        if (len <= 0)
        {
            variable.Value = new NavText("");
            return 0;
        }
        variable.Value = new NavText(_sb.ToString(from0, len));
        return len;
    }

    /// <summary>
    /// ALGetSubText(variable, fromPos1Based) — extracts from position to end.
    /// </summary>
    public int ALGetSubText(ByRef<NavText> variable, int fromPos1Based)
    {
        int from0 = Math.Max(0, fromPos1Based - 1);
        int available = Math.Max(0, _sb.Length - from0);
        if (available <= 0)
        {
            variable.Value = new NavText("");
            return 0;
        }
        variable.Value = new NavText(_sb.ToString(from0, available));
        return available;
    }

    /// <summary>
    /// ALGetSubText — overloads for ref NavText (non-ByRef wrapper).
    /// </summary>
    public int ALGetSubText(ref NavText variable, int fromPos1Based, int length)
    {
        int from0 = Math.Max(0, fromPos1Based - 1);
        int available = Math.Max(0, _sb.Length - from0);
        int len = Math.Min(length, available);
        if (len <= 0)
        {
            variable = new NavText("");
            return 0;
        }
        variable = new NavText(_sb.ToString(from0, len));
        return len;
    }

    public int ALGetSubText(ref NavText variable, int fromPos1Based)
    {
        int from0 = Math.Max(0, fromPos1Based - 1);
        int available = Math.Max(0, _sb.Length - from0);
        if (available <= 0)
        {
            variable = new NavText("");
            return 0;
        }
        variable = new NavText(_sb.ToString(from0, available));
        return available;
    }

    /// <summary>
    /// ALTextPos(substring) — returns the 1-based position of the first occurrence
    /// of the substring, or 0 if not found.
    /// </summary>
    public int ALTextPos(string substring)
    {
        if (string.IsNullOrEmpty(substring)) return 0;
        int idx = _sb.ToString().IndexOf(substring, StringComparison.Ordinal);
        return idx < 0 ? 0 : idx + 1;
    }

    /// <summary>
    /// ALWrite(outStream) — writes the BigText content to a stream.
    /// </summary>
    public bool ALWrite(MockOutStream outStream)
    {
        outStream.WriteText(_sb.ToString());
        return true;
    }

    /// <summary>
    /// ALRead(text, inStream) — reads content from a stream into a BigText.
    /// </summary>
    public static bool ALRead(ByRef<MockBigText> text, MockInStream inStream)
    {
        string content = "";
        inStream.ReadText(ref content, int.MaxValue);
        var bt = new MockBigText(content);
        text.Value = bt;
        return true;
    }

    /// <summary>Default static property for field initialization.</summary>
    public static MockBigText Default => new MockBigText();

    /// <summary>Value property — returns the full text content.</summary>
    public string Value => _sb.ToString();

    /// <summary>Check if empty.</summary>
    public bool IsZeroOrEmpty => _sb.Length == 0;

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(MockBigText bt) => bt._sb.ToString();

    public override string ToString() => _sb.ToString();

    public override bool Equals(object? obj)
    {
        if (obj is MockBigText other) return _sb.ToString() == other._sb.ToString();
        if (obj is string s) return _sb.ToString() == s;
        return false;
    }

    public override int GetHashCode() => _sb.ToString().GetHashCode();
}
