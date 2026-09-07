using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Pins <c>InstallTriggerRunner.InvokeTrigger</c> observing the result of the install
/// trigger it invokes, so a <c>Subtype = Install</c> codeunit's error reaches the caller
/// instead of being parked on a discarded <c>ValueTask</c> (#2960).
///
/// WHY THIS IS A C# TEST AND NOT AL. The broken shape needs an install trigger compiled as
/// an <c>async ValueTask</c> state machine, and no install trigger the runner's own compile
/// path has produced is one. Measured on BC 28.1 by printing <c>trigger.ReturnType</c> at the
/// call site:
///
/// <list type="bullet">
/// <item>all sixteen PRECOMPILED platform install triggers in the closure returned
/// <c>ValueTask</c> (Codeunit1809, 1933, 3999, 3907, 1596, 2014, 290, 4331, 5000, 7582,
/// 7760, 7782, 8352, 9056, 9993 …);</item>
/// <item>every SOURCE-COMPILED one returned <c>Void</c>: both triggers of
/// <c>tests/runner-extras/install-trigger-seed</c>'s Codeunit60711 — whose per-company body
/// is not just <c>Record.Init/Insert</c>, it also calls <c>StartSession</c> — and both
/// triggers of a throwaway probe bundle written specifically to try to force the async shape,
/// one of them a bare <c>Codeunit.Run(Codeunit::"…")</c>.</item>
/// </list>
///
/// <para>WHAT THAT DOES AND DOES NOT ESTABLISH. It is not a property of "the emitter": the
/// runner compiles AL with BC's own compiler, so <c>Void</c> versus <c>async ValueTask</c> is
/// decided by the BODY, and the precompiled sixteen are async because their bodies await. So
/// this is a measurement over the shapes tried, not a law — <c>Codeunit.Run</c> was the most
/// promising candidate for reaching the async shape from first-party AL and it does not, but
/// some other body might, and if one is ever found an AL reproducer becomes possible and
/// should be written. Until then a first-party AL test cannot reach the defect and would pass
/// with and without the fix, which <c>.claude/rules/tdd.md</c> calls noise.</para>
///
/// Same split, and the same reason, as <c>ObservingSubscriberMethodInfoTests</c> (#2932's
/// table-event-subscriber half of this defect) and <c>DispatchObserveAsyncResultTests</c>.
/// The end-to-end proof lives in CI's al-language corpus leg: with the four fixes this test
/// belongs to, the Base Application install pass runs to completion; remove any one of them
/// and the bundle reports EXEC-FAIL before a single test executes.
/// </summary>
public class InstallTriggerAsyncObservationTests
{
    // ---- stand-ins for an emitted Subtype=Install codeunit ---------------------------
    //
    // InvokeTrigger only ever touches the trigger MethodInfo and the instance it invokes it
    // on, so a plain class is a faithful stand-in: the ITreeObject ctor and the Codeunit*
    // naming matter to Scan(), which is not what this pins.

    public sealed class Installer
    {
        public int Steps;

        public void SyncOk() => Steps++;

        public void SyncThrows()
        {
            Steps++;
            throw new InvalidOperationException("SYNC-INSTALL-ERROR");
        }

        public async ValueTask AsyncOk()
        {
            Steps++;
            await Task.Yield();
            Steps++;
        }

        public async ValueTask AsyncThrows()
        {
            Steps++;
            await Task.Yield();
            throw new InvalidOperationException("ASYNC-INSTALL-ERROR");
        }

        // async ValueTask<T> is the only shape that reaches ObserveAsyncResult's reflection
        // arm — a boxed ValueTask<T> is neither a Task nor a ValueTask, so it matches no
        // case label. An install trigger is declared void in AL and BC does not emit this
        // shape for one today; it is covered so the seam cannot regress into a silent
        // fall-through if that ever changes.
        public async ValueTask<int> AsyncGenericThrows()
        {
            Steps++;
            await Task.Yield();
            throw new InvalidOperationException("ASYNC-GENERIC-INSTALL-ERROR");
        }
    }

    private static MethodInfo M(string name) =>
        typeof(Installer).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!;

    private static InstallTriggerRunner.InstallCodeunit Cu() =>
        new(typeof(Installer),
            typeof(Installer).GetConstructors()[0],
            PerCompany: null,
            PerDatabase: null);

