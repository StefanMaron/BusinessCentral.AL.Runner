// CompileFileReadsFingerprintTests — #5368: CompileFileReads.Fingerprint is the one function both the
// change model's baseline (#5087) and the AL-output cache's inputs record (#5368) call, so what it
// hashes for a LISTING decides both. A listing is its names, relative to the app root: not how many
// there are (a rename keeping the count must move it) and not where the tree sits (the same tree
// under another directory must not).
using Xunit;

namespace AlRunner.Tests;

public sealed class CompileFileReadsFingerprintTests : IDisposable
{
    private readonly string _root = TestScratch.Dir("al-runner-fingerprint");

    public CompileFileReadsFingerprintTests() => Directory.CreateDirectory(Path.Combine(_root, "Translations"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string Listing = "list:|Translations/*.xlf";

    private static string Of(string root) => CompileFileReads.Fingerprint(root, new[] { Listing })[Listing];

    [Fact]
    public void ARenameThatKeepsTheCount_MovesTheListingsFingerprint()
    {
        File.WriteAllText(Path.Combine(_root, "Translations", "a.da-DK.xlf"), "x");
        var before = Of(_root);

        File.Move(Path.Combine(_root, "Translations", "a.da-DK.xlf"), Path.Combine(_root, "Translations", "a.de-DE.xlf"));

        Assert.NotEqual(before, Of(_root));
    }

    [Fact]
    public void AnAddedAndARemovedFile_MoveIt_AndAnEmptyListingDiffersFromOne()
    {
        var empty = Of(_root);
        File.WriteAllText(Path.Combine(_root, "Translations", "a.da-DK.xlf"), "x");
        var one = Of(_root);
        File.Delete(Path.Combine(_root, "Translations", "a.da-DK.xlf"));

        Assert.NotEqual(empty, one);
        Assert.Equal(empty, Of(_root));
    }

    [Fact]
    public void ListingTheSameTreeUnderAnotherDirectory_IsTheSameFingerprint()
    {
        File.WriteAllText(Path.Combine(_root, "Translations", "a.da-DK.xlf"), "x");
        File.WriteAllText(Path.Combine(_root, "Translations", "b.de-DE.xlf"), "x");
        var other = TestScratch.Dir("al-runner-fingerprint-other");
        try
        {
            Directory.CreateDirectory(Path.Combine(other, "Translations"));
            File.WriteAllText(Path.Combine(other, "Translations", "a.da-DK.xlf"), "different content, same names");
            File.WriteAllText(Path.Combine(other, "Translations", "b.de-DE.xlf"), "x");

            Assert.Equal(Of(_root), Of(other));
        }
        finally
        {
            try { Directory.Delete(other, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
