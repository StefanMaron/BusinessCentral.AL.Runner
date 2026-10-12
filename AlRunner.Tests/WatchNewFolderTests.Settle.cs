// The half of WatchNewFolderTests that drives WatchSource.SettleChange: a folder event is a hint, and the
// .al snapshot decides whether it starts a cycle (#5526).
using Xunit;

namespace AlRunner.Tests;

public sealed partial class WatchNewFolderTests
{
    // A folder event is a hint, and the snapshot is the judge: nothing new to compile, nothing to run.
    [Fact]
    public async Task FoldersHoldingNoAlSource_AreSignalledButStartNoCycle()
    {
        var root = NewDir();
        WriteApp(Path.Combine(root, "solo"));
        var armed = AlRunner.WatchSource.ArmSourceWatch(new List<string> { root });
        Assert.NotNull(armed);
        var (signal, watchers, activity) = armed!.Value;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bin", "debug"));
            File.WriteAllText(Path.Combine(root, "bin", "debug", "x.dll"), "x");
            Assert.True(await Task.Run(() => signal.Wait(TimeSpan.FromSeconds(10))),
                "creating a folder raised no signal at all: the hint that makes a moved-in app visible is gone.");

            Assert.False(AlRunner.WatchSource.SettleChange(signal, activity),
                "a folder holding no .al source started a cycle.");
            Assert.False(signal.IsSet, "an unconfirmed hint must hand the signal back, or the wait returns at once forever.");

            // Control, on the same session: a real edit after a refused hint is not swallowed with it.
            File.AppendAllText(Path.Combine(root, "solo", "T.Codeunit.al"), "\n");
            Assert.True(await Task.Run(() => signal.Wait(TimeSpan.FromSeconds(10))));
            Assert.True(AlRunner.WatchSource.SettleChange(signal, activity), "an .al edit after a refused hint started no cycle.");
        }
        finally
        {
            AlRunner.WatchSource.DisposeWatch(signal, watchers);
        }
    }

    // A folder moved in AFTER the baseline was taken is the difference the hint is checked against, and
    // an .al file inside it is what makes it one.
    [Fact]
    public async Task AFolderThatGainsAlSourceAfterAnEarlierRefusal_StartsACycle()
    {
        var root = NewDir();
        WriteApp(Path.Combine(root, "solo"));
        var armed = AlRunner.WatchSource.ArmSourceWatch(new List<string> { root });
        var (signal, watchers, activity) = armed!.Value;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "later"));
            Assert.True(await Task.Run(() => signal.Wait(TimeSpan.FromSeconds(10))));
            Assert.False(AlRunner.WatchSource.SettleChange(signal, activity));

            var stage = Path.Combine(NewDir(), "second");
            WriteApp(stage);
            Directory.Move(stage, Path.Combine(root, "second"));
            Assert.True(await Task.Run(() => signal.Wait(TimeSpan.FromSeconds(10))));
            Assert.True(AlRunner.WatchSource.SettleChange(signal, activity),
                "an app folder moved in after an earlier refused hint started no cycle.");
        }
        finally
        {
            AlRunner.WatchSource.DisposeWatch(signal, watchers);
        }
    }

    [Fact]
    public async Task GitMetadataAndPlainFiles_RaiseNoSignal()
    {
        var root = NewDir();
        WriteApp(Path.Combine(root, "solo"));
        var armed = AlRunner.WatchSource.ArmSourceWatch(new List<string> { root });
        var (signal, watchers, _) = armed!.Value;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git", "objects", "ab"));
            File.WriteAllText(Path.Combine(root, ".git", "objects", "ab", "c"), "x");
            File.WriteAllText(Path.Combine(root, "solo", "notes.txt"), "x");
            // The negative has to outwait the watcher's delivery: if either were wrongly heard, it would
            // have arrived well inside this window (the folder tests above get theirs within milliseconds).
            await Task.Delay(1500);
            Assert.False(signal.IsSet, "git's own folders or a plain file raised a signal, so every commit would wake the watch.");
        }
        finally
        {
            AlRunner.WatchSource.DisposeWatch(signal, watchers);
        }
    }

    [Fact]
    public void SettleChange_WithNoSnapshotToAsk_RunsTheCycleRatherThanMissingOne()
    {
        var activity = new AlRunner.WatchSource.WatchActivity();
        var signal = new System.Threading.ManualResetEventSlim(true);
        try
        {
            activity.Touch();
            Assert.True(AlRunner.WatchSource.SettleChange(signal, activity, quietMs: 1),
                "with no baseline there is nothing to prove the tree unchanged; the answer must not be 'nothing changed'.");
        }
        finally { signal.Dispose(); }
    }

    // The snapshot only judges FOLDER hints. A file event the snapshot cannot see (a rewrite that keeps both
    // length and timestamp) has always started a cycle and must still.
    [Fact]
    public async Task AnAlEventTheSnapshotCannotSee_StillStartsACycle()
    {
        var root = NewDir();
        var file = Path.Combine(root, "T.Codeunit.al");
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(file, stamp);
        var armed = AlRunner.WatchSource.ArmSourceWatch(new List<string> { root });
        var (signal, watchers, activity) = armed!.Value;
        try
        {
            File.WriteAllText(file, "y");
            File.SetLastWriteTimeUtc(file, stamp);
            Assert.True(await Task.Run(() => signal.Wait(TimeSpan.FromSeconds(10))));
            Assert.True(AlRunner.WatchSource.SettleChange(signal, activity),
                "an .al event whose rewrite left the snapshot unchanged was dropped: the snapshot must not veto file events.");
        }
        finally
        {
            AlRunner.WatchSource.DisposeWatch(signal, watchers);
        }
    }
}
