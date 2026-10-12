// WatchNewFolderTests — issue #5526: `--watch` ran no cycle for an app folder that appeared (a copy, a
// checkout), was renamed or left the tree, because the watcher only listened for names ending in ".al".
// Measured on Linux: such a folder raises ONE event, for the folder, and none for the files inside it.
// Every folder here arrives by Directory.Move from a staging folder, which is how that happens
// deterministically (files written straight into a new folder race the watch added for it).
using Xunit;

namespace AlRunner.Tests;

[Collection(ConsoleFilterSerialCollection.Name)]
public sealed partial class WatchNewFolderTests
{
    private static string NewDir()
    {
        var dir = TestScratch.Dir("al-runner-watch-newfolder");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteApp(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "app.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "T.Codeunit.al"), "codeunit 50000 T { }");
    }

    /// <summary>Arms a watch over <paramref name="root"/>, runs <paramref name="act"/> from inside onArmed
    /// (the earliest moment an external change can race the watch), and says whether a cycle was started.</summary>
    private static async Task<bool> CycleStartedBy(string root, Action act)
    {
        var task = Task.Run(() => AlRunner.WatchSource.WaitForSourceChange(
            new List<string> { root }, onArmed: act));
        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        // A task that never finishes holds its watchers until the host exits; that only happens when this fails.
        return ReferenceEquals(task, winner) && await task;
    }

    [Fact]
    public async Task AppFolderMovedIntoTheTree_StartsACycle()
    {
        var root = NewDir();
        WriteApp(Path.Combine(root, "solo"));
        var stage = Path.Combine(NewDir(), "solo2");
        WriteApp(stage);

        Assert.True(await CycleStartedBy(root, () => Directory.Move(stage, Path.Combine(root, "solo2"))),
            "an app folder moved into the watched tree started no cycle (#5526).");
    }

    [Fact]
    public async Task FolderRenamedInsideTheTree_StartsACycle()
    {
        var root = NewDir();
        WriteApp(Path.Combine(root, "old"));

        Assert.True(await CycleStartedBy(root, () => Directory.Move(Path.Combine(root, "old"), Path.Combine(root, "new"))),
            "renaming a folder that holds .al source started no cycle (#5526).");
    }

    [Fact]
    public async Task AppFolderMovedOutOfTheTree_StartsACycle()
    {
        var root = NewDir();
        WriteApp(Path.Combine(root, "solo"));
        WriteApp(Path.Combine(root, "gone"));
        var outside = Path.Combine(NewDir(), "gone");

        Assert.True(await CycleStartedBy(root, () => Directory.Move(Path.Combine(root, "gone"), outside)),
            "an app folder moved out of the watched tree started no cycle (#5526).");
    }

    // Sibling of #5526 in the same handler: OnRenamed read only the NEW name, so an .al file renamed to
    // anything else (T.al -> T.bak) removed source from the compile without raising a cycle.
    [Fact]
    public async Task AlFileRenamedToAnotherExtension_StartsACycle()
    {
        var root = NewDir();
        WriteApp(Path.Combine(root, "solo"));

        Assert.True(await CycleStartedBy(root, () =>
                File.Move(Path.Combine(root, "solo", "T.Codeunit.al"), Path.Combine(root, "solo", "T.Codeunit.bak"))),
            "renaming an .al file away started no cycle.");
    }
}
