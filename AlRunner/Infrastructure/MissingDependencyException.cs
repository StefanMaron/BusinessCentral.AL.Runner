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
        // Issue #2236: "Microsoft-published" alone does not mean `provision`/
        // `--auto-provision` can ever fetch it — those two commands (and their narrower
        // --platform-apps/--test-apps forms) only ever download the w1 (worldwide)
        // artifact's curated app set. A Microsoft app outside that set (most commonly a
        // country-localization app like "IRS Forms") is not something a repeated
        // `al-runner provision` will ever produce, no matter how many times it is re-run
        // — the repo owner hit exactly that: byte-identical advice, twice, no progress.
        bool isKnownW1Downloadable = isMicrosoft
            && AlRunner.Infrastructure.ProvisioningCheck.IsKnownW1DownloadableAppName(DepName);

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
        if (isKnownW1Downloadable)
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
        else if (isMicrosoft)
        {
            // Not one of the apps the w1 download path can ever produce. Re-running
            // `provision`/`--auto-provision` cannot fix this — say so, and name the
            // likely reason instead of repeating advice already proven not to work.
            lines.Add($"  '{DepName}' is not part of the w1 (worldwide) artifact set that");
            lines.Add("  `al-runner provision` / `--auto-provision` download — running either");
            lines.Add("  again changes nothing here, no matter how many times you try.");
            lines.Add("");
            lines.Add("  This usually means it is a country/regional localization app (Microsoft");
            lines.Add("  ships those in a separate, per-country artifact channel, not w1).");
            lines.Add("");
            lines.Add("  (a) If you know the target country, re-run with --country to fetch the");
            lines.Add("      localized artifact set instead of w1, e.g.:");
            lines.Add($"        al-runner --auto-provision --country us --bc-version {versionHint} <bundle>");
            lines.Add("");
            lines.Add("  (b) Otherwise, add the package to your --package-cache <dir> by hand");
            lines.Add("      (usually your project's .alpackages).");
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
