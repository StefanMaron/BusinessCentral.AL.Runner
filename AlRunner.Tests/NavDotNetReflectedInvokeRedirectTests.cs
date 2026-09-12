// Runner-mechanism tests for #3174. What AL observes from
// NavUserAccountHelper.IsUserSuperInAllCompanies is a BC-behaviour claim and lives upstream in
// the corpus (al-language-onprem codeunit 61203). These pin the runner's own plumbing: that the
// rewritten Ncl routes NavDotNet.Invoke<T>'s reflective call through the redirect, and that the
// redirect leaves every other member's invocation, including its exception shape, untouched.
using System.Reflection;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class NavDotNetReflectedInvokeRedirectTests
{
    private readonly BcEngineFixture _engine;

    public NavDotNetReflectedInvokeRedirectTests(BcEngineFixture engine) => _engine = engine;

    private static IReadOnlyList<MethodReference> CallsInLoadedNavDotNetInvoke()
    {
        var location = typeof(ITreeObject).Assembly.Location;
        Assert.False(string.IsNullOrEmpty(location), "the loaded Ncl has no file location to read");
        using var module = ModuleDefinition.ReadModule(location, new ReaderParameters { ReadWrite = false });
        var navDotNet = module.GetType("Microsoft.Dynamics.Nav.Runtime.NavDotNet")!;
        var invoke = navDotNet.Methods.Single(m => m.Name == "Invoke" && m.HasGenericParameters && m.Parameters.Count == 6);
        return invoke.Body.Instructions
            .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference)
            .Select(i => (MethodReference)i.Operand)
            .ToList();
    }

    [SkippableFact]
    public void LoadedNcl_NavDotNetInvoke_CallsTheRedirect_AndNoLongerCallsMethodBaseInvoke()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var calls = CallsInLoadedNavDotNetInvoke();

        Assert.Single(calls, c => c.Name == nameof(NavDotNetPatches.InvokeReflectedMember)
                                  && c.DeclaringType.FullName == typeof(NavDotNetPatches).FullName);
        Assert.DoesNotContain(calls, c => c.Name == "Invoke"
                                          && c.DeclaringType.FullName == "System.Reflection.MethodBase"
                                          && c.Parameters.Count == 2);
    }

    private static int Twice(int x) => checked(x * 2);

    [Fact]
    public void Redirect_AnyOtherMember_ReturnsWhatReflectionReturns()
    {
        var method = typeof(NavDotNetReflectedInvokeRedirectTests).GetMethod(nameof(Twice), BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Equal(42, NavDotNetPatches.InvokeReflectedMember(method, null, new object?[] { 21 }));
    }

    [Fact]
    public void Redirect_AnyOtherMember_KeepsTheTargetInvocationExceptionBcCatches()
    {
        var method = typeof(NavDotNetReflectedInvokeRedirectTests).GetMethod(nameof(Twice), BindingFlags.NonPublic | BindingFlags.Static)!;

        var ex = Assert.Throws<TargetInvocationException>(
            () => NavDotNetPatches.InvokeReflectedMember(method, null, new object?[] { int.MaxValue }));
        Assert.IsType<OverflowException>(ex.InnerException);
    }

    public static bool IsUserSuperInAllCompanies() => false;

    [Fact]
    public void Redirect_SameMemberNameOnAnotherType_IsInvokedNotAnswered()
    {
        // Same name and arity as the NavUserAccountHelper member; only the declaring type
        // differs, so this reaches the real body (false) instead of the session lookup, which
        // would throw here with no BC session on the thread.
        var method = typeof(NavDotNetReflectedInvokeRedirectTests).GetMethod(nameof(IsUserSuperInAllCompanies))!;

        Assert.Equal(false, NavDotNetPatches.InvokeReflectedMember(method, null, Array.Empty<object>()));
    }
}
