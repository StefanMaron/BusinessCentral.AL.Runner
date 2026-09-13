// TableRelationsMetadataVirtualTableProviderTests — issue #4088.
//
// A RUNNER-MECHANISM test, not a claim about BC. What Table Relations Metadata (2000000141)
// ANSWERS is pinned upstream (al-language corpus, codeunit 60982). What that corpus codeunit
// cannot see is the wiring: the handout to BC's own TableRelationDataProvider, and the Cecil
// replacement of its key-field walk, both bind by name at runtime, so a BC rename or a dropped
// registration is not a compile error — it is an empty table again, or a failure on first read.
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class TableRelationsMetadataVirtualTableProviderTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string DispatchSource => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Patches", "RecordPatches.DataAccessDispatch.cs"));

    private static string RewriteSource => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Infrastructure", "NclCecilRewrite.Runtime.cs"));

    private static Assembly Ncl => typeof(NCLMetaTable).Assembly;

    private const string HelperName = "TableRelationDataProvider_GetValuesWithinRangeForKeyField";

    [Fact]
    public void TheHandoutForTable2000000141_IsBcsVirtualDataAccess_NotATempStore()
    {
        var src = DispatchSource;
        var at = src.IndexOf("IsTableRelationsMetadataVirtualTable(table)", StringComparison.Ordinal);
        Assert.True(at >= 0, "The Table Relations Metadata branch is gone from GetDataAccessForTableCore.");

        var branch = src.Substring(at, Math.Min(300, src.Length - at));
        Assert.Contains("GetTableRelationsMetadataVirtualDataAccess", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("_mCreateTempDataAccess", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void BcsFactory_StillBuildsTableRelationDataProviderFor2000000141()
    {
        var provider = Ncl.GetType("Microsoft.Dynamics.Nav.Runtime.TableRelationDataProvider");
        Assert.True(provider != null, "TableRelationDataProvider is gone from Ncl.");

        var instance = RuntimeHelpers.GetUninitializedObject(provider!);
        var tableId = provider!.GetProperty("TableId", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(instance);
        Assert.Equal(2000000141, tableId);
        Assert.Equal(2000000141, RecordPatches.TableRelationsMetadataVirtualTableId);
    }

    [Fact]
    public void TheKeyFieldWalkIsReplaced_WithAHelperOfMatchingArity()
    {
        // ReplaceBodyWithHelper forwards `this` plus the five parameters; AssertHelperArityMatches
        // refuses a mismatch at rewrite time, but only once a run reaches the rewrite.
        Assert.Contains(HelperName, RewriteSource, StringComparison.Ordinal);

        var helper = typeof(RecordPatches).GetMethod(HelperName, BindingFlags.Public | BindingFlags.Static);
        Assert.True(helper != null, $"RecordPatches.{HelperName} is gone.");

        var target = Ncl.GetType("Microsoft.Dynamics.Nav.Runtime.TableRelationDataProvider")!
            .GetMethod("GetValuesWithinRangeForKeyField", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(target != null, "TableRelationDataProvider.GetValuesWithinRangeForKeyField is gone.");

        Assert.Equal(
            new[] { "NCLMetaField", "ReadOnlyRecordBuffer", "Range", "SortOrder", "FilterFieldDictionary" },
            target!.GetParameters().Select(p => p.ParameterType.Name).ToArray());
        Assert.Equal(target.GetParameters().Length + 1, helper!.GetParameters().Length);
    }

    [Fact]
    public void TheHelpersReflectionBind_ResolvesEveryBcMemberItDrives()
    {
        // Executes the bind itself against BC's real type. A renamed private iterator, a changed
        // TryGetMetaTableById overload or a moved NclMetadata property throws here, naming the
        // shape, instead of on the first Table Relations Metadata read of a run.
        var providerType = Ncl.GetType("Microsoft.Dynamics.Nav.Runtime.TableRelationDataProvider")!;
        var instance = RuntimeHelpers.GetUninitializedObject(providerType);

        var ensure = typeof(RecordPatches).GetMethod("EnsureTableRelationsReflection",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var ex = Record.Exception(() => ensure.Invoke(null, new[] { instance }));
        Assert.Null(ex);

        foreach (var field in new[] { "_trmGetFieldNos", "_trmGetRelationNos", "_trmGetConditionNos",
                     "_trmCreateEntry", "_trmGetBounds", "_trmTryGetMetaTable", "_trmNclMetadata", "_trmBufferType" })
        {
            var value = typeof(RecordPatches).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null);
            Assert.True(value != null, $"{field} was not bound.");
        }
    }
}
