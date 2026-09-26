// XmlPortMetadataVirtualTableProviderTests — issue #4461, the XMLport Metadata table (2000000280).
//
// A RUNNER-MECHANISM test, not a claim about BC: what the table's columns answer is pinned by the
// al-language corpus. What a corpus test cannot see is the wiring, and like Query Metadata this
// table needs two pieces that bind by name at runtime — the handout to BC's own
// XmlPortDataProvider, and the ObjectType.XmlPort entry in the substituted object snapshot that
// provider walks. Sibling of QueryMetadataVirtualTableProviderTests (#4147).
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class XmlPortMetadataVirtualTableProviderTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static Assembly Ncl => typeof(NCLMetaTable).Assembly;

    [Fact]
    public void TheHandoutForTable2000000280_IsBcsVirtualDataAccess_NotATempStore()
    {
        var src = File.ReadAllText(
            Path.Combine(RepoRoot, "AlRunner", "Patches", "RecordPatches.DataAccessDispatch.cs"));
        var at = src.IndexOf("IsXmlPortMetadataVirtualTable(table)", StringComparison.Ordinal);
        Assert.True(at >= 0, "The XMLport Metadata branch is gone from GetDataAccessForTableCore.");

        var branch = src.Substring(at, Math.Min(300, src.Length - at));
        Assert.Contains("GetBcVirtualDataAccess", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("_mCreateTempDataAccess", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void BcsXmlPortDataProvider_ServesTable2000000280()
    {
        var provider = Ncl.GetType("Microsoft.Dynamics.Nav.Runtime.XmlPortDataProvider");
        Assert.True(provider != null, "XmlPortDataProvider is gone from Ncl.");

        var instance = RuntimeHelpers.GetUninitializedObject(provider!);
        var tableId = provider!.GetProperty("TableId", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(instance);
        Assert.Equal(2000000280, tableId);
        Assert.Equal(2000000280, RecordPatches.XmlPortMetadataVirtualTableId);
    }
}
