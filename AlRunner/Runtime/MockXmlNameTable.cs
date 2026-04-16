using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Runtime;

/// <summary>
/// Safe mock for BC's <c>NavXmlNameTable</c> (the C# representation of AL's
/// <c>XmlNameTable</c> type).
///
/// <para>The real <c>NavXmlNameTable.ALGet</c> throws
/// <c>NavNCLKeyNotFoundException</c> when a requested name is absent.
/// AL semantics require <c>Get(Name, var Value)</c> to return <c>false</c> and
/// leave <c>Value</c> empty — no exception.  This mock reproduces that behaviour
/// with an in-memory dictionary.</para>
///
/// <para>BC lowers <c>XmlNamespaceManager.NameTable()</c> to return a
/// <c>NavXmlNameTable</c>.  The RoslynRewriter rewrites <c>NavXmlNameTable</c>
/// identifiers to <c>MockXmlNameTable</c> and the implicit conversion operator
/// discards the real instance (its internal state is not needed for unit tests)
/// and returns a fresh mock backed by a plain dictionary.</para>
/// </summary>
public class MockXmlNameTable
{
    private readonly System.Collections.Generic.Dictionary<string, string> _table
        = new(System.StringComparer.Ordinal);

    // ── Implicit conversion ──────────────────────────────────────────────────

    /// <summary>
    /// Converts a real <c>NavXmlNameTable</c> (returned by
    /// <c>NavXmlNamespaceManager.ALNameTable()</c>) to a <c>MockXmlNameTable</c>.
    /// The real instance's internal state is discarded; the mock starts empty
    /// and is populated by subsequent <c>ALAdd</c> calls.
    /// </summary>
    public static implicit operator MockXmlNameTable(NavXmlNameTable _)
        => new MockXmlNameTable();

    // ── ALAdd ────────────────────────────────────────────────────────────────

    /// <summary>
    /// BC lowers <c>nt.Add(name)</c> to <c>nt.ALAdd(name)</c>.
    /// Interns <paramref name="name"/>: after this call
    /// <c>ALGet(name, …)</c> returns it unchanged.
    /// </summary>
    public void ALAdd(string name) => _table[name] = name;

    /// <summary>Overload for <c>NavText</c> parameters.</summary>
    public void ALAdd(NavText name) => ALAdd((string)name);

    // ── ALGet ────────────────────────────────────────────────────────────────

    /// <summary>
    /// BC lowers <c>nt.Get(name, var result)</c> to
    /// <c>nt.ALGet(name, ref result)</c>.
    /// Returns <c>true</c> and sets <paramref name="result"/> when
    /// <paramref name="name"/> was previously added; otherwise returns
    /// <c>false</c> and sets <paramref name="result"/> to empty text
    /// (instead of throwing <c>NavNCLKeyNotFoundException</c>).
    /// </summary>
    public bool ALGet(string name, ref NavText result)
    {
        if (_table.TryGetValue(name, out var v))
        {
            result = new NavText(v);
            return true;
        }
        result = NavText.Empty;
        return false;
    }

    /// <summary>Overload for <c>NavText</c> name parameter.</summary>
    public bool ALGet(NavText name, ref NavText result) => ALGet((string)name, ref result);

    // ── ALGet with string result ─────────────────────────────────────────────
    // Some BC compiler variants emit string/ref string rather than NavText/ref NavText.

    /// <summary>Overload for <c>string</c> result parameter.</summary>
    public bool ALGet(string name, ref string result)
    {
        if (_table.TryGetValue(name, out var v))
        {
            result = v;
            return true;
        }
        result = string.Empty;
        return false;
    }
}
