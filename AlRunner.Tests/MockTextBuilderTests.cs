using AlRunner.Runtime;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Unit tests for MockTextBuilder line-terminator behavior.
///
/// BC's TextBuilder.AppendLine appends a bare LF ('\n', Char(10)) on every OS.
/// MockTextBuilder previously delegated to StringBuilder.AppendLine(), which uses
/// Environment.NewLine — "\r\n" on Windows, "\n" on Linux/macOS — so AL code that
/// round-trips through AppendLine produced different bytes depending on the host.
///
/// The CI matrix (ubuntu-latest only) never exercised the Windows path, so the
/// existing AL e2e test tests/bucket-1/17-text-builder passed on CI for the
/// wrong reason and failed on Windows developer machines.
///
/// Note: pre-fix, these assertions fail on Windows (where StringBuilder.AppendLine
/// emits CRLF) but accidentally pass on Linux (where it already emits LF). Post-fix
/// they are byte-exact on every OS and serve as regression guards against any future
/// reintroduction of StringBuilder.AppendLine.
///
/// Issue: #1157
/// </summary>
public class MockTextBuilderTests
{
    [Fact]
    public void ALAppendLine_NoArgs_EmitsLf_NotCrlf()
    {
        var tb = new MockTextBuilder();
        tb.ALAppendLine();

        Assert.Equal("\n", tb.ToString());
        Assert.DoesNotContain("\r", tb.ToString());
    }

    [Fact]
    public void ALAppendLine_WithDataError_EmitsLf_NotCrlf()
    {
        var tb = new MockTextBuilder();
        tb.ALAppendLine(DataError.ThrowError);

        Assert.Equal("\n", tb.ToString());
        Assert.DoesNotContain("\r", tb.ToString());
    }

    [Fact]
    public void ALAppendLine_WithDataErrorAndText_AppendsTextThenLf_NotCrlf()
    {
        var tb = new MockTextBuilder();
        tb.ALAppendLine(DataError.ThrowError, new NavText("hello"));

        Assert.Equal("hello\n", tb.ToString());
        Assert.DoesNotContain("\r", tb.ToString());
    }

    [Fact]
    public void ALAppendLine_MultipleCalls_ProducesLfJoinedLines_NotCrlf()
    {
        var tb = new MockTextBuilder();
        tb.ALAppendLine(DataError.ThrowError, new NavText("one"));
        tb.ALAppendLine(DataError.ThrowError, new NavText("two"));
        tb.ALAppend(DataError.ThrowError, new NavText("three"));

        Assert.Equal("one\ntwo\nthree", tb.ToString());
        Assert.DoesNotContain("\r", tb.ToString());
    }
}
