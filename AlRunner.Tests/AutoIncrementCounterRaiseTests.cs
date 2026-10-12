// AutoIncrementCounterRaiseTests -- the runner-side half of issue #5513.
//
// What BC does with an AutoIncrement key the AL code assigned itself (the next zero-key Insert follows it) is a claim
// about BC and is proven upstream by corpus codeunit 67822 "Test AutoInc Explicit Key" against a real service tier.
// None of that is asserted here. What is runner-specific and not reachable from AL: the counter helper's behaviour on
// a counter that has not started, which the corpus cannot set up (the counter of a corpus table has always started by
// the time a test runs, and BC's own counter persists across tests).
using System;
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

public sealed class AutoIncrementCounterRaiseTests
{
    private static Type BcRuntimeType =>
        typeof(AlRunner.Patches.RecordPatches).Assembly.GetType("AlRunner.BcRuntime")
        ?? throw new InvalidOperationException("AlRunner.BcRuntime not found");

    private static MethodInfo Method(string name) =>
        BcRuntimeType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException($"BcRuntime.{name} not found -- AssignAutoIncrement can no longer raise the counter (#5513)");

    private static long Take(int tableId) => (long)Method("TakeAutoIncrementValue").Invoke(null, new object[] { tableId, 1 })!;

    private static void Raise(int tableId, long explicitKey) =>
        Method("RaiseAutoIncrementCounterTo").Invoke(null, new object[] { tableId, 1, explicitKey });

    // Table ids far outside anything the runner materialises, one per test, so no test sees another's counter.
    private const int FreshTable = 1_900_000_301;
    private const int RaisedTwiceTable = 1_900_000_302;
    private const int NonPositiveTable = 1_900_000_303;
    private const int NeighbourA = 1_900_000_304;
    private const int NeighbourB = 1_900_000_305;

    [Fact]
    public void Raise_OnACounterThatHasNotStarted_StartsItAtTheExplicitKey()
    {
        Raise(FreshTable, 100);
        Assert.Equal(101, Take(FreshTable));
        Assert.Equal(102, Take(FreshTable));
    }

    [Fact]
    public void Raise_NeverLowersTheCounter_AndAHigherKeyRaisesItAgain()
    {
        Raise(RaisedTwiceTable, 100);
        Raise(RaisedTwiceTable, 50);
        Assert.Equal(101, Take(RaisedTwiceTable));

        Raise(RaisedTwiceTable, 150);
        Assert.Equal(151, Take(RaisedTwiceTable));
    }

    [Fact]
    public void Raise_ToAZeroOrNegativeKey_LeavesAFreshCounterStartingAtOne()
    {
        Raise(NonPositiveTable, -5);
        Raise(NonPositiveTable, 0);
        Assert.Equal(1, Take(NonPositiveTable));
    }

    [Fact]
    public void Raise_KeepsEachTablesCounterSeparate()
    {
        Raise(NeighbourA, 500);
        Assert.Equal(1, Take(NeighbourB));
        Assert.Equal(501, Take(NeighbourA));
    }
}
