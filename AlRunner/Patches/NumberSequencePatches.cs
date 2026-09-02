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
///
/// EXCEPTION TYPE IS PART OF THE CONTRACT (AlRunner#2311). Every failure below raises
/// <c>NavALException</c>, the trappable AL error real BC raises, not a BCL exception.
/// The decompiled <c>ALNumberSequence.ALCurrentAsync</c> ends with:
/// <code>
///   if (obj == null)
///       throw new NavALException(string.Format(session.Culture, Lang.NumberSequenceDoesNotExist, name));
/// </code>
/// and ALNextAsync / ALRestartAsync / RangeAsync raise the same NavALException from their
/// <c>catch (NavSqlException ex) when (IsMissingSequence(ex))</c> handlers, ALInsertAsync
/// from <c>catch (NavSqlException ex) when (ex.ErrorNumber == 2714)</c>. NavALException
/// derives from NavBaseException with UntrappableError false, and
/// <c>NavApplicationObjectBase.TryInvokeAsync</c> traps exactly
/// <c>catch (NavBaseException ex) when (!ex.UntrappableError)</c> — so on a real tier an AL
/// [TryFunction] around any of these sees false and carries on. That is load-bearing: Base App
/// codeunit "Sequence No. Mgt." wraps Current/Next/Range in [TryFunction]s and CREATES the
/// sequence when one returns false, and the No. Series code does the same for a
/// sequence-backed No. Series Line. A BCL exception here is not "a different message" — it
/// blows through the try boundary and aborts AL that real BC never aborts.
/// This is not a silent default (.claude/rules/loud-failures.md): the error still happens,
/// still names the sequence, and is still readable from AL via GetLastErrorText. Only the
/// type changes, from one AL cannot trap to the one BC actually throws.
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
        // Real BC issues CREATE SEQUENCE ... INCREMENT BY 0, which SQL Server rejects; the
        // NavSqlException surfaces to AL as a trappable error (ALInsertAsync only converts
        // error 2714, so the rest propagate as NavSqlException, itself a NavBaseException).
        // Trappable either way, so raise the AL error rather than a BCL one.
        if (increment == 0)
            throw AlError($"Number sequence '{name}' cannot be created with an increment of zero.");

        lock (_sync)
        {
            var key = (name, companySpecific);
            if (!_sequences.TryAdd(key, new SequenceState(seed, increment)))
                throw AlError($"Number sequence '{name}' already exists.");
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

    /// <summary>
    /// Deleting a sequence that does not exist SUCCEEDS, matching real BC. ALDeleteAsync
    /// delegates to DeleteAsync, whose whole body is
    /// <c>"DROP SEQUENCE IF EXISTS dbo.[" + sequenceName + "]"</c> — no missing-sequence
    /// branch and no NavALException anywhere on the path. Returning without an error is
    /// therefore observably equivalent, not a swallowed failure.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ALDelete(string name, bool companySpecific)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
            _sequences.Remove((name, companySpecific));
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
        // Real BC hands the count straight to sp_sequence_get_range, which raises a SQL
        // error for a non-positive range size. RangeAsync only converts the missing-sequence
        // numbers, so the rest reach AL as NavSqlException — trappable, like this.
        if (count <= 0)
            throw AlError($"Number sequence '{name}' cannot reserve a range of {count} value(s).");

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

    /// <summary>
    /// BC's own wording is Lang.NumberSequenceDoesNotExist formatted with the sequence name.
    /// The resource text is not reproduced here — issue #2049 permits runner-specific error
    /// text — but the name is kept in the message because AL that reads GetLastErrorText
    /// after a trapped failure needs to know which sequence was missing.
    /// </summary>
    private static Exception MissingSequence(string name) =>
        AlError($"Number sequence '{name}' does not exist.");

    /// <summary>
    /// The one place a NumberSequence failure becomes an exception. NavALException is BC's
    /// trappable AL error: NavBaseException with UntrappableError false, which is exactly
    /// what NavApplicationObjectBase.TryInvoke/TryInvokeAsync catch. See the type comment.
    /// </summary>
    private static Exception AlError(string message) =>
        new Microsoft.Dynamics.Nav.Types.Exceptions.NavALException(message);

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

    /// <summary>
    /// Exhausting a sequence is a SQL error on a real tier (the BIGINT sequence runs past its
    /// maximum), which reaches AL as a trappable NavSqlException. Trappable here too.
    /// </summary>
    private static Exception OutOfRange(string name, OverflowException inner) =>
        new Microsoft.Dynamics.Nav.Types.Exceptions.NavALException(
            $"Number sequence '{name}' moved outside the supported BigInteger range.", inner);
}
