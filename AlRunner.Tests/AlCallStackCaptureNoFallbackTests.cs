using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #1958: an install-trigger failure (or anything else that runs outside a test) must
/// never be reported with an unrelated exception's AL stack.
///
/// <see cref="AlCallStackCapture.GetCaptured(Exception?)"/> deliberately falls back to
/// the most-recent capture — correct for its original caller, a FAILING TEST, where the
/// exception the runner finally reports may have been wrapped/re-created on the way out
/// and the last capture is still that same test's own stack. It is wrong for an install
/// trigger: under <c>--watch</c>, <c>_captured</c> there can be an arbitrarily old,
/// unrelated PREVIOUS cycle's test. <see cref="AlCallStackCapture.GetCapturedFor"/> is
/// the strict counterpart with no such fallback, and is what
/// <see cref="InstallTriggerRunner"/> must use instead.
///
/// These tests seed <c>_captured</c> with a stack that unambiguously belongs to a
/// DIFFERENT exception (never registered for the one under test) — the exact shape of
/// "stale capture left over from an earlier failure" — and prove the strict/fallback
/// contrast directly, plus (<see cref="InstallTriggerFailure_NeverPrintsAnUnrelatedExceptionsAlStack"/>)
/// that <see cref="InstallTriggerRunner"/>'s own diagnostic actually uses the strict path.
///
/// All three tests live in ONE class deliberately: they mutate
/// <c>AlCallStackCapture._captured</c>, a process-global static, and xUnit runs test
/// METHODS within a class sequentially by default (only different classes/collections
/// run in parallel) — split across two classes this raced for real (observed locally:
/// the wiring test's own seed clobbered the fallback test's seed mid-assertion).
/// </summary>
public class AlCallStackCaptureNoFallbackTests
{
    private static FieldInfo GetPrivateStatic(string name)
    {
        var f = typeof(AlCallStackCapture).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(f);
        return f!;
    }

    // Positive: GetCapturedFor DOES answer for an exception that genuinely has its own
    // capture — the strict method must not be "always null", it must be "no FALLBACK".
    [Fact]
    public void GetCapturedFor_ReturnsTheStackCapturedForThatExactException()
    {
        var byExceptionField = GetPrivateStatic("_byException");
        var byException = (System.Runtime.CompilerServices.ConditionalWeakTable<Exception, string>)
            byExceptionField.GetValue(null)!;

        var target = new InvalidOperationException("target");
        const string ownStack = "\"Owner Codeunit\"(CodeUnit 70030).OwnTrigger line 1 - Fixture by AL Runner version 1.0.0.0";
        byException.AddOrUpdate(target, ownStack);
        try
        {
            Assert.Equal(ownStack, AlCallStackCapture.GetCapturedFor(target));
        }
        finally
        {
            byException.Remove(target);
        }
    }

    // The core proving case (#1958): a DIFFERENT exception's stack is the most-recent
    // capture (_captured), but this exception was never registered for one of its own.
    // GetCaptured falls back and returns the unrelated stack (existing, documented
    // behaviour — asserted here so the contrast below is unambiguous); GetCapturedFor
    // must return null, never the unrelated stack.
    [Fact]
    public void GetCapturedFor_HasNoFallback_UnlikeGetCaptured()
    {
        var capturedField = GetPrivateStatic("_captured");
        var previous = capturedField.GetValue(null);
        try
        {
            const string staleStackFromAnUnrelatedTest =
                "\"Some Other Test\"(CodeUnit 70099).SomeOtherTrigger line 3 - Fixture by AL Runner version 1.0.0.0";
            capturedField.SetValue(null, staleStackFromAnUnrelatedTest);

            // An exception that was never registered in _byException at all — models an
            // install-trigger's plain NullReferenceException, which is never a NavException
            // subclass and so the FCE handler never captured anything for it.
            var unrelated = new NullReferenceException("install trigger blew up");

            Assert.Equal(staleStackFromAnUnrelatedTest, AlCallStackCapture.GetCaptured(unrelated));
            Assert.Null(AlCallStackCapture.GetCapturedFor(unrelated));
        }
        finally
        {
            capturedField.SetValue(null, previous);
        }
    }

    // The trigger method InvokeTrigger reflects into and calls. Static, so it needs no
    // real instance; throwing here is what MethodInfo.Invoke wraps in a
    // TargetInvocationException — exactly InvokeTrigger's catch clause shape.
    private static void ThrowsPlainException() =>
        throw new NullReferenceException("simulated non-AL install-trigger failure");

    // Drives InstallTriggerRunner's private InvokeTrigger directly (via reflection — it
    // has no public seam) to prove the WIRING, not just the strict method's own contract:
    // with a stale _captured AL stack left over from an unrelated earlier capture, an
    // install trigger that throws a plain (non-AL) exception must have its diagnostic
    // print the REAL .NET exception, never the stale AL stack.
    [Fact]
    public void InstallTriggerFailure_NeverPrintsAnUnrelatedExceptionsAlStack()
    {
        var capturedField = GetPrivateStatic("_captured");
        var previous = capturedField.GetValue(null);

        var originalErr = Console.Error;
        var buffer = new StringWriter();
        try
        {
            // Seed the stale fallback exactly the way a previous --watch cycle's test
            // would leave it: a real AL stack, for a DIFFERENT exception than the one
            // this install trigger throws.
            const string staleAlStackFromAPreviousCycle =
                "\"Codeunit From A Previous Watch Cycle\"(CodeUnit 60996).SomePassingTest "
                + "line 11 - Watch Discovery Tests by AlRunner Tests version 1.0.0.0";
            capturedField.SetValue(null, staleAlStackFromAPreviousCycle);

            Console.SetError(buffer);

            var cuType = typeof(InstallTriggerRunner).GetNestedType("InstallCodeunit", BindingFlags.NonPublic)!;
            var trigger = typeof(AlCallStackCaptureNoFallbackTests)
                .GetMethod(nameof(ThrowsPlainException), BindingFlags.NonPublic | BindingFlags.Static)!;
            var dummyCtor = typeof(object).GetConstructor(Type.EmptyTypes)!;
            // Records also generate a copy constructor (InstallCodeunit(InstallCodeunit)) —
            // select the 4-parameter primary constructor explicitly rather than relying on
            // GetConstructors() ordering.
            var cuCtor = cuType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(c => c.GetParameters().Length == 4);
            var cu = cuCtor.Invoke(new object?[] { typeof(object), dummyCtor, trigger, null });

            var invokeTrigger = typeof(InstallTriggerRunner).GetMethod(
                "InvokeTrigger", BindingFlags.NonPublic | BindingFlags.Static)!;

            // InvokeTrigger rethrows the original exception after printing the
            // diagnostic — that rethrow is expected, not the thing under test.
            var target = invokeTrigger.Invoke(null, new object?[] { cu, new object(), trigger, "ThrowsPlainException" });
        }
        catch (TargetInvocationException tie) when (tie.InnerException is NullReferenceException)
        {
            // Expected: InvokeTrigger rethrows the NullReferenceException; reflection
            // wraps it once more. This is the success path for THIS test.
        }
        finally
        {
            Console.SetError(originalErr);
            capturedField.SetValue(null, previous);
        }

        var printed = buffer.ToString();
        Assert.DoesNotContain("Codeunit From A Previous Watch Cycle", printed);
        Assert.DoesNotContain("SomePassingTest", printed);
        Assert.Contains("NullReferenceException", printed);
        Assert.Contains("simulated non-AL install-trigger failure", printed);
    }
}
