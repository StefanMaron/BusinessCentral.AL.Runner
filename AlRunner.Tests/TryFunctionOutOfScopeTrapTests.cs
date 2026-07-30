// TryFunctionOutOfScopeTrapTests — what an AL [TryFunction] does with a
// RunnerOutOfScopeException raised inside it.
//
// Root cause being tested
// -----------------------
// Real BC's NavApplicationObjectBase.TryInvoke/TryInvokeAsync (decompiled from
// Ncl 28.1) is exactly:
//
//     try { using (session.CurrentMethodScope.GetTryMethodScope()) { method(); return true; } }
//     catch (NavBaseException ex) when (!ex.UntrappableError) { ...; return false; }
//
// i.e. a [TryFunction] is AL's explicit "this call may fail, give me a bool"
// seam, and every trappable runtime error inside it becomes `false`.
//
// The runner's replacement matched that catch clause literally, so a
// RunnerOutOfScopeException — a plain System.Exception by design, so `asserterror`
// cannot swallow it — tore straight through the TryFunction and failed the test.
//
// That is unfaithful for a PERMANENTLY out-of-scope surface. "Permanently out of
// scope" means the surface does not exist in the runner's world at all (SMTP, an
// Azure Key Vault, external HTTP). A real BC environment that also lacks it — and
// no BC test environment has an App Key Vault provisioned for a third-party app —
// raises a trappable error there, so the AL author's TryFunction sees `false`.
// Measured on Pageworks 2026-07-27: 85 of 200 remaining failures were tests with
// nothing to do with key vaults, dying inside
// "App Key Vault Secret Provider".TryInitializeFromCurrentApp on an incidental
// licensing check that BC answers with a plain `false`.
//
// It stays unfaithful to trap the OTHER kind of OOS: reason "not-yet-implemented"
// marks an IN-scope surface the runner simply has not built yet. Swallowing that
// would turn a runner gap into a green test that lies, which is exactly what
// .claude/rules/loud-failures.md exists to prevent. So the trap is scoped to
// permanent OOS only, and even then it prints a loud named line.

using System;
using System.Threading.Tasks;
using Xunit;
using AlRunner;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

public sealed class TryFunctionOutOfScopeTrapTests
{
    private static RunnerOutOfScopeException PermanentOos() =>
        new("NavDotNet.CreateNavServerHandle",
            "dotnet-server-interop — external assembly/KMS unavailable: " +
            "Microsoft.Dynamics.Nav.AzureKeyVaultClient",
            "crypto-external");

    private static RunnerOutOfScopeException NotYetImplemented() =>
        new("INCLObjectXmlMetadataLoader.GetMetaObjectXmlMetadata",
            "not-yet-implemented — report metadata loader",
            "todo");

    // ── Positive: a permanently-OOS surface reads as `false`, like real BC ──

    [Fact]
    public void TryInvoke_ReturnsFalse_WhenPermanentlyOutOfScopeSurfaceIsTouched()
    {
        var result = BcRuntime.NavApplicationObjectBase_TryInvoke(
            null, () => throw PermanentOos());

        Assert.False(result);
    }

    [Fact]
    public async Task TryInvokeAsync_ReturnsFalse_WhenPermanentlyOutOfScopeSurfaceIsTouched()
    {
        var result = await BcRuntime.NavApplicationObjectBase_TryInvokeAsync(
            null, () => throw PermanentOos());

        Assert.False(result);
    }

    // ── Negative: an in-scope-but-unbuilt surface must still fail LOUD ──

    [Fact]
    public void TryInvoke_StillThrows_ForNotYetImplementedSurface()
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(
                null, () => throw NotYetImplemented()));

        Assert.Contains("not-yet-implemented", ex.Reason);
        Assert.Equal("INCLObjectXmlMetadataLoader.GetMetaObjectXmlMetadata", ex.Api);
    }

    [Fact]
    public async Task TryInvokeAsync_StillThrows_ForNotYetImplementedSurface()
    {
        var ex = await Assert.ThrowsAsync<RunnerOutOfScopeException>(
            async () => await BcRuntime.NavApplicationObjectBase_TryInvokeAsync(
                null, () => throw NotYetImplemented()));

        Assert.Contains("not-yet-implemented", ex.Reason);
    }

    // ── Negative: unrelated non-AL exceptions keep propagating unchanged ──

    [Fact]
    public void TryInvoke_StillPropagates_UnrelatedRunnerExceptions()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(
                null, () => throw new InvalidOperationException("runner bug, not an AL error")));

        Assert.Equal("runner bug, not an AL error", ex.Message);
    }

    // ── The happy path is untouched ──

    [Fact]
    public void TryInvoke_ReturnsTrue_WhenBodySucceeds()
    {
        int ran = 0;
        var result = BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => ran++);

        Assert.True(result);
        Assert.Equal(1, ran);
    }
}
