namespace AlRunner.Runtime;

/// <summary>
/// In-memory replacement for <c>ALNumberSequence</c>. The real BC runtime
/// talks to the service tier through <c>NavSession.ALExistsAsync</c>, which
/// hits a null-reference under standalone mode.
///
/// MockNumberSequence keeps a per-process dictionary of sequence names to
/// their current value. Enough to let test code check Exists, Insert a new
/// sequence, and pull monotonically-increasing Next values.
/// </summary>
public static class MockNumberSequence
{
    private static readonly Dictionary<string, long> _sequences = new(StringComparer.OrdinalIgnoreCase);

    public static void Reset()
    {
        _sequences.Clear();
    }

    public static bool ALExists(string name) => _sequences.ContainsKey(name);
    public static bool ALExists(string name, bool companySpecific) => _sequences.ContainsKey(name);

    public static void ALInsert(string name)
    {
        if (!_sequences.ContainsKey(name))
            _sequences[name] = 0;
    }

    public static void ALInsert(string name, long startValue)
    {
        _sequences[name] = startValue;
    }

    public static void ALInsert(string name, long startValue, long increment)
    {
        _sequences[name] = startValue;
    }

    public static void ALInsert(string name, long startValue, long increment, bool companySpecific)
    {
        _sequences[name] = startValue;
    }

    /// <summary>
    /// AL: NumberSequence.Range(Name, Count [, CompanySpecific]) → BigInteger
    /// Reserves the next Count values and returns the first value of the range.
    /// Advances the sequence counter by Count.
    /// </summary>
    public static long ALRange(string name, long count)
    {
        if (!_sequences.ContainsKey(name))
            _sequences[name] = 0;
        var first = _sequences[name] + 1;
        _sequences[name] += count;
        return first;
    }

    public static long ALRange(string name, long count, bool companySpecific)
        => ALRange(name, count);

    public static long ALNext(string name)
    {
        if (!_sequences.ContainsKey(name))
            _sequences[name] = 0;
        _sequences[name] += 1;
        return _sequences[name];
    }

    public static long ALCurrent(string name)
    {
        return _sequences.TryGetValue(name, out var v) ? v : 0;
    }

    public static void ALRestart(string name)
    {
        _sequences[name] = 0;
    }

    public static void ALRestart(string name, long startValue)
    {
        _sequences[name] = startValue;
    }

    public static void ALDelete(string name)
    {
        _sequences.Remove(name);
    }
}
