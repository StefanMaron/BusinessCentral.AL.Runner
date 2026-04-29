using AlRunner.Runtime;
using Xunit;

namespace AlRunner.Tests;

public class ErrorClassifierTests
{
    // ----- Original tests (retained / upgraded) -----

    [Fact]
    public void Classify_AssertionException_IsAssertion()
    {
        // Uses the real AlRunner.Runtime.AssertException — the type actually thrown by MockAssert.
        var ex = new AssertException("expected 1 got 2");
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
        var ex = new MyDomainCompilationFailedException("compile error");
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
        Assert.Equal(AlErrorKind.Unknown, ErrorClassifier.Classify(null, ctx));
    }

    // ----- New branch-coverage tests -----

    [Fact]
    public void Classify_AssertionExceptionEnding_IsAssertion()
    {
        // Verifies the EndsWith("AssertException") branch fires on its own.
        var ex = new MyDomainAssertException("oops");
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Assertion, ErrorClassifier.Classify(ex, ctx));
    }

    [Fact]
    public void Classify_AssertionExceptionFullSuffix_IsAssertion()
    {
        // Verifies the EndsWith("AssertionException") branch fires.
        var ex = new MyDomainAssertionException("oops");
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Assertion, ErrorClassifier.Classify(ex, ctx));
    }

    [Fact]
    public void Classify_TaskCanceledException_IsTimeout()
    {
        // TaskCanceledException is a subclass of OperationCanceledException — most common cancellation type in .NET.
        var ex = new TaskCanceledException("cancelled");
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Timeout, ErrorClassifier.Classify(ex, ctx));
    }

    [Fact]
    public void Classify_OperationCanceledDuringSetup_IsTimeoutNotSetup()
    {
        // Pins ordering: timeout takes precedence over the InsideTestProc gate.
        var ex = new OperationCanceledException("cancelled in setup");
        var ctx = new TestExecutionContext(InsideTestProc: false);
        Assert.Equal(AlErrorKind.Timeout, ErrorClassifier.Classify(ex, ctx));
    }

    [Fact]
    public void Classify_CompileErrorEnding_IsCompile()
    {
        var ex = new MyDomainCompileErrorException("bad emit");
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Compile, ErrorClassifier.Classify(ex, ctx));
    }

    [Fact]
    public void Classify_AggregateExceptionWrappingAssertion_IsRuntime()
    {
        // Documents that unwrapping AggregateException is the caller's job.
        // Future change: if Executor unwraps before classifying, this expectation moves.
        var inner = new MyDomainAssertException("inner");
        var ex = new AggregateException(inner);
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Runtime, ErrorClassifier.Classify(ex, ctx));
    }

    [Fact]
    public void Classify_BareException_DuringTest_IsRuntime()
    {
        // Verifies the default-Runtime fallthrough on the base Exception type.
        var ex = new Exception("base");
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Runtime, ErrorClassifier.Classify(ex, ctx));
    }

    // ----- Private stubs (name-based matching only) -----

    private sealed class MyDomainAssertException : Exception
    {
        public MyDomainAssertException(string m) : base(m) { }
    }

    private sealed class MyDomainAssertionException : Exception
    {
        public MyDomainAssertionException(string m) : base(m) { }
    }

    private sealed class MyDomainCompilationFailedException : Exception
    {
        public MyDomainCompilationFailedException(string m) : base(m) { }
    }

    private sealed class MyDomainCompileErrorException : Exception
    {
        public MyDomainCompileErrorException(string m) : base(m) { }
    }
}
