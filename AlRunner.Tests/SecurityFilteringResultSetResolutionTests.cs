// SecurityFilteringResultSetResolutionTests — #3702, the first handle the new ratchet retired.
//
// InvalidateCurrentResultSetEnumerator writes two BC members. Before #3702 each write was
// `handle?.SetValue(...)` on a handle resolved with `??=`, so a member BC moved turned both
// writes into silent no-ops and Next() resumed from a stale FindSet enumerator (#4161) with no
// message. Resolution now refuses, once, naming the member.
//
// The stand-ins mirror the real shape: BC declares both members on RecordImplementation, a
// base class of what the runner is handed, so the hierarchy walk is part of what is pinned.
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class SecurityFilteringResultSetResolutionTests
{
    private class FullBase
    {
        public object? ResultSetEnumerator { get; set; } = new object();
#pragma warning disable CS0414
        private bool resultSetReversed = true;
#pragma warning restore CS0414
    }

    private sealed class FullDerived : FullBase { }

    private sealed class NoEnumerator { private bool resultSetReversed; public bool Touch() => resultSetReversed; }

    private sealed class NoReversed { public object? ResultSetEnumerator { get; set; } }

    [Fact]
    public void BothMembers_AreResolved_ThroughTheHierarchy_AndAreTheOnesTheWritesNeed()
    {
        var (enumerator, reversed) = SecurityFilteringPatches.ResolveResultSetMembers(typeof(FullDerived));

        Assert.Equal(typeof(FullBase), enumerator.DeclaringType);
        Assert.Equal("ResultSetEnumerator", enumerator.Name);
        Assert.Equal(typeof(FullBase), reversed.DeclaringType);
        Assert.Equal("resultSetReversed", reversed.Name);

        // And they are what the invalidation writes: the stale state is really cleared.
        var rec = new FullDerived();
        enumerator.SetValue(rec, null);
        reversed.SetValue(rec, false);
        Assert.Null(rec.ResultSetEnumerator);
        Assert.False((bool)reversed.GetValue(rec)!);
    }

    [Fact]
    public void AMissingEnumeratorProperty_Refuses_NamingIt()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => SecurityFilteringPatches.ResolveResultSetMembers(typeof(NoEnumerator)));

        Assert.Contains("ResultSetEnumerator", ex.Member);
        Assert.DoesNotContain("resultSetReversed", ex.Member);
    }

    [Fact]
    public void AMissingReversedField_Refuses_NamingIt()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => SecurityFilteringPatches.ResolveResultSetMembers(typeof(NoReversed)));

        Assert.Contains("resultSetReversed", ex.Member);
        Assert.DoesNotContain("ResultSetEnumerator", ex.Member);
    }
}
