using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class NumberSequenceCecilBindingTests
{
    private readonly BcEngineFixture _engine;

    public NumberSequenceCecilBindingTests(BcEngineFixture engine) => _engine = engine;

    private static Type NumberSequenceType => typeof(ITreeObject).Assembly.GetType(
        "Microsoft.Dynamics.Nav.Runtime.ALNumberSequence")!;

    private static readonly (string Name, Type ReturnType, Type[] Parameters)[] SynchronousEntryPoints =
    {
        ("ALInsert", typeof(void), new[] { typeof(string), typeof(long), typeof(long), typeof(bool) }),
        ("ALRestart", typeof(void), new[] { typeof(string), typeof(long), typeof(bool) }),
        ("ALExists", typeof(bool), new[] { typeof(string), typeof(bool) }),
        ("ALDelete", typeof(void), new[] { typeof(string), typeof(bool) }),
        ("ALNext", typeof(long), new[] { typeof(string), typeof(bool) }),
        ("ALCurrent", typeof(long), new[] { typeof(string), typeof(bool) }),
        ("ALRange", typeof(long), new[] { typeof(string), typeof(int), typeof(bool) }),
        ("ALRange", typeof(long), new[] { typeof(string), typeof(int), typeof(ByRef<long>), typeof(bool) }),
    };

    private static readonly (string Name, Type ReturnType, Type[] Parameters)[] AsynchronousEntryPoints =
    {
        ("ALInsertAsync", typeof(ValueTask), new[] { typeof(NavSession), typeof(string), typeof(long), typeof(long), typeof(bool) }),
        ("ALRestartAsync", typeof(ValueTask), new[] { typeof(NavSession), typeof(string), typeof(long), typeof(bool) }),
        ("ALExistsAsync", typeof(ValueTask<bool>), new[] { typeof(NavSession), typeof(string), typeof(bool) }),
        ("ALDeleteAsync", typeof(ValueTask), new[] { typeof(NavSession), typeof(string), typeof(bool) }),
        ("ALNextAsync", typeof(ValueTask<long>), new[] { typeof(NavSession), typeof(string), typeof(bool) }),
        ("ALCurrentAsync", typeof(ValueTask<long>), new[] { typeof(NavSession), typeof(string), typeof(bool) }),
        ("ALRangeAsync", typeof(ValueTask<long>), new[] { typeof(NavSession), typeof(string), typeof(int), typeof(bool) }),
        ("ALRangeAsync", typeof(ValueTask<long>), new[] { typeof(NavSession), typeof(string), typeof(int), typeof(ByRef<long>), typeof(bool) }),
    };

    private static IEnumerable<(string Name, Type ReturnType, Type[] Parameters)> EntryPoints =>
        SynchronousEntryPoints.Concat(AsynchronousEntryPoints);

    [Fact]
    public void AllAlEntryPointKeys_AreCecilOwned()
    {
        var expectedKeys = EntryPoints.Select(entry =>
            $"Microsoft.Dynamics.Nav.Runtime.ALNumberSequence::{entry.Name}/{entry.Parameters.Length}");

        Assert.All(expectedKeys, key => Assert.Contains(key, NclCecilRewrite.CecilOwned));
    }

    [SkippableFact]
    public void AllAlEntryPoints_AreCecilOwnedWithExactShapes()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        foreach (var expected in EntryPoints)
        {
            var method = NumberSequenceType.GetMethod(expected.Name,
                BindingFlags.Public | BindingFlags.Static, null, expected.Parameters, null);

            Assert.NotNull(method);
            Assert.Equal(expected.ReturnType, method!.ReturnType);
            Assert.Contains(NclCecilRewrite.Key(method), NclCecilRewrite.CecilOwned);
        }
    }

    [SkippableFact]
    public void SynchronousAlEntryPoints_InvokeTheInMemoryStore()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        NumberSequencePatches.ResetForNewExecution();
        try
        {
            Invoke("ALInsert",
                new[] { typeof(string), typeof(long), typeof(long), typeof(bool) },
                "Cecil", 10L, 3L, false);
            Assert.True((bool)Invoke("ALExists",
                new[] { typeof(string), typeof(bool) }, "cecil", false)!);
            Assert.Equal(10L, Invoke("ALNext",
                new[] { typeof(string), typeof(bool) }, "Cecil", false));

            long reportedIncrement = 0;
#pragma warning disable CA1416 // The standalone runner executes BC's platform-annotated ByRef wrapper cross-platform.
            var increment = new ByRef<long>(() => reportedIncrement, value => reportedIncrement = value);
#pragma warning restore CA1416
            Assert.Equal(13L, Invoke("ALRange",
                new[] { typeof(string), typeof(int), typeof(ByRef<long>), typeof(bool) },
                "Cecil", 2, increment, false));
            Assert.Equal(3L, reportedIncrement);
            Assert.Equal(16L, Invoke("ALCurrent",
                new[] { typeof(string), typeof(bool) }, "Cecil", false));

            Invoke("ALRestart",
                new[] { typeof(string), typeof(long), typeof(bool) }, "Cecil", 50L, false);
            Assert.Equal(50L, Invoke("ALRange",
                new[] { typeof(string), typeof(int), typeof(bool) }, "Cecil", 1, false));
            Invoke("ALDelete", new[] { typeof(string), typeof(bool) }, "Cecil", false);
            Assert.False((bool)Invoke("ALExists",
                new[] { typeof(string), typeof(bool) }, "Cecil", false)!);
        }
        finally
        {
            NumberSequencePatches.ResetForNewExecution();
        }
    }

    [SkippableFact]
    public async Task AsynchronousAlEntryPoints_InvokeTheSameInMemoryStore()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        NumberSequencePatches.ResetForNewExecution();
        try
        {
            await (ValueTask)Invoke("ALInsertAsync",
                new[] { typeof(NavSession), typeof(string), typeof(long), typeof(long), typeof(bool) },
                null, "AsyncCecil", 10L, 3L, false)!;
            Assert.True((bool)Invoke("ALExists",
                new[] { typeof(string), typeof(bool) }, "ASYNCCECIL", false)!);
            Assert.True(await (ValueTask<bool>)Invoke("ALExistsAsync",
                new[] { typeof(NavSession), typeof(string), typeof(bool) },
                null, "AsyncCecil", false)!);
            Assert.Equal(10L, await (ValueTask<long>)Invoke("ALNextAsync",
                new[] { typeof(NavSession), typeof(string), typeof(bool) },
                null, "AsyncCecil", false)!);

            long reportedIncrement = 0;
#pragma warning disable CA1416 // The standalone runner executes BC's platform-annotated ByRef wrapper cross-platform.
            var increment = new ByRef<long>(() => reportedIncrement, value => reportedIncrement = value);
#pragma warning restore CA1416
            Assert.Equal(13L, await (ValueTask<long>)Invoke("ALRangeAsync",
                new[] { typeof(NavSession), typeof(string), typeof(int), typeof(ByRef<long>), typeof(bool) },
                null, "AsyncCecil", 2, increment, false)!);
            Assert.Equal(3L, reportedIncrement);
            Assert.Equal(16L, await (ValueTask<long>)Invoke("ALCurrentAsync",
                new[] { typeof(NavSession), typeof(string), typeof(bool) },
                null, "AsyncCecil", false)!);

            await (ValueTask)Invoke("ALRestartAsync",
                new[] { typeof(NavSession), typeof(string), typeof(long), typeof(bool) },
                null, "AsyncCecil", 50L, false)!;
            Assert.Equal(50L, await (ValueTask<long>)Invoke("ALRangeAsync",
                new[] { typeof(NavSession), typeof(string), typeof(int), typeof(bool) },
                null, "AsyncCecil", 1, false)!);
            await (ValueTask)Invoke("ALDeleteAsync",
                new[] { typeof(NavSession), typeof(string), typeof(bool) },
                null, "AsyncCecil", false)!;
            Assert.False((bool)Invoke("ALExists",
                new[] { typeof(string), typeof(bool) }, "AsyncCecil", false)!);
        }
        finally
        {
            NumberSequencePatches.ResetForNewExecution();
        }
    }

    private static object? Invoke(string name, Type[] parameterTypes, params object?[] arguments)
    {
        var method = NumberSequenceType.GetMethod(name,
            BindingFlags.Public | BindingFlags.Static, null, parameterTypes, null);
        Assert.NotNull(method);
        return method!.Invoke(null, arguments);
    }
}
