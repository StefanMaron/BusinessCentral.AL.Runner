// MissingDependencyException — thrown by DependencyResolver when a declared dependency
// cannot be found in any package-cache directory.
//
// Distinct from DependencyLoadException (dep IS found but fails to compile/load).
// This is always a PROVISIONING gap — the fix is to add the missing package to the cache,
// NOT to change user code. The exception carries the full dep identity + searched dirs so
// Program.cs can emit ONE loud, actionable message that names the missing package and the
// exact one-command fix.
//
// See: .claude/rules/loud-failures.md

namespace AlRunner.Infrastructure;

/// <summary>
/// Thrown by DependencyResolver when a declared dependency app cannot be found in any
/// package-cache directory. Carries the full dep identity + searched dirs so callers can
/// emit ONE loud, actionable "provisioning gap" message (not a misleading "your code is wrong"
/// message).
/// </summary>
public sealed class MissingDependencyException : Exception, IDependencyProvisioningDiagnostic
{
    public string DepPublisher { get; }
    public string DepName { get; }
    public string DepVersion { get; }
    public Guid DepAppId { get; }
    public IReadOnlyList<string> SearchedDirs { get; }
    public string? DependencyStack { get; }

    public MissingDependencyException(
        string depPublisher,
        string depName,
        string depVersion,
        Guid depAppId,
        IReadOnlyList<string> searchedDirs,
        string? dependencyStack = null)
        : base(BuildShortMessage(depPublisher, depName, depVersion, depAppId, searchedDirs))
    {
        DepPublisher = depPublisher;
        DepName = depName;
        DepVersion = depVersion;
        DepAppId = depAppId;
        SearchedDirs = searchedDirs;
        DependencyStack = dependencyStack;
    }

    private static string BuildShortMessage(
        string pub, string name, string ver, Guid id, IReadOnlyList<string> dirs)
        => $"Dependency not found: {pub}/{name} v{ver} (id={id}). " +
           $"Searched: {string.Join(", ", dirs)}";

    /// <summary>
    /// One loud, self-contained message: names the missing dependency, where it was
    /// searched, and the exact fix commands. Detailed enough for an end user or an
    /// agent to act on without any additional context.
    /// </summary>
    public string ToDetailedMessage(string? bcVersion = null)
    {
        var versionHint = bcVersion ?? "28.x";
        bool isMicrosoft = string.Equals(DepPublisher, "Microsoft", StringComparison.OrdinalIgnoreCase);

        var lines = new List<string>
        {
            "A required dependency package is missing from your package cache.",
            "  This is a PROVISIONING gap — your code is NOT the problem.",
            "",
            $"  Missing: {DepPublisher}/{DepName} v{DepVersion}",
        };
        if (DepAppId != Guid.Empty)
            lines.Add($"  App ID:  {DepAppId}");
        if (SearchedDirs.Count > 0)
            lines.Add($"  Searched: {string.Join(", ", SearchedDirs.Select(d => $"\"{d}\""))}");
        if (DependencyStack is { Length: > 0 })
            lines.Add($"  Dependency chain: {DependencyStack}");
        lines.Add("");
        lines.Add("  Resolve it:");
        lines.Add("");
        if (isMicrosoft)
        {
            lines.Add("  (a) One command (recommended) — provisions all missing Microsoft artifacts:");
            lines.Add("        al-runner provision");
            lines.Add("      or re-run with --auto-provision.");
            lines.Add("");
            lines.Add("  (b) Force-download Microsoft test-toolkit apps only:");
            lines.Add($"        al-runner provision --test-apps --bc-version {versionHint}");
            lines.Add("");
            lines.Add("  (c) Force-download Microsoft platform apps only:");
            lines.Add($"        al-runner provision --platform-apps --bc-version {versionHint}");
        }
        else
        {
            // Third-party (non-Microsoft) dep: the runner cannot download this from
            // anywhere, so the fix is entirely on the reader — name the flag concretely
            // (with an example dir) so an agent that has never used this tool can act
            // without guessing what "--package-cache" means or where that dir usually is.
            lines.Add("  Add the missing package to your --package-cache <dir> (usually your");
            lines.Add("  project's .alpackages). Verify the package version satisfies the");
            lines.Add("  minimum declared in app.json.");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
