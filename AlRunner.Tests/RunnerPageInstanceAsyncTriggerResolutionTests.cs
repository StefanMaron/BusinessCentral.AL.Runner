// Issue #2522 — which of NavForm's two declarations of a page trigger the runner invokes.
//
// NavForm declares every page trigger TWICE: a synchronous virtual with an EMPTY body
// (`protected virtual void OnOpenPage() {}`) and an async twin
// (`protected virtual ValueTask OnOpenPageAsync() => default`). The runner's own emit
// pipeline overrides the synchronous one; Microsoft's AL compiler overrides the async one.
// BC's own dispatcher picks between them off the page object's `__IsAsync`, verbatim from
// NavForm.RaiseOnOpenPageAsync in BC 28.1's Ncl.dll:
//
//     if (__IsAsync) await OnOpenPageAsync(); else OnOpenPage();
//
// Resolving the synchronous name only bound NavForm's own empty body for every precompiled
// page, invoked it, and reported success having run nothing — Base Application page 973
// "Time Sheet Card" never applied its default owner filter and never raised its
// OnBefore/OnAfterOnOpenPage integration events.
//
// This pins the resolution rule itself. It stands in for the BC-behaviour claim, which is
// not expressible upstream: the corpus compiles its own pages through the runner's pipeline,
// so a corpus page is async-compiled only if Microsoft's compiler produced it.
using System;
using System.Reflection;
using System.Threading.Tasks;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class RunnerPageInstanceAsyncTriggerResolutionTests
{
    /// <summary>Stands in for NavForm: both declarations, both empty.</summary>
    private class FakeNavForm
    {
        protected virtual void OnOpenPage() { }
        protected virtual ValueTask OnOpenPageAsync() => default;
        protected virtual bool OnInsertRecord(bool belowXRec) => true;
        protected virtual ValueTask<bool> OnInsertRecordAsync(bool belowXRec) => new(true);
    }

    /// <summary>A page Microsoft's AL compiler emitted: overrides the ASYNC twin only.</summary>
    private sealed class AsyncCompiledPage : FakeNavForm
    {
        protected override ValueTask OnOpenPageAsync() => default;
        protected override ValueTask<bool> OnInsertRecordAsync(bool belowXRec) => new(false);
    }

    /// <summary>A page the runner compiled itself: overrides the SYNCHRONOUS one only.</summary>
    private sealed class SyncCompiledPage : FakeNavForm
    {
        protected override void OnOpenPage() { }
        protected override bool OnInsertRecord(bool belowXRec) => false;
    }

    /// <summary>A page declaring neither trigger — it inherits both empty bases.</summary>
    private sealed class NoTriggerPage : FakeNavForm { }

    [Theory]
    [InlineData("OnOpenPage")]
    public void AsyncCompiledPage_ResolvesTheAsyncOverride_NotTheEmptySyncBase(string trigger)
    {
        var resolved = RunnerPageInstance.ResolveRecordTriggerOn(
            typeof(AsyncCompiledPage), isAsyncCompiled: true, trigger, Type.EmptyTypes);

        Assert.NotNull(resolved);
        Assert.Equal(trigger + "Async", resolved!.Name);
        Assert.Equal(typeof(AsyncCompiledPage), resolved.DeclaringType);
        Assert.Equal(typeof(ValueTask), resolved.ReturnType);
    }

    [Fact]
    public void AsyncCompiledPage_ArgumentTakingTrigger_ResolvesTheAsyncOverride()
    {
        var resolved = RunnerPageInstance.ResolveRecordTriggerOn(
            typeof(AsyncCompiledPage), isAsyncCompiled: true, "OnInsertRecord", new[] { typeof(bool) });

        Assert.NotNull(resolved);
        Assert.Equal("OnInsertRecordAsync", resolved!.Name);
        Assert.Equal(typeof(AsyncCompiledPage), resolved.DeclaringType);
        Assert.Equal(typeof(ValueTask<bool>), resolved.ReturnType);
    }

    [Fact]
    public void SyncCompiledPage_KeepsResolvingTheSynchronousOverride()
    {
        var resolved = RunnerPageInstance.ResolveRecordTriggerOn(
            typeof(SyncCompiledPage), isAsyncCompiled: false, "OnOpenPage", Type.EmptyTypes);

        Assert.NotNull(resolved);
        Assert.Equal("OnOpenPage", resolved!.Name);
        Assert.Equal(typeof(SyncCompiledPage), resolved.DeclaringType);
        Assert.Equal(typeof(void), resolved.ReturnType);
    }

    [Fact]
    public void SyncCompiledPage_ArgumentTakingTrigger_KeepsResolvingTheSynchronousOverride()
    {
        var resolved = RunnerPageInstance.ResolveRecordTriggerOn(
            typeof(SyncCompiledPage), isAsyncCompiled: false, "OnInsertRecord", new[] { typeof(bool) });

        Assert.NotNull(resolved);
        Assert.Equal("OnInsertRecord", resolved!.Name);
        Assert.Equal(typeof(SyncCompiledPage), resolved.DeclaringType);
        Assert.Equal(typeof(bool), resolved.ReturnType);
    }

    /// <summary>
    /// The negative direction of the same rule: an async-compiled page that declares NO
    /// override still resolves to the base — invoking a base no-op is correct there, and is
    /// exactly the outcome that was wrong for AsyncCompiledPage above.
    /// </summary>
    [Fact]
    public void PageDeclaringNoTrigger_FallsBackToTheBaseDeclaration()
    {
        var resolvedAsync = RunnerPageInstance.ResolveRecordTriggerOn(
            typeof(NoTriggerPage), isAsyncCompiled: true, "OnOpenPage", Type.EmptyTypes);
        var resolvedSync = RunnerPageInstance.ResolveRecordTriggerOn(
            typeof(NoTriggerPage), isAsyncCompiled: false, "OnOpenPage", Type.EmptyTypes);

        Assert.Equal(typeof(FakeNavForm), resolvedAsync!.DeclaringType);
        Assert.Equal("OnOpenPageAsync", resolvedAsync.Name);
        Assert.Equal(typeof(FakeNavForm), resolvedSync!.DeclaringType);
        Assert.Equal("OnOpenPage", resolvedSync.Name);
    }

    /// <summary>A trigger the page does not declare at all stays null, not a wrong method.</summary>
    [Fact]
    public void UnknownTriggerName_ResolvesToNull()
    {
        Assert.Null(RunnerPageInstance.ResolveRecordTriggerOn(
            typeof(AsyncCompiledPage), isAsyncCompiled: true, "OnNotATrigger", Type.EmptyTypes));
    }
}
