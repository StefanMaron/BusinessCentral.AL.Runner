using System.Reflection;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Regression guard for the "lost AL call stacks" bug.
///
/// AlCallStackCapture arms capture via Clear() on the test-executor thread, but the AL
/// test body — and therefore the thrown NavException whose first-chance the capture hooks —
/// runs on a dedicated per-test worker thread (TestExecutor.InvokeWithTimeout, introduced by
/// the timeout-watchdog commit d89b0537). When the arm flag (_captureEnabled) and the result
/// holder (_captured) were [ThreadStatic], the flag set on the executor thread was invisible
/// to the worker thread, so the FCE handler bailed out and no AL stack was ever captured —
/// test failures silently fell back to the raw C# stack.
///
/// These tests pin the fix: the two fields must be process-global, not thread-local.
/// They fail on the buggy ([ThreadStatic]) version and pass once the fields are global.
/// </summary>
public class AlCallStackCaptureThreadingTests
{
    private static FieldInfo GetPrivateStatic(string name)
    {
        var f = typeof(AlCallStackCapture).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(f); // field must exist (renaming it would also break capture wiring)
        return f!;
    }

    [Fact]
    public void CaptureFields_AreNotThreadStatic()
    {
        // [ThreadStatic] on these fields is exactly what broke cross-thread capture.
        foreach (var name in new[] { "_captured", "_captureEnabled" })
        {
            var f = GetPrivateStatic(name);
            var isThreadStatic = f.GetCustomAttribute<ThreadStaticAttribute>() != null;
            Assert.False(isThreadStatic,
                $"AlCallStackCapture.{name} must NOT be [ThreadStatic] — the AL test body throws " +
                $"on a worker thread, not the thread that armed capture via Clear().");
        }
    }

    [Fact]
    public void Clear_ArmsCapture_VisibleFromAnotherThread()
    {
        // Arm on this thread...
        AlCallStackCapture.Clear();

        var enabledField = GetPrivateStatic("_captureEnabled");

        // ...and read the arm flag from a different thread (mirrors the executor→worker split).
        bool seenFromWorker = false;
        var worker = new Thread(() => seenFromWorker = (bool)enabledField.GetValue(null)!);
        worker.Start();
        worker.Join();

        Assert.True(seenFromWorker,
            "Capture armed by Clear() on the executor thread must be visible to the worker " +
            "thread that runs the AL test body — otherwise the FCE handler never captures.");
    }
}
