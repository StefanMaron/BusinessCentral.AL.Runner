// SafeDirectoryScanRunMemoTests — issue #2218: a recursive .alpackages / .deps-bin search walks
// each root once per run, and a new run (a --watch cycle, a --server request) walks afresh.
using Xunit;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

public sealed class SafeDirectoryScanRunMemoTests : IDisposable
{
    private readonly string _root;

    public SafeDirectoryScanRunMemoTests()
    {
        _root = TestScratch.Dir("al-runner-scan-memo");
        Directory.CreateDirectory(Path.Combine(_root, "app-a", ".alpackages"));
        Directory.CreateDirectory(Path.Combine(_root, "app-b", "src"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void WithinOneRun_RepeatedSearchesOfOneRoot_WalkItOnce()
    {
        using var memo = SafeDirectoryScan.BeginRunMemo();

        var first = SafeDirectoryScan.Directories(_root, ".alpackages");
        var second = SafeDirectoryScan.Directories(_root, ".alpackages");
        var third = SafeDirectoryScan.Directories(_root, ".alpackages", out var denied);

        Assert.Equal(3, memo.Calls);
        Assert.Equal(1, memo.Walks);
        var expected = new[] { Path.Combine(_root, "app-a", ".alpackages") };
        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
        Assert.Equal(expected, third);
        Assert.Empty(denied);
    }

    [Fact]
    public void WithinOneRun_EachRootAndPatternIsItsOwnEntry()
    {
        using var memo = SafeDirectoryScan.BeginRunMemo();

        SafeDirectoryScan.Directories(_root, ".alpackages");
        SafeDirectoryScan.Directories(Path.Combine(_root, "app-b"), ".alpackages");
        SafeDirectoryScan.Directories(_root, ".deps-bin");
        SafeDirectoryScan.Directories(_root, ".deps-bin");

        Assert.Equal(4, memo.Calls);
        Assert.Equal(3, memo.Walks);
        Assert.Empty(SafeDirectoryScan.Directories(Path.Combine(_root, "app-b"), ".alpackages"));
    }

    /// <summary>
    /// The --watch / --server shape: a package directory the user adds between two runs in one
    /// process is found by the second run, even though the first run searched the same root
    /// and cached an answer without it.
    /// </summary>
    [Fact]
    public void ADirectoryCreatedBetweenRuns_IsFoundByTheNextRun()
    {
        using (SafeDirectoryScan.BeginRunMemo())
            Assert.Empty(SafeDirectoryScan.Directories(Path.Combine(_root, "app-b"), ".alpackages"));

        var added = Path.Combine(_root, "app-b", ".alpackages");
        Directory.CreateDirectory(added);

        using var next = SafeDirectoryScan.BeginRunMemo();
        Assert.Equal(new[] { added }, SafeDirectoryScan.Directories(Path.Combine(_root, "app-b"), ".alpackages"));
        Assert.Equal(1, next.Walks);
    }

    [Fact]
    public void OutsideAnyRun_EverySearchWalks()
    {
        var appB = Path.Combine(_root, "app-b");
        Assert.Empty(SafeDirectoryScan.Directories(appB, ".alpackages"));

        var added = Path.Combine(appB, ".alpackages");
        Directory.CreateDirectory(added);

        Assert.Equal(new[] { added }, SafeDirectoryScan.Directories(appB, ".alpackages"));
    }

    [Fact]
    public void OnlyPackageDirectoryNamesAreMemoized()
    {
        using var memo = SafeDirectoryScan.BeginRunMemo();

        Assert.Empty(SafeDirectoryScan.Directories(_root, "Add-ins"));
        var added = Path.Combine(_root, "app-b", "Add-ins");
        Directory.CreateDirectory(added);

        Assert.Equal(new[] { added }, SafeDirectoryScan.Directories(_root, "Add-ins"));
        Assert.Equal(0, memo.Calls);
    }

    [Fact]
    public void AReturnedListIsTheCallersOwnCopy()
    {
        using var memo = SafeDirectoryScan.BeginRunMemo();

        var first = (List<string>)SafeDirectoryScan.Directories(_root, ".alpackages");
        first.Clear();

        Assert.Single(SafeDirectoryScan.Directories(_root, ".alpackages"));
    }
}
