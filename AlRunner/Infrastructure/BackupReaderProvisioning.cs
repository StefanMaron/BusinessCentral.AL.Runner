using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AlRunner.Infrastructure;

/// <summary>One platform's release asset: its file name and its pinned SHA-256 (null when the
/// pin carries none, which <see cref="BackupReaderProvisioning"/> refuses).</summary>
internal sealed record BackupReaderAsset(string File, string? Sha256);

/// <summary>
/// #4925: the pinned backup reader release — <c>.github/backup-reader.json</c>, embedded into
/// the runner and read by <c>ms-bucket.yml</c>, so the tag and checksums exist once.
/// </summary>
internal sealed record BackupReaderPin(
    string Repository, string Tag, IReadOnlyDictionary<string, BackupReaderAsset> Assets)
{
    internal const string ResourceName = "AlRunner.backup-reader.json";

    private static BackupReaderPin? _embedded;

    /// <summary>The pin this runner build was compiled with.</summary>
    internal static BackupReaderPin Embedded => _embedded ??= LoadEmbedded();

    private static BackupReaderPin LoadEmbedded()
    {
        using var stream = typeof(BackupReaderPin).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new BackupReaderProvisioningException(BackupReaderProvisioningFailure.PinUnreadable,
                $"--test-data: this runner build carries no '{ResourceName}' resource, so it does not know "
                + $"which backup reader release to install. Set {BackupReaderTool.ExecutableEnvVar} to a reader.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Parses the pin. A missing repository or tag, or a malformed document, refuses:
    /// a pin that cannot be read must not resolve to "install whatever is there".</summary>
    internal static BackupReaderPin Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var repository = root.TryGetProperty("repository", out var r) ? r.GetString() : null;
            var tag = root.TryGetProperty("tag", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(tag))
                throw new BackupReaderProvisioningException(BackupReaderProvisioningFailure.PinUnreadable,
                    "--test-data: the backup reader pin names no repository or no tag.");
            var assets = new Dictionary<string, BackupReaderAsset>(StringComparer.Ordinal);
            if (root.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Object)
                foreach (var p in a.EnumerateObject())
                {
                    var file = p.Value.TryGetProperty("file", out var f) ? f.GetString() : null;
                    var sha = p.Value.TryGetProperty("sha256", out var s) ? s.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(file))
                        assets[p.Name] = new BackupReaderAsset(file, string.IsNullOrWhiteSpace(sha) ? null : sha);
                }
            return new BackupReaderPin(repository, tag, assets);
        }
        catch (JsonException ex)
        {
            throw new BackupReaderProvisioningException(BackupReaderProvisioningFailure.PinUnreadable,
                $"--test-data: the backup reader pin is not valid JSON ({ex.Message}).");
        }
    }

    internal string AssetUrl(BackupReaderAsset asset)
        => $"https://github.com/{Repository}/releases/download/{Tag}/{asset.File}";
}

/// <summary>Why installing the reader failed. Each is its own value so a caller, and a test,
/// can tell a network outage from a tampered asset.</summary>
internal enum BackupReaderProvisioningFailure
{
    PinUnreadable,
    UnsupportedPlatform,
    ChecksumMissing,
    Unreachable,
    AssetMissing,
    ChecksumMismatch,
    InstallFailed,
}

internal sealed class BackupReaderProvisioningException : Exception
{
    internal BackupReaderProvisioningFailure Failure { get; }

    internal BackupReaderProvisioningException(BackupReaderProvisioningFailure failure, string message)
        : base(message) => Failure = failure;
}

/// <summary>
/// #4925: installs the pinned backup reader where <see cref="BackupReaderTool"/> probes it, the
/// per-user cache slot <c>&lt;cache root&gt;/bcbak/bcbak</c>. Only that slot is managed: a reader
/// at <c>AL_RUNNER_BCBAK</c> or on PATH is the user's choice and is never replaced.
/// </summary>
internal static class BackupReaderProvisioning
{
    internal enum Outcome { NotRequested, UserSupplied, AlreadyPresent, Installed, Replaced }

    internal enum FetchStatus { Ok, NotFound }

    /// <summary>Fetches <c>url</c> into <c>destinationFile</c>. <see cref="FetchStatus.NotFound"/>
    /// for an HTTP 404; any other failure throws.</summary>
    internal delegate FetchStatus Fetcher(string url, string destinationFile);

