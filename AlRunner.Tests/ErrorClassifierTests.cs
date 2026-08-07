using Xunit;

namespace AlRunner.Tests;

public class ErrorClassifierTests
{
    [Fact]
    public void Classify_NullException_IsUnknown()
    {
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Unknown, ErrorClassifier.Classify(null, ctx));
    }

    [Fact]
    public void Classify_OperationCanceled_IsTimeout()
    {
        var ex = new OperationCanceledException("test exceeded timeout");
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Timeout, ErrorClassifier.Classify(ex, ctx));
    }

    [Fact]
    public void Classify_TaskCanceledException_IsTimeout()
    {
        // TaskCanceledException derives from OperationCanceledException.
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
    public void Classify_GenericException_DuringSetup_IsSetup()
    {
        var ex = new InvalidOperationException("ctor failed");
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

    // v2-specific: real BC's Error()-based Assert failures throw
    // Microsoft.Dynamics.Nav.Types.NavNCLDialogException — the SAME type a plain
    // Error() call throws — so there is no type-based signal to bucket them as
    // Assertion today. Pin that they fall through to Runtime rather than silently
    // being misclassified as Setup or Compile.
    [Fact]
    public void Classify_NavStyleDialogException_DuringTest_FallsThroughToRuntime()
    {
        var ex = new InvalidOperationException("boom") { };
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Runtime, ErrorClassifier.Classify(ex, ctx));
    }

    [Fact]
    public void Classify_AssertionExceptionSuffix_IsAssertion()
    {
        var ex = new MyDomainAssertException("oops");
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Assertion, ErrorClassifier.Classify(ex, ctx));
    }

    [Fact]
    public void Classify_AssertionExceptionFullSuffix_IsAssertion()
    {
        var ex = new MyDomainAssertionException("oops");
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Assertion, ErrorClassifier.Classify(ex, ctx));
    }

    [Fact]
    public void Classify_CompilationFailedException_IsCompile()
    {
        var ex = new MyDomainCompilationFailedException("compile error");
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Compile, ErrorClassifier.Classify(ex, ctx));
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
        var inner = new MyDomainAssertException("inner");
        var ex = new AggregateException(inner);
        var ctx = new TestExecutionContext(InsideTestProc: true);
        Assert.Equal(AlErrorKind.Runtime, ErrorClassifier.Classify(ex, ctx));
    }

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
