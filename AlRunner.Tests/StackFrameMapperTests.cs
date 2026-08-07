using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// StackFrameMapper parses the BC service-tier call-stack STRING format that
/// AlRunner.Infrastructure.AlCallStackCapture.BuildStack produces:
///   "ObjectName"(ObjectType N).MethodName[(Trigger)][ line L][ - App by Pub version V]
/// one frame per line, deepest (closest to the throw) first — NOT a .NET exception
/// stack trace (v1's StackFrameMapper parsed ".NET at ... in file:line N" text; v2's
/// AlCallStackCapture already produces AL-only frames in BC's own format, so the
/// parser targets that format instead. See #1641).
/// </summary>
public class StackFrameMapperTests
{
    [Fact]
    public void Walk_ParsesSingleFrame_WithLineAndApp()
    {
        var stack = "\"Probe Test\"(CodeUnit 90999).FailsOnPurpose line 2 - Probe by Probe version 1.0.0.0";
        var frames = StackFrameMapper.Walk(stack);

        Assert.Single(frames);
        Assert.Equal("\"Probe Test\"(CodeUnit 90999).FailsOnPurpose", frames[0].Name);
        Assert.Equal(2, frames[0].Line);
        Assert.Null(frames[0].File);
        Assert.True(frames[0].IsUserCode);
        Assert.Equal(FramePresentationHint.Normal, frames[0].Hint);
    }

    [Fact]
    public void Walk_ParsesMultiFrame_DeepestFirst()
    {
        var stack =
            "\"Alert Engine\"(CodeUnit 60021).New line 30 - MainApp by Contoso version 1.0.0.0\n" +
            "\"Alert Engine Test\"(CodeUnit 60022).NewReturnsTrue line 17 - MainApp Test by Contoso version 1.0.0.0";
        var frames = StackFrameMapper.Walk(stack);

        Assert.Equal(2, frames.Count);
        Assert.Equal("\"Alert Engine\"(CodeUnit 60021).New", frames[0].Name);
        Assert.Equal(30, frames[0].Line);
        Assert.Equal("\"Alert Engine Test\"(CodeUnit 60022).NewReturnsTrue", frames[1].Name);
        Assert.Equal(17, frames[1].Line);
    }

    [Fact]
    public void Walk_HandlesTriggerSuffix()
    {
        var stack = "\"Sales Line\"(Table 37).OnInsert(Trigger) line 5 - Base Application by Microsoft version 28.0.0.0";
        var frames = StackFrameMapper.Walk(stack);

        Assert.Single(frames);
        Assert.Equal("\"Sales Line\"(Table 37).OnInsert(Trigger)", frames[0].Name);
        Assert.Equal(5, frames[0].Line);
    }

    [Fact]
    public void Walk_HandlesMissingLineNumber()
    {
        // Real BC omits the line segment for some frames (e.g. GetRelativeLine returns -1).
        var stack = "\"Foo\"(CodeUnit 1).Bar - Foo by Bar version 1.0.0.0";
        var frames = StackFrameMapper.Walk(stack);

        Assert.Single(frames);
        Assert.Equal("\"Foo\"(CodeUnit 1).Bar", frames[0].Name);
        Assert.Null(frames[0].Line);
    }

    [Fact]
    public void Walk_UnescapesDoubledQuotesInObjectName()
    {
        // BC doubles embedded quotes: AppendQuoted does name.Replace("\"", "\"\"").
        var stack = "\"Say \"\"Hi\"\"\"(CodeUnit 2).Run line 1 - App by Pub version 1.0.0.0";
        var frames = StackFrameMapper.Walk(stack);

        Assert.Single(frames);
        // The Name carries the still-quoted-for-display object segment; only
        // the caller-facing helper below is expected to unescape for display.
        Assert.Equal("Say \"Hi\"", StackFrameMapper.UnquoteObjectName(frames[0]));
    }

    [Fact]
    public void Walk_NullOrEmptyStack_ReturnsEmptyList()
    {
        Assert.Empty(StackFrameMapper.Walk(null));
        Assert.Empty(StackFrameMapper.Walk(""));
        Assert.Empty(StackFrameMapper.Walk("   "));
    }

    [Fact]
    public void Walk_UnparsableLine_FallsBackToRawNameWithoutLying()
    {
        // A line that doesn't match BC's format at all must not be silently dropped,
        // but must also not invent a line number or file it doesn't have.
        var stack = "some unexpected diagnostic text";
        var frames = StackFrameMapper.Walk(stack);

        Assert.Single(frames);
        Assert.Equal("some unexpected diagnostic text", frames[0].Name);
        Assert.Null(frames[0].Line);
        Assert.Null(frames[0].File);
    }

    [Fact]
    public void Walk_AllFramesAreUserCode_NoCSharpNoiseInV2Capture()
    {
        // AlCallStackCapture.BuildStack only ever records scopes with a non-null
        // ApplicationObject (real AL frames); there is no C# infra frame to dim,
        // unlike v1's mixed .NET stack traces.
        var stack = "\"A\"(CodeUnit 1).M line 1 - App by Pub version 1.0.0.0";
        var frames = StackFrameMapper.Walk(stack);

        Assert.All(frames, f => Assert.True(f.IsUserCode));
        Assert.All(frames, f => Assert.Equal(FramePresentationHint.Normal, f.Hint));
    }
}
