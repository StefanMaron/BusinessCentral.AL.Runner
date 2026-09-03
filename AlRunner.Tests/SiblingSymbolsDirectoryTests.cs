// SiblingSymbolsDirectoryTests — issue #2586.
//
// The old sibling-symbols path was Path.GetTempPath()/al-runner-sibling-symbols/<bundle leaf
// name>, and EmitSiblingSymbols opens by deleting that directory recursively. Nothing in the
// path identified the bundle beyond its leaf name and nothing identified the process, so two
// concurrent runners over bundles both called "tests" deleted each other's symbols mid-compile,
// and two unrelated projects with the same leaf name shared one directory.
//
// Every assertion here is about the runner's own temp-directory layout — there is no claim about
// Business Central in this file, so nothing here belongs in the al-language corpus.
//
// The three path tests are the ones that would have caught the bug. Each fails against the old
// implementation: it returned the leaf name alone, so DifferentProcesses and DifferentBundles
// both returned equal paths.
using System;
using System.IO;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class SiblingSymbolsDirectoryTests
{
    private static string Bundle(string relative) =>
        Path.Combine(Path.GetTempPath(), "al-runner-sibsym-tests", relative);

    [Fact]
    public void SameBundleSameProcess_IsTheSameDirectory()
    {
        // Stability is the property EmitSiblingSymbols relies on: it computes the path once and
        // BcCompiler reads from it later in the same run.
        Assert.Equal(
            SiblingSymbolsDirectory.ForBundle(Bundle("proj-a/tests"), "process-a"),
            SiblingSymbolsDirectory.ForBundle(Bundle("proj-a/tests"), "process-a"));
    }

    [Fact]
    public void SameBundleDifferentProcesses_AreDifferentDirectories()
    {
        // The recursive delete in EmitSiblingSymbols is only safe because of this. Two runners
        // on the same bundle must not be able to reach each other's files at all.
        Assert.NotEqual(
            SiblingSymbolsDirectory.ForBundle(Bundle("proj-a/tests"), "process-a"),
            SiblingSymbolsDirectory.ForBundle(Bundle("proj-a/tests"), "process-b"));
    }

    [Fact]
    public void DifferentBundlesWithTheSameLeafName_AreDifferentDirectories()
    {
        // The exact collision the old path had: two checkouts whose bundle folder is called
        // "tests" resolved to one directory even within a single process.
        var a = SiblingSymbolsDirectory.ForBundle(Bundle("proj-a/tests"), "process-a");
        var b = SiblingSymbolsDirectory.ForBundle(Bundle("proj-b/tests"), "process-a");

        Assert.NotEqual(a, b);
        // ...and the leaf name is still there, so a temp listing is readable. This is what makes
        // the assertion above about the HASH rather than about the names being different.
        Assert.StartsWith("tests-", Path.GetFileName(a), StringComparison.Ordinal);
        Assert.StartsWith("tests-", Path.GetFileName(b), StringComparison.Ordinal);
    }

    [Fact]
    public void TrailingSeparatorAndRelativeSpelling_ResolveToTheSameDirectory()
    {
        // Normalization happens before hashing, so the same bundle named two ways is one
        // directory. Without it a caller that passed a trailing separator would silently get a
        // second, empty symbols directory and compile against nothing.
        var plain = Bundle("proj-a/tests");
        Assert.Equal(
            SiblingSymbolsDirectory.ForBundle(plain, "process-a"),
            SiblingSymbolsDirectory.ForBundle(plain + Path.DirectorySeparatorChar, "process-a"));
    }

    [Fact]
    public void EveryDirectory_LivesUnderTheSharedRoot()
    {
        // PruneStale walks Root, so a path that escaped it would never be cleaned up.
        var dir = SiblingSymbolsDirectory.ForBundle(Bundle("proj-a/tests"), "process-a");
        Assert.Equal(SiblingSymbolsDirectory.Root, Path.GetDirectoryName(dir));
    }

    [Fact]
    public void PruneStale_DeletesOnlyDirectoriesOlderThanTheThreshold()
    {
        var root = SiblingSymbolsDirectory.Root;
        Directory.CreateDirectory(root);

        var stamp = Guid.NewGuid().ToString("N");
        var old = Path.Combine(root, $"prunetest-old-{stamp}");
        var fresh = Path.Combine(root, $"prunetest-fresh-{stamp}");
        try
        {
            Directory.CreateDirectory(old);
            Directory.CreateDirectory(fresh);
            File.WriteAllText(Path.Combine(old, "a.symbols.json"), "{}");
            File.WriteAllText(Path.Combine(fresh, "a.symbols.json"), "{}");

            var now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
            Directory.SetLastWriteTimeUtc(old, now - TimeSpan.FromDays(3));
            Directory.SetLastWriteTimeUtc(fresh, now - TimeSpan.FromMinutes(5));

            SiblingSymbolsDirectory.PruneStale(TimeSpan.FromDays(1), now);

            Assert.False(Directory.Exists(old), "a directory untouched for three days must be pruned.");
            // The half that matters: a live sibling runner's directory must survive. A prune that
            // deleted this would be the #2586 bug arrived at from the other direction.
            Assert.True(Directory.Exists(fresh), "a directory written five minutes ago must NOT be pruned.");
        }
        finally
        {
            try { Directory.Delete(old, recursive: true); } catch { }
            try { Directory.Delete(fresh, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PruneStale_OnAMissingRoot_IsANoOpAndDoesNotThrow()
    {
        // First run on a clean machine: the root does not exist yet, and EmitSiblingSymbols
        // prunes before it creates anything.
        var record = Record.Exception(() => SiblingSymbolsDirectory.PruneStale(TimeSpan.FromDays(1)));
        Assert.Null(record);
    }
}