    internal static readonly Fetcher HttpFetch = (url, destinationFile) =>
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return FetchStatus.NotFound;
        response.EnsureSuccessStatusCode();
        using var body = response.Content.ReadAsStream();
        using var file = File.Create(destinationFile);
        body.CopyTo(file);
        return FetchStatus.Ok;
    };

    /// <summary>The release platform key for this process, or null when it is none of the
    /// shapes the reader release publishes.</summary>
    internal static string? CurrentPlatform()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (arch == null) return null;
        if (OperatingSystem.IsLinux()) return $"linux-{arch}";
        if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        return null;
    }

    internal static Outcome EnsureReader(bool testDataEnabled, bool autoProvision, string? envValue,
        string? cacheRoot, Func<string?> findOnPath, BackupReaderPin pin, string? platform,
        Fetcher fetch, Action<string> report)
    {
        if (!testDataEnabled || !autoProvision) return Outcome.NotRequested;

        // Walk the slots in BackupReaderTool.Resolve's order, so the reader judged here is the
        // one the run will start.
        var managed = BackupReaderTool.ManagedInstallPath(cacheRoot);
        var existing = BackupReaderTool.CandidateExecutables(envValue, cacheRoot).FirstOrDefault(File.Exists);
        if (existing != null && !string.Equals(existing, managed, StringComparison.Ordinal))
            return Outcome.UserSupplied;
        if (existing == null && findOnPath() != null)
            return Outcome.UserSupplied;

        if (platform == null || !pin.Assets.TryGetValue(platform, out var asset))
            throw new BackupReaderProvisioningException(BackupReaderProvisioningFailure.UnsupportedPlatform,
                $"--test-data: backup reader {pin.Tag} publishes no binary for this platform "
                + $"({platform ?? RuntimeInformation.RuntimeIdentifier}); it publishes "
                + string.Join(", ", pin.Assets.Keys.OrderBy(k => k, StringComparer.Ordinal))
                + $". Build one from https://github.com/{pin.Repository} and set {BackupReaderTool.ExecutableEnvVar} to it.");
        var expected = asset.Sha256?.ToLowerInvariant();
        if (expected == null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new BackupReaderProvisioningException(BackupReaderProvisioningFailure.ChecksumMissing,
                $"--test-data: the backup reader pin carries no valid SHA-256 for {platform} "
                + $"('{asset.Sha256}'), so {asset.File} cannot be verified and is not downloaded.");
        if (managed == null)
            throw new BackupReaderProvisioningException(BackupReaderProvisioningFailure.InstallFailed,
                "--test-data: the per-user cache root could not be determined, so there is nowhere to install "
                + $"the backup reader; set {BackupReaderTool.ExecutableEnvVar} to one.");

        var replacing = existing != null;
        if (replacing && string.Equals(Sha256Of(managed), expected, StringComparison.Ordinal))
            return Outcome.AlreadyPresent;

        var url = pin.AssetUrl(asset);
        var dir = Path.GetDirectoryName(managed)!;
        report(replacing
            ? $"[provision] --test-data: replacing the backup reader at {managed}, which is not {pin.Tag}, with {pin.Tag} ({asset.File})..."
            : $"[provision] --test-data: downloading backup reader {pin.Tag} ({asset.File}) into {managed}...");

        Directory.CreateDirectory(dir);
        var partial = Path.Combine(dir, $".{Path.GetFileName(managed)}.{Guid.NewGuid():N}.partial");
        try
        {
            FetchStatus status;
            try { status = fetch(url, partial); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                throw new BackupReaderProvisioningException(BackupReaderProvisioningFailure.Unreachable,
                    $"--test-data: could not download backup reader {pin.Tag} from {url}: {ex.GetType().Name}: "
                    + $"{ex.Message.ReplaceLineEndings(" ")}. Nothing was installed; retry, or set {BackupReaderTool.ExecutableEnvVar}.");
            }
            if (status == FetchStatus.NotFound || !File.Exists(partial))
                throw new BackupReaderProvisioningException(BackupReaderProvisioningFailure.AssetMissing,
                    $"--test-data: backup reader release {pin.Tag} has no asset at {url}. Nothing was installed.");

            var actual = Sha256Of(partial);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new BackupReaderProvisioningException(BackupReaderProvisioningFailure.ChecksumMismatch,
                    $"--test-data: backup reader {asset.File} from {url} has SHA-256 {actual}, but the pin says "
                    + $"{expected}. Refusing to install it; nothing was changed.");

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            // A rename within one directory: a concurrent run sees the old reader or the new one.
            File.Move(partial, managed, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { }
        }

        report($"[provision] --test-data: installed backup reader {pin.Tag} at {managed}.");
        return replacing ? Outcome.Replaced : Outcome.Installed;
    }

    internal static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
