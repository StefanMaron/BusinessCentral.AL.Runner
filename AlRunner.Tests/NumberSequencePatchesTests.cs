using System.Collections.Concurrent;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types.Exceptions;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class NumberSequencePatchesTests : IDisposable
{
    public NumberSequencePatchesTests() => NumberSequencePatches.ResetForNewExecution();

    public void Dispose() => NumberSequencePatches.ResetForNewExecution();

    [Fact]
    public void Insert_CurrentAndNext_StartAtSeed_ThenHonorIncrement()
    {
        NumberSequencePatches.ALInsert("Orders", 10, 3, true);

        Assert.Equal(10, NumberSequencePatches.ALCurrent("Orders", true));
        Assert.Equal(10, NumberSequencePatches.ALNext("Orders", true));
        Assert.Equal(13, NumberSequencePatches.ALNext("Orders", true));
        Assert.Equal(13, NumberSequencePatches.ALCurrent("Orders", true));
    }

    [Fact]
    public void NamesAreCaseInsensitive_ButCompanyScopesAreIndependent()
    {
        NumberSequencePatches.ALInsert("Orders", 1, 1, true);
        NumberSequencePatches.ALInsert("orders", 100, 10, false);

        Assert.True(NumberSequencePatches.ALExists("ORDERS", true));
        Assert.True(NumberSequencePatches.ALExists("ORDERS", false));
        Assert.Equal(1, NumberSequencePatches.ALNext("orders", true));
        Assert.Equal(100, NumberSequencePatches.ALNext("Orders", false));

        var duplicate = Assert.Throws<NavALException>(
            () => NumberSequencePatches.ALInsert("ORDERS", 999, 1, true));
        Assert.Contains("already exists", duplicate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, NumberSequencePatches.ALNext("Orders", true));
    }

    [Fact]
    public void Restart_SetsTheNextValue_AndPreservesIncrement()
    {
        NumberSequencePatches.ALInsert("Orders", 10, 3, true);
        Assert.Equal(10, NumberSequencePatches.ALNext("Orders", true));

        NumberSequencePatches.ALRestart("Orders", 50, true);

        Assert.Equal(50, NumberSequencePatches.ALCurrent("Orders", true));
        Assert.Equal(50, NumberSequencePatches.ALNext("Orders", true));
        Assert.Equal(53, NumberSequencePatches.ALNext("Orders", true));
    }

    [Fact]
    public void Range_ReturnsStart_ReportsIncrement_AndAdvancesCurrent()
    {
        NumberSequencePatches.ALInsert("Orders", 10, 3, true);
        long reportedIncrement = 0;
#pragma warning disable CA1416 // The standalone runner executes BC's platform-annotated ByRef wrapper cross-platform.
        var increment = new ByRef<long>(() => reportedIncrement, value => reportedIncrement = value);
#pragma warning restore CA1416

        var rangeStart = NumberSequencePatches.ALRange("Orders", 4, increment, true);

        Assert.Equal(10, rangeStart);
        Assert.Equal(3, reportedIncrement);
        Assert.Equal(19, NumberSequencePatches.ALCurrent("Orders", true));
        Assert.Equal(22, NumberSequencePatches.ALNext("Orders", true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Range_InvalidCount_DoesNotAdvance(int count)
    {
        NumberSequencePatches.ALInsert("Orders", 10, 3, true);

        Assert.Throws<NavALException>(
            () => NumberSequencePatches.ALRange("Orders", count, true));

        Assert.Equal(10, NumberSequencePatches.ALNext("Orders", true));
    }

    [Fact]
    public void MissingOperations_FailWithoutCreatingTheSequence()
    {
        Assert.False(NumberSequencePatches.ALExists("Missing", true));

        foreach (var operation in new Action[]
                 {
                     () => NumberSequencePatches.ALCurrent("Missing", true),
                     () => NumberSequencePatches.ALNext("Missing", true),
                     () => NumberSequencePatches.ALRestart("Missing", 1, true),
                     () => NumberSequencePatches.ALRange("Missing", 1, true),
                 })
        {
            var error = Assert.Throws<NavALException>(operation);
            Assert.Contains("Missing", error.Message, StringComparison.Ordinal);
            Assert.Contains("does not exist", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.False(NumberSequencePatches.ALExists("Missing", true));
    }

    /// <summary>
    /// AlRunner#2311. The mechanism the 29-test Tests-SINGLESERVER cluster tripped over:
    /// Base App codeunit "Sequence No. Mgt." asks for a number sequence from inside an AL
    /// [TryFunction] and creates it when the answer is "no". A BCL exception is not a
    /// NavBaseException, so it blew through TryInvokeAsync and aborted the test instead of
    /// returning false. Assert the runner's own try-function boundary — the same method the
    /// Cecil rewrite puts in front of NavApplicationObjectBase.TryInvokeAsync — now returns
    /// false for every missing-sequence operation.
    /// </summary>
    [Fact]
    public void MissingOperations_AreTrappedByAnAlTryFunction()
    {
        Assert.False(NumberSequencePatches.ALExists("Missing", true));

        Assert.False(TrapsLikeAlTryFunction(() => NumberSequencePatches.ALCurrent("Missing", true)));
        Assert.False(TrapsLikeAlTryFunction(() => NumberSequencePatches.ALNext("Missing", true)));
        Assert.False(TrapsLikeAlTryFunction(() => NumberSequencePatches.ALRestart("Missing", 1, true)));
        Assert.False(TrapsLikeAlTryFunction(() => NumberSequencePatches.ALRange("Missing", 1, true)));

        // Positive direction: the same boundary reports true when nothing failed, so a
        // false above means the error was trapped, not that the boundary always says false.
        Assert.True(TrapsLikeAlTryFunction(() => NumberSequencePatches.ALInsert("Missing", 7, 1, true)));
        Assert.True(TrapsLikeAlTryFunction(() => NumberSequencePatches.ALCurrent("Missing", true)));
        Assert.Equal(7, NumberSequencePatches.ALCurrent("Missing", true));
    }

    /// <summary>
    /// Runs <paramref name="body"/> through the runner's replacement for
    /// NavApplicationObjectBase.TryInvokeAsync, which is what an AL [TryFunction] compiles
    /// down to. Returns what AL would see.
    /// </summary>
    private static bool TrapsLikeAlTryFunction(Action body) =>
        BcRuntime.NavApplicationObjectBase_TryInvokeAsync(
            session: null,
            method: () => { body(); return ValueTask.CompletedTask; })
            .GetAwaiter().GetResult();

    /// <summary>
    /// Real BC's ALDeleteAsync issues DROP SEQUENCE IF EXISTS, so deleting a sequence that
    /// was never created succeeds. Documented in the decompiled DeleteAsync body.
    /// </summary>
    [Fact]
    public void Delete_MissingSequence_Succeeds()
    {
        Assert.False(NumberSequencePatches.ALExists("Missing", true));

        NumberSequencePatches.ALDelete("Missing", true);

        Assert.False(NumberSequencePatches.ALExists("Missing", true));
    }

    [Fact]
    public void Delete_RemovesOnlyTheSelectedScope()
    {
        NumberSequencePatches.ALInsert("Orders", 1, 1, true);
        NumberSequencePatches.ALInsert("Orders", 100, 10, false);

        NumberSequencePatches.ALDelete("orders", true);

        Assert.False(NumberSequencePatches.ALExists("Orders", true));
        Assert.Equal(100, NumberSequencePatches.ALNext("Orders", false));
    }

    [Fact]
    public void Insert_RejectsZeroIncrement_WithoutCreatingTheSequence()
    {
        Assert.Throws<NavALException>(
            () => NumberSequencePatches.ALInsert("Orders", 1, 0, true));

        Assert.False(NumberSequencePatches.ALExists("Orders", true));
    }

    [Fact]
    public void Overflow_FailsWithoutAdvancing()
    {
        NumberSequencePatches.ALInsert("Orders", long.MaxValue - 1, 2, true);
        Assert.Equal(long.MaxValue - 1, NumberSequencePatches.ALNext("Orders", true));

        var error = Assert.Throws<NavALException>(
            () => NumberSequencePatches.ALNext("Orders", true));

        Assert.Contains("range", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(long.MaxValue - 1, NumberSequencePatches.ALCurrent("Orders", true));
    }

    [Fact]
    public void ByRefSetterFailure_DoesNotAdvanceRange()
    {
        NumberSequencePatches.ALInsert("Orders", 10, 3, true);
#pragma warning disable CA1416 // The standalone runner executes BC's platform-annotated ByRef wrapper cross-platform.
        var increment = new ByRef<long>(() => 0, _ => throw new InvalidOperationException("setter failed"));
#pragma warning restore CA1416

        Assert.Throws<InvalidOperationException>(
            () => NumberSequencePatches.ALRange("Orders", 2, increment, true));

        Assert.Equal(10, NumberSequencePatches.ALNext("Orders", true));
    }

    [Fact]
    public void ConcurrentNext_AllocatesEachValueOnce()
    {
        NumberSequencePatches.ALInsert("Orders", 1, 1, true);
        var values = new ConcurrentBag<long>();

        Parallel.For(0, 100, _ => values.Add(NumberSequencePatches.ALNext("Orders", true)));

        Assert.Equal(Enumerable.Range(1, 100).Select(value => (long)value), values.Order());
    }

    [Fact]
    public void ConcurrentRange_AllocatesNonOverlappingBlocks()
    {
        NumberSequencePatches.ALInsert("Orders", 1, 2, true);
        var starts = new ConcurrentBag<long>();

        Parallel.For(0, 50, _ => starts.Add(NumberSequencePatches.ALRange("Orders", 3, true)));

        Assert.Equal(
            Enumerable.Range(0, 50).Select(index => 1L + (index * 6L)),
            starts.Order());
        Assert.Equal(299, NumberSequencePatches.ALCurrent("Orders", true));
    }

    [Fact]
    public void PerTestRollbackReset_DoesNotReturnAllocatedValues()
    {
        NumberSequencePatches.ALInsert("Orders", 1, 1, true);
        Assert.Equal(1, NumberSequencePatches.ALNext("Orders", true));

        RecordPatches.ResetPerTestState();

        Assert.True(NumberSequencePatches.ALExists("Orders", true));
        Assert.Equal(2, NumberSequencePatches.ALNext("Orders", true));
    }

    [Fact]
    public void ResetForNewExecution_ClearsAllScopes()
    {
        NumberSequencePatches.ALInsert("Orders", 1, 1, true);
        NumberSequencePatches.ALInsert("Orders", 1, 1, false);

        NumberSequencePatches.ResetForNewExecution();

        Assert.False(NumberSequencePatches.ALExists("Orders", true));
        Assert.False(NumberSequencePatches.ALExists("Orders", false));
    }
}
