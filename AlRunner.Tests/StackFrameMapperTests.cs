using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public class StackFrameMapperTests
{
    [Fact]
    public void Walk_ParsesAlFilenames_AsUserCode()
    {
        var trace = "   at Foo.Bar.RunTest() in src/Foo.al:line 42\n";
        var ex = MakeExceptionWithStackTrace(trace);
        var frames = StackFrameMapper.Walk(ex);
        Assert.Single(frames);
        Assert.Equal("src/Foo.al", frames[0].File);
        Assert.Equal(42, frames[0].Line);
        Assert.True(frames[0].IsUserCode);
        Assert.Equal(FramePresentationHint.Normal, frames[0].Hint);
    }

    [Fact]
    public void Walk_DimsRuntimeFrames()
    {
        var trace = "   at AlRunner.Runtime.MockRecord.Insert() in mock.cs:line 15\n";
        var ex = MakeExceptionWithStackTrace(trace);
        var frames = StackFrameMapper.Walk(ex);
        Assert.Single(frames);
        Assert.False(frames[0].IsUserCode);
        Assert.Equal(FramePresentationHint.Subtle, frames[0].Hint);
    }

    [Fact]
    public void Walk_HandlesUnknownFrames()
    {
        var trace = "   at SomeMethod()\n";
        var ex = MakeExceptionWithStackTrace(trace);
        var frames = StackFrameMapper.Walk(ex);
        Assert.Single(frames);
        Assert.Null(frames[0].File);
        Assert.Null(frames[0].Line);
        Assert.False(frames[0].IsUserCode);
    }

    [Fact]
    public void FindDeepestUserFrame_ReturnsLastUserFrame()
    {
        var trace =
            "   at AlRunner.Runtime.MockRecord.Insert() in mock.cs:line 5\n" +
            "   at MainApp.AlertEngine.New() in src/AlertEngine.Codeunit.al:line 30\n" +
            "   at AlRunner.Runtime.MockCodeunit.Invoke() in mock2.cs:line 10\n" +
            "   at MainApp.Tests.NewReturnsTrue() in test/AlertEngineTest.al:line 17\n";
        var ex = MakeExceptionWithStackTrace(trace);
        var frames = StackFrameMapper.Walk(ex);
        var deepest = StackFrameMapper.FindDeepestUserFrame(frames);
        Assert.NotNull(deepest);
        Assert.Equal("test/AlertEngineTest.al", deepest!.File);
        Assert.Equal(17, deepest.Line);
    }

    [Fact]
    public void FindDeepestUserFrame_NoUserFrames_ReturnsNull()
    {
        var trace = "   at AlRunner.Runtime.MockRecord.Insert() in mock.cs:line 5\n";
        var ex = MakeExceptionWithStackTrace(trace);
        var frames = StackFrameMapper.Walk(ex);
        Assert.Null(StackFrameMapper.FindDeepestUserFrame(frames));
    }

    [Fact]
    public void Walk_EmptyOrNullStackTrace_ReturnsEmptyList()
    {
        var ex = new InvalidOperationException("test");
        var frames = StackFrameMapper.Walk(ex);
        Assert.NotNull(frames);
    }

    [Fact]
    public void Walk_QuotedPathsWithSpaces()
    {
        var trace = "   at Foo.Bar() in src/Some Folder/Customer Score.Codeunit.al:line 99\n";
        var ex = MakeExceptionWithStackTrace(trace);
        var frames = StackFrameMapper.Walk(ex);
        Assert.Single(frames);
        Assert.Equal("src/Some Folder/Customer Score.Codeunit.al", frames[0].File);
        Assert.Equal(99, frames[0].Line);
    }

    [Fact]
    public void ClassifyHint_MockTypeIsSubtle()
    {
        Assert.Equal(FramePresentationHint.Subtle,
            StackFrameMapper.ClassifyHint("mock.cs", "AlRunner.Runtime.MockRecord.Insert"));
    }

    [Fact]
    public void ClassifyHint_MicrosoftDynamicsIsSubtle()
    {
        Assert.Equal(FramePresentationHint.Subtle,
            StackFrameMapper.ClassifyHint("nav.cs", "Microsoft.Dynamics.Nav.Some.Method"));
    }

    [Fact]
    public void ClassifyHint_UserCodeIsNormal()
    {
        Assert.Equal(FramePresentationHint.Normal,
            StackFrameMapper.ClassifyHint("src/Foo.al", "MainApp.Foo.Method"));
    }

    [Fact]
    public void ClassifyHint_UnknownDeemphasize()
    {
        Assert.Equal(FramePresentationHint.Deemphasize,
            StackFrameMapper.ClassifyHint(null, null));
    }

    private static Exception MakeExceptionWithStackTrace(string trace)
        => new ExceptionWithFakeStackTrace(trace);

    private sealed class ExceptionWithFakeStackTrace : Exception
    {
        private readonly string _trace;
        public ExceptionWithFakeStackTrace(string trace) : base("fake") => _trace = trace;
        public override string? StackTrace => _trace;
    }
}
