using AlRunner.Provisioning;

namespace AlRunnerV2.Infrastructure;

/// <summary>
/// Verifies that the selected BC version's engine artifact closure is COMPLETE, and —
/// when asked — auto-resolves it in-process by downloading the missing pieces from the
/// public BC artifact CDN.
///
/// Why this exists: <see cref="BcArtifacts.SelectVersion"/> already fails loud when the
/// artifact root or the requested version dir is entirely absent. This class covers the
/// subtler case: the version dir EXISTS but is incomplete (e.g. a partial /service/ closure
/// download). Since the version-agnostic engine (StripBcAppClosureFromCopyLocal) now serves
/// the BC-app external closure from this dir at runtime, a partial closure fails deep in a
/// FileLoadException instead of at the surface — so we check it up front.
///
/// Policy (per the runner's "no silent download" rule): a missing piece produces ONE loud,
/// detailed message naming every missing file, its exact expected path, the precise manual
/// download command, AND a single one-command auto-resolve (`al-runner provision` /
/// `--auto-provision`). The runner never downloads unless the user opts in.
/// </summary>
public static class ProvisioningCheck
{
    // The engine DLLs the runner binds directly (must be present in the artifact dir so the
    // ALC resolver and the Cecil rewrite can load them).
    private static readonly string[] CoreEngineDlls =
    {
        "Microsoft.Dynamics.Nav.Ncl.dll",
        "Microsoft.Dynamics.Nav.Types.dll",
        "Microsoft.Dynamics.Nav.Common.dll",
        "Microsoft.Dynamics.Nav.Language.dll",
        "Microsoft.Dynamics.Nav.CodeAnalysis.dll",
    };

    // Sentinel of the BC-app external closure that the version-agnostic engine relies on
    // being served from the artifact dir (it was the exact DLL whose absence/skew produced
    // FileLoadException 0x80131621). Its presence signals the full /service/ closure landed.
    private const string ClosureSentinel = "Microsoft.Identity.ServiceEssentials.Core.dll";

    public sealed record Report(string Version, string ServiceTierDir, IReadOnlyList<string> MissingFiles)
    {
        public bool Ok => MissingFiles.Count == 0;

        /// <summary>
        /// One loud, self-contained message: names every missing file + its full path, the
        /// exact manual command to fetch them, and the one-command auto-resolve. Detailed
        /// enough for a human or an agent to fix by hand.
        /// </summary>
        public string ToDetailedMessage(string? projectPathForProvisionCmd = null)
        {
            var provisionTarget = projectPathForProvisionCmd is { Length: > 0 }
                ? $" \"{projectPathForProvisionCmd}\""
                : "";
            var lines = new List<string>
            {
                $"BC {Version} engine artifacts are incomplete — the runner will not auto-download.",
                $"Expected under: {ServiceTierDir}",
                "",
                "Missing:",
            };
            foreach (var f in MissingFiles)
                lines.Add($"  - {Path.Combine(ServiceTierDir, f)}");
            lines.Add("");
            lines.Add("Resolve it ONE of these ways:");
            lines.Add("");
            lines.Add("  (a) One command (recommended) — the runner downloads the missing pieces:");
            lines.Add($"        al-runner provision{provisionTarget}");
            lines.Add($"      or re-run your command with --auto-provision.");
            lines.Add("");
            lines.Add("  (b) Manually — fetch the full service-tier closure for this version:");
            lines.Add($"        dotnet run --project tools/DownloadArtifacts -- service-tier {Version} \"{ServiceTierDir}\"");
            lines.Add("");
            lines.Add("  (c) Point the runner at an existing artifact dir with --artifact-path <dir>,");
            lines.Add("      or select a different cached version with --bc-version <ver>.");
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Check whether the given version's artifact <paramref name="serviceTierDir"/> holds a
    /// complete engine closure. Never throws; returns a <see cref="Report"/> listing what
    /// (if anything) is missing.
    /// </summary>
    public static Report Check(string version, string serviceTierDir)
    {
        var missing = new List<string>();
        if (!Directory.Exists(serviceTierDir))
        {
            // The whole dir is gone — report every required file as missing.
            missing.AddRange(CoreEngineDlls);
            missing.Add(ClosureSentinel);
            return new Report(version, serviceTierDir, missing);
        }
        foreach (var dll in CoreEngineDlls)
            if (!File.Exists(Path.Combine(serviceTierDir, dll)))
                missing.Add(dll);
        if (!File.Exists(Path.Combine(serviceTierDir, ClosureSentinel)))
            missing.Add(ClosureSentinel);
        return new Report(version, serviceTierDir, missing);
    }

    /// <summary>
    /// Download the engine service-tier closure for <paramref name="version"/> into
    /// <paramref name="serviceTierDir"/> (the full /service/ closure — the same set the
    /// manual `service-tier` command fetches). Returns true on success. This is the
    /// opt-in auto-resolve; callers gate it behind `al-runner provision` / `--auto-provision`.
    /// </summary>
    public static bool AutoProvision(string version, string serviceTierDir, Action<string>? log = null)
    {
        var logf = log ?? Console.Error.WriteLine;
        logf($"[provision] downloading BC {version} engine service-tier closure → {serviceTierDir}");
        var rc = ArtifactDownloader.ServiceTier(version, serviceTierDir, logf);
        if (rc != 0)
        {
            logf($"[provision] download failed (exit {rc}). See messages above.");
            return false;
        }
        var after = Check(version, serviceTierDir);
        if (!after.Ok)
        {
            logf($"[provision] still incomplete after download; missing: {string.Join(", ", after.MissingFiles)}");
            return false;
        }
        logf($"[provision] BC {version} engine artifacts complete.");
        return true;
    }
}
