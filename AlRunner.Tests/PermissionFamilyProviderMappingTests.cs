// PermissionFamilyProviderMappingTests — the runner-side wiring behind #2910 and #3705.
//
// What BC answers for "Permission" (2000000005), "Metadata Permission" (2000000251) and
// "Expanded Permission" (2000000254) is adjudicated upstream by corpus codeunits 60604 and
// 67945. What is pinned here is the runner's own table: which BC provider each table is
// routed to, and that every request path redrives the three of them. A swapped mapping
// serves "declared grants" where "expanded grants" were asked for — a silently wrong answer
// with every row well-formed — and a missing redrive answers from a stale store.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class PermissionFamilyProviderMappingTests
{
    private const string Rt = "Microsoft.Dynamics.Nav.Runtime.";

    private static readonly string SourceRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AlRunner"));

    private static string Read(params string[] parts)
    {
        var path = Path.Combine(new[] { SourceRoot }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"expected runner source at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>The constant BC's own <c>TableId</c> override returns, read from its IL.</summary>
    private static int BcProviderTableId(string providerTypeName)
    {
        var location = typeof(NCLMetaTable).Assembly.Location;
        using var module = ModuleDefinition.ReadModule(location);
        var type = module.GetType(Rt + providerTypeName);
        Assert.True(type != null, $"{providerTypeName} is not a type in {location}");
        var getter = type!.Properties.Single(p => p.Name == "TableId").GetMethod;
        var ldc = getter.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldc_I4);
        return (int)ldc.Operand;
    }

    [Theory]
    [InlineData(2000000005)]
    [InlineData(2000000251)]
    [InlineData(2000000254)]
    public void EachFamilyTable_IsRoutedToTheProviderWhoseOwnTableIdIsThatTable(int tableId)
    {
        var entry = RecordPatches.PermissionFamilyTables.Single(t => t.TableId == tableId);

        Assert.Equal(tableId, BcProviderTableId(entry.ProviderTypeName));

        var type = typeof(NCLMetaTable).Assembly.GetType(Rt + entry.ProviderTypeName)!;
        Assert.Equal("PermissionDataProviderBase", type.BaseType?.Name);

        // The constructor the runner invokes must be the one BC declares: the PermissionProvider
        // argument is present exactly when the entry says so, or the Invoke fails at run time.
        var ctor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();
        var parameters = ctor.GetParameters().Select(p => p.ParameterType.Name).ToArray();
        var expected = entry.TakesPermissionProvider
            ? new[] { "NavSession", "NCLMetadata", "PermissionProvider" }
            : new[] { "NavSession", "NCLMetadata" };
        Assert.Equal(expected, parameters);
    }

    [Fact]
    public void TheFamily_IsExactlyTheThreePermissionDataProviderBaseTables()
    {
        Assert.Equal(new[] { 2000000005, 2000000251, 2000000254 },
            RecordPatches.PermissionFamilyTables.Select(t => t.TableId).OrderBy(i => i).ToArray());

        // The population, read from BC rather than from this file: every concrete
        // PermissionDataProviderBase in the loaded Ncl is routed, and nothing else is.
        Type[] nclTypes;
        try { nclTypes = typeof(NCLMetaTable).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { nclTypes = e.Types.Where(t => t != null).ToArray()!; }
        var bcProviders = nclTypes
            .Where(t => !t.IsAbstract && t.BaseType?.FullName == Rt + "PermissionDataProviderBase")
            .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(bcProviders);
        Assert.Equal(bcProviders,
            RecordPatches.PermissionFamilyTables.Select(t => t.ProviderTypeName).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        foreach (var id in new[] { 2000000005, 2000000251, 2000000254 })
            Assert.True(RecordPatches.IsPermissionFamilyTableId(id), $"{id} must be redriven");

        // The neighbouring permission tables have their own providers and their own guards;
        // routing one of them through this family would answer it with the wrong provider.
        foreach (var id in new[] { 2000000004, 2000000250, 2000000167, 2000000165, 2000000166 })
            Assert.False(RecordPatches.IsPermissionFamilyTableId(id), $"{id} is not a family table");
    }

    [Theory]
    [InlineData("DataAccess_PermissionFamilyGuardForCount", "CountAsync", "CountCacheRequest")]
    [InlineData("DataAccess_PermissionFamilyGuardForExists", "ExistsAsync", "ExistsCacheRequest")]
    [InlineData("DataAccess_PermissionFamilyGuardForGet", "InternalTryGetByPrimaryKeyAsync", "PrimaryKeyCacheRequest")]
    public void EachNonFindRequestPath_RegistersTheFamilyGuard(string guard, string method, string requestType)
    {
        var src = Read("Infrastructure", "NclCecilRewrite.Runtime.cs");
        Assert.Equal(1, src.Split($"\"{guard}\"").Length - 1);

        var at = src.IndexOf($"\"{guard}\"", StringComparison.Ordinal);
        var start = src.LastIndexOf("PrependStaticCall(", at, StringComparison.Ordinal);
        var registration = src.Substring(start, src.IndexOf(';', at) - start);
        Assert.Contains($"\"{method}\", \"{requestType}\"", registration);
        Assert.Contains("argSlots: 2", registration);

        // A Cecil `call` binds by name and arity, so the guard's shape is part of the contract.
        var mi = typeof(RecordPatches).GetMethod(guard, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(mi);
        Assert.Equal(new[] { typeof(object), typeof(object) },
            mi!.GetParameters().Select(p => p.ParameterType).ToArray());
    }

    [Fact]
    public void TheFindPath_RedrivesTheFamily_AndFallsThroughToBcsFind()
    {
        var src = Read("Patches", "RecordPatches.FieldFindIntercept.cs");
        var at = src.IndexOf("IsPermissionFamilyTableId(tableId)", StringComparison.Ordinal);
        Assert.True(at > 0, "DataAccess_IsManagedFindRequest has no Permission-family branch");

        var branch = src.Substring(at, src.IndexOf('}', at) - at);
        Assert.Contains("RedrivePermissionFamilyForRequest(self, request);", branch);
        // `true` would take the managed Field-table bypass instead of BC's own InnerFindAsync.
        Assert.Contains("return false;", branch);
    }
}
