using System.Text.RegularExpressions;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Holds docs/bc-symbol-cache-versions.md to BcAppSymbolCache.CacheVersion (#3918). The history
/// moved out of the source file, so nothing else fails when a bump lands without its row, or a
/// row is lost in an edit — and the rows are what make a CacheVersion collision diagnosable.
/// </summary>
public sealed class BcAppSymbolCacheVersionHistoryTests
{
    private const string DocRelativePath = "docs/bc-symbol-cache-versions.md";

    // The first recorded version; v1 and v2 predate the record.
    private const int FirstRecordedVersion = 3;

    private static readonly Regex EntryLine =
        new(@"^- \*\*v(\d+)\*\*:", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    [Fact]
    public void NewestHistoryEntry_IsTheLiveCacheVersion()
    {
        var versions = ReadHistoryVersions();

        Assert.Equal(BcAppSymbolCache.CacheVersionForTests, versions.Max());
    }

    [Fact]
    public void History_HasExactlyOneEntryPerVersion_FromTheFirstRecordedUpToTheLiveOne()
    {
        var versions = ReadHistoryVersions();

        var expected = Enumerable.Range(FirstRecordedVersion,
            BcAppSymbolCache.CacheVersionForTests - FirstRecordedVersion + 1).ToList();
        Assert.Equal(expected, versions);
    }

    private static List<int> ReadHistoryVersions()
    {
        var path = Path.Combine(RepoRoot(), DocRelativePath);
        Assert.True(File.Exists(path), $"{DocRelativePath} is missing at {path}");

        var versions = EntryLine.Matches(File.ReadAllText(path))
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();
        // A pattern that drifted to match nothing must fail here, not pass on an empty list.
        Assert.NotEmpty(versions);
        return versions;
    }

    // The test binary sits at <repo>/AlRunner.Tests/bin/<config>/<tfm>/.
    private static string RepoRoot() => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
