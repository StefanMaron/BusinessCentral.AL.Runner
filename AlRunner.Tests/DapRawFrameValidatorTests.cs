using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Proves DapRawFrameValidator actually detects corruption — not just that it
/// accepts well-formed input, which a no-op "always return N" implementation would
/// also do. Each negative case reproduces a concrete way a stray byte could reach
/// stdout (the exact failure mode issue #2058 exists to rule out) and asserts the
/// validator throws naming an offset, not that it silently under- or over-counts.
/// </summary>
public class DapRawFrameValidatorTests
{
    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        return header.Concat(body).ToArray();
    }

    [Fact]
    public void CountFramesOrThrow_TwoWellFormedFrames_ReturnsTwo()
    {
        var raw = Frame("""{"a":1}""").Concat(Frame("""{"b":2}""")).ToArray();
        Assert.Equal(2, DapRawFrameValidator.CountFramesOrThrow(raw));
    }

    [Fact]
    public void CountFramesOrThrow_EmptyInput_ReturnsZero()
    {
        Assert.Equal(0, DapRawFrameValidator.CountFramesOrThrow(Array.Empty<byte>()));
    }

    [Fact]
    public void CountFramesOrThrow_StrayBannerBeforeFirstFrame_Throws()
    {
        // Reproduces exactly the bug #2058 exists to prevent: a startup banner
        // ("[dap] listening on ...", the kind of line RunDapLoop used to print via
        // plain Console.WriteLine) landing on stdout ahead of the first real frame.
        var raw = Encoding.ASCII.GetBytes("[dap] listening on 127.0.0.1:4711\n")
            .Concat(Frame("""{"a":1}""")).ToArray();
        var ex = Assert.Throws<InvalidDataException>(() => DapRawFrameValidator.CountFramesOrThrow(raw));
        Assert.Contains("offset 0", ex.Message);
    }

    [Fact]
    public void CountFramesOrThrow_StrayBytesBetweenFrames_Throws()
    {
        var frame1 = Frame("""{"a":1}""");
        var raw = frame1.Concat(Encoding.ASCII.GetBytes("oops\n")).Concat(Frame("""{"b":2}""")).ToArray();
        var ex = Assert.Throws<InvalidDataException>(() => DapRawFrameValidator.CountFramesOrThrow(raw));
        Assert.Contains($"offset {frame1.Length}", ex.Message);
    }

    [Fact]
    public void CountFramesOrThrow_TrailingGarbageAfterLastFrame_Throws()
    {
        var frame1 = Frame("""{"a":1}""");
        var raw = frame1.Concat(Encoding.ASCII.GetBytes("[dap] client connected.\n")).ToArray();
        var ex = Assert.Throws<InvalidDataException>(() => DapRawFrameValidator.CountFramesOrThrow(raw));
        Assert.Contains($"offset {frame1.Length}", ex.Message);
    }

    [Fact]
    public void CountFramesOrThrow_TruncatedBody_Throws()
    {
        var full = Frame("""{"hello":"world"}""");
        var truncated = full.AsSpan(0, full.Length - 3).ToArray(); // chop off the last 3 body bytes
        Assert.Throws<InvalidDataException>(() => DapRawFrameValidator.CountFramesOrThrow(truncated));
    }

    [Fact]
    public void CountFramesOrThrow_WrongContentLengthClaimsMoreThanActualBody_Throws()
    {
        var body = Encoding.UTF8.GetBytes("""{"a":1}""");
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length + 100}\r\n\r\n");
        var raw = header.Concat(body).ToArray();
        Assert.Throws<InvalidDataException>(() => DapRawFrameValidator.CountFramesOrThrow(raw));
    }
}
