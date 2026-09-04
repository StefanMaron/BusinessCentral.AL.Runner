// ExecFailureTests — the suite-error line for an app group whose test run threw (issue #2612).
//
// The claim is small and precise: the line names the APP GROUP. It used to start with the
// literal "<bundled>", the marker every bundle-level failure uses, which is wrong here — this
// failure happens inside the loop over a bundle's app groups, so it means one app contributed
// zero results while its siblings ran normally. Reading such a line told you an app's tests had
// vanished and not whose.
using System.Reflection;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ExecFailureTests
{
    [Fact]
    public void Describe_NamesTheAppGroupAndTheCause()
    {
        var line = ExecFailure.Describe("Contoso_Sales_1_0_0_0", new InvalidOperationException("session is not open"));

        Assert.Equal("Contoso_Sales_1_0_0_0: EXEC-FAIL: session is not open", line);
    }

    /// <summary>The negative direction of the same claim, and the actual regression this guards:
    /// the bundle-level marker must not appear, because it is what made the line useless.</summary>
    [Fact]
    public void Describe_DoesNotUseTheBundleLevelMarker()
    {
        var line = ExecFailure.Describe("Contoso_Sales_1_0_0_0", new InvalidOperationException("boom"));

        Assert.DoesNotContain("<bundled>", line, StringComparison.Ordinal);
        Assert.StartsWith("Contoso_Sales_1_0_0_0:", line, StringComparison.Ordinal);
    }

    /// <summary>Only the first line of the message: these go into a one-line-per-error summary,
    /// and a multi-line exception message would break the shape of every consumer downstream.</summary>
    [Fact]
    public void Describe_KeepsOnlyTheFirstLineOfAMultiLineMessage()
    {
        var line = ExecFailure.Describe("App", new InvalidOperationException("first line\nsecond line\nthird"));

        Assert.Equal("App: EXEC-FAIL: first line", line);
        Assert.DoesNotContain("second line", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ReflectionTypeLoadException's own message names nothing ("Unable to load one or more of
    /// the requested types"). The concrete reasons underneath are the whole diagnosis — almost
    /// always a dependency whose runtime DLL was never built.
    /// </summary>
    [Fact]
    public void Describe_UnwrapsLoaderExceptionsSoTheRealCauseIsVisible()
    {
        var ex = new ReflectionTypeLoadException(
            new Type?[] { null },
            new Exception?[] { new FileNotFoundException("Could not load Contoso.Base"), null },
            "Unable to load one or more of the requested types.");

        var line = ExecFailure.Describe("Contoso_Sales", ex);

        Assert.Contains("Contoso_Sales: EXEC-FAIL: Unable to load one or more of the requested types.", line, StringComparison.Ordinal);
        Assert.Contains("Could not load Contoso.Base", line, StringComparison.Ordinal);
    }

    /// <summary>The same unwrapping when the loader failure arrives WRAPPED, which is how it
    /// usually arrives — through a TargetInvocationException from a reflected call.</summary>
    [Fact]
    public void Describe_UnwrapsAWrappedLoaderException()
    {
        var inner = new ReflectionTypeLoadException(
            new Type?[] { null },
            new Exception?[] { new FileNotFoundException("Could not load Contoso.Base") },
            "Unable to load one or more of the requested types.");

        var line = ExecFailure.Describe("Contoso_Sales", new TargetInvocationException("wrapper", inner));

        Assert.Contains("Could not load Contoso.Base", line, StringComparison.Ordinal);
    }

    /// <summary>Repeated loader reasons are one cause reported many times; quoting each once
    /// keeps the line readable, and the cap keeps it a line.</summary>
    [Fact]
    public void Describe_DeduplicatesAndCapsLoaderReasons()
    {
        var reasons = Enumerable.Range(0, 9)
            .Select(i => (Exception?)new FileNotFoundException($"missing dep {i % 2}"))
            .ToArray();
        var ex = new ReflectionTypeLoadException(new Type?[reasons.Length], reasons, "Unable to load.");

        var line = ExecFailure.Describe("App", ex);

        Assert.Equal(2, line.Split(" | ").Length);
        Assert.Contains("missing dep 0", line, StringComparison.Ordinal);
        Assert.Contains("missing dep 1", line, StringComparison.Ordinal);
    }

    /// <summary>A loader exception carrying no readable reasons still produces a usable line
    /// rather than one ending in a dangling separator.</summary>
    [Fact]
    public void Describe_LoaderExceptionWithNoReasons_StillNamesTheAppGroup()
    {
        var ex = new ReflectionTypeLoadException(new Type?[] { null }, new Exception?[] { null }, "Unable to load.");

        var line = ExecFailure.Describe("App", ex);

        Assert.Equal("App: EXEC-FAIL: Unable to load.", line);
        Assert.DoesNotContain("—", line, StringComparison.Ordinal);
    }

    // ── TargetInvocationException: the wrapper that names nothing ──────────────
    //
    // Reflection-invoked test entry points surface every failure as
    // TargetInvocationException, whose own message is the fixed string "Exception has been
    // thrown by the target of an invocation." — it names no app, no API and no cause, and the
    // real exception is on InnerException. Describe unwrapped ReflectionTypeLoadException for
    // exactly this reason and stopped there, so a whole bucket could report one useless line.
    //
    // Measured on Microsoft's Tests-SINGLESERVER bucket (BC 28.1): the entire bucket's only
    // diagnostic was "Tests-SINGLESERVER: EXEC-FAIL: Exception has been thrown by the target of
    // an invocation.", with nothing more under --verbose.

    /// <summary>The wrapper's fixed message must be replaced by the inner cause, not printed.</summary>
    [Fact]
    public void Describe_UnwrapsTargetInvocationException_AndReportsTheRealCause()
    {
        var line = ExecFailure.Describe("Tests-SINGLESERVER",
            new TargetInvocationException(new InvalidOperationException("Company 'CRONUS' does not exist")));

        Assert.Equal("Tests-SINGLESERVER: EXEC-FAIL: Company 'CRONUS' does not exist", line);
        Assert.DoesNotContain("target of an invocation", line, StringComparison.Ordinal);
    }

    /// <summary>Nested wrappers unwrap all the way down — reflection over reflection is common
    /// on the test-invocation path, and stopping at the first level just prints a second
    /// wrapper's equally contentless message.</summary>
    [Fact]
    public void Describe_UnwrapsNestedTargetInvocationExceptions()
    {
        var line = ExecFailure.Describe("App",
            new TargetInvocationException(new TargetInvocationException(new DivideByZeroException("real cause"))));

        Assert.Equal("App: EXEC-FAIL: real cause", line);
    }

    /// <summary>Negative: a TargetInvocationException with NO inner exception has nothing better
    /// to say, so its own message must still be reported rather than producing an empty line.</summary>
    [Fact]
    public void Describe_KeepsTheWrapperMessage_WhenThereIsNoInnerException()
    {
        var line = ExecFailure.Describe("App", new TargetInvocationException(null));

        Assert.StartsWith("App: EXEC-FAIL: ", line, StringComparison.Ordinal);
        Assert.True(line.Length > "App: EXEC-FAIL: ".Length, "the line must still carry a message");
    }

    /// <summary>Negative: unwrapping must not lose the ReflectionTypeLoadException handling that
    /// already worked — a RTLE reached THROUGH a TargetInvocationException must still have its
    /// LoaderExceptions quoted, which is the whole diagnosis in that case.</summary>
    [Fact]
    public void Describe_StillQuotesLoaderExceptions_WhenReachedThroughATargetInvocationException()
    {
        var rtle = new ReflectionTypeLoadException(
            new Type[] { null! },
            new Exception?[] { new FileNotFoundException("Contoso.Dep.dll not found") });

        var line = ExecFailure.Describe("App", new TargetInvocationException(rtle));

        Assert.Contains("Contoso.Dep.dll not found", line, StringComparison.Ordinal);
    }
}
