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

    public void ALAppendLine(DataError errorLevel, NavText text)
    {
        _sb.AppendLine((string)text);
    }

    /// <summary>
    /// Returns the accumulated text as a NavText value.
    /// </summary>
    public NavText ALToText()
    {
        return new NavText(_sb.ToString());
    }

    /// <summary>
    /// Implicit conversion to string for formatting contexts.
    /// </summary>
    public override string ToString() => _sb.ToString();
}
