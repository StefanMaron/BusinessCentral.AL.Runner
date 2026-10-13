// DapSourceBreakpointsTests - #3526: DAP's setBreakpoints is the COMPLETE set for one source.
// Scope-level state (AlDapSession) cannot say which file armed what, so DapSourceBreakpoints
// remembers it. These facts drive that registry against the real AlDapSession lock, with
// distinct System types standing in for the AL scope types a run would resolve.

using System.Reflection;
using System.Threading;
using Xunit;

namespace AlRunner.Tests;

public class DapSourceBreakpointsTests
{
    // Distinct per class so these never collide with another test touching AlDapSession's
    // statics; no test here calls AlDapSession.Reset.
    private static readonly MemberInfo ScopeA = typeof(DapSourceBreakpointsTests);
    private static readonly MemberInfo ScopeB = typeof(AlRunner.Infrastructure.DapSourceBreakpoints);
    private static readonly MemberInfo ScopeC = typeof(AlRunner.Infrastructure.DapBreakpointRequest);

    private static (MemberInfo, int) Bp(MemberInfo scope, int stmt) => (scope, stmt);

    private static bool Armed(MemberInfo scope, int stmt) =>
        AlRunner.Infrastructure.AlDapSession.IsBreakpointArmed(scope, stmt);

    [Fact]
    public void ReplacingWithAnEmptySet_DisarmsWhatTheSourceHadArmed()
    {
        var reg = new AlRunner.Infrastructure.DapSourceBreakpoints();
        reg.Replace("/ws/empty.al", new[] { Bp(ScopeA, 11), Bp(ScopeA, 12) });
        Assert.True(Armed(ScopeA, 11));
        Assert.True(Armed(ScopeA, 12));

        reg.Replace("/ws/empty.al", Array.Empty<(MemberInfo, int)>());

        Assert.False(Armed(ScopeA, 11));
        Assert.False(Armed(ScopeA, 12));
        Assert.Empty(reg.ScopesOf("/ws/empty.al"));
    }

    [Fact]
    public void ReplacingWithAScopeElsewhere_LeavesOnlyTheNewLocationArmed()
    {
        var reg = new AlRunner.Infrastructure.DapSourceBreakpoints();
        reg.Replace("/ws/move.al", new[] { Bp(ScopeA, 21) });

        reg.Replace("/ws/move.al", new[] { Bp(ScopeB, 22) });

        Assert.False(Armed(ScopeA, 21));
        Assert.True(Armed(ScopeB, 22));
        Assert.Equal(new[] { ScopeB }, reg.ScopesOf("/ws/move.al"));
    }

    [Fact]
    public void ReplacingAStatementInTheSameScope_LeavesOnlyTheNewStatementArmed()
    {
        var reg = new AlRunner.Infrastructure.DapSourceBreakpoints();
        reg.Replace("/ws/same-scope.al", new[] { Bp(ScopeA, 31), Bp(ScopeA, 32) });

        reg.Replace("/ws/same-scope.al", new[] { Bp(ScopeA, 33) });

        Assert.False(Armed(ScopeA, 31));
        Assert.False(Armed(ScopeA, 32));
        Assert.True(Armed(ScopeA, 33));
    }

    [Fact]
    public void ReplacingOneSource_LeavesAnotherSourcesBreakpointsUntouched()
    {
        var reg = new AlRunner.Infrastructure.DapSourceBreakpoints();
        reg.Replace("/ws/one.al", new[] { Bp(ScopeA, 41) });
        reg.Replace("/ws/two.al", new[] { Bp(ScopeB, 42) });

        reg.Replace("/ws/one.al", Array.Empty<(MemberInfo, int)>());
        Assert.False(Armed(ScopeA, 41));
        Assert.True(Armed(ScopeB, 42), "replacing /ws/one.al disarmed /ws/two.al's breakpoint");

        reg.Replace("/ws/one.al", new[] { Bp(ScopeC, 43) });
        Assert.True(Armed(ScopeC, 43));
        Assert.True(Armed(ScopeB, 42));
        Assert.Equal(new[] { ScopeB }, reg.ScopesOf("/ws/two.al"));
    }

    // The reader must have done this many reads WHILE replacements were running. A fixed
    // writer iteration count measured nothing when the reader was not scheduled before the
    // writer finished (#5554); this makes the precondition the loop's exit condition.
    private const int MinReadsDuringReplacements = 20_000;
    private static readonly TimeSpan SchedulingBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A breakpoint the replacement KEEPS must stay armed throughout it. The AL thread reads
    /// through the same lock, so a gap between "clear the old set" and "arm the new one" is a
    /// stop a running program can slip past - with the client never told the breakpoint was
    /// ever missing. RED while Replace was Clear-then-Set as two lock acquisitions.
    ///
    /// The reader is a dedicated thread (not the pool, which a loaded leg starves), the writer
    /// does not start until the reader has completed a read, and the writer keeps replacing
    /// until the reader has done <see cref="MinReadsDuringReplacements"/> reads during it.
    /// If the reader can never be scheduled the test fails saying so, not as "measured nothing".
    /// </summary>
    [Fact]
    public void AKeptBreakpoint_IsNeverObservedDisarmedDuringAReplacement()
    {
        var reg = new AlRunner.Infrastructure.DapSourceBreakpoints();
        reg.Replace("/ws/race.al", new[] { Bp(ScopeA, 51) });

        var stop = false;
        long misses = 0, reads = 0;
        using var firstRead = new ManualResetEventSlim(false);
        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                if (!Armed(ScopeA, 51)) Interlocked.Increment(ref misses);
                Interlocked.Increment(ref reads);
                if (!firstRead.IsSet) firstRead.Set();
            }
        }) { IsBackground = true, Name = "dap-breakpoint-reader" };
        reader.Start();

        long replacements = 0, readsAtStart, readsDuring;
        try
        {
            Assert.True(firstRead.Wait(SchedulingBudget),
                $"the reader thread completed no read within {SchedulingBudget.TotalSeconds:0}s of starting; "
                + "the host could not schedule it, so atomicity was not measured (#5554)");

            readsAtStart = Interlocked.Read(ref reads);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Alternates two sets that BOTH contain (A,51), so a correct replacement never has
            // a moment where it is absent.
            while (Interlocked.Read(ref reads) - readsAtStart < MinReadsDuringReplacements)
            {
                Assert.True(clock.Elapsed < SchedulingBudget,
                    $"only {Interlocked.Read(ref reads) - readsAtStart} reader reads happened during "
                    + $"{replacements} replacements in {SchedulingBudget.TotalSeconds:0}s "
                    + $"(need {MinReadsDuringReplacements}); the reader was starved, so atomicity was not measured (#5554)");
                reg.Replace("/ws/race.al", (replacements++ & 1) == 0
                    ? new[] { Bp(ScopeA, 51), Bp(ScopeA, 52) }
                    : new[] { Bp(ScopeA, 51) });
            }
            readsDuring = Interlocked.Read(ref reads) - readsAtStart;
        }
        finally
        {
            Volatile.Write(ref stop, true);
            reader.Join();
        }

        Assert.True(replacements > 0 && readsDuring >= MinReadsDuringReplacements);
        Assert.True(Interlocked.Read(ref misses) == 0,
            $"a kept breakpoint was observed disarmed {misses} time(s) in {reads} reads "
            + $"({readsDuring} of them during {replacements} replacements)");
    }
}
