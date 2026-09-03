using System.Reflection;

namespace AlRunner.Infrastructure;

/// <summary>
/// The version string al-runner reports about itself.
/// </summary>
internal static class RunnerVersion
{
    /// <summary>
    /// The build's own version, prerelease suffix included.
    ///
    /// <c>Assembly.GetName().Version</c> is a numeric quad and cannot carry a prerelease
    /// suffix — .NET drops it — so reading it printed "2.0.0.0" for a build whose
    /// <c>&lt;Version&gt;</c> is 2.0.0-preview.1, throwing away exactly the part that says which
    /// build someone is holding. <c>AssemblyInformationalVersion</c> keeps it.
    ///
    /// SemVer build metadata — everything after a '+' — is stripped. The released package is
    /// packed with <c>IncludeSourceRevisionInInformationalVersion=true</c>
    /// (<c>publish.yml</c>), which appends the 40-character commit sha, and a released
    /// version number already identifies its build. Keeping the sha would put 40 characters
    /// of noise into a line that <c>--guide</c> asks coding agents to paste into gap reports,
    /// and would make the same source print differently depending on how it was packed.
    ///
    /// Falls back to the numeric version for an assembly carrying no informational attribute,
    /// and to "unknown" for one carrying neither.
    /// </summary>
    internal static string Informational(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            var trimmed = plus >= 0 ? informational[..plus] : informational;
            if (!string.IsNullOrWhiteSpace(trimmed)) return trimmed;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
