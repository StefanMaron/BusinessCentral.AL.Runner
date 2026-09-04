// DepExtractionDirTests — dependency source extraction must not be shared across processes
// (issue #2696).
//
// The directory a dependency's AL source is extracted into used to be keyed only by app
// identity, under the machine-wide temp dir, and the extraction DELETES the .al files already
// there before rewriting them. Two runners resolving the same dependency at the same moment
// therefore raced: one deleted the files the other was compiling from.
//
// Measured under `--jobs 6` on Microsoft's BaseApp buckets: `Tests-TestLibraries` is a
// dependency of nearly every bucket, all six workers extracted it at once, and one worker died
// with `FileNotFoundException: .../BackupManagement.Codeunit.al` before starting ANY of its
// bundles — taking Tests-SCM (8,526 tests) and two more buckets out of a run that still printed
// a confident aggregate.
//
// This is not specific to --jobs. Two terminals, a --watch session beside a CLI run, or two CI
// jobs on one self-hosted runner share a TMPDIR too; --jobs only makes it near-certain.
//
// Sharing bought nothing: the real cache is the compiled DLL, which returns before this point
// on a hit, so this directory is scratch space that is fully rewritten on every miss.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class DepExtractionDirTests
{
    /// <summary>The defect: the path must not be the bare identity-only directory that every
    /// process on the machine computes identically.</summary>
    [Fact]
    public void Path_IsNotSharedAcrossProcesses()
    {
        var dir = DepExtractionDir.For("Microsoft", "Tests-TestLibraries", "28.1.49838.53910");

        Assert.Contains(Environment.ProcessId.ToString(), dir, StringComparison.Ordinal);
    }

    /// <summary>Stable within one process: a dependency resolved twice (ordinary under --watch)
    /// must reuse its directory rather than leaking a new one per call.</summary>
    [Fact]
    public void Path_IsStableWithinOneProcess()
    {
        var a = DepExtractionDir.For("Microsoft", "Tests-TestLibraries", "28.1.49838.53910");
        var b = DepExtractionDir.For("Microsoft", "Tests-TestLibraries", "28.1.49838.53910");

        Assert.Equal(a, b);
    }

    /// <summary>Different dependencies stay separate — sharing one directory between two apps
    /// would let one app's delete-then-rewrite wipe the other's sources.</summary>
    [Fact]
    public void Path_DiffersPerDependency()
    {
        var a = DepExtractionDir.For("Microsoft", "Tests-TestLibraries", "28.1.49838.53910");
        var b = DepExtractionDir.For("Microsoft", "Tests-ERM", "28.1.49838.53910");
        var c = DepExtractionDir.For("Microsoft", "Tests-TestLibraries", "28.2.0.0");

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    /// <summary>Names that are not safe as a path segment must not escape the root — a
    /// publisher or app name is attacker-influenced only in the sense that it comes from a
    /// third-party .app manifest, but a '/' or '..' in one should still land inside the root.</summary>
    [Fact]
    public void Path_StaysInsideTheRoot_ForAwkwardNames()
    {
        var dir = DepExtractionDir.For("Ev/il", "..\\..\\escape", "1.0.0.0");

        Assert.StartsWith(DepExtractionDir.Root, System.IO.Path.GetFullPath(dir), StringComparison.Ordinal);
    }

    /// <summary>The per-process root sits under the shared parent, so the old cleanup habits and
    /// anything that clears al-runner-deps still finds it.</summary>
    [Fact]
    public void Root_IsUnderTheSharedAlRunnerDepsParent()
        => Assert.Contains("al-runner-deps", DepExtractionDir.Root, StringComparison.Ordinal);

    /// <summary>The claim that actually matters, stated directly: two processes get different
    /// roots. Asserting only that the live path contains the current pid would still pass if the
    /// id were folded in somewhere that collided.</summary>
    [Fact]
    public void Root_DiffersBetweenProcesses()
    {
        Assert.NotEqual(DepExtractionDir.RootForProcess(1001), DepExtractionDir.RootForProcess(1002));
        Assert.Equal(DepExtractionDir.RootForProcess(1001), DepExtractionDir.RootForProcess(1001));
    }
}
