namespace AlRunner.Infrastructure;

/// <summary>
/// #4923: fetches the BC backup `--test-data` hydrates from when none of the paths
/// <see cref="TestDataOptions.ResolveBackupPath"/> probes holds it. Runs only under
/// auto-provision (the default) or the `provision` subcommand, and never for
/// `--test-data=PATH`. It writes to the runner artifacts root, the second path
/// <see cref="TestDataOptions.CandidateBackupPaths"/> probes.
/// </summary>
internal static class TestDataBackupProvisioning
{
    internal enum Outcome { NotRequested, AlreadyPresent, Downloaded }

    /// <summary>The network download, replaceable so tests stay offline. Same contract as
    /// <see cref="AlRunner.Provisioning.ArtifactDownloader.TestData"/>: (version, outputDir,
    /// country, log) → 0 on success. That downloader writes through a <c>.partial</c> file and
    /// moves it into place, so an interrupted download never reads as a backup.</summary>
    internal static Func<string, string, string, Action<string>, int> Download =
        (version, outputDir, country, log) =>
            AlRunner.Provisioning.ArtifactDownloader.TestData(version, outputDir, country, log);

    /// <summary>
    /// Ensures a backup exists for <paramref name="version"/>/<paramref name="country"/>.
    /// Throws <see cref="TestDataUnavailableException"/> when the download fails: the run must
    /// stop rather than proceed against an empty database.
    /// </summary>
    internal static Outcome EnsureBackup(bool autoProvision, string version, string country,
        string? home, string? runnerArtifactsRoot, Action<string> report, bool verbose)
    {
        if (!TestDataOptions.Enabled || TestDataOptions.ExplicitBackupPath != null || !autoProvision)
            return Outcome.NotRequested;

        var candidates = TestDataOptions.CandidateBackupPaths(home, runnerArtifactsRoot, version, country);
        if (candidates.Any(File.Exists))
            return Outcome.AlreadyPresent;

        var fileName = TestDataOptions.BackupFileName(country);
        var url = AlRunner.Provisioning.ArtifactDownloader.TestDataArtifactUrl(version, country);
        if (string.IsNullOrEmpty(runnerArtifactsRoot))
            throw new TestDataUnavailableException(
                $"--test-data: cannot download {fileName} for BC {version} ({country}) from {url}: "
                + "the runner artifacts root could not be determined.");

        var targetDir = Path.Combine(runnerArtifactsRoot, version, country);
        report($"[provision] --test-data: downloading {fileName} for BC {version} ({country}) "
            + $"into {targetDir} (about 1 GB, one time)...");

        // The downloader's own "Error:" line is the reason; keep it for the first line of the
        // exception, which is the only line the bundle reporter shows.
        string? firstError = null;
        var sink = ProvisionProgressLog.Condense(report, verbose);
        int rc;
        try
        {
            rc = Download(version, targetDir, country, message =>
            {
                if (firstError == null && ProvisionProgressLog.IsError(message))
                    firstError = message.Trim();
                sink(message);
            });
        }
        catch (Exception ex)
        {
            throw new TestDataUnavailableException(
                $"--test-data: downloading {fileName} for BC {version} ({country}) from {url} failed: "
                + $"{ex.GetType().Name}: {ex.Message}");
        }

        var written = Path.Combine(targetDir, fileName);
        if (rc != 0 || !File.Exists(written))
            throw new TestDataUnavailableException(
                $"--test-data: downloading {fileName} for BC {version} ({country}) from {url} failed: "
                + (firstError ?? (rc != 0 ? $"downloader exit code {rc}" : $"it reported success but wrote no {written}"))
                + ". No tests ran; retry, or pass --test-data=/path/to/" + fileName + ".");

        report($"[provision] --test-data: wrote {written}.");
        return Outcome.Downloaded;
    }
}
