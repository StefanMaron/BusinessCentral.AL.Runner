// KeyVirtualTableProviderTests — issue #4147, the Key table (2000000063).
//
// A RUNNER-MECHANISM test, not a claim about BC. What Key ANSWERS is pinned upstream
// (al-language corpus, codeunit 60936 — including the two things only a service tier could
// settle: four rows for three declared keys, and the implicit key's '$systemId' spelling).
// What that corpus codeunit cannot see is the wiring: the handout to BC's own KeyDataProvider,
// and the Cecil replacement of its key-field walk, both bind BY NAME at runtime, so a BC
// rename or a dropped registration is not a compile error — it is an empty table again, or a
// failure on first read.
//
// Sibling of TableRelationsMetadataVirtualTableProviderTests (#4088), whose shape this copies.
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class KeyVirtualTableProviderTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string DispatchSource => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Patches", "RecordPatches.DataAccessDispatch.cs"));

    private static string RewriteSource => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Infrastructure", "NclCecilRewrite.Runtime.cs"));

    private static Assembly Ncl => typeof(NCLMetaTable).Assembly;

    private const string HelperName = "KeyDataProvider_GetValuesWithinRangeForKeyField";
    private const string ProviderTypeName = "Microsoft.Dynamics.Nav.Runtime.KeyDataProvider";

    [Fact]
    public void TheHandoutForTable2000000063_IsBcsVirtualDataAccess_NotATempStore()
    {
        var src = DispatchSource;
        var at = src.IndexOf("IsKeyVirtualTable(table)", StringComparison.Ordinal);
        Assert.True(at >= 0, "The Key branch is gone from GetDataAccessForTableCore.");

        var branch = src.Substring(at, Math.Min(300, src.Length - at));
        Assert.Contains("GetKeyVirtualDataAccess", branch, StringComparison.Ordinal);
        // A temp store is what the table fell through to before #4147, and it answers no rows
        // for every table without failing — the silent shape this whole fix exists to remove.
        Assert.DoesNotContain("_mCreateTempDataAccess", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void BcsFactory_StillBuildsKeyDataProviderFor2000000063()
    {
        var provider = Ncl.GetType(ProviderTypeName);
        Assert.True(provider != null, "KeyDataProvider is gone from Ncl.");

        var instance = RuntimeHelpers.GetUninitializedObject(provider!);
        var tableId = provider!.GetProperty("TableId", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(instance);
        Assert.Equal(2000000063, tableId);
        Assert.Equal(2000000063, RecordPatches.KeyVirtualTableId);
    }

    [Fact]
    public void TheKeyFieldWalkIsReplaced_WithAHelperOfMatchingArity()
    {
        // ReplaceBodyWithHelper forwards `this` plus the five parameters; AssertHelperArityMatches
        // refuses a mismatch at rewrite time, but only once a run reaches the rewrite.
        Assert.Contains(HelperName, RewriteSource, StringComparison.Ordinal);

        var helper = typeof(RecordPatches).GetMethod(HelperName, BindingFlags.Public | BindingFlags.Static);
        Assert.True(helper != null, $"RecordPatches.{HelperName} is gone.");

        var target = Ncl.GetType(ProviderTypeName)!
            .GetMethod("GetValuesWithinRangeForKeyField", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(target != null, "KeyDataProvider.GetValuesWithinRangeForKeyField is gone.");

        Assert.Equal(
            new[] { "NCLMetaField", "ReadOnlyRecordBuffer", "Range", "SortOrder", "FilterFieldDictionary" },
            target!.GetParameters().Select(p => p.ParameterType.Name).ToArray());
        Assert.Equal(target.GetParameters().Length + 1, helper!.GetParameters().Length);
    }

    [Fact]
    public void GetKeysOnTable_IsStillPrivateInstance_WithTheArityTheHelperForwards()
    {
        // The whole reason key field 2 is forwarded rather than rebuilt: this method reads
        // NCLMetaTable.Keys and supplies BC's own columns, implicit SystemId key included.
        // It is PRIVATE, so nothing but this test notices a signature change until a read fails.
        var m = Ncl.GetType(ProviderTypeName)!
            .GetMethod("GetKeysOnTable", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(m != null, "KeyDataProvider.GetKeysOnTable is gone — key field 2 has no owner.");
        Assert.Equal(
            new[] { "NavInteger", "NavText", "Range", "SortOrder", "FilterFieldDictionary" },
            m!.GetParameters().Select(p => p.ParameterType.Name).ToArray());
    }

    [Fact]
    public void TheHelpersReflectionBind_ResolvesEveryBcMemberItDrives()
    {
        // Executes the bind itself against BC's real type. A renamed private iterator, a changed
        // TryGetMetaTableById overload or a moved NclMetadata property throws here, naming the
        // shape, instead of on the first Key read of a run.
        var instance = RuntimeHelpers.GetUninitializedObject(Ncl.GetType(ProviderTypeName)!);

        var ensure = typeof(RecordPatches).GetMethod("EnsureKeyReflection",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var ex = Record.Exception(() => ensure.Invoke(null, new[] { instance }));
        Assert.Null(ex);

        foreach (var field in new[] { "_keyGetKeysOnTable", "_keyGetBounds", "_keyTryGetMetaTable",
                     "_keyNclMetadata", "_keyBufferType" })
        {
            var value = typeof(RecordPatches).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null);
            Assert.True(value != null, $"{field} was not bound.");
        }
    }
}
