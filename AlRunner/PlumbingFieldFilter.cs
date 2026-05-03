namespace AlRunner;

/// <summary>
/// Shared filter for runtime "plumbing" fields injected by the AL→C#
/// transpiler — variables whose names start with code points that the
/// transpiler reserves for its own bookkeeping (e.g. `β`-prefixed temps).
/// Both ValueCaptureInjector and IterationInjector use this filter to
/// avoid emitting capture calls for runtime-internal identifiers.
///
/// Extending: add new prefix code points here. Both injectors get the
/// updated filter automatically.
/// </summary>
internal static class PlumbingFieldFilter
{
    /// <summary>
    /// Returns true when the identifier starts with a code point reserved
    /// for AL→C# transpiler bookkeeping. Matches β (U+03B2) and γ (U+03B3).
    /// </summary>
    public static bool IsPlumbingField(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return false;
        var firstChar = identifier[0];
        return firstChar == 'β' || firstChar == 'γ'; // β, γ
    }
}
