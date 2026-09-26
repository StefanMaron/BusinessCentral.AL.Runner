using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #4702: TddWatchTests' cycle 2 once reported no PASS because the runner ran that cycle against
/// the truncated, not-yet-written target file. These drive the real <see cref="WatchSource"/>
/// against <see cref="WatchEdit.Replace"/> with the writer stalled for several quiet windows —
/// the stall that, with an in-place write, releases the watch on an empty file.
/// </summary>
public sealed class WatchEditTests
{
    private static (string Dir, string File) NewBundle(string content)
    {
        var dir = TestScratch.Dir("al-runner-watchedit-tests");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), "{}");
        var file = Path.Combine(dir, "Target.Codeunit.al");
        File.WriteAllText(file, content);
        return (dir, file);
    }

    [Fact]
    public async Task Replace_WriterStallsLongerThanQuietWindow_WatchReleasesOnlyOnCompleteContent()
    {
        var (dir, file) = NewBundle("codeunit 50000 Old { }");
        const string edited = "codeunit 50000 New { procedure DoubleIt() begin end; }";
        var armed = new TaskCompletionSource();
        string? seenAtRelease = null;

        var watch = Task.Run(() =>
        {
            var changed = WatchSource.WaitForSourceChange(new List<string> { dir }, () => armed.SetResult());
            seenAtRelease = File.ReadAllText(file); // what the next cycle would compile
            return changed;
        });
        await armed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        WatchEdit.Replace(file, edited, beforeCommit: () => Thread.Sleep(WatchSource.QuietMs * 8));

        Assert.True(await watch.WaitAsync(TimeSpan.FromSeconds(30)), "the edit must wake the watch");
        Assert.Equal(edited, seenAtRelease);
    }

    [Fact]
    public void Replace_LeavesOnlyTheTargetBehind()
    {
        var (dir, file) = NewBundle("old");

        WatchEdit.Replace(file, "new");

        Assert.Equal("new", File.ReadAllText(file));
        Assert.Equal(
            new[] { "Target.Codeunit.al", "app.json" },
            Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
}
