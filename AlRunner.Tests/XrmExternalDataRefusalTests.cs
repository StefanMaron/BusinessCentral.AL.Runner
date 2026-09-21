// Runner-mechanism tests for #3515. The BC-behaviour half — what a real service tier does with
// a populated proxy registry — is not asserted here and cannot be: a tier's registry comes from
// its own DataSources/DataSources.json, which is deployment configuration rather than anything
// the corpus can pin. What these pin is the runner's own decision: that the external-data proxy
// lookup is refused by name on the lazy AL-invocation path, that the refusal carries the API and
// the scope anchor a reader and tests/expectations/ key on, and that nothing else is refused
// along with it.
using System.Reflection;
using System.Reflection.Emit;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class XrmExternalDataRefusalTests
{
    // Stands in for CrmHelper.GetProxyIdList: same member name and staticness, and the test
    // supplies the declaring-type name separately, so the predicate is exercised on its real
    // discriminators without needing the BC assembly loaded.
    private static class CrmHelperDouble
    {
        public static List<int> GetProxyIdList() => new();
        public static List<int> SomethingElse() => new();
    }

    private static MethodInfo Member(string name) =>
        typeof(CrmHelperDouble).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;

    [Fact]
    public void Refusal_NamesTheApiAndTheScopeAnchor()
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(() => XrmExternalDataRefusal.Throw());

        // The API slot: a reader sees the surface AL touched, not the temp table three frames up.
        Assert.Equal("CrmHelper.GetProxyIdList", ex.Api);
        // The anchor tests/expectations/ matches on, and the section that declares the boundary.
        Assert.Equal("table-connections", ex.DocAnchor);
        Assert.StartsWith("table-connections — ", ex.Reason);
        // The message convention AL asserts against.
        Assert.Contains("out-of-scope: CrmHelper.GetProxyIdList", ex.Message);
        Assert.Contains("docs/scope.md#table-connections", ex.Message);
    }

    [Fact]
    public void Refusal_ExplainsWhyTheRunnerCannotAnswer()
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(() => XrmExternalDataRefusal.Throw());

        // Without this the message names a boundary but not the reason, and the reader has no
        // way to tell an out-of-scope surface from a runner gap that could be closed.
        Assert.Contains("DataSources/DataSources.json", ex.Message);
        Assert.Contains("5330", ex.Message);
        Assert.Contains("7200", ex.Message);
    }

    [Fact]
    public void ThePredicate_IsKeyedOnTheDeclaringTypeAsWellAsTheName()
    {
        // The real member, by name and staticness — but on another type. The declaring-type
        // term is what keeps a same-named member anywhere else out of the refusal.
        Assert.False(XrmExternalDataRefusal.IsExternalDataProxyLookup(Member("GetProxyIdList")));
        Assert.False(XrmExternalDataRefusal.Matches(
            true, "GetProxyIdList", "Contoso.SomethingElse.CrmHelper"));
    }

    [Fact]
    public void ThePredicate_IsKeyedOnTheMemberName()
    {
        Assert.False(XrmExternalDataRefusal.IsExternalDataProxyLookup(Member("SomethingElse")));
        Assert.False(XrmExternalDataRefusal.Matches(
            true, "SomethingElse", XrmExternalDataRefusal.CrmHelperTypeName));
    }

    [Fact]
    public void ThePredicate_IsKeyedOnStaticness()
    {
        // BC declares GetProxyIdList static; an instance member of the same name is a
        // different thing and is invoked, not refused.
        Assert.False(XrmExternalDataRefusal.Matches(
            false, "GetProxyIdList", XrmExternalDataRefusal.CrmHelperTypeName));
    }

    [Fact]
    public void ThePredicate_AcceptsTheRealBcMember()
    {
        // The TRUE arm. Nothing else in this file reaches it: the refusal is keyed on a BC
        // type no unit test can load, so without this the predicate could match nothing at
        // all and every other assertion here would still pass.
        Assert.True(XrmExternalDataRefusal.Matches(
            true, "GetProxyIdList", XrmExternalDataRefusal.CrmHelperTypeName));
    }

    // A type whose runtime FullName is exactly BC's, built at run time so the production
    // path can be driven end to end without loading Microsoft.Dynamics.Nav.CrmCustomizationHelper.dll.
    private static MethodInfo RealNamedGetProxyIdList()
    {
        var asm = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("XrmRefusalProbe"), AssemblyBuilderAccess.Run);
        var module = asm.DefineDynamicModule("XrmRefusalProbe");
        var type = module.DefineType(XrmExternalDataRefusal.CrmHelperTypeName,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var method = type.DefineMethod("GetProxyIdList",
            MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        var il = method.GetILGenerator();
        // A body that returns 0 — the empty-registry answer the refusal exists to replace.
        // If the refusal does not fire, the call returns this instead of throwing.
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
        var built = type.CreateType();
        return built.GetMethod("GetProxyIdList", BindingFlags.Public | BindingFlags.Static)!;
    }

    [Fact]
    public void TheRedirect_RefusesTheExternalDataLookup_EndToEnd()
    {
        // The wiring, through the method the rewritten Ncl actually calls. Without this the
        // refusal can be removed from InvokeReflectedMember entirely and every other test in
        // this file stays green — measured: Failed: 0, Passed: 6 with the call site deleted.
        var method = RealNamedGetProxyIdList();
        Assert.Equal(XrmExternalDataRefusal.CrmHelperTypeName, method.DeclaringType!.FullName);

        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => NavDotNetPatches.InvokeReflectedMember(method, null, Array.Empty<object>()));

        Assert.Equal("CrmHelper.GetProxyIdList", ex.Api);
        Assert.Equal("table-connections", ex.DocAnchor);
    }

    [Fact]
    public void TheRefusedTypeAndMemberAreSpeltAsBcSpellsThem()
    {
        // The predicate compares against these two constants, so a typo in either makes the
        // refusal silently never fire — the failure mode loud-failures.md exists to stop, and
        // one no behavioural test can see because the surface simply behaves as before.
        // Read off Microsoft.Dynamics.Nav.CrmCustomizationHelper.dll's MethodDefinition and
        // TypeDefinition tables on 28.1.49838.53910.
        Assert.Equal("Microsoft.Dynamics.Nav.CrmCustomizationHelper.CrmHelper",
            XrmExternalDataRefusal.CrmHelperTypeName);
        Assert.Equal("GetProxyIdList", XrmExternalDataRefusal.GetProxyIdListName);
    }

    [Fact]
    public void TheRedirect_LeavesEveryOtherMemberAlone()
    {
        // Scoping control for the wiring in InvokeReflectedMember: a member that is not the
        // external-data lookup is still invoked and still returns what reflection returns.
        var result = NavDotNetPatches.InvokeReflectedMember(
            Member("SomethingElse"), null, Array.Empty<object>());

        Assert.Equal(new List<int>(), result);
    }
}
