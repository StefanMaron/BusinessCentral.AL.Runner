namespace AlRunner.Infrastructure;

/// <summary>
/// What one BC artifact version directory actually is. Four states, not two — see
/// <c>.claude/rules/guards-need-a-third-state.md</c>.
///
/// <para><see cref="Absent"/> and <see cref="Unreadable"/> are deliberately distinct.
/// <see cref="ProvisioningCheck.Check"/> reports both as "all six files missing", but the
/// remedies differ: an absent version is provisioned, while a path blocked by a file needs
/// the obstruction removed first, and `provision` cannot do that.</para>
/// </summary>
public enum ArtifactDirStatus
{
    /// <summary>The engine closure is complete. The only usable state.</summary>
    Complete,

    /// <summary>
    /// Nothing is there. A legitimate state, never an error by itself — the version was
    /// simply never provisioned.
    /// </summary>
    Absent,

    /// <summary>
    /// The directory exists and something wrote to it, but the engine closure is short.
    /// This is the #3878 shape: it can carry <c>Ncl.dll</c> and pass every "is this a
    /// service-tier directory" check while still failing deep inside an assembly load.
    /// </summary>
    Partial,

    /// <summary>
    /// The third state: the directory could not be measured at all. Distinct from
    /// <see cref="Complete"/> (which would be a false green) and from
    /// <see cref="Absent"/> (which would send the reader to the wrong remedy).
    /// </summary>
    Unreadable,
}

/// <summary>
/// Classifies a BC artifact version directory before a consumer opens anything in it.
///
/// <para><b>Why this exists (#3878).</b> A partially-provisioned directory used to be
/// indistinguishable from a complete one until something failed deep inside an assembly
/// load, at which point the failure read as a code fault. It cost three separate
/// diagnoses of one cause in a single session: a `load_assembly` failure traced to an
/// absent <c>Framework.UI.dll</c>, a feasibility measurement that contributed zero
/// packages, and a resolver probe that died with exit 134 on an absent
/// <c>Mono.Cecil.dll</c>.</para>
///
/// <para><b>What distinguishes complete from partial, and why it is not a DLL count.</b>
/// 501 is one version's number, not a constant, so a threshold would be fragile in both
/// directions. The predicate is the one
/// <see cref="ProvisioningCheck.Check"/> already uses and that the download path already
/// re-checks after every fetch: the five core engine DLLs plus the closure sentinel
/// <c>Microsoft.Identity.ServiceEssentials.Core.dll</c>. Measured against the two broken
/// directories on the reporting box, that predicate already separates both from every
/// healthy one — 27.5.46862.48827 is short exactly the sentinel, and 28.0.46665.54452 is
/// short all six. So no new on-disk marker is needed and none is written: the signal was
/// already there, unread by anything outside the runner's own startup path.</para>
///
/// <para>The consequence worth stating plainly: because this reads the closure rather
/// than a marker, it classifies directories <b>already on disk</b> — including the two
/// that motivated the issue. A completion marker would have applied only to directories
/// provisioned after it shipped.</para>
/// </summary>
public static class ArtifactDirState
{
    /// <summary>
    /// One directory's verdict: its <see cref="Status"/>, the closure files absent from it,
    /// whether it carries the engine entrypoint (what makes the two broken shapes different
    /// from each other), and — for <see cref="ArtifactDirStatus.Unreadable"/> — why the
    /// measurement could not be made.
    /// </summary>
    public sealed record Result(
        string Directory,
        ArtifactDirStatus Status,
        IReadOnlyList<string> MissingFiles,
        bool HasEngineEntrypoint,
        string? UnreadableReason,
        bool HasSiblingProvisionPayload = false)
    {
        /// <summary>True only for <see cref="ArtifactDirStatus.Complete"/>.</summary>
        public bool IsUsable => Status == ArtifactDirStatus.Complete;

        /// <summary>
        /// One line per fact, naming the directory and what is wrong with it — so a
        /// consumer reports "this artifact directory is broken" instead of letting a
        /// FileNotFoundException from inside an assembly load stand in for it.
        /// </summary>
        public string Explain()
        {
            switch (Status)
            {
                case ArtifactDirStatus.Complete:
                    return $"BC artifact directory {Directory} holds a complete engine closure.";

                case ArtifactDirStatus.Absent:
                    // Deliberately NOT "partially provisioned": nothing is broken here.
                    return $"BC artifact directory {Directory} does not exist — that version is not provisioned. "
                         + $"Fix: {BcArtifacts.ProvisionHint()}";

                case ArtifactDirStatus.Unreadable:
                    return $"BC artifact directory {Directory} could not be inspected: {UnreadableReason}. "
                         + "This is not a missing download — remove or rename whatever occupies that path first.";

                default:
                    var lines = new List<string>
                    {
                        $"BC artifact directory {Directory} is partially provisioned — it exists but its engine closure is incomplete.",
                    };
                    if (HasEngineEntrypoint)
                        // The dangerous shape: it looks like a service tier, so consumers
                        // accept it and fail later, deeper and less legibly.
                        lines.Add("  It carries Microsoft.Dynamics.Nav.Ncl.dll, so it passes a presence check and fails later inside an assembly load.");
                    else if (HasSiblingProvisionPayload)
                        // #2226: `provision`'s platform-app and service-tier sub-steps can
                        // resolve to DIFFERENT patch builds of one major.minor, leaving a
                        // directory that is a complete platform-apps cache and an empty
                        // service tier. Nothing here is corrupt, so the message must not
                        // send the reader looking for corruption.
                        lines.Add("  It carries no engine DLLs at all, only a platform-apps/test-apps payload — "
                                + "the service tier for this major.minor landed under a different patch directory (#2226). "
                                + "The payload here is usable; this directory is simply not a service tier.");
                    else
                        lines.Add("  It does not carry Microsoft.Dynamics.Nav.Ncl.dll, so it is not a usable service-tier directory at all.");
                    lines.Add("  Missing:");
                    foreach (var f in MissingFiles)
                        lines.Add($"    - {Path.Combine(Directory, f)}");
                    lines.Add($"  Fix: al-runner provision --service-tier --bc-version {Path.GetFileName(Directory)} --force");
                    return string.Join(Environment.NewLine, lines);
            }
        }
    }

