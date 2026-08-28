using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

/// <summary>
/// Process-local backing store for AL's NumberSequence data type. Values are shared for
/// one runner execution and cleared explicitly at the CLI/watch/server execution boundary.
/// Like BC's SQL sequence, Current initially exposes the configured start value, Next first
/// returns that start and then applies the increment, Restart supplies the next value, Range
/// atomically reserves values, and allocations survive AL transaction rollback. Every invalid
/// operation still fails, although issue #2049 explicitly permits runner-specific error text.
/// The (name, CompanySpecific) key intentionally models only the runner's single-company scope;
/// database persistence and company switching remain outside that issue's initial contract.
/// </summary>
public static class NumberSequencePatches
{
    private sealed class SequenceState
    {
        public SequenceState(long seed, long increment)
        {
            Current = seed;
            Increment = increment;
        }

        public long Current { get; set; }
        public long Increment { get; }
        public bool HasAllocated { get; set; }
    }

    private sealed class SequenceKeyComparer : IEqualityComparer<(string Name, bool CompanySpecific)>
    {
        public bool Equals(
            (string Name, bool CompanySpecific) left,
            (string Name, bool CompanySpecific) right) =>
            left.CompanySpecific == right.CompanySpecific &&
            StringComparer.OrdinalIgnoreCase.Equals(left.Name, right.Name);

        public int GetHashCode((string Name, bool CompanySpecific) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name), key.CompanySpecific);
    }

    private static readonly object _sync = new();
    private static readonly Dictionary<(string Name, bool CompanySpecific), SequenceState> _sequences =
        new(new SequenceKeyComparer());

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ALInsert(string name, long seed, long increment, bool companySpecific)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (increment == 0)
            throw new ArgumentOutOfRangeException(nameof(increment), "Number sequence increment cannot be zero.");

        lock (_sync)
        {
            var key = (name, companySpecific);
            if (!_sequences.TryAdd(key, new SequenceState(seed, increment)))
                throw new InvalidOperationException($"Number sequence '{name}' already exists.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALExists(string name, bool companySpecific)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
            return _sequences.ContainsKey((name, companySpecific));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long ALCurrent(string name, bool companySpecific)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
            return GetExisting(name, companySpecific).Current;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long ALNext(string name, bool companySpecific)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
        {
            var state = GetExisting(name, companySpecific);
            var next = state.HasAllocated
                ? AddChecked(name, state.Current, state.Increment)
                : state.Current;
            state.Current = next;
            state.HasAllocated = true;
            return next;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ALRestart(string name, long seed, bool companySpecific)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
        {
            var state = GetExisting(name, companySpecific);
            state.Current = seed;
            state.HasAllocated = false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ALDelete(string name, bool companySpecific)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
        {
            if (!_sequences.Remove((name, companySpecific)))
                throw MissingSequence(name);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long ALRange(string name, int count, bool companySpecific) =>
        ReserveRange(name, count, incrementOutput: null, companySpecific);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long ALRange(
        string name,
        int count,
        ByRef<long> increment,
        bool companySpecific)
    {
        ArgumentNullException.ThrowIfNull(increment);
        return ReserveRange(name, count, increment, companySpecific);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask ALInsertAsync(
        NavSession _, string name, long seed, long increment, bool companySpecific)
    {
        ALInsert(name, seed, increment, companySpecific);
        return ValueTask.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask ALRestartAsync(
        NavSession _, string name, long seed, bool companySpecific)
    {
        ALRestart(name, seed, companySpecific);
        return ValueTask.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<bool> ALExistsAsync(NavSession _, string name, bool companySpecific) =>
        ValueTask.FromResult(ALExists(name, companySpecific));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask ALDeleteAsync(NavSession _, string name, bool companySpecific)
    {
        ALDelete(name, companySpecific);
        return ValueTask.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<long> ALNextAsync(NavSession _, string name, bool companySpecific) =>
        ValueTask.FromResult(ALNext(name, companySpecific));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<long> ALCurrentAsync(NavSession _, string name, bool companySpecific) =>
        ValueTask.FromResult(ALCurrent(name, companySpecific));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<long> ALRangeAsync(
        NavSession _, string name, int count, bool companySpecific) =>
        ValueTask.FromResult(ALRange(name, count, companySpecific));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<long> ALRangeAsync(
        NavSession _, string name, int count, ByRef<long> increment, bool companySpecific) =>
        ValueTask.FromResult(ALRange(name, count, increment, companySpecific));

    public static void ResetForNewExecution()
    {
        lock (_sync)
            _sequences.Clear();
    }

    private static long ReserveRange(
        string name,
        int count,
        ByRef<long>? incrementOutput,
        bool companySpecific)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Number sequence range count must be greater than zero.");

        lock (_sync)
        {
            var state = GetExisting(name, companySpecific);
            var first = state.HasAllocated
                ? AddChecked(name, state.Current, state.Increment)
                : state.Current;
            var last = AddChecked(name, first, MultiplyChecked(name, state.Increment, count - 1L));

            // Ncl's ByRef setter is a runtime-generated write to the caller's variable.
            // Keep it inside the reservation lock so failed writeback leaves the sequence
            // untouched and no concurrent allocation can interleave before state commits.
            if (incrementOutput != null)
                incrementOutput.Value = state.Increment;
            state.Current = last;
            state.HasAllocated = true;
            return first;
        }
    }

    private static SequenceState GetExisting(string name, bool companySpecific)
    {
        if (_sequences.TryGetValue((name, companySpecific), out var state))
            return state;
        throw MissingSequence(name);
    }

    private static InvalidOperationException MissingSequence(string name) =>
        new($"Number sequence '{name}' does not exist.");

    private static long AddChecked(string name, long left, long right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException exception)
        {
            throw OutOfRange(name, exception);
        }
    }

    private static long MultiplyChecked(string name, long left, long right)
    {
        try
        {
            return checked(left * right);
        }
        catch (OverflowException exception)
        {
            throw OutOfRange(name, exception);
        }
    }

    private static InvalidOperationException OutOfRange(string name, OverflowException inner) =>
        new($"Number sequence '{name}' moved outside the supported BigInteger range.", inner);
}
