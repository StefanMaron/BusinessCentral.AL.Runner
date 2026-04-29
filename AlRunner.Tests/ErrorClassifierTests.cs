using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public class ErrorClassifierTests
{
    [Fact]
    public void Classify_AssertionException_IsAssertion()
    {
        var ex = new MockAssertException("expected 1 got 2");
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Assertion, ErrorClassifier.Classify(ex, ctx));
    }

    [Fact]
    public void Classify_OperationCanceled_IsTimeout()
    {
        var ex = new OperationCanceledException("test exceeded timeout");
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Timeout, ErrorClassifier.Classify(ex, ctx));
    }

    [Fact]
    public void Classify_CompilationFailedException_IsCompile()
    {
        var ex = new CompilationFailedExceptionStub("compile error");
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Compile, ErrorClassifier.Classify(ex, ctx));
    }

    [Fact]
    public void Classify_GenericException_DuringSetup_IsSetup()
    {
        var ex = new InvalidOperationException("setup failed");
        var ctx = new TestExecutionContext(InsideTestProc: false);
        Assert.Equal(AlErrorKind.Setup, ErrorClassifier.Classify(ex, ctx));
    }

    [Fact]
    public void Classify_GenericException_DuringTest_IsRuntime()
    {
        var ex = new InvalidOperationException("runtime error");
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Runtime, ErrorClassifier.Classify(ex, ctx));
    }

    [Fact]
    public void Classify_NullException_IsUnknown()
    {
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Unknown, ErrorClassifier.Classify(null!, ctx));
    }

    private sealed class MockAssertException : Exception
    {
        public MockAssertException(string m) : base(m) { }
    }

    private sealed class CompilationFailedExceptionStub : Exception
    {
        public CompilationFailedExceptionStub(string m) : base(m) { }
    }
}
