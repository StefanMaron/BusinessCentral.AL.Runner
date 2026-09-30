using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #4826: under --isolation test every later test of a codeunit is restored to the post-OnRun
/// snapshot, not the install baseline. A --test-data table lazily loaded after that snapshot was
/// taken must be appended to it as well, or the next test's restore drops the table and, because
/// storage presence is the "already loaded" answer, it is never reloaded
/// (InstallBaselineAppendConcurrencyTests has the #2262/#2914 background).
/// </summary>
[Collection(InstallBaselineStaticsCollection.Name)]
public sealed class PostOnRunBaselineAppendTests : IDisposable
{
    private const int Table = 61032;
    private readonly List<RecordPatches.BaselineSource>? _savedInstallBaseline;

    public PostOnRunBaselineAppendTests()
    {
        _savedInstallBaseline = RecordPatches.InstallBaselineForTests;
        RecordPatches.InstallBaselineForTests = null;
        RecordPatches.SetActiveDepCompanyBaseline(null);
        RecordPatches.SetActivePostOnRunBaseline(null);
    }

    public void Dispose()
    {
        RecordPatches.InstallBaselineForTests = _savedInstallBaseline;
        RecordPatches.SetActiveDepCompanyBaseline(null);
        RecordPatches.SetActivePostOnRunBaseline(null);
    }

    private static RecordPatches.InstallBaselineSnapshot EmptySnapshot()
        => new(new List<RecordPatches.BaselineSource>(), null, null);

    private static NavValue[][] Rows(string value) => new[] { new NavValue[] { new NavText(0, value) } };

    [Fact]
    public void LazyLoadedTable_IsAppendedToTheActivePostOnRunSnapshot()
    {
        var source = new object();
        var postOnRun = EmptySnapshot();
        RecordPatches.SetActivePostOnRunBaseline(postOnRun);

        RecordPatches.AppendBaselineTable(source, Table, new object(), Rows("LAZY"));

        var only = Assert.Single(postOnRun.Sources);
        Assert.Same(source, only.Source);
        var table = Assert.Single(only.Tables);
        Assert.Equal(Table, table.TableId);
        Assert.Equal("LAZY", table.Rows[0][0].ToString());
    }

    [Fact]
    public void ClearedPostOnRunSnapshot_ReceivesNothing()
    {
        var postOnRun = EmptySnapshot();
        RecordPatches.SetActivePostOnRunBaseline(postOnRun);
        RecordPatches.SetActivePostOnRunBaseline(null);

        RecordPatches.AppendBaselineTable(new object(), Table, new object(), Rows("LAZY"));

        Assert.Empty(postOnRun.Sources);
    }
}
