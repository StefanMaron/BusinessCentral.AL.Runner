// WatchSourceTests — deterministic proof for #1822's arm-before-announce contract.
//
// WatchTests.Watch_PicksUpEdit_InProcess_OnNextCycle (spawns the real runner, polls the
// console for the "waiting for AL source changes" marker on a 200ms loop, then edits the
// fixture) is a flaky reproduction of the race, not a proof: whether it goes red depends
// on OS scheduling luck between Console.Out.Flush() and EnableRaisingEvents = true. These
// tests instead encode the ordering CONTRACT directly — onArmed is invoked by
// WatchSource itself, only once every FileSystemWatcher is already live — so an edit made
// from *inside* onArmed is the earliest possible moment a real editor could race the
// watcher, and it must always be seen. No polling, no console output, no child process.
using Xunit;

namespace AlRunner.Tests;

public sealed class WatchSourceTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-watchsource-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Reach the internal WatchSource type via reflection-free direct reference: the type
    // is `internal` in namespace AlRunner, and AlRunner.csproj declares
    // InternalsVisibleTo("AlRunner.Tests"), so a plain `AlRunner.WatchSource` reference
    // compiles directly — no reflection needed. (Comment kept because the AL-runner
    // pattern elsewhere in this suite reaches internals via reflection; this one doesn't
    // need to.)

    [Fact]
    public async Task WaitForSourceChange_EditMadeFromInsideOnArmed_IsSeen()
    {
        // This is the exact #1822 race, made deterministic: onArmed is the earliest
        // possible instant an external editor could act on "now watching". If the
        // implementation ever regresses to announcing before arming, an edit made here
        // races (and can lose to) EnableRaisingEvents = true and the wait below times out.
        var dir = NewTempDir();
        var file = Path.Combine(dir, "Some.Table.al");
        File.WriteAllText(file, "table 60000 Some { }");
        bool onArmedRan = false;

        var task = Task.Run(() => AlRunner.WatchSource.WaitForSourceChange(
            new List<string> { dir },
            onArmed: () =>
            {
                onArmedRan = true;
                File.WriteAllText(file, "table 60000 Some { fields { field(1; A; Integer) { } } }");
            }));

        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(onArmedRan, "onArmed must run on the success path.");
        Assert.True(ReferenceEquals(task, winner),
            "WaitForSourceChange did not observe the edit written from inside onArmed within " +
            "15s — this means onArmed ran before the watchers were live (the #1822 regression). " +
            "(Not awaiting the hung task further to avoid blocking the test run.)");
        Assert.True(await task, "WaitForSourceChange must report a change was detected.");
    }

    [Fact]
    public void ArmSourceWatch_NoExistingSourceDir_ReturnsNull_AndDoesNotInvokeOnArmed()
    {
        // A bundle path that resolves to nothing on disk: no app.json to climb to, and the
        // bundle directory itself does not exist either.
        var missing = Path.Combine(Path.GetTempPath(), "al-runner-watchsource-tests", "does-not-exist-" + Guid.NewGuid().ToString("N"));
        bool onArmedRan = false;

        var savedErr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        (System.Threading.ManualResetEventSlim Signal, List<FileSystemWatcher> Watchers)? armed;
        try
        {
            armed = AlRunner.WatchSource.ArmSourceWatch(
                new List<string> { missing },
                onArmed: () => onArmedRan = true);
        }
        finally
        {
            Console.SetError(savedErr);
        }

        Assert.Null(armed);
        Assert.False(onArmedRan, "onArmed must NOT run on the nothing-to-watch path.");
        Assert.Contains("[watch] no source directories to watch.", captured.ToString());
    }

    [Fact]
    public void ArmSourceWatch_ExistingSourceDir_ReturnsWatchers_AndRunsOnArmedExactlyOnce()
    {
        var dir = NewTempDir();
        int onArmedCount = 0;

        var armed = AlRunner.WatchSource.ArmSourceWatch(
            new List<string> { dir },
            onArmed: () => onArmedCount++);

        try
        {
            Assert.NotNull(armed);
            var (signal, watchers) = armed!.Value;
            Assert.NotEmpty(watchers);
            Assert.All(watchers, w => Assert.True(w.EnableRaisingEvents));
            Assert.Equal(1, onArmedCount);
            Assert.False(signal.IsSet);
        }
        finally
        {
            if (armed != null)
                foreach (var w in armed.Value.Watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
            armed?.Signal.Dispose();
        }
    }
}
