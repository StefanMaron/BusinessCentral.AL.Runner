// NavDotNetPatchesTests — contract tests for the two helpers the Cecil rewrite of
// NavDotNet.CreateNavServerHandle / NavDotNet.CreateDotNet calls into.
//
// The behavioural proof (a real absent-server-assembly access → loud OOS instead of a
// silent NRE) is the manual Pageworks CU50364 repro: a source AL `DotNet` reference
// needs the assembly's metadata at COMPILE time, so the runtime-absent-assembly split
// that triggers this path cannot be expressed as a source-compiled runner-extra. These
// tests pin the message/rethrow CONTRACT the patched IL depends on: the exact OOS
// api/reason/anchor string that test output matches on, and the rethrow guard's
// both-directions behaviour (propagate OOS, no-op everything else).

using System;
using Xunit;
using AlRunner.Patches;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

public sealed class NavDotNetPatchesTests
{
    [Fact]
    public void ThrowServerInteropOOS_Throws_NamedOOS_WithAssemblyAndAnchor()
    {
        const string asm = "Microsoft.Dynamics.Nav.AzureKeyVaultClient, Version=28.0.0.0";

        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => NavDotNetPatches.ThrowServerInteropOOS(asm));

        // The api half is what AL/CI output greps on — must stay stable.
        Assert.Equal("NavDotNet.CreateNavServerHandle", ex.Api);
        Assert.Equal("crypto-external", ex.DocAnchor);
        // Reason names the surface and the exact absent assembly (actionable message).
        Assert.Contains("dotnet-server-interop", ex.Reason);
        Assert.Contains(asm, ex.Reason);
        // Rendered message carries the stable prefix + the single scope.md#anchor link
        // (no duplicated "see docs/scope.md").
        Assert.StartsWith("out-of-scope: NavDotNet.CreateNavServerHandle —", ex.Message);
        Assert.Contains(asm, ex.Message);
        Assert.EndsWith("see docs/scope.md#crypto-external", ex.Message);
        Assert.Equal(ex.Message.IndexOf("docs/scope.md", StringComparison.Ordinal),
                     ex.Message.LastIndexOf("docs/scope.md", StringComparison.Ordinal)); // link appears exactly once
    }

    [Fact]
    public void RethrowIfRunnerOOS_Rethrows_TheSameOOS_Instance()
    {
        var original = new RunnerOutOfScopeException("NavDotNet.CreateNavServerHandle", "reason", "crypto-external");

        // Must propagate the ORIGINAL instance so the OOS signal survives CreateDotNet's
        // catch-all (which would otherwise wrap it in a trappable NavNCLDotNetCreateException).
        var rethrown = Assert.Throws<RunnerOutOfScopeException>(
            () => NavDotNetPatches.RethrowIfRunnerOOS(original));
        Assert.Same(original, rethrown);
    }

    [Fact]
    public void RethrowIfRunnerOOS_IsNoOp_ForNonOOSException()
    {
        // A non-OOS exception must pass through untouched (helper returns; the original
        // catch-all logic then runs). Any throw here would corrupt in-scope error handling.
        var other = new InvalidOperationException("some BC error");
        var record = Record.Exception(() => NavDotNetPatches.RethrowIfRunnerOOS(other));
        Assert.Null(record);
    }

    [Fact]
    public void RethrowIfRunnerOOS_IsNoOp_ForNull()
    {
        var record = Record.Exception(() => NavDotNetPatches.RethrowIfRunnerOOS(null));
        Assert.Null(record);
    }
}
