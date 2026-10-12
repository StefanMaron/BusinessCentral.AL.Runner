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

    // The ".git" rule is whole path SEGMENTS below the watched root: .github, a.git, .gitx and .gitignore are
    // not git's folders, so a folder created under them is still a hint; a ".git" at any depth is.
    private static async Task<bool> FolderHintRaised(string root, string parent, string child, bool assertQuietThenControl)
    {
        Directory.CreateDirectory(Path.Combine(root, parent));
        var armed = AlRunner.WatchSource.ArmSourceWatch(new List<string> { root });
        var (signal, watchers, _) = armed!.Value;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, parent, child));
            if (!assertQuietThenControl)
                return await Task.Run(() => signal.Wait(TimeSpan.FromSeconds(10)));
            await Task.Delay(1000);
            var raised = signal.IsSet;
            // A quiet run proves nothing unless this watch can hear: the control must arrive.
            Directory.CreateDirectory(Path.Combine(root, "control"));
            Assert.True(await Task.Run(() => signal.Wait(TimeSpan.FromSeconds(10))), "the control folder raised no signal: the watch was deaf.");
            return raised;
        }
        finally
        {
            AlRunner.WatchSource.DisposeWatch(signal, watchers);
        }
    }

    [Theory]
    [InlineData(".github", "workflows")]
    [InlineData("a.git", "b")]
    [InlineData(".gitx", "b")]
    [InlineData(".gitignore", "b")]
    public async Task AFolderUnderANameThatOnlyLooksLikeGit_IsStillAHint(string parent, string child)
    {
        Assert.True(await FolderHintRaised(NewDir(), parent, child, assertQuietThenControl: false),
            $"a folder created under '{parent}' raised no signal: '.git' must match a whole path segment.");
    }

    [Theory]
    [InlineData(".git", "objects")]
    [InlineData("sub/.git", "objects")]
    [InlineData("sub", ".git")]
    public async Task AFolderInsideGitAtAnyDepth_IsNotAHint(string parent, string child)
    {
        Assert.False(await FolderHintRaised(NewDir(), parent, child, assertQuietThenControl: true),
            $"a folder created at '{parent}/{child}' raised a signal: git's own folders must be ignored.");
    }

    // The rule is judged BELOW the watched root: a root that itself sits inside a ".git" folder still hears its folders.
    [Fact]
    public async Task ARootInsideAGitFolder_StillHearsItsFolders()
    {
        var root = Path.Combine(NewDir(), ".git", "work");
        Directory.CreateDirectory(root);
        Assert.True(await FolderHintRaised(root, "solo", "later", assertQuietThenControl: false),
            "a folder created under a watched root that sits inside '.git' raised no signal: the rule must judge the path below the root.");
    }
}
