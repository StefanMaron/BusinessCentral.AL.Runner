// NavNCLDialogExceptionResolutionTests — issue #1883, the NavDialog orphaned-hook cluster.
//
// BcRuntime resolves NavNCLDialogException out of the loaded Types assembly and stores it in
// _navNCLDialogExceptionType. MethodScopePatches' NavMethodScopeCtorReplacement throws that type
// when AL blows the recursion ceiling, so AL traps the refusal as an AL error with a call stack
// instead of a raw CLR fault. When the lookup answers null the refusal silently degrades to an
// InvalidOperationException — nothing fails, and correct AL starts reporting a process fault.
//
// That lookup used to live INSIDE BcRuntime's NavDialog.ALError hook block, whose other contents
// were dead JmpHook registrations. Deleting the dead code would have taken the live lookup with
// it. It now sits beside its consumer, and this file pins the two properties the consumer needs:
// the name still resolves against a real shipped Types assembly, and the resolved type still has
// the (string) constructor Activator.CreateInstance is handed at the throw site.
//
// The END-TO-END observable — that exceeding the ceiling raises a trappable AL error rather than
// a process fault — is a BC-behaviour claim and is asserted upstream, by corpus codeunit 60035's
// RecursionDepth_PastTheCeiling_IsRefused, which requires a non-empty GetLastErrorCallStack().
// That corpus test is what goes red if the assignment is deleted; these tests are what go red if
// the NAME stops resolving. Two different failure modes, so both are measured.

using System;
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

public sealed class NavNCLDialogExceptionResolutionTests
{
    /// <summary>The real Microsoft.Dynamics.Nav.Types, obtained through a type the test project
    /// already references, so this needs no engine bootstrap and no patch application.</summary>
    private static Assembly TypesAssembly =>
        typeof(Microsoft.Dynamics.Nav.Types.ALErrorType).Assembly;

    [Fact]
    public void Resolves_TheExceptionType_FromARealTypesAssembly()
    {
        var t = BcRuntime.ResolveNavNCLDialogExceptionType(TypesAssembly);

        Assert.NotNull(t);
        Assert.Equal("NavNCLDialogException", t!.Name);
        // Asserted as well as the name: the throw site casts the constructed instance to
        // Exception, so a same-named type that stopped deriving from it would not do.
        Assert.True(typeof(Exception).IsAssignableFrom(t),
            $"{t.FullName} must be throwable — NavMethodScopeCtorReplacement casts it to Exception");
    }

    [Fact]
    public void ResolvedType_HasTheSingleStringConstructor_TheThrowSiteUses()
    {
        var t = BcRuntime.ResolveNavNCLDialogExceptionType(TypesAssembly);
        Assert.NotNull(t);

        // MethodScopePatches: Activator.CreateInstance(_navNCLDialogExceptionType, msg).
        // Without this ctor the recursion refusal throws MissingMethodException from inside the
        // guard — a worse failure than the one the guard exists to report.
        var ctor = t!.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: new[] { typeof(string) }, modifiers: null);

        Assert.NotNull(ctor);

        const string Msg = "Maximum recursion depth (1000) exceeded";
        var instance = Activator.CreateInstance(t, Msg) as Exception;
        Assert.NotNull(instance);
        Assert.Contains(Msg, instance!.Message);
    }

    [Fact]
    public void NullTypesAssembly_ResolvesToNull_RatherThanThrowing()
    {
        // The negative direction. ApplyAllPatches passes whatever FirstOrDefault found, which is
        // null when Types is not loaded; the resolver must answer null there rather than take
        // the whole run down with a NullReferenceException.
        Assert.Null(BcRuntime.ResolveNavNCLDialogExceptionType(null));
    }
}
