namespace AlRunner.Infrastructure;

/// <summary>
/// Two counters that are always reported together and where <c>Part</c> can never exceed
/// <c>Total</c> — calls made / calls that did work, MethodLoad events / BC MethodLoad events.
/// Both halves live in one <see cref="long"/> (Total high 32 bits, Part low 32), so every
/// write is a single Interlocked op and <see cref="Read"/> is a single load: the pair it
/// returns existed at one instant, which two separate loads of two separate counters cannot
/// promise (#3169, the #3025 shape). No lock: readers run on a ProcessExit handler and the
/// JIT callback thread; writers run on every AL statement. The halves are NOT independent
/// counters: one Interlocked.Add over the packed word means they can never disagree, but a
/// carry out of Part would propagate into Total rather than wrapping Part alone. That needs
/// ~4.29e9 (2^32) increments in one process, which is unreachable here, and it is the
/// deliberate trade for not allocating per AL statement (#3170's CompareExchange'd record
/// does). These are diagnostics, not meters.
/// </summary>
internal sealed class CountPair
{
    private const int Shift = 32;
    private const long PartMask = 0xFFFFFFFFL;

    private long _packed;

    public readonly record struct Snapshot(long Total, long Part);

    public Snapshot IncrementTotal() => Add(1, 0);
    public Snapshot IncrementPart() => Add(0, 1);

    /// <summary>One atomic add to both halves, so a caller that must bump Total and Part for
    /// the same event publishes them together rather than one after the other.</summary>
    public Snapshot Add(long total, long part)
        => Unpack(Interlocked.Add(ref _packed, (total << Shift) | (part & PartMask)));

    public void ClearPart() => Interlocked.And(ref _packed, ~PartMask);

    public Snapshot Read() => Unpack(Volatile.Read(ref _packed));

    private static Snapshot Unpack(long packed)
        => new((packed >> Shift) & PartMask, packed & PartMask);
}