    /// <summary>Run InvokeTrigger with stderr redirected, returning what it wrote.</summary>
    private static string InvokeCapturingStdErr(
        Installer target, string method, string triggerName, out Exception? thrown)
    {
        var original = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            thrown = Xunit.Record.Exception(
                () => InstallTriggerRunner.InvokeTrigger(Cu(), target, M(method), triggerName));
        }
        finally
        {
            Console.SetError(original);
        }
        return buffer.ToString();
    }

    // ---- the defect ------------------------------------------------------------------

    [Fact]
    public void AsyncInstallTriggerThatThrows_SurfacesTheErrorInsteadOfSwallowingIt()
    {
        var target = new Installer();

        // The pre-#2960 behaviour, spelled out so the RED is in the test rather than only in
        // the commit history: invoking the trigger and DISCARDING its result returns
        // normally. The error exists only on the returned ValueTask.
        var discarded = (ValueTask)M(nameof(Installer.AsyncThrows)).Invoke(target, null)!;
        var onlyOnTheTask = Assert.Throws<InvalidOperationException>(
            () => discarded.AsTask().GetAwaiter().GetResult());
        Assert.Equal("ASYNC-INSTALL-ERROR", onlyOnTheTask.Message);

        // Through InvokeTrigger the same call raises, with the ORIGINAL exception type and
        // message — not a TargetInvocationException, and not an AggregateException.
        var target2 = new Installer();
        var thrown = Assert.Throws<InvalidOperationException>(
            () => InstallTriggerRunner.InvokeTrigger(
                Cu(), target2, M(nameof(Installer.AsyncThrows)), "OnInstallAppPerCompany"));
        Assert.Equal("ASYNC-INSTALL-ERROR", thrown.Message);
    }

    [Fact]
    public void AsyncInstallTriggerThatThrows_IsReportedLoudlyNamingTheCodeunitAndTrigger()
    {
        var stderr = InvokeCapturingStdErr(
            new Installer(), nameof(Installer.AsyncThrows), "OnInstallAppPerCompany", out var thrown);

        Assert.IsType<InvalidOperationException>(thrown);
        // The async case must reach the SAME diagnostic the synchronous case always had.
        // Before the fix it reached no diagnostic at all, because the state machine never
        // let the exception out of MethodInfo.Invoke.
        Assert.Contains("[install-trigger]", stderr);
        Assert.Contains("Installer.OnInstallAppPerCompany", stderr);
        Assert.Contains("InvalidOperationException", stderr);
        Assert.Contains("ASYNC-INSTALL-ERROR", stderr);
    }

    [Fact]
    public void AsyncGenericInstallTriggerThatThrows_SurfacesTheError()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => InstallTriggerRunner.InvokeTrigger(
                Cu(), new Installer(), M(nameof(Installer.AsyncGenericThrows)), "OnInstallAppPerDatabase"));
        Assert.Equal("ASYNC-GENERIC-INSTALL-ERROR", thrown.Message);
    }

    // ---- what must NOT change --------------------------------------------------------

    [Fact]
    public void SyncInstallTriggerThatThrows_StillRaisesTheOriginalExceptionType()
    {
        // The pre-existing TargetInvocationException arm: MethodInfo.Invoke wraps a
        // synchronously-thrown exception, and InvokeTrigger has always unwrapped it so a
        // RunnerOutOfScopeException stays itself. Folding the two arms together must not
        // start leaking the wrapper.
        var stderr = InvokeCapturingStdErr(
            new Installer(), nameof(Installer.SyncThrows), "OnInstallAppPerCompany", out var thrown);

        var ex = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Equal("SYNC-INSTALL-ERROR", ex.Message);
        Assert.Contains("Installer.OnInstallAppPerCompany", stderr);
        Assert.Contains("SYNC-INSTALL-ERROR", stderr);
    }

    [Fact]
    public void AsyncInstallTriggerThatSucceeds_RunsItsWholeBodyAndDoesNotRaise()
    {
        var target = new Installer();
        InstallTriggerRunner.InvokeTrigger(
            Cu(), target, M(nameof(Installer.AsyncOk)), "OnInstallAppPerCompany");

        // Both sides of the suspension point ran. A helper that returned as soon as the
        // ValueTask was handed to it would leave Steps at 1 whenever the continuation had
        // not yet been scheduled, so this is the assertion that the result is genuinely
        // awaited rather than merely touched.
        Assert.Equal(2, target.Steps);
    }

    [Fact]
    public void SyncInstallTriggerThatSucceeds_RunsAndDoesNotRaise()
    {
        var target = new Installer();
        InstallTriggerRunner.InvokeTrigger(
            Cu(), target, M(nameof(Installer.SyncOk)), "OnInstallAppPerDatabase");
        Assert.Equal(1, target.Steps);
    }

    [Fact]
    public void AbsentTrigger_IsANoOp()
    {
        // A codeunit declaring only one of the two lifecycle triggers passes null for the
        // other; that must stay a no-op rather than becoming a null-reference failure now
        // that the call site also inspects the returned value.
        InstallTriggerRunner.InvokeTrigger(Cu(), new Installer(), null, "OnInstallAppPerCompany");
    }
}
