using System;
using System.Reflection;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Pins <see cref="BcRuntime.RethrowPreservingStack"/> — the shared rethrow helper both
/// <c>CodeunitEventDispatch_OnRunEventAsync</c> and <c>DispatchCore</c>'s per-subscriber catch
/// funnel through.
///
/// Issue #1955: <c>DispatchCore</c>'s catch used to do <c>throw tie.InnerException ?? tie;</c> —
/// a bare rethrow of a caught-and-referenced exception, which RESETS the exception's stack
/// trace to the rethrow site. Every frame identifying WHICH subscriber threw and where inside
/// it was discarded, so every subscriber failure read as "NullReferenceException at
/// DispatchCore" — the dispatcher blaming itself for its callees. The sibling catch a few
/// lines above (now the definition site of the shared helper) already used the correct
/// <c>ExceptionDispatchInfo.Capture(...).Throw()</c> form; this fix makes both call sites use
/// exactly the same helper so neither can regress independently.
///
/// NOTE ON COVERAGE: the actual dispatch path (reflection Invoke through a real BC subscriber
/// method, wrapped in TargetInvocationException by the CLR) needs a compiled AL bundle and is
/// exercised end-to-end by the runner-extras repro in issue #1955/#1956 (measured manually:
/// before the fix, a table-sender subscriber's NRE read as "at DispatchCore line 202"; after,
/// it names "Codeunit70012.OnTableDiscover(INavRecordHandle sender)" outright). This test pins
/// the specific rethrow defect at the seam, the same pattern
/// DispatchObserveAsyncResultTests.cs and DispatchCoerceArgByRefTests.cs use for their seams
/// in the same file.
/// </summary>
public class RethrowPreservingStackTests
{
    // Two nested frames below the reflection Invoke boundary — named distinctively so their
    // presence/absence in the resulting exception's StackTrace is unambiguous evidence.
    private static void InnermostSubscriberFrame() =>
        throw new NullReferenceException("subscriber wrote through a null sender");

    private static void MiddleSubscriberFrame() => InnermostSubscriberFrame();

    private static TargetInvocationException CaptureRealDispatchShapedException()
    {
        // Mirrors exactly how DispatchCore observes a subscriber failure: MethodInfo.Invoke
        // wraps whatever the invoked method threw in a TargetInvocationException.
        var method = typeof(RethrowPreservingStackTests).GetMethod(nameof(MiddleSubscriberFrame),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            method.Invoke(null, Array.Empty<object>());
            throw new InvalidOperationException("expected the target method to throw");
        }
        catch (TargetInvocationException tie)
        {
            return tie;
        }
    }

    [Fact]
    public void PreservesTheOriginalFramesBelowTheRethrowSite()
    {
        var tie = CaptureRealDispatchShapedException();

        var ex = Assert.Throws<NullReferenceException>(
            () => BcRuntime.RethrowPreservingStack(tie.InnerException ?? tie));

        // The decisive assertion: with the fix, the frames identifying WHICH method threw and
        // where survive the rethrow. A bare `throw tie.InnerException ?? tie;` would reset the
        // stack trace to the rethrow site and neither frame name would appear.
        Assert.Contains(nameof(InnermostSubscriberFrame), ex.StackTrace);
        Assert.Contains(nameof(MiddleSubscriberFrame), ex.StackTrace);
    }

    [Fact]
    public void PreservesTheOriginalExceptionIdentityAndMessage()
    {
        var tie = CaptureRealDispatchShapedException();
        var original = tie.InnerException!;

        var ex = Assert.Throws<NullReferenceException>(
            () => BcRuntime.RethrowPreservingStack(tie.InnerException ?? tie));

        // Same instance, not a wrapped copy — callers upstream (TestExecutor's Unwrap/catch)
        // must see the exact exception the subscriber raised.
        Assert.Same(original, ex);
        Assert.Equal("subscriber wrote through a null sender", ex.Message);
    }

    [Fact]
    public void BareRethrow_WouldResetTheStackTrace_ProvingTheDefectTheFixRemoves()
    {
        // Negative control: reproduces the EXACT defect #1955 fixed (`throw tie.InnerException
        // ?? tie;`) side-by-side with the real helper, so this test would fail (i.e. the
        // contrast would vanish) if RethrowPreservingStack regressed back to a bare rethrow.
        var tie = CaptureRealDispatchShapedException();
        var inner = tie.InnerException!;

        NullReferenceException bareRethrown;
        try
        {
            throw inner; // the pre-fix idiom, reproduced deliberately
        }
        catch (NullReferenceException caught)
        {
            bareRethrown = caught;
        }

        Assert.DoesNotContain(nameof(InnermostSubscriberFrame), bareRethrown.StackTrace ?? "");
    }
}
