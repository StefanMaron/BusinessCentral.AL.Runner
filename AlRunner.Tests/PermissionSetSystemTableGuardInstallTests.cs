// PermissionSetSystemTableGuardInstallTests — issue #3344.
//
// What this pins, and why it is a RUNNER-INTERNAL claim
// ----------------------------------------------------
// The "Permission Set" system table (2000000004) is served from permission-set metadata,
// and real BC recomputes it on every request. The runner's store does not: a NavRecord's
// DataAccess wrapper is resolved at most once (RecordImplementation.InitializeImpl), so
// without a per-request guard only a record variable's FIRST touch would populate the
// store. FOUR separate request paths reach a table — find, count, exists and
// get-by-primary-key — and the runner has repeatedly shipped a table wired to some of them
// and silently wrong on the rest (#2504 Aggregate Permission Set, #2648 Date, #2792 Field,
// #3006 IsEmpty). A missing wiring has no symptom of its own: the table answers over
// whatever the store last held, which reads as a legitimate zero.
//
// So this file asserts the wiring itself: three Cecil prepends and one branch in
// DataAccess_IsManagedFindRequest, each naming its own guard, plus the guards' own
// signatures — a Cecil `call` binds to those by name and arity, so a signature change
// turns into a load-time failure rather than a compile error.
//
// Nothing here claims anything about what BC answers. That is asserted upstream by corpus
// codeunit 60291 "Test Permission Set Table", against a real service tier, and the
// runtime proof that these four paths behave is that codeunit going RED -> GREEN.
//
// Why the registrations are read as SOURCE rather than out of the rewritten image. All
// four DataAccess methods are `async`, so the compiler-generated stub bodies in the Ncl
// image on disk are Create/Start/get_Task and the rewrite happens on the loaded module in
// memory. Measured while writing this file: reading typeof(ITreeObject).Assembly.Location
// with Cecil shows no guard call in CountAsync, ExistsAsync or InternalTryGetByPrimaryKey
// Async — not even the long-standing Date ones — so an image-shape assertion there would
// pin nothing at all. The asymmetry these tests exist to protect (the get path populates
// non-assignable sets, the other three do not) is a property of the registration, and that
// is what is read.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class PermissionSetSystemTableGuardInstallTests
{
    private static readonly string SourceRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AlRunner"));

    private static string Read(params string[] parts)
    {
        var path = Path.Combine(new[] { SourceRoot }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"expected runner source at {path}");
        return File.ReadAllText(path);
    }

    private static string CecilRewrite() => Read("Infrastructure", "NclCecilRewrite.Runtime.cs");
    private static string FindIntercept() => Read("Patches", "RecordPatches.FieldFindIntercept.cs");
    private static string ProviderFile() => Read("Patches", "RecordPatches.PermissionSetSystemTable.cs");

    /// <summary>
    /// The text of the one PrependStaticCall registration naming <paramref name="guard"/>,
    /// from its `PrependStaticCall(` through the end of that statement. Empty when the guard
    /// is registered nowhere — which is the RED state the first test pins.
    /// </summary>
    private static string RegistrationFor(string guard)
    {
        var src = CecilRewrite();
        var at = src.IndexOf(guard, StringComparison.Ordinal);
        if (at < 0) return string.Empty;
        var start = src.LastIndexOf("PrependStaticCall(", at, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        var end = src.IndexOf(";", at, StringComparison.Ordinal);
        return end < 0 ? string.Empty : src.Substring(start, end - start);
    }

    // RED before the fix: none of these three guards existed, so 2000000004 was populated
    // only on a record variable's first touch and answered zero rows before that — which is
    // what made Microsoft's "Permissions Mock".Assign fail its PermissionSet.Get(RoleID).
    [Theory]
    [InlineData("DataAccess_PermissionSetSystemTableGuardForCount", "CountAsync", "CountCacheRequest")]
    [InlineData("DataAccess_PermissionSetSystemTableGuardForExists", "ExistsAsync", "ExistsCacheRequest")]
    [InlineData("DataAccess_PermissionSetSystemTableGuardForGet", "InternalTryGetByPrimaryKeyAsync", "PrimaryKeyCacheRequest")]
    public void EachNonFindRequestPath_RegistersItsOwnGuard(string guard, string method, string requestType)
    {
        var registration = RegistrationFor(guard);
        Assert.False(registration.Length == 0, $"{guard} is registered by no PrependStaticCall");

        // The registration must name the RIGHT method and request type — a guard prepended
        // to the wrong path is worse than none, because it looks wired.
        Assert.Contains($"\"{method}\"", registration);
        Assert.Contains($"\"{requestType}\"", registration);
        Assert.Contains("argSlots: 2", registration);
    }

    // The negative control: one guard per path, not one guard sprayed across every path.
    // A copy-paste registering the get guard on CountAsync as well would make Count()
    // answer over the non-assignable rows the walk must not list.
    [Fact]
    public void TheGetGuard_IsRegisteredExactlyOnce_OnTheGetPath()
    {
        var occurrences = CecilRewrite().Split("DataAccess_PermissionSetSystemTableGuardForGet").Length - 1;
        Assert.Equal(1, occurrences);
        Assert.Contains("InternalTryGetByPrimaryKeyAsync",
            RegistrationFor("DataAccess_PermissionSetSystemTableGuardForGet"));
    }

    // The fourth path. Find()/FindSet() does not take a Cecil prepend of its own — it is a
    // branch inside the shared DataAccess_IsManagedFindRequest predicate — and it must ask
    // for the assignable-only shape, since walking the table lists only assignable sets.
    [Fact]
    public void TheFindPath_RepopulatesInTheAssignableOnlyShape()
    {
        var src = FindIntercept();
        var at = src.IndexOf("PermissionSetSystemTableId", StringComparison.Ordinal);
        Assert.True(at > 0, "DataAccess_IsManagedFindRequest has no Permission Set branch");

        var branch = src.Substring(at, Math.Min(900, src.Length - at));
        Assert.Contains("RepopulatePermissionSetSystemTableForRequest", branch);
        Assert.Contains("includeNonAssignable: false", branch);
    }

    // The asymmetry itself, stated as one assertion a reviewer can check: across every file
    // that populates this table, exactly one call site asks for the non-assignable rows.
    // This is BC's own split (MetadataPermissionSetDataProvider.TryGetByPrimaryKey never
    // consults Assignable; its GetAllItems filters on it), and wiring the paths alike is
    // the easy mistake.
    [Fact]
    public void ExactlyOneCallSite_AsksForNonAssignableSets_AndItIsTheGetGuard()
    {
        var everywhere = CecilRewrite() + FindIntercept() + Read("Patches", "RecordPatches.cs") + ProviderFile();
        Assert.Equal(1, everywhere.Split("includeNonAssignable: true").Length - 1);

        var provider = ProviderFile();
        var guardAt = provider.IndexOf(
            "public static void DataAccess_PermissionSetSystemTableGuardForGet", StringComparison.Ordinal);
        Assert.True(guardAt > 0, "the get guard is not declared in the provider file");
        Assert.Contains("includeNonAssignable: true",
            provider.Substring(guardAt, Math.Min(500, provider.Length - guardAt)));
    }

    // A Cecil `call` binds by name and arity, so the guards' shape is part of the contract.
    [Theory]
    [InlineData("DataAccess_PermissionSetSystemTableGuardForCount")]
    [InlineData("DataAccess_PermissionSetSystemTableGuardForExists")]
    [InlineData("DataAccess_PermissionSetSystemTableGuardForGet")]
    public void EachGuard_IsAPublicStaticTwoObjectHook(string name)
    {
        var mi = typeof(RecordPatches).GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(mi);
        Assert.True(mi!.IsStatic);
        Assert.Equal(typeof(void), mi.ReturnType);
        Assert.Equal(new[] { typeof(object), typeof(object) },
            mi.GetParameters().Select(p => p.ParameterType).ToArray());
    }
}
