// DependencyVersionMismatchException — thrown by DependencyResolver when a declared
// dependency IS present in one or more package-cache directories, but every available
// build is below the minimum version app.json declares.
//
// Distinct from MissingDependencyException (dep is absent everywhere) — the two need
// different advice: "add the package" vs. "get a newer build of the package you already
// have". Neither is a compile failure, so callers must not fold it into a generic
// "COMPILE-FAIL" line (see #2095).
//
// See: .claude/rules/loud-failures.md

namespace AlRunner.Infrastructure;

/// <summary>
/// Common shape for the two dependency-resolution exceptions that are provisioning
/// problems, not compile failures: <see cref="MissingDependencyException"/> (absent
/// everywhere) and <see cref="DependencyVersionMismatchException"/> (present, too old).
/// Lets a single catch clause in Program.cs recognize either without special-casing each
/// type by name.
/// </summary>
public interface IDependencyProvisioningDiagnostic
{
    /// <summary>
    /// One loud, self-contained message naming the problem and the exact fix. Detailed
    /// enough for an end user or an agent to act on without any additional context.
    /// </summary>
    string ToDetailedMessage(string? bcVersion = null);
}

/// <summary>
/// Thrown by DependencyResolver when a declared dependency app IS found in the package
/// cache, but every candidate version is below the minimum app.json declares. Carries the
/// dep identity + the too-old versions that were found so callers can emit ONE loud,
/// actionable "version gap" message (not a misleading "your code is wrong" message, and
/// not the "add the missing package" advice that fits MissingDependencyException instead).
/// </summary>
public sealed class DependencyVersionMismatchException : Exception, IDependencyProvisioningDiagnostic
{
    public string DepPublisher { get; }
    public string DepName { get; }
    public string DepMinVersion { get; }
    public Guid DepAppId { get; }
    public IReadOnlyList<string> SearchedDirs { get; }
    public string AvailableVersions { get; }
    /// <summary>
    /// Human-readable "A → B → C" chain leading to this dependency, or null when this
    /// dependency was a root of the resolve call (no chain to show — omit the whole
    /// segment rather than print an empty one).
    /// </summary>
    public string? DependencyStack { get; }

    public DependencyVersionMismatchException(
        string depPublisher,
        string depName,
        string depMinVersion,
        Guid depAppId,
        IReadOnlyList<string> searchedDirs,
        string availableVersions,
        string? dependencyStack = null)
        : base(BuildShortMessage(depPublisher, depName, depMinVersion, searchedDirs, availableVersions, dependencyStack))
    {
        DepPublisher = depPublisher;
        DepName = depName;
        DepMinVersion = depMinVersion;
        DepAppId = depAppId;
        SearchedDirs = searchedDirs;
        AvailableVersions = availableVersions;
        DependencyStack = dependencyStack;
    }

    private static string BuildShortMessage(
        string pub, string name, string minVersion, IReadOnlyList<string> dirs,
        string availableVersions, string? stack)
    {
        var msg = $"Dependency version too old: {pub}/{name} requires >= v{minVersion} " +
                  $"(found {availableVersions} — all below minimum v{minVersion}). " +
                  $"Searched: {string.Join(", ", dirs)}";
        // Omit the "Stack:" segment entirely for a root-level dependency (empty chain) —
        // printing it unconditionally left a dangling "Stack: " with nothing after it.
        if (!string.IsNullOrEmpty(stack))
            msg += $". Stack: {stack}";
        return msg;
    }

    /// <inheritdoc/>
    public string ToDetailedMessage(string? bcVersion = null)
    {
        var lines = new List<string>
        {
            "A dependency package is present in your package cache, but every available",
            "build is older than the version required by app.json.",
            "  This is a VERSION gap — your code is NOT the problem.",
            "",
            $"  Required: {DepPublisher}/{DepName} v{DepMinVersion} or newer",
        };
        if (DepAppId != Guid.Empty)
            lines.Add($"  App ID:  {DepAppId}");
        lines.Add($"  Available (all too old): {AvailableVersions}");
        if (DependencyStack is { Length: > 0 })
            lines.Add($"  Dependency chain: {DependencyStack}");
        lines.Add("");
        lines.Add("  Resolve it:");
        lines.Add("");
        lines.Add($"  Obtain a build of {DepPublisher}/{DepName} at or above v{DepMinVersion} and");
        lines.Add("  add it to your --package-cache <dir> (usually your project's .alpackages).");

        return string.Join(Environment.NewLine, lines);
    }
}