    /// <summary>
    /// The engine entrypoint every "is this a service-tier directory" check keys on. Its
    /// presence is what makes the 82-DLL shape pass such a check and fail later; its
    /// absence is what makes the zero-DLL shape fail one immediately. Recorded so the two
    /// broken shapes stay distinguishable rather than collapsing into one "broken".
    /// </summary>
    private const string EngineEntrypoint = "Microsoft.Dynamics.Nav.Ncl.dll";

    /// <summary>
    /// The runner-owned payload subdirectories `provision` writes beside an engine closure.
    /// Their presence in a directory with no engine DLLs is the #2226 signature: the
    /// platform-app sub-step resolved a different patch build from the service-tier one, so
    /// one major.minor ends up split across two directories, each complete for its own half.
    /// </summary>
    private static readonly string[] SiblingPayloadDirs = { "platform-apps", "test-apps" };

    /// <summary>
    /// Classify one artifact version directory. Never throws: an I/O failure is the
    /// <see cref="ArtifactDirStatus.Unreadable"/> third state, reported with its reason,
    /// rather than an exception a caller would have to guess the meaning of.
    /// </summary>
    public static Result Classify(string versionDir)
    {
        if (string.IsNullOrWhiteSpace(versionDir))
            return new Result(versionDir ?? "", ArtifactDirStatus.Unreadable,
                Array.Empty<string>(), false, "no path was supplied");

        // A file sitting where a directory belongs is NOT an absent version: `provision`
        // cannot fix it, so it must not be reported as something `provision` would fix.
        if (File.Exists(versionDir) && !Directory.Exists(versionDir))
            return new Result(versionDir, ArtifactDirStatus.Unreadable,
                Array.Empty<string>(), false, "the path is a file, not a directory");

        bool exists;
        try
        {
            exists = Directory.Exists(versionDir);
        }
        catch (Exception ex)
        {
            return new Result(versionDir, ArtifactDirStatus.Unreadable,
                Array.Empty<string>(), false, ex.Message);
        }

        // Ask ProvisioningCheck rather than re-listing the closure here: one definition of
        // "complete", shared with the startup gate and the post-download re-check.
        var version = Path.GetFileName(versionDir.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        ProvisioningCheck.Report report;
        bool hasEntrypoint;
        bool hasPayload;
        try
        {
            report = ProvisioningCheck.Check(version, versionDir);
            hasEntrypoint = exists && File.Exists(Path.Combine(versionDir, EngineEntrypoint));
            hasPayload = exists && SiblingPayloadDirs.Any(
                sub => Directory.Exists(Path.Combine(versionDir, sub)));
        }
        catch (Exception ex)
        {
            return new Result(versionDir, ArtifactDirStatus.Unreadable,
                Array.Empty<string>(), false, ex.Message);
        }

        if (!exists)
            // The genuinely-absent case stays a legitimate state, per
            // guards-need-a-third-state.md's constraint. Note it shares its missing-file
            // list with the empty-directory case below and is still a different status.
            return new Result(versionDir, ArtifactDirStatus.Absent,
                report.MissingFiles, false, null);

        return report.Ok
            ? new Result(versionDir, ArtifactDirStatus.Complete, report.MissingFiles, hasEntrypoint, null, hasPayload)
            : new Result(versionDir, ArtifactDirStatus.Partial, report.MissingFiles, hasEntrypoint, null, hasPayload);
    }

    /// <summary>
    /// Classify every version-named directory under an artifacts root, newest first.
    ///
    /// <para>A root that does not exist yields an empty list rather than throwing — a
    /// machine with nothing provisioned is a legitimate state, and a probe that hard-errors
    /// there would swap a false green for a false red.</para>
    ///
    /// <para>Entries whose names do not parse as a version are skipped: the root also holds
    /// non-artifact directories, and reporting one as a broken artifact directory would be
    /// a false positive.</para>
    /// </summary>
    public static IReadOnlyList<Result> ScanRoot(string artifactsRootDir)
    {
        if (string.IsNullOrWhiteSpace(artifactsRootDir) || !Directory.Exists(artifactsRootDir))
            return Array.Empty<Result>();

        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(artifactsRootDir).ToList();
        }
        catch
        {
            return Array.Empty<Result>();
        }

        return dirs
            .Select(d => (Dir: d, Ver: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
            .Where(t => t.Ver != null)
            .OrderByDescending(t => t.Ver)
            .Select(t => Classify(t.Dir))
            .ToList();
    }
}
