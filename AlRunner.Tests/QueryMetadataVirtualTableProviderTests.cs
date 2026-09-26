// QueryMetadataVirtualTableProviderTests — issue #4147, the Query Metadata table (2000000142).
//
// A RUNNER-MECHANISM test, not a claim about BC. What the table ANSWERS is pinned upstream
// (al-language corpus, codeunit 60913 — Caption falling back to Name, the API columns set on an
// API query and blank on a Normal one, and the declaring extension's app id rather than the
// empty GUID). What that corpus codeunit cannot see is the wiring, and this table needs TWO
// independent pieces of it that both bind BY NAME at runtime:
//
//   1. the handout of 2000000142 to BC's own QueryDataProvider, without which the table falls
//      through to the empty temp store and answers no rows, silently;
//   2. the snapshot substitution the provider's key-field-1 walk reads, without which the
//      provider is constructed and still finds nothing to iterate.
//
// Neither is a compile error when it breaks, and — measured while writing this — neither
// failure explains the other: the table was silent with (2) landed and (1) missing, which is
// what made the first diagnosis of #4147 look like a Cecil problem when the rewrite had been
// applying correctly all along.
//
// Sibling of KeyVirtualTableProviderTests (#4191), whose shape this copies.
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class QueryMetadataVirtualTableProviderTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string DispatchSource => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Patches", "RecordPatches.DataAccessDispatch.cs"));

    private static string RewriteSource => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Infrastructure", "NclCecilRewrite.Records.cs"));

    private static Assembly Ncl => typeof(NCLMetaTable).Assembly;

    private const string HelperName = "NCLMetadata_GetSnapshotOfAllObjects";
    private const string ProviderTypeName = "Microsoft.Dynamics.Nav.Runtime.QueryDataProvider";
    private const string MetadataTypeName = "Microsoft.Dynamics.Nav.Runtime.NCLMetadata";

    [Fact]
    public void TheHandoutForTable2000000142_IsBcsVirtualDataAccess_NotATempStore()
    {
        var src = DispatchSource;
        var at = src.IndexOf("IsQueryMetadataVirtualTable(table)", StringComparison.Ordinal);
        Assert.True(at >= 0, "The Query Metadata branch is gone from GetDataAccessForTableCore.");

        var branch = src.Substring(at, Math.Min(300, src.Length - at));
        Assert.Contains("GetQueryMetadataVirtualDataAccess", branch, StringComparison.Ordinal);
        // A temp store is what the table fell through to before #4147, and it answers no rows
        // for every table without failing — the silent shape this whole fix exists to remove.
        Assert.DoesNotContain("_mCreateTempDataAccess", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void BcsFactory_StillBuildsQueryDataProviderFor2000000142()
    {
        var provider = Ncl.GetType(ProviderTypeName);
        Assert.True(provider != null, "QueryDataProvider is gone from Ncl.");

        var instance = RuntimeHelpers.GetUninitializedObject(provider!);
        var tableId = provider!.GetProperty("TableId", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(instance);
        Assert.Equal(2000000142, tableId);
        Assert.Equal(2000000142, RecordPatches.QueryMetadataVirtualTableId);
    }

    [Fact]
    public void TheSnapshotIsReplaced_WithAHelperOfMatchingArity()
    {
        // ReplaceBodyWithHelper forwards `this` plus the one parameter. The rewrite targets
        // GetSnapshotOfAllObjects(int) — BC's callers all spell it GetSnapshotOfAllObjects()
        // because appGroupId has a C# default, so the ONE-parameter target is correct and a
        // reader checking it against a call site will think otherwise.
        Assert.Contains(HelperName, RewriteSource, StringComparison.Ordinal);

        var helper = typeof(RecordPatches).GetMethod(HelperName, BindingFlags.Public | BindingFlags.Static);
        Assert.True(helper != null, $"RecordPatches.{HelperName} is gone.");

        var target = Ncl.GetType(MetadataTypeName)!
            .GetMethod("GetSnapshotOfAllObjects", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(target != null, "NCLMetadata.GetSnapshotOfAllObjects is gone.");

        Assert.Equal(new[] { "Int32" }, target!.GetParameters().Select(p => p.ParameterType.Name).ToArray());
        Assert.Equal(target.GetParameters().Length + 1, helper!.GetParameters().Length);
    }

    [Fact]
    public void TryGetMetaQueryById_StillTakesFourParameters_NotThree()
    {
        // The defect this test exists for. The signature is
        //   TryGetMetaQueryById(int queryId, out NCLMetaQuery, bool requireCompiled, int appGroupId)
        // and the last parameter has a C# DEFAULT, so every decompiled call site shows THREE
        // arguments. A bind written from a call site (`GetParameters().Length == 3`) resolves
        // NOTHING, and the helper then refuses with a shape-gap naming members that are all
        // present — a null bind reported as "BC changed", when BC had not changed.
        var m = Ncl.GetType(MetadataTypeName)!.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(x => x.Name == "TryGetMetaQueryById");
        Assert.True(m != null, "NCLMetadata.TryGetMetaQueryById is gone.");

        Assert.Equal(
            new[] { "Int32", "NCLMetaQuery&", "Boolean", "Int32" },
            m!.GetParameters().Select(p => p.ParameterType.Name).ToArray());
        // MethodInfo.Invoke does not apply C# defaults, so the helper must pass all four.
        Assert.True(m.GetParameters()[3].HasDefaultValue,
            "appGroupId lost its default — BC's own call sites would stop compiling, and the "
            + "arity trap above would no longer be the reason a 3-parameter bind fails.");
    }

    [Fact]
    public void TheHelpersReflectionBind_ResolvesEveryBcMemberItDrives()
    {
        // Executes the bind itself against BC's real type — the check that catches an arity or
        // receiver mistake here rather than on the first Query Metadata read of a run. `self`
        // is the NCLMetadata instance, because the helper replaces a method ON NCLMetadata.
        var instance = RuntimeHelpers.GetUninitializedObject(Ncl.GetType(MetadataTypeName)!);

        var ensure = typeof(RecordPatches).GetMethod("EnsureQuerySnapshotReflection",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var ex = Record.Exception(() => ensure.Invoke(null, new[] { instance }));
        Assert.Null(ex);

        foreach (var field in new[] { "_qmTryGetMetaQuery", "_qmTryGetMetaApplicationObject",
                     "_qmSnapshotOuter", "_qmSnapshotInner", "_qmEntryType", "_qmObjectTypeEnum" })
        {
            var value = typeof(RecordPatches).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null);
            Assert.True(value != null, $"{field} was not bound.");
        }
    }

    [Fact]
    public void GetAppId_StillReadsOwningApp_WhichTheRunnerMustSupply()
    {
        // Why RecordPatches.NclMetaQueryBuilder.cs writes the owning app at all: BC's
        // MetadataDataProvider.GetAppId returns NavGuid.Default when OwningApp is null, so the
        // row's "App ID" column is the EMPTY GUID unless the runner fills it. BC's own lazy
        // cannot: it is gated on MetadataAppGroup.GroupId != 0 and the runner plants
        // NavAppGroup.BaseGroup, whose GroupId is 0.
        var getAppId = Ncl.GetType("Microsoft.Dynamics.Nav.Runtime.MetadataDataProvider")!
            .GetMethod("GetAppId", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(getAppId != null, "MetadataDataProvider.GetAppId is gone.");

        var owningApp = Ncl.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject")!
            .GetProperty("OwningApp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(owningApp != null,
            "NCLMetaApplicationObject.OwningApp is gone — the App ID column has no source.");
    }
}
