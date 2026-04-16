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
}
