using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Runtime;

/// <summary>
/// Replacement for <c>NavVersion</c> in standalone mode.
/// BC lowers AL <c>Version</c> variables to <c>NavVersion</c>; the real type
/// reaches into the BC service-tier environment. This mock stores
/// major/minor/build/revision as plain integers and reproduces the
/// AL-callable API surface: <c>ALCreate</c>, <c>ALMajor</c>, <c>ALMinor</c>,
/// <c>ALBuild</c>, <c>ALRevision</c>, <c>ALToText</c>.
/// </summary>
public struct MockVersion
{
    private int _major;
    private int _minor;
    private int _build;
    private int _revision;

    /// <summary>
    /// BC lowers <c>Version.Create(major, minor, build, revision)</c>
    /// to <c>NavVersion.ALCreate(session, major, minor, build, revision)</c>.
    /// Returns a new <see cref="MockVersion"/> populated with the given components.
    /// </summary>
    public static MockVersion ALCreate(
        object? session,
        object? major, object? minor, object? build, object? revision)
    {
        var result = new MockVersion();
        result._major    = AlCompat.NavIndirectValueToInt32(major);
        result._minor    = AlCompat.NavIndirectValueToInt32(minor);
        result._build    = AlCompat.NavIndirectValueToInt32(build);
        result._revision = AlCompat.NavIndirectValueToInt32(revision);
        return result;
    }

    /// <summary>Overload without session for older BC compiler variants.</summary>
    public static MockVersion ALCreate(
        object? major, object? minor, object? build, object? revision)
        => ALCreate(null, major, minor, build, revision);

    /// <summary>
    /// BC lowers <c>Version.Create(Text)</c> to <c>NavVersion.ALCreate(text)</c>.
    /// Parses a dotted version string (e.g. <c>"25.1.30000.12345"</c>) into its
    /// major/minor/build/revision components.
    /// </summary>
    public static MockVersion ALCreate(NavText versionText)
    {
        var text = versionText.ToString();
        var parts = text.Split('.');
        var result = new MockVersion();
        if (parts.Length > 0 && int.TryParse(parts[0], out var maj)) result._major    = maj;
        if (parts.Length > 1 && int.TryParse(parts[1], out var min)) result._minor    = min;
        if (parts.Length > 2 && int.TryParse(parts[2], out var bld)) result._build    = bld;
        if (parts.Length > 3 && int.TryParse(parts[3], out var rev)) result._revision = rev;
        return result;
    }

    /// <summary>
    /// Default (zero) instance — BC emits <c>NavVersion.Default</c> for uninitialized
    /// <c>Version</c> variables (0.0.0.0).
    /// </summary>
    public static MockVersion Default => new MockVersion();

    /// <summary>
    /// Implicit conversion from the real <c>NavVersion</c> BC runtime type to
    /// <c>MockVersion</c>. BC may assign a real <c>NavVersion</c> (e.g. from
    /// <c>ModuleInfo.AppVersion</c>) to a variable whose declared type was rewritten
    /// to <c>MockVersion</c>. Reads the int properties on the real type; falls back
    /// to zero on any exception (service-tier types unavailable standalone).
    /// </summary>
    public static implicit operator MockVersion(Microsoft.Dynamics.Nav.Runtime.NavVersion nav)
    {
        var result = new MockVersion();
        try
        {
            result._major    = nav.ALMajor;
            result._minor    = nav.ALMinor;
            result._build    = nav.ALBuild;
            result._revision = nav.ALRevision;
        }
        catch { /* NavVersion properties may not work without the service tier */ }
        return result;
    }

    /// <summary>BC lowers <c>ver.Major()</c> to the int property <c>ver.ALMajor</c>.</summary>
    public int ALMajor => _major;

    /// <summary>BC lowers <c>ver.Minor()</c> to the int property <c>ver.ALMinor</c>.</summary>
    public int ALMinor => _minor;

    /// <summary>BC lowers <c>ver.Build()</c> to the int property <c>ver.ALBuild</c>.</summary>
    public int ALBuild => _build;

    /// <summary>BC lowers <c>ver.Revision()</c> to the int property <c>ver.ALRevision</c>.</summary>
    public int ALRevision => _revision;

    /// <summary>
    /// BC lowers <c>ver.ToText()</c> to <c>ver.ALToText(session)</c> or <c>ver.ALToText()</c>.
    /// Returns <c>"Major.Minor.Build.Revision"</c>.
    /// </summary>
    public NavText ALToText(object? session = null)
        => new NavText($"{_major}.{_minor}.{_build}.{_revision}");

    // ── Comparison / equality operators ───────────────────────────────────────
    // BC lowers AL's v1 = v2, v1 > v2 etc. to C# operator expressions on the
    // emitted type. Provide MockVersion vs MockVersion overloads that compare
    // lexicographically on (major, minor, build, revision).

    private long ToSortKey()
        => ((long)_major << 48) | ((long)(_minor & 0xFFFF) << 32) | ((long)(_build & 0xFFFF) << 16) | (long)(_revision & 0xFFFF);

    public static bool operator ==(MockVersion a, MockVersion b)
        => a._major == b._major && a._minor == b._minor && a._build == b._build && a._revision == b._revision;

    public static bool operator !=(MockVersion a, MockVersion b) => !(a == b);

    public static bool operator <(MockVersion a, MockVersion b) => a.ToSortKey() < b.ToSortKey();
    public static bool operator >(MockVersion a, MockVersion b) => a.ToSortKey() > b.ToSortKey();
    public static bool operator <=(MockVersion a, MockVersion b) => a.ToSortKey() <= b.ToSortKey();
    public static bool operator >=(MockVersion a, MockVersion b) => a.ToSortKey() >= b.ToSortKey();

    public override bool Equals(object? obj) => obj is MockVersion other && this == other;
    public override int GetHashCode() => ToSortKey().GetHashCode();
}
